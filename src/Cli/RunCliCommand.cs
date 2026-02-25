using System.CommandLine;
using Spectre.Console;

internal sealed class RunCliCommand : Command
{
    private readonly OpencodeLauncherService _launcherService;
    private readonly Option<string?> _profileOption;
    private readonly Option<bool> _noMountOption;

    public RunCliCommand(OpencodeLauncherService launcherService)
        : base("run", "Run opencode with a selected profile config.")
    {
        _launcherService = launcherService;
        _profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Profile name from a directory under $HOME/.opencode-wrap."
        };
        _noMountOption = new Option<bool>("--no-mount")
        {
            Description = "Do not mount the current workspace; run from the container home directory."
        };

        Add(_profileOption);
        Add(_noMountOption);

        SetAction(async parseResult =>
        {
            string? profile = parseResult.GetValue(_profileOption);
            if(String.IsNullOrWhiteSpace(profile))
            {
                profile = PromptForProfileName();
                if(String.IsNullOrWhiteSpace(profile))
                {
                    return 1;
                }
            }

            bool noMount = parseResult.GetValue(_noMountOption);
            return await _launcherService.ExecuteAsync([], requestedProfileName: profile, includeProfileConfig: true, disableWorkspaceMount: noMount);
        });
    }

    private static string? PromptForProfileName()
    {
        var (success, catalog) = ProfileService.TryLoadProfileCatalog();
        if(!success)
        {
            return null;
        }

        var profileNames = new HashSet<string>(catalog.ProfileDirectories.Keys, StringComparer.OrdinalIgnoreCase);
        foreach(string builtInProfileName in ProfileService.GetBuiltInProfileNames())
        {
            profileNames.Add(builtInProfileName);
        }

        if(profileNames.Count == 0)
        {
            AppIO.WriteError("No profiles found. Use 'ocw profile add <name>' first.");
            return null;
        }

        if(!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AppIO.WriteError("No profile provided and interactive selection is unavailable. Pass --profile <name>.");
            return null;
        }

        List<ProfileChoice> profileChoices = profileNames
            .OrderByDescending(name => String.Equals(name, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ProfileChoice(name, String.Equals(name, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        ProfileChoice selectedProfile = AnsiConsole.Prompt(
            new SelectionPrompt<ProfileChoice>()
                .Title("Select a profile")
                .PageSize(Math.Min(profileChoices.Count, 10))
                .UseConverter(choice => choice.IsDefault ? $"{choice.Name} (default)" : choice.Name)
                .AddChoices(profileChoices));

        return selectedProfile.Name;
    }

    private sealed record ProfileChoice(string Name, bool IsDefault);
}
