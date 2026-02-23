using System.CommandLine;

internal sealed class AddProfileCliCommand : Command
{
    private readonly ProfileService _profileService;
    private readonly Argument<string> _nameArgument;

    public AddProfileCliCommand(ProfileService profileService)
        : base("add", "Add a new profile with a starter Dockerfile.")
    {
        _profileService = profileService;
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
        if(!ProfileService.IsValidProfileName(normalizedName))
        {
            AppIO.WriteError(ProfileService.InvalidProfileNameMessage);
            return 1;
        }

        var catalogResult = await _profileService.TryLoadProfileCatalogAsync();
        if(!catalogResult.Success)
        {
            return 1;
        }

        var catalog = catalogResult.Catalog;
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

        try
        {
            Directory.CreateDirectory(profileDirectoryPath);
            if(File.Exists(dockerfilePath))
            {
                AppIO.WriteError($"Cannot add profile '{normalizedName}' because '{dockerfilePath}' already exists.");
                return 1;
            }

            await File.WriteAllTextAsync(dockerfilePath, _profileService.StarterDockerfileTemplate);
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to add profile '{normalizedName}': {ex.Message}");
            return 1;
        }

        AppIO.WriteSuccess($"Added profile '{normalizedName}' at '{profileDirectoryPath}'.");
        return 0;
    }
}
