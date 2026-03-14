namespace OpencodeWrap.Services.Profile;

internal static class BuiltInProfileTemplateService
{
    private const string _defaultEntrypointResourceName = "ProfileTemplates.entrypoint.sh";

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

    public static IReadOnlyList<BuiltInProfileTemplate> BuiltInProfiles { get; } = _builtInProfiles;
    public static BuiltInProfileTemplate StarterProfile { get; } = _builtInProfiles.First(profile => profile.IsDefault);
    public static string DefaultEntrypointScript { get; } = LoadEmbeddedTextResource(_defaultEntrypointResourceName);

    public static async Task<(bool Success, string TemporaryDirectoryPath)> TryMaterializeBuiltInProfileAsync(BuiltInProfileTemplate builtInProfile)
    {
        string temporaryDirectoryPath = Path.Combine(Path.GetTempPath(), $"ocw-profile-{builtInProfile.Name}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(temporaryDirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(temporaryDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME), builtInProfile.Dockerfile);
            string opencodeDirectoryPath = Path.Combine(temporaryDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);
            Directory.CreateDirectory(opencodeDirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(opencodeDirectoryPath, "opencode.json"), builtInProfile.OpencodeConfig);
            await WriteDefaultEntrypointAsync(temporaryDirectoryPath);
            return (true, temporaryDirectoryPath);
        }
        catch(Exception ex)
        {
            AppIO.TryDeleteDirectory(temporaryDirectoryPath);
            AppIO.WriteError($"Failed to prepare built-in profile '{builtInProfile.Name}': {ex.Message}");
            return (false, "");
        }
    }

    public static async Task WriteDefaultEntrypointAsync(string profileDirectoryPath)
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
