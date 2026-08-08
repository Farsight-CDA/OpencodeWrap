using System.Text.Json.Serialization;

namespace OpencodeWrap.Services.Opencode;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpencodePackagePin))]
[JsonSerializable(typeof(ResolvedOpencodeRelease))]
internal sealed partial class OpencodeJsonContext : JsonSerializerContext
{
}
