namespace OpencodeWrap.Services.Profile;

internal sealed record ResolvedProfile(string Name, string DirectoryPath, string DockerfilePath, string? ConfigDirectoryPath = null, string? CleanupDirectoryPath = null);
internal sealed record ProfileCatalog(string ConfigRoot, string ProfilesRoot, string DefaultProfileName, IReadOnlyDictionary<string, string> ProfileDirectories);

internal sealed class ProfileService
{
    public const string INVALID_PROFILE_NAME_MESSAGE = "Profile name may only contain letters, numbers, '-', '_', and '.'.";

    public static bool TryEnsureInitialized()
        => DockerHostService.TryEnsureGlobalConfigDirectory(out string configRoot)
            && TryEnsureProfilesRoot(configRoot, out _);

    public static async Task<(bool Success, ResolvedProfile Profile)> TryResolveProfileAsync(string? requestedProfileName)
    {
        var emptyProfile = new ResolvedProfile("", "", "", null, null);

        var (success, catalog) = TryLoadProfileCatalog();
        if(!success)
        {
            return (false, emptyProfile);
        }

        string selectedProfileName = requestedProfileName?.Trim() ?? "";
        if(selectedProfileName.Length == 0)
        {
            selectedProfileName = catalog.DefaultProfileName;
        }

        if(!IsValidProfileName(selectedProfileName))
        {
            AppIO.WriteError(INVALID_PROFILE_NAME_MESSAGE);
            return (false, emptyProfile);
        }

        if(BuiltInProfileTemplateService.BuiltInProfiles.Any(profile =>
            profile.Name.Equals(selectedProfileName, StringComparison.OrdinalIgnoreCase)))
        {
            return await TryResolveBuiltInProfileAsync(catalog, selectedProfileName, emptyProfile);
        }

        if(!catalog.ProfileDirectories.TryGetValue(selectedProfileName, out string? relativeDirectoryPath))
        {
            AppIO.WriteError($"Profile '{selectedProfileName}' does not exist.");
            return (false, emptyProfile);
        }

        if(!TryResolveProfileDirectoryPath(catalog.ProfilesRoot, relativeDirectoryPath, out string profileDirectoryPath))
        {
            AppIO.WriteError($"Profile '{selectedProfileName}' directory resolves outside '{catalog.ProfilesRoot}'.");
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
        if(!DockerHostService.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return (false, CreateEmptyCatalog());
        }

        if(!TryEnsureProfilesRoot(configRoot, out string profilesRoot))
        {
            return (false, CreateEmptyCatalog());
        }

        var profileDirectories = DiscoverProfileDirectories(profilesRoot);
        var catalog = new ProfileCatalog(configRoot, profilesRoot, BuiltInProfileTemplateService.StarterProfile.Name, profileDirectories);
        return (true, catalog);
    }

    private static Dictionary<string, string> DiscoverProfileDirectories(string profilesRoot)
    {
        var profileDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(profilesRoot);
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to enumerate profiles in '{profilesRoot}': {ex.Message}");
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
            "",
            "",
            "",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static bool TryEnsureProfilesRoot(string configRoot, out string profilesRoot)
    {
        profilesRoot = Path.Combine(configRoot, OpencodeWrapConstants.HOST_PROFILE_ROOT_DIRECTORY_NAME);

        try
        {
            Directory.CreateDirectory(profilesRoot);
            return TryMigrateLegacyProfiles(configRoot, profilesRoot);
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to create profiles directory '{profilesRoot}': {ex.Message}");
            return false;
        }
    }

    private static bool TryMigrateLegacyProfiles(string configRoot, string profilesRoot)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(configRoot);
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to enumerate legacy profiles in '{configRoot}': {ex.Message}");
            return false;
        }

        foreach(string directoryPath in directories)
        {
            string directoryName = Path.GetFileName(directoryPath);
            if(directoryName.Equals(OpencodeWrapConstants.HOST_PROFILE_ROOT_DIRECTORY_NAME, StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals(OpencodeWrapConstants.HOST_SESSION_ROOT_DIRECTORY_NAME, StringComparison.OrdinalIgnoreCase)
                || directoryName.Equals(OpencodeWrapConstants.HOST_SESSION_PASTE_DIRECTORY_NAME, StringComparison.OrdinalIgnoreCase)
                || !IsValidProfileName(directoryName)
                || !LooksLikeProfileDirectory(directoryPath))
            {
                continue;
            }

            string destinationPath = Path.Combine(profilesRoot, directoryName);
            if(Directory.Exists(destinationPath))
            {
                continue;
            }

            try
            {
                Directory.Move(directoryPath, destinationPath);
            }
            catch(Exception ex)
            {
                AppIO.WriteError($"Failed to migrate profile '{directoryName}' into '{profilesRoot}': {ex.Message}");
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeProfileDirectory(string directoryPath)
        => File.Exists(Path.Combine(directoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME))
            || File.Exists(Path.Combine(directoryPath, OpencodeWrapConstants.PROFILE_ENTRYPOINT_FILE_NAME))
            || Directory.Exists(Path.Combine(directoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME));

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
        var builtInProfile = BuiltInProfileTemplateService.BuiltInProfiles.FirstOrDefault(profile =>
            profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if(builtInProfile is null)
        {
            AppIO.WriteError($"Built-in profile template not found for '{profileName}'.");
            return (false, emptyProfile);
        }

        string relativeDirectoryPath = catalog.ProfileDirectories.TryGetValue(profileName, out string? overrideRelativePath)
            ? overrideRelativePath
            : profileName;

        if(!TryResolveProfileDirectoryPath(catalog.ProfilesRoot, relativeDirectoryPath, out string overrideDirectoryPath))
        {
            AppIO.WriteError($"Profile '{profileName}' directory resolves outside '{catalog.ProfilesRoot}'.");
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

        var (materialized, temporaryDirectoryPath) = await BuiltInProfileTemplateService.TryMaterializeBuiltInProfileAsync(builtInProfile);
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
