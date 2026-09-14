namespace Rod.Transport.Endpoints;

/// <summary>
/// The operator API's single error envelope: every non-success endpoint answer
/// carries <c>{ "error": ... }</c>, so clients read one shape. One definition
/// for every endpoint file; the operator layer (Rod.Operators) carries its own
/// because the layer rule forbids it from referencing transport.
/// </summary>
internal sealed record Problem(string Error);
