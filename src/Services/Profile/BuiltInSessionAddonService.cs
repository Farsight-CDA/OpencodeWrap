namespace OpencodeWrap.Services.Profile;

internal sealed partial class BuiltInSessionAddonService : Singleton
{
    private const string SESSION_ADDON_TEMPLATES_PREFIX = "OpencodeWrap.SessionAddonTemplates/";

    private static readonly BuiltInSessionAddon[] _builtInAddons = BuildBuiltInAddons();

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public IReadOnlyList<BuiltInSessionAddon> BuiltInAddons { get; } = _builtInAddons;

    public bool TryMaterializeBuiltInAddon(BuiltInSessionAddon builtInAddon, string materializationRootDirectory, out string addonDirectoryPath)
    {
        addonDirectoryPath = Path.Combine(materializationRootDirectory, "addons", builtInAddon.Name);

        try
        {
            Directory.CreateDirectory(addonDirectoryPath);
            var assembly = typeof(BuiltInSessionAddonService).Assembly;

            foreach(var (relativePath, resourceName) in builtInAddon.ResourceNames)
            {
                string destinationPath = Path.Combine(addonDirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                string? parentDirectoryPath = Path.GetDirectoryName(destinationPath);
                if(!String.IsNullOrWhiteSpace(parentDirectoryPath))
                {
                    Directory.CreateDirectory(parentDirectoryPath);
                }

                using var resource = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Cannot open embedded resource '{resourceName}'.");
                using var destination = File.Create(destinationPath);
                resource.CopyTo(destination);
            }

            return true;
        }
        catch(Exception ex)
        {
            AppIO.TryDeleteDirectory(addonDirectoryPath);
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.PROFILE, $"Failed to prepare built-in session addon '{builtInAddon.Name}': {ex.Message}");
            addonDirectoryPath = "";
            return false;
        }
    }

    private static BuiltInSessionAddon[] BuildBuiltInAddons()
    {
        var assembly = typeof(BuiltInSessionAddonService).Assembly;
        var filesByFolder = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach(string resourceName in assembly.GetManifestResourceNames())
        {
            string normalizedName = resourceName.Replace('\\', '/');
            if(!normalizedName.StartsWith(SESSION_ADDON_TEMPLATES_PREFIX, StringComparison.Ordinal))
            {
                continue;
            }

            string relativeName = normalizedName[SESSION_ADDON_TEMPLATES_PREFIX.Length..];
            int folderSeparatorIndex = relativeName.IndexOf('/');
            if(folderSeparatorIndex < 0)
            {
                continue;
            }

            string addonName = relativeName[..folderSeparatorIndex];
            string relativePath = relativeName[(folderSeparatorIndex + 1)..];

            if(!filesByFolder.TryGetValue(addonName, out var folderFiles))
            {
                folderFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                filesByFolder[addonName] = folderFiles;
            }

            folderFiles.Add(relativePath, resourceName);
        }

        return [.. filesByFolder.Select(static entry => new BuiltInSessionAddon(entry.Key, entry.Value))];
    }
}
