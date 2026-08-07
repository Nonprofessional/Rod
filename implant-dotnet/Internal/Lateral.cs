using System.Security.Cryptography;
using Rod.V1;

namespace Rod.Implant.Internal;

// Holds the lateral.move child-derivation verb the reference implant advertises
// (architecture.md Sec 10.1, roadmap M9.1). A lateral.move task tells this
// implant to derive a child: enroll a fresh implant identity against the same
// teamserver, naming itself as the parent, and report the child id back.
//
// The child's stager token is not baked into this implant (its own token is
// spent at its own enroll); the operator provisions it in the task arguments.
// This keeps derivation inside the M5.2 token-gated authorization model -- the
// server still resolves and scope-checks the parent before recording the
// linkage -- and mirrors how the recon verbs take their target in arguments.
//
// As with the other reference handlers, this performs no evasion, no
// obfuscation, and no destructive behavior (RESPONSIBLE-USE.md, architecture.md
// Sec 7). The operator is responsible for targeting only systems they are
// authorized to test (RESPONSIBLE-USE.md).

internal static class Lateral
{
    /// <summary>
    /// Derives a child implant by enrolling a fresh identity against the
    /// teamserver this implant enrolled into, naming itself as the parent
    /// (architecture.md Sec 10.1). Arguments are "&lt;token&gt;" or
    /// "&lt;token&gt; &lt;class&gt;", whitespace-separated; the token is the
    /// child's stager secret (provisioned by the operator) and the optional class
    /// names a non-default implant class.
    /// </summary>
    /// <returns>
    /// Succeeded with the child implant id (and the echoed parent when the server
    /// returns one) when the enroll round-trip completes; Failed with a clear
    /// cause otherwise. A handler built without an enroll bundle (derivation
    /// disabled) reports Failed so the operator sees the cause rather than a
    /// silent no-op.
    /// </returns>
    public static async Task<(TaskOutcome Outcome, string Output)> MoveAsync(
        string arguments,
        EnrollBundle? enroll)
    {
        if (enroll is null)
            return (TaskOutcome.Failed, "lateral.move is not available (no enroll bundle)");

        if (!TryParseArgs(arguments, out var token, out _))
            return (TaskOutcome.Failed, "lateral.move expects '<token>' or '<token> <class>'");

        // A child owns its own keypair; only the public half crosses enroll
        // (architecture.md Sec 9). 2048-bit RSA matches the parent's key size.
        using var childKey = RSA.Create(2048);
        try
        {
            // The child enrolls against the same endpoint, naming this implant as
            // parent. The server resolves and scope-checks the parent before
            // recording the linkage (architecture.md Sec 10.1).
            var enrolled = await C2.EnrollAsync(
                enroll.Url, token, enroll.ParentId, childKey, enroll.CAs, enroll.Profile);
            // Report the child id so the operator can confirm the recorded lineage.
            // The server echoes the parent back, so include it when present as an
            // independent confirmation the linkage landed.
            var output = enrolled.ParentImplantId.Length > 0
                ? $"{enrolled.ImplantId}\nparent={enrolled.ParentImplantId}"
                : enrolled.ImplantId;
            return (TaskOutcome.Succeeded, output);
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, "enroll child: " + ex.Message);
        }
    }

    /// <summary>
    /// Synchronous wrapper over <see cref="MoveAsync"/> for the dispatch switch,
    /// which is itself synchronous (the dispatch loop blocks on each task). Runs
    /// the async enroll on the thread pool and waits for it.
    /// </summary>
    public static (TaskOutcome Outcome, string Output) Move(string arguments, EnrollBundle? enroll)
        => MoveAsync(arguments, enroll).GetAwaiter().GetResult();

    // Splits the lateral.move argument string into the child stager token and an
    // optional implant class. Returns false when the token is empty or more than
    // two fields are present, mirroring the recon verbs' strict parse.
    private static bool TryParseArgs(string arguments, out string token, out string? @class)
    {
        token = string.Empty;
        @class = null;
        var fields = arguments.Split(StringSeparators.Space, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is 0 or > 2)
            return false;
        token = fields[0];
        if (fields.Length == 2)
            @class = fields[1];
        return true;
    }

    private static class StringSeparators
    {
        public static readonly char[] Space = { ' ' };
    }
}
