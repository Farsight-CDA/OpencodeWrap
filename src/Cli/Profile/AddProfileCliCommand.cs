using System.CommandLine;

internal sealed class AddProfileCliCommand : Command
{
    private readonly Argument<string> _nameArgument;

    public AddProfileCliCommand()
        : base("add", "Add a new profile with a starter Dockerfile.")
    {
        _nameArgument = new Argument<string>("name")
        {
            Description = "New profile name."
        };

        Add(_nameArgument);

        SetAction(async parseResult =>
        {
            string name = parseResult.GetRequiredValue(_nameArgument);
            return await ExecuteAsync(name);
        });
    }

    private static async Task<int> ExecuteAsync(string profileName)
    {
        string normalizedName = profileName.Trim();
        if(!ProfileService.IsValidProfileName(normalizedName))
        {
            AppIO.WriteError(ProfileService.INVALID_PROFILE_NAME_MESSAGE);
            return 1;
        }

        var (success, catalog) = ProfileService.TryLoadProfileCatalog();
        if(!success)
        {
            return 1;
        }
        if(catalog.ProfileDirectories.ContainsKey(normalizedName))
        {
            AppIO.WriteError($"Profile '{normalizedName}' already exists.");
            return 1;
        }

        if(!ProfileService.TryResolveProfileDirectoryPath(catalog.ConfigRoot, normalizedName, out string profileDirectoryPath))
        {
            AppIO.WriteError($"Profile directory '{profileDirectoryPath}' resolves outside '{catalog.ConfigRoot}'.");
            return 1;
        }

        string dockerfilePath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME);
        string opencodeDirectoryPath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);
        string opencodeConfigPath = Path.Combine(opencodeDirectoryPath, "opencode.json");

        try
        {
            Directory.CreateDirectory(profileDirectoryPath);
            if(File.Exists(dockerfilePath))
            {
                AppIO.WriteError($"Cannot add profile '{normalizedName}' because '{dockerfilePath}' already exists.");
                return 1;
            }

            Directory.CreateDirectory(opencodeDirectoryPath);

            var builtInTemplate = BuiltInProfileTemplateService.TryGetBuiltInProfileTemplate(normalizedName);
            if(builtInTemplate is not null)
            {
                await File.WriteAllTextAsync(dockerfilePath, builtInTemplate.Value.Dockerfile);
                await File.WriteAllTextAsync(opencodeConfigPath, builtInTemplate.Value.OpencodeConfig);
            }
            else
            {
                await File.WriteAllTextAsync(dockerfilePath, BuiltInProfileTemplateService.StarterDockerfileTemplate);
            }
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to add profile '{normalizedName}': {ex.Message}");
            return 1;
        }

        string mode = BuiltInProfileTemplateService.IsBuiltInProfileName(normalizedName) ? "override" : "profile";
        AppIO.WriteSuccess($"Added {mode} '{normalizedName}' at '{profileDirectoryPath}'.");
        return 0;
    }
}
