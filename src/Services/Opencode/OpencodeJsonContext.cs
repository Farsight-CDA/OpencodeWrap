using System.Text.Json.Serialization;

namespace OpencodeWrap.Services.Opencode;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpencodeReleaseAsset))]
[JsonSerializable(typeof(Dictionary<string, OpencodeReleaseAsset>))]
[JsonSerializable(typeof(LatestOpencodeRelease))]
[JsonSerializable(typeof(CachedLatestOpencodeRelease))]
[JsonSerializable(typeof(ManagedHostOpencodeMetadata))]
internal sealed partial class OpencodeJsonContext : JsonSerializerContext
{
}
