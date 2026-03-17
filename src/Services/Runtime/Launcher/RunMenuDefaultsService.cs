using OpencodeWrap.Services.Runtime.Infrastructure;
using System.Text.Json;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class RunMenuDefaultsService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly DockerHostService _dockerHostService;

    public bool TryLoadDefaults(out RunMenuDefaults defaults)
    {
        defaults = RunMenuDefaults.Empty;

        if(!_dockerHostService.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return false;
        }

        string defaultsPath = GetDefaultsPath(configRoot);
        if(!File.Exists(defaultsPath))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(defaultsPath);
            using var document = JsonDocument.Parse(stream);
            defaults = ReadDefaults(document.RootElement);
            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole("run", $"Failed to read run menu defaults from '{defaultsPath}': {ex.Message}");
            return false;
        }
    }

    public bool TrySaveDefaults(RunMenuDefaults defaults)
    {
        if(!_dockerHostService.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return false;
        }

        string defaultsPath = GetDefaultsPath(configRoot);

        try
        {
            var normalizedDefaults = NormalizeDefaults(defaults);

            using var stream = File.Create(defaultsPath);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true
            });

            writer.WriteStartObject();
            if(!String.IsNullOrWhiteSpace(normalizedDefaults.DefaultProfileName))
            {
                writer.WriteString("defaultProfileName", normalizedDefaults.DefaultProfileName);
            }

            if(normalizedDefaults.DefaultUiMode is { } defaultUiMode)
            {
                writer.WriteString("defaultUiMode", GetPersistedRunUiModeValue(defaultUiMode));
            }

            if(!String.IsNullOrWhiteSpace(normalizedDefaults.DefaultDockerNetworkMode))
            {
                writer.WriteString("defaultDockerNetworkMode", normalizedDefaults.DefaultDockerNetworkMode);
            }

            writer.WritePropertyName("resourceDirectories");
            writer.WriteStartArray();
            foreach(string directoryPath in normalizedDefaults.ResourceDirectories)
            {
                writer.WriteStringValue(directoryPath);
            }

            writer.WriteEndArray();

            writer.WritePropertyName("dockerNetworks");
            writer.WriteStartArray();
            foreach(string networkName in normalizedDefaults.DockerNetworks)
            {
                writer.WriteStringValue(networkName);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole("run", $"Failed to save run menu defaults to '{defaultsPath}': {ex.Message}");
            return false;
        }
    }

    private static RunMenuDefaults ReadDefaults(JsonElement rootElement)
    {
        if(rootElement.ValueKind is not JsonValueKind.Object)
        {
            return RunMenuDefaults.Empty;
        }

        string? defaultProfileName = null;
        if(rootElement.TryGetProperty("defaultProfileName", out var defaultProfileElement)
            && defaultProfileElement.ValueKind is JsonValueKind.String)
        {
            defaultProfileName = defaultProfileElement.GetString();
        }

        RunUiMode? defaultUiMode = null;
        if(rootElement.TryGetProperty("defaultUiMode", out var defaultUiModeElement)
            && defaultUiModeElement.ValueKind is JsonValueKind.String
            && TryParseRunUiMode(defaultUiModeElement.GetString(), out var parsedDefaultUiMode))
        {
            defaultUiMode = parsedDefaultUiMode;
        }

        string? defaultDockerNetworkMode = null;
        if(rootElement.TryGetProperty("defaultDockerNetworkMode", out var defaultDockerNetworkModeElement)
            && defaultDockerNetworkModeElement.ValueKind is JsonValueKind.String)
        {
            defaultDockerNetworkMode = NormalizeDockerNetworkMode(defaultDockerNetworkModeElement.GetString());
        }

        return NormalizeDefaults(new RunMenuDefaults(
            defaultProfileName,
            defaultUiMode,
            defaultDockerNetworkMode,
            ReadStringArray(rootElement, "resourceDirectories"),
            ReadStringArray(rootElement, "dockerNetworks")));
    }

    private static List<string> ReadStringArray(JsonElement rootElement, string propertyName)
    {
        var values = new List<string>();
        if(!rootElement.TryGetProperty(propertyName, out var propertyElement)
            || propertyElement.ValueKind is not JsonValueKind.Array)
        {
            return values;
        }

        foreach(var item in propertyElement.EnumerateArray())
        {
            if(item.ValueKind is JsonValueKind.String && item.GetString() is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static RunMenuDefaults NormalizeDefaults(RunMenuDefaults defaults)
    {
        string? defaultProfileName = String.IsNullOrWhiteSpace(defaults.DefaultProfileName)
            ? null
            : defaults.DefaultProfileName.Trim();
        string? defaultDockerNetworkMode = NormalizeDockerNetworkMode(defaults.DefaultDockerNetworkMode);

        var seenResourceDirectories = new HashSet<string>(GetHostPathComparer());
        List<string> resourceDirectories = [.. defaults.ResourceDirectories
            .Where(path => !String.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Where(seenResourceDirectories.Add)];

        var seenNetworkNames = new HashSet<string>(StringComparer.Ordinal);
        List<string> dockerNetworks = [.. defaults.DockerNetworks
            .Where(name => !String.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Where(seenNetworkNames.Add)];

        return new RunMenuDefaults(defaultProfileName, defaults.DefaultUiMode, defaultDockerNetworkMode, resourceDirectories, dockerNetworks);
    }

    private static string? NormalizeDockerNetworkMode(string? persistedValue) => persistedValue?.Trim().ToLowerInvariant() switch
    {
        "bridge" => "bridge",
        "host" => "host",
        _ => null
    };

    private static string GetPersistedRunUiModeValue(RunUiMode runUiMode) => runUiMode switch
    {
        RunUiMode.Web => "web",
        RunUiMode.Desktop => "desktop",
        _ => "tui"
    };

    private static bool TryParseRunUiMode(string? persistedValue, out RunUiMode runUiMode)
    {
        switch(persistedValue?.Trim().ToLowerInvariant())
        {
            case "tui":
                runUiMode = RunUiMode.Tui;
                return true;
            case "web":
                runUiMode = RunUiMode.Web;
                return true;
            case "desktop":
                runUiMode = RunUiMode.Desktop;
                return true;
            default:
                runUiMode = default;
                return false;
        }
    }

    private static string GetDefaultsPath(string configRoot)
        => Path.Combine(configRoot, OpencodeWrapConstants.HOST_RUN_MENU_DEFAULTS_FILE_NAME);

    private static StringComparer GetHostPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed record RunMenuDefaults(string? DefaultProfileName, RunUiMode? DefaultUiMode, string? DefaultDockerNetworkMode, IReadOnlyList<string> ResourceDirectories, IReadOnlyList<string> DockerNetworks)
{
    public static RunMenuDefaults Empty { get; } = new(null, null, null, [], []);
}
