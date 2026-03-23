namespace OpencodeWrap.Services.Profile;

internal sealed record BuiltInSessionAddon(
    string Name,
    IReadOnlyDictionary<string, string> Files
);