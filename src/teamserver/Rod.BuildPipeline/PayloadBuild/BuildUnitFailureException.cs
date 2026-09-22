namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// A build unit could not run its toolchain: the source tree is missing,
/// cargo fails to start or exits nonzero, the expected artifact never
/// appears. These are environment faults on the server -- not operator
/// mistakes -- so the surfaces that catch them report a server-side failure
/// (HTTP 500, an opaque job error) instead of the 400 / verbatim-message
/// treatment contract refusals get. A missing cross toolchain read as a
/// "bad request" once cost a day of triage: the request was fine.
/// </summary>
public sealed class BuildUnitFailureException : Exception
{
    public BuildUnitFailureException(string message) : base(message)
    {
    }
}
