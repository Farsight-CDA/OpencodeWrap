using System.Reflection;

namespace OpencodeWrap.Services.Profile;

internal sealed partial class BuiltInSessionAddonService : Singleton
{
    private const string SESSION_ADDON_TEMPLATES_MARKER = ".SessionAddonTemplates.";

    private static readonly (string ResourceFolderSlug, string Name)[] _addonDefinitions =
    [
        ("question_affinity", "question-affinity"),
        ("web_search", "web-search"),
        ("cursor_auth", "cursor-auth"),
        ("frontend_design", "frontend-design")
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
        var assembly = typeof(BuiltInSessionAddonService).Assembly;
        var filesByFolder = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach(string fullName in assembly.GetManifestResourceNames())
        {
            int markerIndex = fullName.IndexOf(SESSION_ADDON_TEMPLATES_MARKER, StringComparison.Ordinal);
            if(markerIndex < 0)
            {
                continue;
            }

            string afterMarker = fullName[(markerIndex + SESSION_ADDON_TEMPLATES_MARKER.Length)..];
            int folderSeparatorIndex = afterMarker.IndexOf('.');
            if(folderSeparatorIndex < 0)
            {
                continue;
            }

            string resourceFolderSlug = afterMarker[..folderSeparatorIndex];
            string remainder = afterMarker[(folderSeparatorIndex + 1)..];
            string relativePath = RemainderToAddonRelativePath(remainder);
            string text = ReadManifestResourceText(assembly, fullName);

            if(!filesByFolder.TryGetValue(resourceFolderSlug, out var folderFiles))
            {
                folderFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                filesByFolder[resourceFolderSlug] = folderFiles;
            }

            folderFiles[relativePath] = text;
        }

        var addons = new BuiltInSessionAddon[_addonDefinitions.Length];

        for(int i = 0; i < _addonDefinitions.Length; i++)
        {
            var (resourceFolderSlug, name) = _addonDefinitions[i];

            if(!filesByFolder.TryGetValue(resourceFolderSlug, out var folderFiles) || folderFiles.Count == 0)
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

        string[] parts = remainder.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length <= 2)
        {
            return NormalizeEmbeddedPathSegment(remainder);
        }

        string directoryPath = String.Join('/', parts[..^2].Select(NormalizeEmbeddedPathSegment));
        string fileName = $"{NormalizeEmbeddedPathSegment(parts[^2])}.{parts[^1]}";
        return $"{directoryPath}/{fileName}";
    }

    private static string NormalizeEmbeddedPathSegment(string value)
        => value.Replace('_', '-');

    private static string ReadManifestResourceText(Assembly assembly, string fullName)
    {
        using var stream = assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Cannot open embedded resource '{fullName}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
