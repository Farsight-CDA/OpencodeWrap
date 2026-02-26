using Spectre.Console;
using System.CommandLine;

namespace OpencodeWrap.Cli.Run;

internal sealed class RunCliCommand : Command
{
    private readonly OpencodeLauncherService _launcherService;
    private readonly Option<string?> _profileOption;
    private readonly Option<bool> _noMountOption;
    private const string _noMountChoiceLabel = "Run with no workspace mount";
    private const string _mountCurrentChoiceLabel = "Mount current workspace";

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
            bool noMount = parseResult.GetValue(_noMountOption);
            if(String.IsNullOrWhiteSpace(profile))
            {
                RunSelection? selection = PromptForRunSelection(defaultNoMount: noMount);
                if(selection is null)
                {
                    return 1;
                }

                profile = selection.ProfileName;
                noMount = selection.NoMount;
            }

            return await _launcherService.ExecuteAsync([], requestedProfileName: profile, includeProfileConfig: true, disableWorkspaceMount: noMount);
        });
    }

    private static RunSelection? PromptForRunSelection(bool defaultNoMount)
    {
        var (success, catalog) = ProfileService.TryLoadProfileCatalog();
        if(!success)
        {
            return null;
        }

        var profileNames = new HashSet<string>(catalog.ProfileDirectories.Keys, StringComparer.OrdinalIgnoreCase);
        foreach(var builtInProfile in BuiltInProfileTemplateService.BuiltInProfiles)
        {
            profileNames.Add(builtInProfile.Name);
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

        List<ProfileChoice> profileChoices = [.. profileNames
            .OrderByDescending(name => String.Equals(name, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ProfileChoice(name, String.Equals(name, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase)))];

        var selectedProfile = AnsiConsole.Prompt(
            new SelectionPrompt<ProfileChoice>()
                .Title("Select a profile")
                .PageSize(Math.Min(profileChoices.Count, 10))
                .UseConverter(choice => choice.IsDefault ? $"{choice.Name} (default)" : choice.Name)
                .AddChoices(profileChoices));

        string currentWorkspacePath = Path.GetFullPath(Directory.GetCurrentDirectory());
        string defaultModePreview = defaultNoMount
            ? "No mount"
            : $"Mount current ([deepskyblue1]{Markup.Escape(currentWorkspacePath)}[/])";
        string toggleChoiceLabel = defaultNoMount ? _mountCurrentChoiceLabel : _noMountChoiceLabel;

        var mountModePrompt = new MultiSelectionPrompt<string>();
        mountModePrompt
            .Title($"Workspace mount mode\n[grey]Current workspace path:[/] [deepskyblue1]{Markup.Escape(currentWorkspacePath)}[/]\n[grey]Default:[/] {defaultModePreview}")
            .InstructionsText("[grey](Press [blue]<space>[/] to toggle mode, [green]<enter>[/] to continue)[/]")
            .NotRequired()
            .PageSize(4)
            .UseConverter(choice =>
                String.Equals(choice, toggleChoiceLabel, StringComparison.Ordinal)
                    ? $"{choice} [grey](mount current: {Markup.Escape(currentWorkspacePath)})[/]"
                    : Markup.Escape(choice));

        mountModePrompt.AddChoice(toggleChoiceLabel);

        List<string> selectedMountMode = AnsiConsole.Prompt(mountModePrompt);
        bool toggled = selectedMountMode.Contains(toggleChoiceLabel, StringComparer.Ordinal);
        bool noMount = defaultNoMount ? !toggled : toggled;

        return new RunSelection(selectedProfile.Name, noMount);
    }

    private sealed record RunSelection(string ProfileName, bool NoMount);
    private sealed record ProfileChoice(string Name, bool IsDefault);
}
