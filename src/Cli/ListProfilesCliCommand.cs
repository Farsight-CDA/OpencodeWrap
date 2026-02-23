using System.CommandLine;

internal sealed class ListProfilesCliCommand : Command
{
    private readonly ProfileService _profileService;

    public ListProfilesCliCommand(ProfileService profileService)
        : base("list", "List all configured profiles.")
    {
        _profileService = profileService;

        SetAction(async _ => await ExecuteAsync());
    }

    private async Task<int> ExecuteAsync()
    {
        var catalogResult = await _profileService.TryLoadProfileCatalogAsync();
        if(!catalogResult.Success)
        {
            return 1;
        }

        ProfileCatalog catalog = catalogResult.Catalog;
        if(catalog.ProfileDirectories.Count == 0)
        {
            AppIO.WriteWarning("No profiles found.");
            return 0;
        }

        AppIO.WriteInfo($"Profiles ('{catalog.DefaultProfileName}' is the fixed built-in default profile name):");

        foreach(var profile in catalog.ProfileDirectories.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            string marker = String.Equals(profile.Key, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase)
                ? " [default]"
                : String.Empty;

            AppIO.WriteInfo($"- {profile.Key}{marker} ({profile.Value})");
        }

        return 0;
    }
}
