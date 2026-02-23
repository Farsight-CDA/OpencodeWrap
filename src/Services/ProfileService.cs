internal sealed record ResolvedProfile(string Name, string DirectoryPath, string DockerfilePath);
internal sealed record ProfileCatalog(string ConfigRoot, string DefaultProfileName, IReadOnlyDictionary<string, string> ProfileDirectories);

internal sealed class ProfileService
{
    private readonly DockerHostService _host;

    private static readonly string DefaultDockerfile = LoadEmbeddedTextResource("ProfileTemplates.default.Dockerfile");
    private static readonly string DotnetDockerfile = LoadEmbeddedTextResource("ProfileTemplates.dotnet.Dockerfile");
    private static readonly string DefaultOpencodeConfig = LoadEmbeddedTextResource("ProfileTemplates.default.opencode.json");
    private static readonly string DotnetOpencodeConfig = LoadEmbeddedTextResource("ProfileTemplates.dotnet.opencode.json");

    public const string InvalidProfileNameMessage = "Profile name may only contain letters, numbers, '-', '_', and '.'.";
    public string StarterDockerfileTemplate => DefaultDockerfile;

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

    public async Task<(bool Success, ResolvedProfile Profile)> TryResolveProfileAsync(string? requestedProfileName)
    {
        var emptyProfile = new ResolvedProfile(String.Empty, String.Empty, String.Empty);

        var catalogResult = await TryLoadProfileCatalogAsync();
        if(!catalogResult.Success)
        {
            return (false, emptyProfile);
        }

        ProfileCatalog catalog = catalogResult.Catalog;

        string selectedProfileName = requestedProfileName?.Trim() ?? String.Empty;
        if(selectedProfileName.Length == 0)
        {
            selectedProfileName = catalog.DefaultProfileName;
        }

        if(!IsValidProfileName(selectedProfileName))
        {
            AppIO.WriteError(InvalidProfileNameMessage);
            return (false, emptyProfile);
        }

        if(!catalog.ProfileDirectories.TryGetValue(selectedProfileName, out string? relativeDirectoryPath))
        {
            AppIO.WriteError($"Profile '{selectedProfileName}' does not exist.");
            return (false, emptyProfile);
        }

        if(!TryResolveProfileDirectoryPath(catalog.ConfigRoot, relativeDirectoryPath, out string profileDirectoryPath))
        {
            AppIO.WriteError($"Profile '{selectedProfileName}' directory resolves outside '{catalog.ConfigRoot}'.");
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

    public async Task<(bool Success, ProfileCatalog Catalog)> TryLoadProfileCatalogAsync()
    {
        if(!await TryEnsureInitializedAsync())
        {
            return (false, CreateEmptyCatalog());
        }

        if(!_host.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return (false, CreateEmptyCatalog());
        }

        Dictionary<string, string> profileDirectories = DiscoverProfileDirectories(configRoot);
        var catalog = new ProfileCatalog(configRoot, OpencodeWrapConstants.DEFAULT_PROFILE_NAME, profileDirectories);
        return (true, catalog);
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
            if(!IsValidProfileName(profileName))
            {
                continue;
            }

            profileDirectories[profileName] = profileName.Replace('\\', '/');
        }

        return profileDirectories;
    }

    public static bool IsValidProfileName(string profileName)
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

    public static bool TryResolveProfileDirectoryPath(string configRoot, string profileRelativePath, out string profileDirectoryPath)
    {
        profileDirectoryPath = Path.GetFullPath(Path.Combine(configRoot, profileRelativePath));
        return PathIsWithin(configRoot, profileDirectoryPath);
    }

    private static ProfileCatalog CreateEmptyCatalog()
    {
        return new ProfileCatalog(
            String.Empty,
            String.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
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
