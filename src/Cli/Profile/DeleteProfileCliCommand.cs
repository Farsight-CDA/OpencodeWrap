using System.CommandLine;

namespace OpencodeWrap.Cli.Profile;

internal sealed class DeleteProfileCliCommand : Command
{
    private readonly Argument<string> _nameArgument;

    public DeleteProfileCliCommand()
        : base("delete", "Delete a profile and its directory.")
    {
        _nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to delete."
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

        bool hasOverrideDirectory = catalog.ProfileDirectories.TryGetValue(normalizedName, out string? relativeDirectoryPath);
        bool isBuiltIn = BuiltInProfileTemplateService.BuiltInProfiles.Any(profile =>
            profile.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));

        if(!hasOverrideDirectory)
        {
            if(isBuiltIn)
            {
                AppIO.WriteInfo($"Profile '{normalizedName}' is using the built-in template. Nothing to delete.");
                return 0;
            }

            AppIO.WriteError($"Profile '{normalizedName}' does not exist.");
            return 1;
        }

        if(!ProfileService.TryResolveProfileDirectoryPath(catalog.ConfigRoot, relativeDirectoryPath!, out string profileDirectoryPath))
        {
            AppIO.WriteError($"Profile '{normalizedName}' directory resolves outside '{catalog.ConfigRoot}'.");
            return 1;
        }

        string confirmMessage = isBuiltIn
            ? $"Delete override profile '{normalizedName}' and remove '{profileDirectoryPath}'? This falls back to the built-in template."
            : $"Delete profile '{normalizedName}' and remove '{profileDirectoryPath}'?";

        if(!AppIO.Confirm(confirmMessage))
        {
            AppIO.WriteWarning("profile delete cancelled.");
            return 0;
        }

        try
        {
            if(Directory.Exists(profileDirectoryPath))
            {
                Directory.Delete(profileDirectoryPath, recursive: true);
            }
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to delete profile '{normalizedName}': {ex.Message}");
            return 1;
        }

        if(isBuiltIn)
        {
            AppIO.WriteSuccess($"Deleted override profile '{normalizedName}'. Now using built-in template.");
            return 0;
        }

        AppIO.WriteSuccess($"Deleted profile '{normalizedName}'.");
        return 0;
    }
}
