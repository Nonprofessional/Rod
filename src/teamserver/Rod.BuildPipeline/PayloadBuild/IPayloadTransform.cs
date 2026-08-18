namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// One post-build payload transform (architecture.md Sec 6, the transform
/// seam): the hook where an operator's build-time artifact transformation
/// runs. The chain is the platform's entire commitment -- each transform
/// names itself, receives the built bytes plus the build context, and returns
/// transformed bytes plus metadata that lands in the audit trail.
///
/// Everything else belongs to the transform. This is the boundary MSF drew
/// wrong with in-tree encoders: concrete transforms stay out-of-tree by
/// architecture.md Sec 13, and each transform owns its key material and its
/// decode contract end to end -- the service generates none, stores none, and
/// knows nothing about how the bytes are meant to be unwrapped on the target.
/// No in-tree transform ships; the empty chain is the seam, exactly like the
/// capability placeholders.
/// </summary>
public interface IPayloadTransform
{
    /// <summary>
    /// The transform's name, recorded on the built artifact and in the
    /// <c>PayloadBuilt</c> audit event so the trail names every transform
    /// that produced the stored bytes. Stable and specific -- an operator
    /// reading the trail later must be able to find the code behind the name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Applies the transform: <paramref name="input"/> carries the artifact
    /// bytes as the build unit produced them (or as the previous transform
    /// left them) plus the full build context. Returns the transformed bytes
    /// and a short metadata note for the audit trail (null when there is
    /// nothing worth recording beyond the name).
    /// </summary>
    Task<PayloadTransformOutput> ApplyAsync(
        PayloadTransformInput input,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The transform's input: the artifact bytes at this point in the chain plus
/// the build context (the request's class, target, transport, and beacon
/// profiles -- everything the build unit saw).
/// </summary>
public sealed record PayloadTransformInput(
    BuildParams Params,
    byte[] Artifact);

/// <summary>
/// The transform's output: the transformed bytes that continue down the chain
/// (or become the stored artifact), plus an optional note recorded alongside
/// the transform's name in the audit trail.
/// </summary>
public sealed record PayloadTransformOutput(
    byte[] Artifact,
    string? Metadata = null);

/// <summary>
/// One applied transform, as the chain reports it for the artifact and the
/// audit trail: the transform's name in application order and the metadata
/// note it returned (null when it returned none).
/// </summary>
public sealed record PayloadTransformApplied(
    string Name,
    string? Metadata);
