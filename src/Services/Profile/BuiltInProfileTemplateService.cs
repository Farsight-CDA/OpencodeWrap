namespace OpencodeWrap.Services.Profile;

internal sealed partial class BuiltInProfileTemplateService : Singleton
{
    private const string DEFAULT_ENTRYPOINT_RESOURCE_NAME = "ProfileTemplates.entrypoint.sh";

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    private static readonly BuiltInProfileTemplate[] _builtInProfiles = [
        new BuiltInProfileTemplate(
            "default",
            LoadEmbeddedTextResource("ProfileTemplates.default.Dockerfile"),
            LoadEmbeddedTextResource("ProfileTemplates.default.opencode.json"),
            IsDefault: true),
        new BuiltInProfileTemplate(
            "frontend",
            LoadEmbeddedTextResource("ProfileTemplates.frontend.Dockerfile"),
            LoadEmbeddedTextResource("ProfileTemplates.frontend.opencode.json")),
        new BuiltInProfileTemplate(
            "dotnet",
            LoadEmbeddedTextResource("ProfileTemplates.dotnet.Dockerfile"),
            LoadEmbeddedTextResource("ProfileTemplates.dotnet.opencode.json")),
        new BuiltInProfileTemplate(
            "data-science",
            LoadEmbeddedTextResource("ProfileTemplates.data-science.Dockerfile"),
            LoadEmbeddedTextResource("ProfileTemplates.data-science.opencode.json")),
        new BuiltInProfileTemplate(
            "solidity",
            LoadEmbeddedTextResource("ProfileTemplates.solidity.Dockerfile"),
            LoadEmbeddedTextResource("ProfileTemplates.solidity.opencode.json"))
    ];

    public IReadOnlyList<BuiltInProfileTemplate> BuiltInProfiles { get; } = _builtInProfiles;
    public BuiltInProfileTemplate StarterProfile { get; } = _builtInProfiles.First(profile => profile.IsDefault);
    public string DefaultEntrypointScript { get; } = LoadEmbeddedTextResource(DEFAULT_ENTRYPOINT_RESOURCE_NAME);
    public string ProfileBinReadme { get; } = $$"""
        Place helper executables or scripts for this profile in this directory.

        OCW mounts this directory inside the container at `{{OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}}/{{OpencodeWrapConstants.PROFILE_BIN_DIRECTORY_NAME}}`
        and adds it to `PATH` for profile runs.

        On Unix-like hosts, remember to mark scripts or binaries as executable.
        """;

    public async Task<(bool Success, string TemporaryDirectoryPath)> TryMaterializeBuiltInProfileAsync(BuiltInProfileTemplate builtInProfile, string? materializationRootDirectory = null)
    {
        string temporaryDirectoryPath = String.IsNullOrWhiteSpace(materializationRootDirectory)
            ? Path.Combine(Path.GetTempPath(), $"ocw-profile-{builtInProfile.Name}-{Guid.NewGuid():N}")
            : Path.Combine(materializationRootDirectory, "profile");

        try
        {
            Directory.CreateDirectory(temporaryDirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(temporaryDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME), builtInProfile.Dockerfile);
            string opencodeDirectoryPath = await EnsureProfileSupportDirectoriesAsync(temporaryDirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(opencodeDirectoryPath, "opencode.json"), builtInProfile.OpencodeConfig);
            await WriteDefaultEntrypointAsync(temporaryDirectoryPath);
            return (true, temporaryDirectoryPath);
        }
        catch(Exception ex)
        {
            AppIO.TryDeleteDirectory(temporaryDirectoryPath);
            _deferredSessionLogService.WriteErrorOrConsole("profile", $"Failed to prepare built-in profile '{builtInProfile.Name}': {ex.Message}");
            return (false, "");
        }
    }

    public async Task WriteDefaultEntrypointAsync(string profileDirectoryPath)
    {
        string entrypointPath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_ENTRYPOINT_FILE_NAME);
        await File.WriteAllTextAsync(entrypointPath, DefaultEntrypointScript);

        if(OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                entrypointPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch(Exception)
        {
        }
    }

    public async Task<string> EnsureProfileSupportDirectoriesAsync(string profileDirectoryPath)
    {
        string opencodeDirectoryPath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);
        string binDirectoryPath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_BIN_DIRECTORY_NAME);
        string binReadmePath = Path.Combine(binDirectoryPath, "README.md");

        Directory.CreateDirectory(opencodeDirectoryPath);
        Directory.CreateDirectory(binDirectoryPath);

        if(!File.Exists(binReadmePath))
        {
            await File.WriteAllTextAsync(binReadmePath, ProfileBinReadme);
        }

        return opencodeDirectoryPath;
    }

    private static string LoadEmbeddedTextResource(string resourceNameSuffix)
    {
        var assembly = typeof(BuiltInProfileTemplateService).Assembly;
        string[] resourceNames = assembly.GetManifestResourceNames();

        string normalizedSuffix = resourceNameSuffix.Replace('-', '_');

        string? fullResourceName = resourceNames.FirstOrDefault(name =>
            name.EndsWith(resourceNameSuffix, StringComparison.Ordinal) ||
            name.EndsWith(normalizedSuffix, StringComparison.Ordinal)) ?? throw new InvalidOperationException($"Missing embedded resource '{resourceNameSuffix}'.");

        using var stream = assembly.GetManifestResourceStream(fullResourceName) ?? throw new InvalidOperationException($"Cannot open embedded resource '{fullResourceName}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
