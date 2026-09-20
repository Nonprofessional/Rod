using System.Text.Json.Serialization;

namespace Rod.Implant.Internal;

// The source-generated serializer for the JSON enroll contract. The two enroll
// call sites it serves were the tree's only reflection-based serialization:
// generated code keeps every artifact shape -- trimmed, native AOT, and the
// plain single-file build -- off the reflection path while producing the same
// bytes on the wire (every property name is pinned by [JsonPropertyName], so
// the context's default options match the reflection defaults exactly).
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
internal sealed partial class EnrollJsonContext : JsonSerializerContext
{
}
