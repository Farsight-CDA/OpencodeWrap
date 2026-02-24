using System.CommandLine;
using Spectre.Console;

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

        var allProfileNames = new HashSet<string>(catalog.ProfileDirectories.Keys, StringComparer.OrdinalIgnoreCase);
        foreach(string builtInProfileName in ProfileService.GetBuiltInProfileNames())
        {
            allProfileNames.Add(builtInProfileName);
        }

        if(allProfileNames.Count == 0)
        {
            AppIO.WriteWarning("No profiles found.");
            return 0;
        }

        AppIO.WriteInfo($"Profiles (default: '{catalog.DefaultProfileName}'):");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[deepskyblue1]Profile[/]")
            .AddColumn("[deepskyblue1]Type[/]")
            .AddColumn("[deepskyblue1]Location[/]");

        int invalidPathCount = 0;

        foreach(string profileName in allProfileNames
            .OrderByDescending(name => String.Equals(name, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            bool isDefault = String.Equals(profileName, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase);
            bool isBuiltIn = ProfileService.IsBuiltInProfileName(profileName);
            bool hasOverride = catalog.ProfileDirectories.ContainsKey(profileName);

            string typeLabel = isBuiltIn
                ? hasOverride ? "built-in override" : "built-in"
                : "custom";

            string displayPath;
            if(!hasOverride)
            {
                displayPath = "[grey](embedded)[/]";
            }
            else
            {
                string relativeDirectoryPath = catalog.ProfileDirectories[profileName];
                if(ProfileService.TryResolveProfileDirectoryPath(catalog.ConfigRoot, relativeDirectoryPath, out string profileDirectoryPath))
                {
                    displayPath = Markup.Escape(profileDirectoryPath);
                }
                else
                {
                    invalidPathCount++;
                    displayPath = $"[red](invalid: {Markup.Escape(relativeDirectoryPath)})[/]";
                }
            }

            string profileCell = isDefault ? $"[bold]{Markup.Escape(profileName)}[/]" : Markup.Escape(profileName);
            table.AddRow(profileCell, Markup.Escape(typeLabel), displayPath);
        }

        AnsiConsole.Write(table);

        if(invalidPathCount > 0)
        {
            AppIO.WriteWarning($"Found {invalidPathCount} profile path issue(s). Consider fixing or re-adding those profiles.");
        }

        AppIO.WriteInfo("Use 'ocw profile add <name>' to create a custom profile or override a built-in one.");

        return 0;
    }
}
