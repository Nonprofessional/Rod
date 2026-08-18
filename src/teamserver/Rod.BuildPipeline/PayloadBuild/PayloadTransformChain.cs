namespace Rod.BuildPipeline.PayloadBuild;

/// <summary>
/// The ordered post-build transform chain (architecture.md Sec 6, the
/// transform seam): applied to the build unit's output before the artifact is
/// fingerprinted and recorded, so the stored fingerprint covers exactly the
/// bytes the target will run and the trail names every transform that
/// produced them. Each transform's output feeds the next one's input.
///
/// The chain the composition root registers is the whole platform surface:
/// <see cref="Empty"/> is the default (no in-tree transforms ship -- the
/// empty chain is the seam, exactly like the capability placeholders), and
/// out-of-tree transforms arrive only through the config-listed loader
/// (<c>Build:Transforms</c>, the same explicit-list shape as
/// <c>Tradecraft:Modules</c>).
/// </summary>
public sealed class PayloadTransformChain
{
    private readonly IReadOnlyList<IPayloadTransform> _transforms;

    public PayloadTransformChain(IReadOnlyList<IPayloadTransform> transforms)
        => _transforms = transforms;

    /// <summary>The default chain: no transforms, bytes pass through as built.</summary>
    public static PayloadTransformChain Empty { get; } = new(Array.Empty<IPayloadTransform>());

    /// <summary>Whether any transform is registered.</summary>
    public bool IsEmpty => _transforms.Count == 0;

    /// <summary>
    /// Runs the chain over the built bytes: transform one's output becomes
    /// transform two's input, and so on. Returns the final bytes plus the
    /// applied transforms in order -- name and metadata note each -- for the
    /// artifact record and the audit trail.
    /// </summary>
    public async Task<(byte[] Artifact, IReadOnlyList<PayloadTransformApplied> Applied)> ApplyAsync(
        BuildParams @params,
        byte[] artifact,
        CancellationToken cancellationToken = default)
    {
        if (_transforms.Count == 0)
            return (artifact, Array.Empty<PayloadTransformApplied>());

        var content = artifact;
        var applied = new List<PayloadTransformApplied>(_transforms.Count);
        foreach (var transform in _transforms)
        {
            var output = await transform.ApplyAsync(new PayloadTransformInput(@params, content), cancellationToken);
            content = output.Artifact;
            applied.Add(new PayloadTransformApplied(transform.Name, output.Metadata));
        }
        return (content, applied);
    }
}
