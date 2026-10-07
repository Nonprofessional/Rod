namespace Rod.Operators.Endpoints;

/// <summary>
/// The error envelope this layer's endpoints answer with. A sibling of
/// transport's own <c>Problem</c> (that one is internal to Rod.Transport):
/// the layer rule keeps Rod.Operators from referencing it, but within this
/// layer one definition serves every endpoint group.
/// </summary>
internal sealed record Problem(string Error);
