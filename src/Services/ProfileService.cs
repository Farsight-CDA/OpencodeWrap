internal sealed record ResolvedProfile(string Name, string DirectoryPath, string DockerfilePath);

internal sealed class ProfileService
{
    private readonly DockerHostService _host;

    private const string DefaultProfilesYaml = "default: default\nprofiles:\n  default:\n    directory: profiles/default\n  dotnet:\n    directory: profiles/dotnet\n";

    private const string DefaultDockerfile = """
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        ca-certificates \
        coreutils \
        curl \
        file \
        git \
        iproute2 \
        jq \
        less \
        procps \
        python3 \
        unzip \
        zip \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /opt/opencode /home/opencode \
    && chmod 755 /opt/opencode \
    && chmod 777 /home/opencode

RUN HOME=/opt/opencode bash -lc "curl -fsSL https://opencode.ai/install | bash -s -- --no-modify-path"

WORKDIR /workspace

ENV PATH="/opt/opencode/.opencode/bin:/opt/opencode/.local/share/opencode/bin:/opt/opencode/.local/bin:${PATH}"
""";

    private const string DotnetDockerfile = """
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bash \
        ca-certificates \
        coreutils \
        curl \
        file \
        git \
        gpg \
        iproute2 \
        jq \
        less \
        procps \
        python3 \
        unzip \
        zip \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /etc/apt/keyrings/microsoft.gpg \
    && chmod go+r /etc/apt/keyrings/microsoft.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/microsoft.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" > /etc/apt/sources.list.d/microsoft-prod.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends dotnet-sdk-10.0 \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /opt/opencode /home/opencode \
    && chmod 755 /opt/opencode \
    && chmod 777 /home/opencode

RUN HOME=/opt/opencode bash -lc "curl -fsSL https://opencode.ai/install | bash -s -- --no-modify-path"

WORKDIR /workspace

ENV PATH="/opt/opencode/.opencode/bin:/opt/opencode/.local/share/opencode/bin:/opt/opencode/.local/bin:${PATH}"
""";

    public ProfileService(DockerHostService host)
    {
        _host = host;
    }

    public Task<bool> TryEnsureInitializedAsync()
    {
        if(!_host.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return Task.FromResult(false);
        }

        return TryBootstrapIfConfigRootIsEmptyAsync(configRoot);
    }

    public async Task<bool> TryAddProfileAsync(string profileName)
    {
        string normalizedName = profileName.Trim();
        if(!TryValidateProfileName(normalizedName))
        {
            AppIO.WriteError("Profile name may only contain letters, numbers, '-', '_', and '.'.");
            return false;
        }

        var config = await TryLoadConfigAsync();
        if(!config.Success)
        {
            return false;
        }

        Dictionary<string, string> profileDirectories = config.ProfileDirectories;
        if(profileDirectories.ContainsKey(normalizedName))
        {
            AppIO.WriteError($"Profile '{normalizedName}' already exists.");
            return false;
        }

        string relativeDirectoryPath = BuildDefaultRelativeProfilePath(normalizedName);
        string profileDirectoryPath = Path.GetFullPath(Path.Combine(config.ConfigRoot, relativeDirectoryPath));
        if(!PathIsWithin(config.ConfigRoot, profileDirectoryPath))
        {
            AppIO.WriteError($"Profile directory '{profileDirectoryPath}' resolves outside '{config.ConfigRoot}'.");
            return false;
        }

        string dockerfilePath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME);

        try
        {
            Directory.CreateDirectory(profileDirectoryPath);
            if(File.Exists(dockerfilePath))
            {
                AppIO.WriteError($"Cannot add profile '{normalizedName}' because '{dockerfilePath}' already exists.");
                return false;
            }

            await File.WriteAllTextAsync(dockerfilePath, DefaultDockerfile);
            profileDirectories[normalizedName] = relativeDirectoryPath;
            if(!await TryWriteProfilesAsync(config.ProfilesFilePath, config.DefaultProfileName, profileDirectories))
            {
                return false;
            }
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to add profile '{normalizedName}': {ex.Message}");
            return false;
        }

