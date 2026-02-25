internal sealed record ResolvedProfile(string Name, string DirectoryPath, string DockerfilePath, string? ConfigDirectoryPath = null, string? CleanupDirectoryPath = null);
internal sealed record ProfileCatalog(string ConfigRoot, string DefaultProfileName, IReadOnlyDictionary<string, string> ProfileDirectories);

internal sealed class ProfileService
{
    public const string INVALID_PROFILE_NAME_MESSAGE = "Profile name may only contain letters, numbers, '-', '_', and '.'.";

    public static bool TryEnsureInitialized() => DockerHostService.TryEnsureGlobalConfigDirectory(out _);

    public static async Task<(bool Success, ResolvedProfile Profile)> TryResolveProfileAsync(string? requestedProfileName)
    {
        var emptyProfile = new ResolvedProfile(String.Empty, String.Empty, String.Empty, null, null);

        var (success, catalog) = TryLoadProfileCatalog();
        if(!success)
        {
            return (false, emptyProfile);
        }

        string selectedProfileName = requestedProfileName?.Trim() ?? String.Empty;
        if(selectedProfileName.Length == 0)
        {
            selectedProfileName = catalog.DefaultProfileName;
        }

        if(!IsValidProfileName(selectedProfileName))
        {
            AppIO.WriteError(INVALID_PROFILE_NAME_MESSAGE);
            return (false, emptyProfile);
        }

        if(BuiltInProfileTemplateService.IsBuiltInProfileName(selectedProfileName))
        {
            return await TryResolveBuiltInProfileAsync(catalog, selectedProfileName, emptyProfile);
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

        string configDirectoryPath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);
        return (true, new ResolvedProfile(
            selectedProfileName,
            profileDirectoryPath,
            dockerfilePath,
            ConfigDirectoryPath: Directory.Exists(configDirectoryPath) ? configDirectoryPath : null));
    }

    public static (bool Success, ProfileCatalog Catalog) TryLoadProfileCatalog()
    {
        if(!TryEnsureInitialized())
        {
            return (false, CreateEmptyCatalog());
        }

        if(!DockerHostService.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return (false, CreateEmptyCatalog());
        }

        var profileDirectories = DiscoverProfileDirectories(configRoot);
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

    private static ProfileCatalog CreateEmptyCatalog() => new ProfileCatalog(
            String.Empty,
            String.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

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

    private static async Task<(bool Success, ResolvedProfile Profile)> TryResolveBuiltInProfileAsync(ProfileCatalog catalog, string profileName, ResolvedProfile emptyProfile)
    {
        string relativeDirectoryPath = catalog.ProfileDirectories.TryGetValue(profileName, out string? overrideRelativePath)
            ? overrideRelativePath
            : profileName;

        if(!TryResolveProfileDirectoryPath(catalog.ConfigRoot, relativeDirectoryPath, out string overrideDirectoryPath))
        {
            AppIO.WriteError($"Profile '{profileName}' directory resolves outside '{catalog.ConfigRoot}'.");
            return (false, emptyProfile);
        }

        if(Directory.Exists(overrideDirectoryPath))
        {
            string overrideDockerfilePath = Path.Combine(overrideDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME);
            if(!File.Exists(overrideDockerfilePath))
            {
                AppIO.WriteError($"Profile Dockerfile not found: '{overrideDockerfilePath}'.");
                return (false, emptyProfile);
            }

            string overrideConfigDirectoryPath = Path.Combine(overrideDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);
            return (true, new ResolvedProfile(
                profileName,
                overrideDirectoryPath,
                overrideDockerfilePath,
                ConfigDirectoryPath: Directory.Exists(overrideConfigDirectoryPath) ? overrideConfigDirectoryPath : null));
        }

        var (materialized, temporaryDirectoryPath) = await BuiltInProfileTemplateService.TryMaterializeBuiltInProfileAsync(profileName);
        if(!materialized)
        {
            return (false, emptyProfile);
        }

        return (true, new ResolvedProfile(
            profileName,
            temporaryDirectoryPath,
            Path.Combine(temporaryDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME),
            ConfigDirectoryPath: Path.Combine(temporaryDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME),
            CleanupDirectoryPath: temporaryDirectoryPath));
    }

}
