namespace OpencodeWrap.Services.Profile;

internal sealed record BuiltInSessionAddon(
    string Name,
    IReadOnlyDictionary<string, string> ResourceNames
)
{
    public bool HasBuildStep =>
        ResourceNames.ContainsKey($"{OpencodeWrapConstants.ADDON_BUILD_DIRECTORY_NAME}/{OpencodeWrapConstants.ADDON_INSTALL_FILE_NAME}");
}
