namespace OpencodeWrap.Services.Profile;

internal sealed record BuiltInProfileTemplate(
    string Name,
    string Dockerfile,
    string OpencodeConfig,
    bool IsDefault = false
);
