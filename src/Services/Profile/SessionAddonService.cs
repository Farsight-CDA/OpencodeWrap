namespace OpencodeWrap.Services.Profile;

internal sealed record SessionAddonCatalog(string ConfigRoot, string AddonsRoot, IReadOnlyDictionary<string, SessionAddonCatalogEntry> Addons);
internal sealed record SessionAddonCatalogEntry(string Name, string? DirectoryPath, BuiltInSessionAddon? BuiltInAddon = null);
internal sealed record ResolvedSessionAddon(string Name, string DirectoryPath, string? CleanupDirectoryPath = null);

internal sealed partial class SessionAddonService : Singleton
{
    public const string INVALID_ADDON_NAME_MESSAGE = "Addon name cannot be empty, '.', '..', or contain path separators or invalid filename characters.";

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly DockerHostService _dockerHostService;

    [Inject]
    private readonly BuiltInSessionAddonService _builtInSessionAddonService;

    public bool TryLoadCatalog(out SessionAddonCatalog catalog)
    {
        if(!_dockerHostService.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            catalog = CreateEmptyCatalog();
            return false;
        }

        if(!TryEnsureAddonsRoot(configRoot, out string addonsRoot))
        {
            catalog = CreateEmptyCatalog();
            return false;
        }

        catalog = new SessionAddonCatalog(configRoot, addonsRoot, DiscoverAddons(addonsRoot));
        return true;
    }

    public bool IsValidAddonName(string addonName)
    {
        if(String.IsNullOrWhiteSpace(addonName))
        {
            return false;
        }

        string normalizedName = addonName.Trim();
        if(normalizedName is "." or "..")
        {
            return false;
        }

        if(normalizedName.Contains(Path.DirectorySeparatorChar) || normalizedName.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach(char c in normalizedName)
        {
            if(invalidChars.Contains(c))
            {
                return false;
            }
        }

        return true;
    }

    public string? TryGetAddonNameValidationError(string addonName)
        => IsValidAddonName(addonName)
            ? null
            : INVALID_ADDON_NAME_MESSAGE;

    public bool TryResolveAddonDirectoryPath(string addonsRoot, string addonName, out string addonDirectoryPath)
    {
        addonDirectoryPath = Path.GetFullPath(Path.Combine(addonsRoot, addonName));
        return PathIsWithin(addonsRoot, addonDirectoryPath);
    }

    public void EnsureAddonSupportDirectories(string addonDirectoryPath)
    {
        Directory.CreateDirectory(Path.Combine(addonDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME));
        Directory.CreateDirectory(Path.Combine(addonDirectoryPath, OpencodeWrapConstants.PROFILE_BIN_DIRECTORY_NAME));
    }

    public bool TryResolveAddons(IReadOnlyList<string>? requestedAddonNames, string? materializationRootDirectory, out IReadOnlyList<ResolvedSessionAddon> addons)
    {
        addons = [];
        if(requestedAddonNames is null || requestedAddonNames.Count == 0)
        {
            return true;
        }

        if(!TryLoadCatalog(out var catalog))
        {
            return false;
        }

        var resolvedAddons = new List<ResolvedSessionAddon>();
        var seenAddonNames = new HashSet<string>(GetHostNameComparer());

        foreach(string requestedAddonName in requestedAddonNames)
        {
            if(String.IsNullOrWhiteSpace(requestedAddonName))
            {
                _deferredSessionLogService.WriteErrorOrConsole("profile", "Session addon names cannot be empty.");
                return false;
            }

            string normalizedAddonName = requestedAddonName.Trim();
            if(!seenAddonNames.Add(normalizedAddonName))
            {
                continue;
            }

            if(!catalog.Addons.TryGetValue(normalizedAddonName, out var addonEntry))
            {
                _deferredSessionLogService.WriteErrorOrConsole("profile", $"Session addon '{normalizedAddonName}' was not found under '{catalog.AddonsRoot}'.");
                return false;
            }

            if(addonEntry.DirectoryPath is { } addonDirectoryPath)
            {
                resolvedAddons.Add(new ResolvedSessionAddon(addonEntry.Name, addonDirectoryPath));
                continue;
            }

            if(addonEntry.BuiltInAddon is null)
            {
                _deferredSessionLogService.WriteErrorOrConsole("profile", $"Session addon '{normalizedAddonName}' is not available.");
                return false;
            }

            if(!_builtInSessionAddonService.TryMaterializeBuiltInAddon(addonEntry.BuiltInAddon, materializationRootDirectory, out string builtInAddonDirectoryPath))
            {
                return false;
            }

            resolvedAddons.Add(new ResolvedSessionAddon(
                addonEntry.Name,
                builtInAddonDirectoryPath,
                CleanupDirectoryPath: String.IsNullOrWhiteSpace(materializationRootDirectory) ? builtInAddonDirectoryPath : null));
        }

        addons = resolvedAddons;
        return true;
    }

    private Dictionary<string, SessionAddonCatalogEntry> DiscoverAddons(string addonsRoot)
    {
        var addons = new Dictionary<string, SessionAddonCatalogEntry>(GetHostNameComparer());

        foreach(var builtInAddon in _builtInSessionAddonService.BuiltInAddons)
        {
            addons[builtInAddon.Name] = new SessionAddonCatalogEntry(builtInAddon.Name, null, builtInAddon);
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(addonsRoot);
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole("profile", $"Failed to enumerate session addons in '{addonsRoot}': {ex.Message}");
            return addons;
        }

        foreach(string directoryPath in directories)
        {
            string addonName = Path.GetFileName(directoryPath);
            if(String.IsNullOrWhiteSpace(addonName))
            {
                continue;
            }

            addons[addonName] = new SessionAddonCatalogEntry(addonName, directoryPath);
        }

        return addons;
    }

    private bool TryEnsureAddonsRoot(string configRoot, out string addonsRoot)
    {
        addonsRoot = Path.Combine(configRoot, OpencodeWrapConstants.HOST_ADDON_ROOT_DIRECTORY_NAME);

        try
        {
            Directory.CreateDirectory(addonsRoot);
            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole("profile", $"Failed to create session addons directory '{addonsRoot}': {ex.Message}");
            return false;
        }
    }

    private static SessionAddonCatalog CreateEmptyCatalog()
        => new("", "", new Dictionary<string, SessionAddonCatalogEntry>(GetHostNameComparer()));

    private static bool PathIsWithin(string parentDirectoryPath, string childDirectoryPath)
    {
        string normalizedParent = Path.GetFullPath(parentDirectoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string normalizedChild = Path.GetFullPath(childDirectoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return normalizedChild.StartsWith(normalizedParent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static StringComparer GetHostNameComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
