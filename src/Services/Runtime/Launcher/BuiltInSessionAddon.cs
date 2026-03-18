namespace OpencodeWrap.Services.Runtime.Launcher;

internal sealed record BuiltInSessionAddon(
    string Name,
    IReadOnlyDictionary<string, string> Files
);
