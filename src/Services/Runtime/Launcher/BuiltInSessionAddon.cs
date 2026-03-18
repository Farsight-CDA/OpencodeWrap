namespace OpencodeWrap.Services.Runtime;

internal sealed record BuiltInSessionAddon(
    string Name,
    IReadOnlyDictionary<string, string> Files
);