        AppIO.WriteSuccess($"Added profile '{normalizedName}' at '{profileDirectoryPath}'.");
        return true;
    }

    public async Task<bool> TryDeleteProfileAsync(string profileName)
    {
        string normalizedName = profileName.Trim();
        if(!TryValidateProfileName(normalizedName))
        {
            AppIO.WriteError("Profile name may only contain letters, numbers, '-', '_', and '.'.");
            return false;
        }

        var config = await TryLoadConfigAsync();
        if(!config.Success)
        {
            return false;
        }

        if(!config.ProfileDirectories.TryGetValue(normalizedName, out string? relativeDirectoryPath))
        {
            AppIO.WriteError($"Profile '{normalizedName}' does not exist.");
            return false;
        }

        if(String.Equals(config.DefaultProfileName, normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            AppIO.WriteError($"Cannot delete default profile '{normalizedName}'.");
            return false;
        }

        string profileDirectoryPath = Path.GetFullPath(Path.Combine(config.ConfigRoot, relativeDirectoryPath));
        if(!PathIsWithin(config.ConfigRoot, profileDirectoryPath))
        {
            AppIO.WriteError($"Profile '{normalizedName}' directory resolves outside '{config.ConfigRoot}'.");
            return false;
        }

        try
        {
            config.ProfileDirectories.Remove(normalizedName);
            if(!await TryWriteProfilesAsync(config.ProfilesFilePath, config.DefaultProfileName, config.ProfileDirectories))
            {
                return false;
            }

            if(Directory.Exists(profileDirectoryPath))
            {
                Directory.Delete(profileDirectoryPath, recursive: true);
            }
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to delete profile '{normalizedName}': {ex.Message}");
            return false;
        }

        AppIO.WriteSuccess($"Deleted profile '{normalizedName}'.");
        return true;
    }

    public async Task<bool> TryOpenProfilesDirectoryAsync()
    {
        if(!await TryEnsureInitializedAsync())
        {
            return false;
        }

        if(!_host.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return false;
        }

        string profilesDirectoryPath = Path.Combine(configRoot, OpencodeWrapConstants.HOST_PROFILES_DIRECTORY_NAME);

        try
        {
            Directory.CreateDirectory(profilesDirectoryPath);
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to ensure profiles directory '{profilesDirectoryPath}': {ex.Message}");
            return false;
        }

        bool opened = await ProcessRunner.TryOpenDirectoryAsync(
            profilesDirectoryPath,
            _host.IsWindows,
            onFailurePrefix: $"Failed to open profile directory '{profilesDirectoryPath}'.");

        if(!opened)
        {
            return false;
        }

        AppIO.WriteInfo($"Opened profile directory: '{profilesDirectoryPath}'.");
        return true;
    }

    public async Task<(bool Success, ResolvedProfile Profile)> TryResolveProfileAsync(string? requestedProfileName)
    {
        var emptyProfile = new ResolvedProfile(String.Empty, String.Empty, String.Empty);

        if(!await TryEnsureInitializedAsync())
        {
            return (false, emptyProfile);
        }

        if(!_host.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return (false, emptyProfile);
        }

        string profilesFilePath = Path.Combine(configRoot, OpencodeWrapConstants.HOST_PROFILES_FILE_NAME);
        if(!File.Exists(profilesFilePath))
        {
            AppIO.WriteError($"Profiles file not found: '{profilesFilePath}'.");
            return (false, emptyProfile);
        }

        var loadedProfiles = await TryLoadProfilesAsync(profilesFilePath);
        if(!loadedProfiles.Success)
        {
            return (false, emptyProfile);
        }

        string defaultProfileName = loadedProfiles.DefaultProfileName;
        Dictionary<string, string> profileDirectories = loadedProfiles.ProfileDirectories;

        string selectedProfileName = requestedProfileName?.Trim() ?? String.Empty;
        if(selectedProfileName.Length == 0)
        {
            selectedProfileName = defaultProfileName;
        }

        if(!profileDirectories.TryGetValue(selectedProfileName, out string? relativeDirectoryPath))
        {
            AppIO.WriteError($"Profile '{selectedProfileName}' is not defined in '{profilesFilePath}'.");
            return (false, emptyProfile);
        }

        string profileDirectoryPath = Path.GetFullPath(Path.Combine(configRoot, relativeDirectoryPath));
        if(!PathIsWithin(configRoot, profileDirectoryPath))
        {
            AppIO.WriteError($"Profile '{selectedProfileName}' directory resolves outside '{configRoot}'.");
            return (false, emptyProfile);
        }

        if(!Directory.Exists(profileDirectoryPath))
        {
            AppIO.WriteError($"Profile directory not found: '{profileDirectoryPath}'.");
            return (false, emptyProfile);
        }

        string dockerfilePath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME);
        if(!File.Exists(dockerfilePath))
        {
            AppIO.WriteError($"Profile Dockerfile not found: '{dockerfilePath}'.");
            return (false, emptyProfile);
        }

        return (true, new ResolvedProfile(selectedProfileName, profileDirectoryPath, dockerfilePath));
    }

    private async Task<(bool Success, string ConfigRoot, string ProfilesFilePath, string DefaultProfileName, Dictionary<string, string> ProfileDirectories)> TryLoadConfigAsync()
    {
        if(!await TryEnsureInitializedAsync())
        {
            return (false, String.Empty, String.Empty, String.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        if(!_host.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return (false, String.Empty, String.Empty, String.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        string profilesFilePath = Path.Combine(configRoot, OpencodeWrapConstants.HOST_PROFILES_FILE_NAME);
        if(!File.Exists(profilesFilePath))
        {
            AppIO.WriteError($"Profiles file not found: '{profilesFilePath}'.");
            return (false, String.Empty, String.Empty, String.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var loadedProfiles = await TryLoadProfilesAsync(profilesFilePath);
        if(!loadedProfiles.Success)
        {
            return (false, String.Empty, String.Empty, String.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        return (true, configRoot, profilesFilePath, loadedProfiles.DefaultProfileName, loadedProfiles.ProfileDirectories);
    }

    private static async Task<bool> TryWriteProfilesAsync(string profilesFilePath, string defaultProfileName, Dictionary<string, string> profileDirectories)
    {
        if(!profileDirectories.ContainsKey(defaultProfileName))
        {
            AppIO.WriteError($"Default profile '{defaultProfileName}' is missing and cannot be written.");
            return false;
        }

        try
        {
            var yamlLines = new List<string>
            {
                $"default: {defaultProfileName}",
                "profiles:"
            };

            foreach(var kvp in profileDirectories.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                yamlLines.Add($"  {kvp.Key}:");
                yamlLines.Add($"    directory: {kvp.Value.Replace('\\', '/')}");
            }

            string yamlContent = String.Join('\n', yamlLines) + '\n';
            await File.WriteAllTextAsync(profilesFilePath, yamlContent);
            return true;
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to write profiles file '{profilesFilePath}': {ex.Message}");
            return false;
        }
    }

    private static string BuildDefaultRelativeProfilePath(string profileName)
    {
        return $"{OpencodeWrapConstants.HOST_PROFILES_DIRECTORY_NAME}/{profileName}";
    }

    private static bool TryValidateProfileName(string profileName)
    {
        if(profileName.Length == 0)
        {
            return false;
        }

        foreach(char c in profileName)
        {
            if(Char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static async Task<(bool Success, string DefaultProfileName, Dictionary<string, string> ProfileDirectories)> TryLoadProfilesAsync(string profilesFilePath)
    {
        string defaultProfileName = String.Empty;
        var profileDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        bool inProfilesSection = false;
        string? currentProfileName = null;
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(profilesFilePath);
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to read profiles file '{profilesFilePath}': {ex.Message}");
            return (false, String.Empty, profileDirectories);
        }

        foreach(string rawLine in lines)
        {
            string withoutComment = StripComment(rawLine);
            string line = withoutComment.Trim();
            if(line.Length == 0)
            {
                continue;
            }

            int indentLevel = CountLeadingSpaces(withoutComment);
            if(!TryParseYamlMapping(line, out string key, out string value))
            {
                continue;
            }

            if(indentLevel == 0)
            {
                inProfilesSection = String.Equals(key, "profiles", StringComparison.OrdinalIgnoreCase);
                currentProfileName = null;

                if(String.Equals(key, "default", StringComparison.OrdinalIgnoreCase))
                {
                    defaultProfileName = UnquoteYamlValue(value);
                }

                continue;
            }

            if(!inProfilesSection)
            {
                continue;
            }

            if(indentLevel == 2)
            {
                currentProfileName = key;
                continue;
            }

            if(indentLevel == 4 && currentProfileName is not null)
            {
                if(String.Equals(key, "directory", StringComparison.OrdinalIgnoreCase))
                {
                    profileDirectories[currentProfileName] = UnquoteYamlValue(value);
                }
            }
        }

        if(defaultProfileName.Length == 0)
        {
            AppIO.WriteError($"Profiles file '{profilesFilePath}' must define a default profile.");
            return (false, String.Empty, profileDirectories);
        }

        if(!profileDirectories.ContainsKey(defaultProfileName))
        {
            AppIO.WriteError($"Default profile '{defaultProfileName}' is missing a 'profiles.{defaultProfileName}.directory' entry in '{profilesFilePath}'.");
            return (false, String.Empty, profileDirectories);
        }

        return (true, defaultProfileName, profileDirectories);
    }

    private static string StripComment(string line)
    {
        bool inSingleQuotes = false;
        bool inDoubleQuotes = false;

        for(int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if(c == '\'' && !inDoubleQuotes)
            {
                inSingleQuotes = !inSingleQuotes;
                continue;
            }

            if(c == '"' && !inSingleQuotes)
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if(c == '#' && !inSingleQuotes && !inDoubleQuotes)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static int CountLeadingSpaces(string line)
    {
        int count = 0;
        while(count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static bool TryParseYamlMapping(string line, out string key, out string value)
    {
        int colonIndex = line.IndexOf(':');
        if(colonIndex <= 0)
        {
            key = String.Empty;
            value = String.Empty;
            return false;
        }

        key = line[..colonIndex].Trim();
        value = line[(colonIndex + 1)..].Trim();
        return key.Length > 0;
    }

    private static string UnquoteYamlValue(string value)
    {
        if(value.Length >= 2)
        {
            if((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
            {
                return value[1..^1];
            }
        }

        return value;
    }

    private static bool PathIsWithin(string parentDirectoryPath, string childDirectoryPath)
    {
        string normalizedParent = Path.GetFullPath(parentDirectoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string normalizedChild = Path.GetFullPath(childDirectoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryBootstrapIfConfigRootIsEmptyAsync(string configRoot)
    {
        bool hasEntries;
        try
        {
            hasEntries = Directory.EnumerateFileSystemEntries(configRoot).Any();
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to inspect config directory '{configRoot}': {ex.Message}");
            return false;
        }

        if(hasEntries)
        {
            return true;
        }

        string profilesFilePath = Path.Combine(configRoot, OpencodeWrapConstants.HOST_PROFILES_FILE_NAME);
        string defaultProfileDirectory = Path.Combine(configRoot, OpencodeWrapConstants.HOST_PROFILES_DIRECTORY_NAME, OpencodeWrapConstants.DEFAULT_PROFILE_NAME);
        string defaultDockerfilePath = Path.Combine(defaultProfileDirectory, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME);
        string dotnetProfileDirectory = Path.Combine(configRoot, OpencodeWrapConstants.HOST_PROFILES_DIRECTORY_NAME, "dotnet");
        string dotnetDockerfilePath = Path.Combine(dotnetProfileDirectory, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME);

        try
        {
            Directory.CreateDirectory(defaultProfileDirectory);
            Directory.CreateDirectory(dotnetProfileDirectory);
            await File.WriteAllTextAsync(profilesFilePath, DefaultProfilesYaml);
            await File.WriteAllTextAsync(defaultDockerfilePath, DefaultDockerfile);
            await File.WriteAllTextAsync(dotnetDockerfilePath, DotnetDockerfile);
            return true;
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to initialize default profile in '{configRoot}': {ex.Message}");
            return false;
        }
    }
}
