using System.Collections.Frozen;
using System.Text.Json;

namespace OpencodeWrap.Services.Docker;

internal sealed partial class DockerHostService : Singleton
{
    private static readonly FrozenSet<string> _reservedNetworkModeNames =
        new[] { "bridge", "host", "none" }.ToFrozenSet(StringComparer.Ordinal);
    private const string DOCKER_DESKTOP_HOST_NETWORKING_SETTINGS_KEY = "HostNetworkingEnabled";
    private const string DOCKER_DESKTOP_LEGACY_HOST_NETWORKING_SETTINGS_KEY = "hostNetworkingEnabled";

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public DockerDesktopHostNetworkingState GetDockerDesktopHostNetworkingState()
    {
        string appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if(String.IsNullOrWhiteSpace(appDataDirectory))
        {
            return DockerDesktopHostNetworkingState.Unknown;
        }

        string settingsStorePath = Path.Combine(appDataDirectory, "Docker", "settings-store.json");
        if(!File.Exists(settingsStorePath))
        {
            return DockerDesktopHostNetworkingState.Unknown;
        }

        try
        {
            using var stream = File.OpenRead(settingsStorePath);
            using var document = JsonDocument.Parse(stream);
            if(document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return DockerDesktopHostNetworkingState.Unknown;
            }

            if(TryReadHostNetworkingSetting(document.RootElement, out bool enabled))
            {
                return enabled
                    ? DockerDesktopHostNetworkingState.Enabled
                    : DockerDesktopHostNetworkingState.Disabled;
            }
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.DOCKER, $"Failed to read Docker Desktop settings from '{settingsStorePath}': {ex.Message}");
        }

        return DockerDesktopHostNetworkingState.Unknown;
    }

    public async Task<DockerResult<IReadOnlyList<string>>> TryListNetworkNamesAsync()
    {
        var networkList = await DockerCommand.RunAsync(["network", "ls", "--format", "{{.Name}}"]);
        if(networkList is not DockerValue<DockerOutput>)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Failed to list Docker networks.");
            WriteDockerErrorDetails(networkList.ErrorDetails);
        }

        return networkList.Select<IReadOnlyList<string>>(output => [.. output.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !_reservedNetworkModeNames.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)]);
    }

    public async Task<DockerResult<IReadOnlyList<string>>> TryListVolumeNamesAsync()
    {
        var volumeList = await DockerCommand.RunAsync(["volume", "ls", "--format", "{{.Name}}"]);
        if(volumeList is not DockerValue<DockerOutput>)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Failed to list Docker volumes.");
            WriteDockerErrorDetails(volumeList.ErrorDetails);
        }

        return volumeList.Select<IReadOnlyList<string>>(output => [.. output.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)]);
    }

    public bool TryEnsureGlobalConfigDirectory(out string configDirectory)
    {
        configDirectory = "";

        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(String.IsNullOrWhiteSpace(homeDirectory))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Unable to resolve host home directory.");
            return false;
        }

        configDirectory = Path.Combine(homeDirectory, OpencodeWrapConstants.HOST_GLOBAL_CONFIG_DIRECTORY_NAME);

        try
        {
            Directory.CreateDirectory(configDirectory);
            Directory.CreateDirectory(Path.Combine(configDirectory, OpencodeWrapConstants.HOST_ADDON_ROOT_DIRECTORY_NAME));
            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, $"Failed to create host config directory '{configDirectory}': {ex.Message}");
            return false;
        }
    }

    private void WriteDockerErrorDetails(string? dockerError)
    {
        if(String.IsNullOrWhiteSpace(dockerError))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.DOCKER, "install Docker and ensure the daemon is running.");
            return;
        }

        string details = dockerError.Trim();
        _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, details);

        if(details.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.DOCKER, "start the Docker daemon and try again.");
        }
    }

    private static bool TryReadHostNetworkingSetting(JsonElement rootElement, out bool enabled)
    {
        enabled = false;

        if(rootElement.TryGetProperty(DOCKER_DESKTOP_HOST_NETWORKING_SETTINGS_KEY, out var currentValue)
            && currentValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            enabled = currentValue.GetBoolean();
            return true;
        }

        if(rootElement.TryGetProperty(DOCKER_DESKTOP_LEGACY_HOST_NETWORKING_SETTINGS_KEY, out var legacyValue)
            && legacyValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            enabled = legacyValue.GetBoolean();
            return true;
        }

        return false;
    }
}

internal enum DockerDesktopHostNetworkingState
{
    Unknown,
    Disabled,
    Enabled
}
