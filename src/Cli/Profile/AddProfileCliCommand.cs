using System.CommandLine;

namespace OpencodeWrap.Cli.Profile;

internal sealed class AddProfileCliCommand : Command
{
    private readonly Argument<string> _nameArgument;
    private readonly ProfileService _profileService;
    private readonly BuiltInProfileTemplateService _builtInProfileTemplateService;

    public AddProfileCliCommand(ProfileService profileService, BuiltInProfileTemplateService builtInProfileTemplateService)
        : base("add", "Add a new profile with a starter Dockerfile and entrypoint script.")
    {
        _profileService = profileService;
        _builtInProfileTemplateService = builtInProfileTemplateService;
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

    private async Task<int> ExecuteAsync(string profileName)
    {
        string normalizedName = profileName.Trim();
        if(!_profileService.IsValidProfileName(normalizedName))
        {
            AppIO.WriteError(ProfileService.INVALID_PROFILE_NAME_MESSAGE);
            return 1;
        }

        var (success, catalog) = _profileService.TryLoadProfileCatalog();
        if(!success)
        {
            return 1;
        }
        if(catalog.ProfileDirectories.ContainsKey(normalizedName))
        {
            AppIO.WriteError($"Profile '{normalizedName}' already exists.");
            return 1;
        }

        if(!_profileService.TryResolveProfileDirectoryPath(catalog.ProfilesRoot, normalizedName, out string profileDirectoryPath))
        {
            AppIO.WriteError($"Profile directory '{profileDirectoryPath}' resolves outside '{catalog.ProfilesRoot}'.");
            return 1;
        }

        string dockerfilePath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME);
        string opencodeDirectoryPath = Path.Combine(profileDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);
        string opencodeConfigPath = Path.Combine(opencodeDirectoryPath, "opencode.json");

        try
        {
            bool created = await AppIO.RunWithLoadingStateAsync($"Creating profile '{normalizedName}'...", async () =>
            {
                Directory.CreateDirectory(profileDirectoryPath);
                if(File.Exists(dockerfilePath))
                {
                    AppIO.WriteError($"Cannot add profile '{normalizedName}' because '{dockerfilePath}' already exists.");
                    return false;
                }

                Directory.CreateDirectory(opencodeDirectoryPath);

                var builtInProfile = _builtInProfileTemplateService.BuiltInProfiles.FirstOrDefault(profile =>
                    profile.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));

                if(builtInProfile is not null)
                {
                    await File.WriteAllTextAsync(dockerfilePath, builtInProfile.Dockerfile);
                    await File.WriteAllTextAsync(opencodeConfigPath, builtInProfile.OpencodeConfig);
                }
                else
                {
                    await File.WriteAllTextAsync(dockerfilePath, _builtInProfileTemplateService.StarterProfile.Dockerfile);
                }

                await _builtInProfileTemplateService.WriteDefaultEntrypointAsync(profileDirectoryPath);
                return true;
            });

            if(!created)
            {
                return 1;
            }
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to add profile '{normalizedName}': {ex.Message}");
            return 1;
        }

        string mode = _builtInProfileTemplateService.BuiltInProfiles.Any(profile =>
            profile.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            ? "override"
            : "profile";
        AppIO.WriteSuccess($"Added {mode} '{normalizedName}' at '{profileDirectoryPath}'.");
        return 0;
    }
}
