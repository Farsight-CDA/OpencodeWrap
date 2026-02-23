using System.CommandLine;

internal sealed class ListProfilesCliCommand : Command
{
    public ListProfilesCliCommand(ProfileService _)
        : base("list", "List all configured profiles.")
    {
        SetAction(async _ => await ExecuteAsync());
    }

    private static async Task<int> ExecuteAsync()
    {
        var (success, catalog) = await ProfileService.TryLoadProfileCatalogAsync();
        if(!success)
        {
            return 1;
        }
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
