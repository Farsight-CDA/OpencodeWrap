internal static class BuiltInProfileTemplateService
{
    private static readonly string[] _builtInProfileNames = [
        OpencodeWrapConstants.DEFAULT_PROFILE_NAME,
        OpencodeWrapConstants.DOTNET_PROFILE_NAME,
        OpencodeWrapConstants.DATA_SCIENCE_PROFILE_NAME
    ];

    private static string DefaultDockerfile { get; } = LoadEmbeddedTextResource("ProfileTemplates.default.Dockerfile");
    private static string DotnetDockerfile { get; } = LoadEmbeddedTextResource("ProfileTemplates.dotnet.Dockerfile");
    private static string DataScienceDockerfile { get; } = LoadEmbeddedTextResource("ProfileTemplates.data-science.Dockerfile");
    private static string DefaultOpencodeConfig { get; } = LoadEmbeddedTextResource("ProfileTemplates.default.opencode.json");
    private static string DotnetOpencodeConfig { get; } = LoadEmbeddedTextResource("ProfileTemplates.dotnet.opencode.json");
    private static string DataScienceOpencodeConfig { get; } = LoadEmbeddedTextResource("ProfileTemplates.data-science.opencode.json");

    public static string StarterDockerfileTemplate => DefaultDockerfile;

    public static bool IsBuiltInProfileName(string profileName) => _builtInProfileNames.Contains(profileName, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> GetBuiltInProfileNames() => _builtInProfileNames;

    public static (string Dockerfile, string OpencodeConfig)? TryGetBuiltInProfileTemplate(string profileName)
    {
        if(String.Equals(profileName, OpencodeWrapConstants.DEFAULT_PROFILE_NAME, StringComparison.OrdinalIgnoreCase))
        {
            return (DefaultDockerfile, DefaultOpencodeConfig);
        }

        if(String.Equals(profileName, OpencodeWrapConstants.DOTNET_PROFILE_NAME, StringComparison.OrdinalIgnoreCase))
        {
            return (DotnetDockerfile, DotnetOpencodeConfig);
        }

        if(String.Equals(profileName, OpencodeWrapConstants.DATA_SCIENCE_PROFILE_NAME, StringComparison.OrdinalIgnoreCase))
        {
            return (DataScienceDockerfile, DataScienceOpencodeConfig);
        }

        return null;
    }

    public static async Task<(bool Success, string TemporaryDirectoryPath)> TryMaterializeBuiltInProfileAsync(string profileName)
    {
        var builtInTemplate = TryGetBuiltInProfileTemplate(profileName);
        if(builtInTemplate is null)
        {
            AppIO.WriteError($"Built-in profile template not found for '{profileName}'.");
            return (false, String.Empty);
        }

        string temporaryDirectoryPath = Path.Combine(Path.GetTempPath(), $"ocw-profile-{profileName}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(temporaryDirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(temporaryDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME), builtInTemplate.Value.Dockerfile);
            string opencodeDirectoryPath = Path.Combine(temporaryDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);
            Directory.CreateDirectory(opencodeDirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(opencodeDirectoryPath, "opencode.json"), builtInTemplate.Value.OpencodeConfig);
            return (true, temporaryDirectoryPath);
        }
        catch(Exception ex)
        {
            AppIO.TryDeleteDirectory(temporaryDirectoryPath);
            AppIO.WriteError($"Failed to prepare built-in profile '{profileName}': {ex.Message}");
            return (false, String.Empty);
        }
    }

    private static string LoadEmbeddedTextResource(string resourceNameSuffix)
    {
        var assembly = typeof(BuiltInProfileTemplateService).Assembly;
        string[] resourceNames = assembly.GetManifestResourceNames();

        string? fullResourceName = resourceNames.FirstOrDefault(name =>
            name.EndsWith(resourceNameSuffix, StringComparison.Ordinal)) ?? throw new InvalidOperationException($"Missing embedded resource '{resourceNameSuffix}'.");

        using var stream = assembly.GetManifestResourceStream(fullResourceName) ?? throw new InvalidOperationException($"Cannot open embedded resource '{fullResourceName}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
