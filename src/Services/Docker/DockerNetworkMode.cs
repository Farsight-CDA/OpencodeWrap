namespace OpencodeWrap.Services.Docker;

internal enum DockerNetworkMode
{
    Bridge,
    Host
}

internal static class DockerNetworkModeExtensions
{
    public static bool SupportsAdditionalNetworks(this DockerNetworkMode dockerNetworkMode)
        => dockerNetworkMode is DockerNetworkMode.Bridge;

    public static string GetLabel(this DockerNetworkMode dockerNetworkMode) => dockerNetworkMode switch
    {
        DockerNetworkMode.Host => "host",
        _ => "bridge"
    };

    public static bool IsHost(this DockerNetworkMode dockerNetworkMode)
        => dockerNetworkMode is DockerNetworkMode.Host;

    public static bool TryParsePersistedValue(string? persistedValue, out DockerNetworkMode dockerNetworkMode)
    {
        switch(persistedValue?.Trim().ToLowerInvariant())
        {
            case "bridge":
                dockerNetworkMode = DockerNetworkMode.Bridge;
                return true;
            case "host":
                dockerNetworkMode = DockerNetworkMode.Host;
                return true;
            default:
                dockerNetworkMode = default;
                return false;
        }
    }
}
