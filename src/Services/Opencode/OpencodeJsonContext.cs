using System.Text.Json.Serialization;

namespace OpencodeWrap.Services.Opencode;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpencodePackagePin))]
[JsonSerializable(typeof(OpencodeNpmPackageAsset))]
[JsonSerializable(typeof(Dictionary<string, OpencodeNpmPackageAsset>))]
[JsonSerializable(typeof(ResolvedOpencodeRelease))]
internal sealed partial class OpencodeJsonContext : JsonSerializerContext
{
}
