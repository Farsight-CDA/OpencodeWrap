using Spectre.Console;
using System.CommandLine;

namespace OpencodeWrap.Cli.Profile;

internal sealed class ListProfilesCliCommand : Command
{
    private readonly ProfileService _profileService;
    private readonly BuiltInProfileTemplateService _builtInProfileTemplateService;

    public ListProfilesCliCommand(ProfileService profileService, BuiltInProfileTemplateService builtInProfileTemplateService)
        : base("list", "List all configured profiles.")
    {
        _profileService = profileService;
        _builtInProfileTemplateService = builtInProfileTemplateService;
        SetAction(async _ => await ExecuteAsync());
    }

    private async Task<int> ExecuteAsync()
    {
        var (success, catalog) = _profileService.TryLoadProfileCatalog();
        if (!success)
        {
            return 1;
        }

        var allProfileNames = new HashSet<string>(catalog.ProfileDirectories.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var builtInProfile in _builtInProfileTemplateService.BuiltInProfiles)
        {
            allProfileNames.Add(builtInProfile.Name);
        }

        if (allProfileNames.Count == 0)
        {
            AppIO.WriteWarning("No profiles found.");
            return 0;
        }

        AppIO.WriteHeader("Profiles", $"Default profile: {catalog.DefaultProfileName}");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.DeepSkyBlue1)
            .AddColumn("[deepskyblue1]Profile[/]")
            .AddColumn("[deepskyblue1]Kind[/]")
            .AddColumn("[deepskyblue1]Location[/]");

        int invalidPathCount = 0;

        foreach(string profileName in allProfileNames
            .OrderByDescending(name => String.Equals(name, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            bool isDefault = String.Equals(profileName, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase);
            bool isBuiltIn = _builtInProfileTemplateService.BuiltInProfiles.Any(builtInProfile =>
                builtInProfile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
            bool hasOverride = catalog.ProfileDirectories.ContainsKey(profileName);

            string typeLabel = isBuiltIn
                ? hasOverride ? "🛠 built-in override" : "📦 built-in"
                : "✨ custom";

            string displayPath;
            if (!hasOverride)
            {
                displayPath = "[grey](embedded)[/]";
            }
            else
            {
                string relativeDirectoryPath = catalog.ProfileDirectories[profileName];
                if (_profileService.TryResolveProfileDirectoryPath(catalog.ProfilesRoot, relativeDirectoryPath, out string profileDirectoryPath))
                {
                    displayPath = Markup.Escape(profileDirectoryPath);
                }
                else
                {
                    invalidPathCount++;
                    displayPath = $"[red](invalid: {Markup.Escape(relativeDirectoryPath)})[/]";
                }
            }

            string profileCell = isDefault ? $"[bold green]★ {Markup.Escape(profileName)}[/]" : Markup.Escape(profileName);
            table.AddRow(profileCell, Markup.Escape(typeLabel), displayPath);
        }

        AnsiConsole.Write(table);

        if(invalidPathCount > 0)
        {
            AppIO.WriteWarning($"Found {invalidPathCount} profile path issue(s). Consider fixing or re-adding those profiles.");
        }

        AppIO.WriteInfo("Tip: run 'ocw profile add <name>' to create a custom profile or override a built-in one.");

        return 0;
    }
}
