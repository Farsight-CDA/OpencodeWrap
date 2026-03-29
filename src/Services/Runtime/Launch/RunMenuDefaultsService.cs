using OpencodeWrap.Services.Docker;
using OpencodeWrap.Services.Runtime.Core;
using System.Text.Json;

namespace OpencodeWrap.Services.Runtime.Launch;

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

            if(normalizedDefaults.DefaultDockerNetworkMode is { } defaultDockerNetworkMode)
            {
                writer.WriteString("defaultDockerNetworkMode", defaultDockerNetworkMode.GetLabel());
            }

            writer.WritePropertyName("containerMounts");
            writer.WriteStartArray();
            foreach(ContainerMount containerMount in normalizedDefaults.ContainerMounts)
            {
                writer.WriteStartObject();
                writer.WriteString("sourceType", GetPersistedContainerMountSourceTypeValue(containerMount.SourceType));
                writer.WriteString("source", containerMount.Source);
                writer.WriteString("containerPath", containerMount.ContainerPath);
                writer.WriteString("accessMode", GetPersistedContainerMountAccessModeValue(containerMount.AccessMode));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WritePropertyName("sessionAddons");
            writer.WriteStartArray();
            foreach(string addonName in normalizedDefaults.SessionAddons)
            {
                writer.WriteStringValue(addonName);
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

        DockerNetworkMode? defaultDockerNetworkMode = null;
        if(rootElement.TryGetProperty("defaultDockerNetworkMode", out var defaultDockerNetworkModeElement)
            && defaultDockerNetworkModeElement.ValueKind is JsonValueKind.String)
        {
            string? persistedDockerNetworkMode = defaultDockerNetworkModeElement.GetString();
            if(DockerNetworkModeExtensions.TryParsePersistedValue(persistedDockerNetworkMode, out var parsedDockerNetworkMode))
            {
                defaultDockerNetworkMode = parsedDockerNetworkMode;
            }
        }

        return NormalizeDefaults(new RunMenuDefaults(
            defaultProfileName,
            defaultUiMode,
            defaultDockerNetworkMode,
            ReadContainerMounts(rootElement),
            ReadStringArray(rootElement, "sessionAddons"),
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

    private static List<ContainerMount> ReadContainerMounts(JsonElement rootElement)
    {
        var mounts = new List<ContainerMount>();

        if(rootElement.TryGetProperty("containerMounts", out var propertyElement)
            && propertyElement.ValueKind is JsonValueKind.Array)
        {
            foreach(var item in propertyElement.EnumerateArray())
            {
                if(item.ValueKind is not JsonValueKind.Object
                    || !item.TryGetProperty("sourceType", out var sourceTypeElement)
                    || !item.TryGetProperty("source", out var sourceElement)
                    || !item.TryGetProperty("containerPath", out var containerPathElement)
                    || sourceTypeElement.ValueKind is not JsonValueKind.String
                    || sourceElement.ValueKind is not JsonValueKind.String
                    || containerPathElement.ValueKind is not JsonValueKind.String
                    || sourceTypeElement.GetString() is not { } sourceType
                    || sourceElement.GetString() is not { } source
                    || containerPathElement.GetString() is not { } containerPath
                    || !TryParseContainerMountSourceType(sourceType, out var parsedSourceType))
                {
                    continue;
                }

                ContainerMountAccessMode accessMode = ContainerMountAccessMode.ReadWrite;
                if(item.TryGetProperty("accessMode", out var accessModeElement)
                    && accessModeElement.ValueKind is JsonValueKind.String
                    && accessModeElement.GetString() is { } persistedAccessMode
                    && TryParseContainerMountAccessMode(persistedAccessMode, out var parsedAccessMode))
                {
                    accessMode = parsedAccessMode;
                }

                mounts.Add(new ContainerMount(parsedSourceType, source, containerPath, accessMode));
            }
        }

        mounts.AddRange(ReadLegacyDirectoryMounts(rootElement));
        mounts.AddRange(ReadLegacyNamedVolumeMounts(rootElement));
        return mounts;
    }

    private static List<ContainerMount> ReadLegacyDirectoryMounts(JsonElement rootElement)
    {
        if(!rootElement.TryGetProperty("resourceDirectories", out var propertyElement)
            || propertyElement.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var mounts = new List<ContainerMount>();
        var seenContainerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string requestedDirectory in ReadStringArray(rootElement, "resourceDirectories"))
        {
            if(!TryNormalizeDirectorySource(requestedDirectory, out string normalizedDirectory))
            {
                continue;
            }

            string containerName = BuildLegacyContainerMountName(normalizedDirectory, seenContainerNames);
            mounts.Add(new ContainerMount(
                ContainerMountSourceType.Directory,
                normalizedDirectory,
                $"{OpencodeWrapConstants.CONTAINER_RESOURCE_ROOT}/{containerName}",
                ContainerMountAccessMode.ReadOnly));
        }

        return mounts;
    }

    private static List<ContainerMount> ReadLegacyNamedVolumeMounts(JsonElement rootElement)
    {
        if(!rootElement.TryGetProperty("namedVolumeMounts", out var propertyElement)
            || propertyElement.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var mounts = new List<ContainerMount>();
        foreach(var item in propertyElement.EnumerateArray())
        {
            if(item.ValueKind is not JsonValueKind.Object
                || !item.TryGetProperty("volumeName", out var volumeNameElement)
                || !item.TryGetProperty("containerPath", out var containerPathElement)
                || volumeNameElement.ValueKind is not JsonValueKind.String
                || containerPathElement.ValueKind is not JsonValueKind.String
                || volumeNameElement.GetString() is not { } volumeName
                || containerPathElement.GetString() is not { } containerPath)
            {
                continue;
            }

            mounts.Add(new ContainerMount(ContainerMountSourceType.NamedVolume, volumeName, containerPath, ContainerMountAccessMode.ReadWrite));
        }

        return mounts;
    }

    private static RunMenuDefaults NormalizeDefaults(RunMenuDefaults defaults)
    {
        string? defaultProfileName = String.IsNullOrWhiteSpace(defaults.DefaultProfileName)
            ? null
            : defaults.DefaultProfileName.Trim();

        var seenContainerPaths = new HashSet<string>(StringComparer.Ordinal);
        var seenContainerMounts = new HashSet<ContainerMount>();
        var containerMounts = new List<ContainerMount>();
        foreach(ContainerMount requestedMount in defaults.ContainerMounts)
        {
            if(!TryNormalizeContainerMount(requestedMount, out var normalizedMount)
                || !seenContainerPaths.Add(normalizedMount.ContainerPath)
                || !seenContainerMounts.Add(normalizedMount))
            {
                continue;
            }

            containerMounts.Add(normalizedMount);
        }

        var seenNetworkNames = new HashSet<string>(StringComparer.Ordinal);
        List<string> dockerNetworks = [.. defaults.DockerNetworks
            .Where(name => !String.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Where(seenNetworkNames.Add)];

        var seenAddonNames = new HashSet<string>(GetHostPathComparer());
        List<string> sessionAddons = [.. defaults.SessionAddons
            .Where(name => !String.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Where(seenAddonNames.Add)];

        return new RunMenuDefaults(defaultProfileName, defaults.DefaultUiMode, defaults.DefaultDockerNetworkMode, containerMounts, sessionAddons, dockerNetworks);
    }

    private static bool TryNormalizeContainerMount(ContainerMount requestedMount, out ContainerMount normalizedMount)
    {
        normalizedMount = requestedMount;
        if(!ContainerPathUtility.TryNormalizeAbsolutePath(requestedMount.ContainerPath, out string normalizedContainerPath))
        {
            return false;
        }

        string normalizedSource;
        switch(requestedMount.SourceType)
        {
            case ContainerMountSourceType.Directory:
                if(!TryNormalizeDirectorySource(requestedMount.Source, out normalizedSource))
                {
                    return false;
                }

                break;
            case ContainerMountSourceType.NamedVolume:
                normalizedSource = requestedMount.Source?.Trim() ?? String.Empty;
                if(normalizedSource.Length == 0)
                {
                    return false;
                }

                break;
            default:
                return false;
        }

        normalizedMount = new ContainerMount(requestedMount.SourceType, normalizedSource, normalizedContainerPath, requestedMount.AccessMode);
        return true;
    }

    private static bool TryNormalizeDirectorySource(string requestedSource, out string normalizedSource)
    {
        normalizedSource = String.Empty;
        if(String.IsNullOrWhiteSpace(requestedSource))
        {
            return false;
        }

        try
        {
            normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedSource));
            return true;
        }
        catch(Exception)
        {
            normalizedSource = String.Empty;
            return false;
        }
    }

    private static string BuildLegacyContainerMountName(string hostPath, HashSet<string> seenContainerNames)
    {
        string baseName = Path.GetFileName(hostPath);
        if(String.IsNullOrWhiteSpace(baseName))
        {
            baseName = "resource";
        }

        char[] sanitizedChars = [.. baseName.Select(ch => Char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')];
        string sanitizedName = new string(sanitizedChars).Trim('-', '.', '_');
        if(String.IsNullOrWhiteSpace(sanitizedName))
        {
            sanitizedName = "resource";
        }

        string candidateName = sanitizedName;
        int suffix = 2;
        while(!seenContainerNames.Add(candidateName))
        {
            candidateName = $"{sanitizedName}-{suffix}";
            suffix++;
        }

        return candidateName;
    }

    private static string GetPersistedRunUiModeValue(RunUiMode runUiMode) => runUiMode switch
    {
        RunUiMode.Web => "web",
        RunUiMode.Desktop => "desktop",
        _ => "tui"
    };

    private static string GetPersistedContainerMountSourceTypeValue(ContainerMountSourceType sourceType) => sourceType switch
    {
        ContainerMountSourceType.NamedVolume => "namedVolume",
        _ => "directory"
    };

    private static string GetPersistedContainerMountAccessModeValue(ContainerMountAccessMode accessMode) => accessMode switch
    {
        ContainerMountAccessMode.ReadOnly => "readOnly",
        _ => "readWrite"
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

    private static bool TryParseContainerMountSourceType(string? persistedValue, out ContainerMountSourceType sourceType)
    {
        switch(persistedValue?.Trim())
        {
            case "directory":
                sourceType = ContainerMountSourceType.Directory;
                return true;
            case "namedVolume":
                sourceType = ContainerMountSourceType.NamedVolume;
                return true;
            default:
                sourceType = default;
                return false;
        }
    }

    private static bool TryParseContainerMountAccessMode(string? persistedValue, out ContainerMountAccessMode accessMode)
    {
        switch(persistedValue?.Trim())
        {
            case "readOnly":
                accessMode = ContainerMountAccessMode.ReadOnly;
                return true;
            case "readWrite":
                accessMode = ContainerMountAccessMode.ReadWrite;
                return true;
            default:
                accessMode = default;
                return false;
        }
    }

    private static string GetDefaultsPath(string configRoot)
        => Path.Combine(configRoot, OpencodeWrapConstants.HOST_RUN_MENU_DEFAULTS_FILE_NAME);

    private static StringComparer GetHostPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed record RunMenuDefaults(string? DefaultProfileName, RunUiMode? DefaultUiMode, DockerNetworkMode? DefaultDockerNetworkMode, IReadOnlyList<ContainerMount> ContainerMounts, IReadOnlyList<string> SessionAddons, IReadOnlyList<string> DockerNetworks)
{
    public static RunMenuDefaults Empty { get; } = new(null, null, null, [], [], []);
}
