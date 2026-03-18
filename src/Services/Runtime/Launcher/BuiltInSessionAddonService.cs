namespace OpencodeWrap.Services.Runtime;

internal sealed partial class BuiltInSessionAddonService : Singleton
{
    private const string QUESTION_AFFINITY_NAME = "Question Affinity";
    private const string QUESTION_AFFINITY_INSTRUCTIONS = """
        # Agent Behavior

        - Whenever you think you can improve your solution by asking additional questions for clarification please do so.
        - If a user request contains any ambiguity please use questions to clarify further.
        - When asking questions always use the questions tool.
        """;

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    private static readonly BuiltInSessionAddon[] _builtInAddons =
    [
        new(
            QUESTION_AFFINITY_NAME,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [$"{OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME}/{OpencodeWrapConstants.AGENTS_FILE_NAME}"] = QUESTION_AFFINITY_INSTRUCTIONS
            })
    ];

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
}
