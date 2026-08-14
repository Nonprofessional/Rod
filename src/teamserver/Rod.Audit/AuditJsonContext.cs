using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rod.Audit;

/// <summary>
/// Source-generated JSON wiring for the durable audit/artifact stores. The file-backed adapters serialize the chained <see cref="AuditEvent"/>
/// and the metadata-only <see cref="Artifact"/> records to JSON Lines; a
/// <see cref="JsonSerializerContext"/> keeps that serialization reflection-free
/// and trim/AOT-clean, so the audit layer adds no runtime reflection cost and
/// ships no extra package.
///
/// The JSON form is the *storage* encoding only -- it is never an input to the
/// hash chain. <see cref="AuditChain"/> computes the hash over its hand-built
/// canonical join (runtime-independent, serializer-free) before the event is
/// written, and the stored line carries the already-stamped <see cref="AuditEvent.PreviousHash"/>
/// and <see cref="AuditEvent.Hash"/> verbatim. A reloaded trail therefore
/// round-trips through <see cref="AuditChain.VerifyTrail"/> unchanged: the same
/// bytes in, the same chain out.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(AuditEvent))]
[JsonSerializable(typeof(Artifact))]
[JsonSerializable(typeof(PayloadRecord))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;
