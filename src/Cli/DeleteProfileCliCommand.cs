using System.CommandLine;

internal sealed class DeleteProfileCliCommand : Command
{
    private readonly Argument<string> _nameArgument;

    public DeleteProfileCliCommand(ProfileService _)
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

        if(String.Equals(OpencodeWrapConstants.DEFAULT_PROFILE_NAME, normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            AppIO.WriteError($"Cannot delete default profile '{normalizedName}'.");
            return 1;
        }

        var (success, catalog) = await ProfileService.TryLoadProfileCatalogAsync();
        if(!success)
        {
            return 1;
        }
        if(!catalog.ProfileDirectories.ContainsKey(normalizedName))
        {
            AppIO.WriteError($"Profile '{normalizedName}' does not exist.");
            return 1;
        }

        if(!ProfileService.TryResolveProfileDirectoryPath(catalog.ConfigRoot, normalizedName, out string profileDirectoryPath))
        {
            AppIO.WriteError($"Profile '{normalizedName}' directory resolves outside '{catalog.ConfigRoot}'.");
            return 1;
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

        AppIO.WriteSuccess($"Deleted profile '{normalizedName}'.");
        return 0;
    }
}
