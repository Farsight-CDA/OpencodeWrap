using System.Reflection;

namespace OpencodeWrap.Services.Profile;

internal sealed partial class BuiltInSessionAddonService : Singleton
{
    private const string SessionAddonTemplatesMarker = ".SessionAddonTemplates.";

    private static readonly (string ResourceFolderSlug, string Name)[] AddonDefinitions =
    [
        ("question_affinity", "Question Affinity"),
        ("web_search", "Web Search"),
        ("cursor_auth", "cursor-auth")
    ];

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    private static readonly BuiltInSessionAddon[] _builtInAddons = BuildBuiltInAddons();

    public IReadOnlyList<BuiltInSessionAddon> BuiltInAddons { get; } = _builtInAddons;

    public bool TryMaterializeBuiltInAddon(BuiltInSessionAddon builtInAddon, string? materializationRootDirectory, out string addonDirectoryPath)
    {
        addonDirectoryPath = String.IsNullOrWhiteSpace(materializationRootDirectory)
            ? Path.Combine(Path.GetTempPath(), $"ocw-addon-{builtInAddon.Name}-{Guid.NewGuid():N}")
            : Path.Combine(materializationRootDirectory, "addons", builtInAddon.Name);

        try
        {
            Directory.CreateDirectory(addonDirectoryPath);

            foreach(var (relativePath, content) in builtInAddon.Files)
            {
                string destinationPath = Path.Combine(addonDirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                string? parentDirectoryPath = Path.GetDirectoryName(destinationPath);
                if(!String.IsNullOrWhiteSpace(parentDirectoryPath))
                {
                    Directory.CreateDirectory(parentDirectoryPath);
                }

                File.WriteAllText(destinationPath, content);
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
        Assembly assembly = typeof(BuiltInSessionAddonService).Assembly;
        var filesByFolder = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach(string fullName in assembly.GetManifestResourceNames())
        {
            int markerIndex = fullName.IndexOf(SessionAddonTemplatesMarker, StringComparison.Ordinal);
            if(markerIndex < 0)
            {
                continue;
            }

            string afterMarker = fullName[(markerIndex + SessionAddonTemplatesMarker.Length)..];
            int folderSeparatorIndex = afterMarker.IndexOf('.');
            if(folderSeparatorIndex < 0)
            {
                continue;
            }

            string resourceFolderSlug = afterMarker[..folderSeparatorIndex];
            string remainder = afterMarker[(folderSeparatorIndex + 1)..];
            string relativePath = RemainderToAddonRelativePath(remainder);
            string text = ReadManifestResourceText(assembly, fullName);

            if(!filesByFolder.TryGetValue(resourceFolderSlug, out Dictionary<string, string>? folderFiles))
            {
                folderFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                filesByFolder[resourceFolderSlug] = folderFiles;
            }

            folderFiles[relativePath] = text;
        }

        var addons = new BuiltInSessionAddon[AddonDefinitions.Length];

        for(int i = 0; i < AddonDefinitions.Length; i++)
        {
            (string resourceFolderSlug, string name) = AddonDefinitions[i];

            if(!filesByFolder.TryGetValue(resourceFolderSlug, out Dictionary<string, string>? folderFiles) || folderFiles.Count == 0)
            {
                throw new InvalidOperationException($"Missing embedded files for built-in session addon '{name}' (resource folder '{resourceFolderSlug}').");
            }

            addons[i] = new BuiltInSessionAddon(name, new Dictionary<string, string>(folderFiles, StringComparer.OrdinalIgnoreCase));
        }

        return addons;
    }

    private static string RemainderToAddonRelativePath(string remainder)
    {
        if(remainder.Length == 0 || remainder[0] == '.')
        {
            return remainder;
        }

        int firstDotIndex = remainder.IndexOf('.');
        if(firstDotIndex < 0)
        {
            return remainder;
        }

        if(remainder.IndexOf('.', firstDotIndex + 1) < 0)
        {
            return remainder;
        }

        return $"{remainder[..firstDotIndex]}/{remainder[(firstDotIndex + 1)..]}";
    }

    private static string ReadManifestResourceText(Assembly assembly, string fullName)
    {
        using Stream? stream = assembly.GetManifestResourceStream(fullName);
        if(stream is null)
        {
            throw new InvalidOperationException($"Cannot open embedded resource '{fullName}'.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
