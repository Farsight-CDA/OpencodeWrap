internal sealed record ResolvedProfile(string Name, string DirectoryPath, string DockerfilePath);

internal sealed class ProfileService
{
    private readonly DockerHostService _host;

    private static readonly string DefaultDockerfile = LoadEmbeddedTextResource("ProfileTemplates.default.Dockerfile");
    private static readonly string DotnetDockerfile = LoadEmbeddedTextResource("ProfileTemplates.dotnet.Dockerfile");
    private static readonly string DefaultOpencodeConfig = LoadEmbeddedTextResource("ProfileTemplates.default.opencode.json");
    private static readonly string DotnetOpencodeConfig = LoadEmbeddedTextResource("ProfileTemplates.dotnet.opencode.json");

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

        if(config.ProfileDirectories.ContainsKey(normalizedName))
        {
            AppIO.WriteError($"Profile '{normalizedName}' already exists.");
            return false;
        }

        string profileDirectoryPath = Path.GetFullPath(Path.Combine(config.ConfigRoot, normalizedName));
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

        if(String.Equals(OpencodeWrapConstants.DEFAULT_PROFILE_NAME, normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            AppIO.WriteError($"Cannot delete default profile '{normalizedName}'.");
            return false;
        }

        var config = await TryLoadConfigAsync();
        if(!config.Success)
        {
            return false;
        }

        if(!config.ProfileDirectories.ContainsKey(normalizedName))
        {
            AppIO.WriteError($"Profile '{normalizedName}' does not exist.");
            return false;
        }

        string profileDirectoryPath = Path.GetFullPath(Path.Combine(config.ConfigRoot, normalizedName));
        if(!PathIsWithin(config.ConfigRoot, profileDirectoryPath))
        {
            AppIO.WriteError($"Profile '{normalizedName}' directory resolves outside '{config.ConfigRoot}'.");
            return false;
        }

        try
        {
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

        bool opened = await ProcessRunner.TryOpenDirectoryAsync(
            configRoot,
            _host.IsWindows,
            onFailurePrefix: $"Failed to open profile directory '{configRoot}'.");

        if(!opened)
        {
            return false;
        }

        AppIO.WriteInfo($"Opened profile directory: '{configRoot}'.");
        return true;
    }

    public async Task<bool> TryListProfilesAsync()
    {
        var config = await TryLoadConfigAsync();
        if(!config.Success)
        {
            return false;
        }

        if(config.ProfileDirectories.Count == 0)
        {
            AppIO.WriteWarning("No profiles found.");
            return true;
        }

        AppIO.WriteInfo($"Profiles (default: '{config.DefaultProfileName}'):");

        foreach(var kvp in config.ProfileDirectories.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            string marker = String.Equals(kvp.Key, config.DefaultProfileName, StringComparison.OrdinalIgnoreCase)
                ? " [default]"
                : String.Empty;

            AppIO.WriteInfo($"- {kvp.Key}{marker} ({kvp.Value})");
        }

        return true;
    }

    public async Task<(bool Success, ResolvedProfile Profile)> TryResolveProfileAsync(string? requestedProfileName)
    {
        var emptyProfile = new ResolvedProfile(String.Empty, String.Empty, String.Empty);

        var config = await TryLoadConfigAsync();
        if(!config.Success)
        {
            return (false, emptyProfile);
        }

        string selectedProfileName = requestedProfileName?.Trim() ?? String.Empty;
        if(selectedProfileName.Length == 0)
        {
            selectedProfileName = config.DefaultProfileName;
        }

        if(!TryValidateProfileName(selectedProfileName))
        {
            AppIO.WriteError("Profile name may only contain letters, numbers, '-', '_', and '.'.");
            return (false, emptyProfile);
        }

        if(!config.ProfileDirectories.TryGetValue(selectedProfileName, out string? relativeDirectoryPath))
        {
            AppIO.WriteError($"Profile '{selectedProfileName}' does not exist.");
            return (false, emptyProfile);
        }

        string profileDirectoryPath = Path.GetFullPath(Path.Combine(config.ConfigRoot, relativeDirectoryPath));
        if(!PathIsWithin(config.ConfigRoot, profileDirectoryPath))
        {
            AppIO.WriteError($"Profile '{selectedProfileName}' directory resolves outside '{config.ConfigRoot}'.");
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

    private async Task<(bool Success, string ConfigRoot, string DefaultProfileName, Dictionary<string, string> ProfileDirectories)> TryLoadConfigAsync()
    {
        if(!await TryEnsureInitializedAsync())
        {
            return (false, String.Empty, String.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        if(!_host.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return (false, String.Empty, String.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var profileDirectories = DiscoverProfileDirectories(configRoot);
        return (true, configRoot, OpencodeWrapConstants.DEFAULT_PROFILE_NAME, profileDirectories);
    }

    private static Dictionary<string, string> DiscoverProfileDirectories(string configRoot)
    {
        var profileDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(configRoot);
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to enumerate profiles in '{configRoot}': {ex.Message}");
            return profileDirectories;
        }

        foreach(string directoryPath in directories)
        {
            string profileName = Path.GetFileName(directoryPath);
            if(!TryValidateProfileName(profileName))
            {
                continue;
            }

            profileDirectories[profileName] = profileName.Replace('\\', '/');
        }

        return profileDirectories;
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

    private static string LoadEmbeddedTextResource(string resourceNameSuffix)
    {
        var assembly = typeof(ProfileService).Assembly;
        string[] resourceNames = assembly.GetManifestResourceNames();

        string? fullResourceName = resourceNames.FirstOrDefault(name =>
            name.EndsWith(resourceNameSuffix, StringComparison.Ordinal));

        if(fullResourceName is null)
        {
            throw new InvalidOperationException($"Missing embedded resource '{resourceNameSuffix}'.");
        }

        using Stream? stream = assembly.GetManifestResourceStream(fullResourceName);
        if(stream is null)
        {
            throw new InvalidOperationException($"Cannot open embedded resource '{fullResourceName}'.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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

        string defaultProfileDirectory = Path.Combine(configRoot, OpencodeWrapConstants.DEFAULT_PROFILE_NAME);
        string defaultDockerfilePath = Path.Combine(defaultProfileDirectory, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME);
        string defaultOpencodeConfigPath = Path.Combine(defaultProfileDirectory, "opencode.json");
        string dotnetProfileDirectory = Path.Combine(configRoot, "dotnet");
        string dotnetDockerfilePath = Path.Combine(dotnetProfileDirectory, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME);
        string dotnetOpencodeConfigPath = Path.Combine(dotnetProfileDirectory, "opencode.json");

        try
        {
            Directory.CreateDirectory(defaultProfileDirectory);
            Directory.CreateDirectory(dotnetProfileDirectory);
            await File.WriteAllTextAsync(defaultDockerfilePath, DefaultDockerfile);
            await File.WriteAllTextAsync(dotnetDockerfilePath, DotnetDockerfile);
            await File.WriteAllTextAsync(defaultOpencodeConfigPath, DefaultOpencodeConfig);
            await File.WriteAllTextAsync(dotnetOpencodeConfigPath, DotnetOpencodeConfig);
            return true;
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to initialize default profile in '{configRoot}': {ex.Message}");
            return false;
        }
    }
}
