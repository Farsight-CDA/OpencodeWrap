using Spectre.Console;
using System.CommandLine;

namespace OpencodeWrap.Cli.Run;

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

        string currentWorkspacePath = Path.GetFullPath(Directory.GetCurrentDirectory());
        int selectedIndex = profileChoices.FindIndex(choice => choice.IsDefault);
        if(selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        bool noMount = defaultNoMount;

        while(true)
        {
            RenderRunSelectionScreen(profileChoices, selectedIndex, noMount, currentWorkspacePath);
            ConsoleKeyInfo? keyInfo = AnsiConsole.Console.Input.ReadKey(intercept : true);
            if(keyInfo is null)
            {
                continue;
            }

            switch(keyInfo.Value.Key)
            {
                case ConsoleKey.UpArrow:
                    selectedIndex = selectedIndex <= 0 ? profileChoices.Count - 1 : selectedIndex - 1;
                    break;
                case ConsoleKey.DownArrow:
                    selectedIndex = selectedIndex >= profileChoices.Count - 1 ? 0 : selectedIndex + 1;
                    break;
                case ConsoleKey.Spacebar:
                    noMount = !noMount;
                    break;
                case ConsoleKey.Enter:
                    AnsiConsole.Clear();
                    return new RunSelection(profileChoices[selectedIndex].Name, noMount);
                case ConsoleKey.Escape:
                    AnsiConsole.Clear();
                    return null;
            }
        }
    }

    private static void RenderRunSelectionScreen(IReadOnlyList<ProfileChoice> profileChoices, int selectedIndex, bool noMount, string currentWorkspacePath)
    {
        AnsiConsole.Clear();

        string mountMode = noMount
            ? "[yellow]No Mount[/]"
            : $"[deepskyblue1]{Markup.Escape($"Mount[{currentWorkspacePath}]")}[/]";

        AnsiConsole.MarkupLine("Select a profile");
        AnsiConsole.MarkupLine($"[grey]Mount mode:[/] {mountMode}");
        AnsiConsole.MarkupLine("[grey](Use [blue]<up>/<down>[/] to select profile, [blue]<space>[/] to toggle mount mode, [green]<enter>[/] to continue, [red]<esc>[/] to cancel)[/]");
        AnsiConsole.WriteLine();

        for(int i = 0; i < profileChoices.Count; i++)
        {
            ProfileChoice choice = profileChoices[i];
            string cursor = i == selectedIndex ? "[green]>[/]" : " ";
            string escapedName = Markup.Escape(choice.Name);
            string label = choice.IsDefault ? $"{escapedName} [grey](default)[/]" : escapedName;
            if(i == selectedIndex)
            {
                label = $"[deepskyblue1]{label}[/]";
            }

            AnsiConsole.MarkupLine($"{cursor} {label}");
        }
    }

    private sealed record RunSelection(string ProfileName, bool NoMount);
    private sealed record ProfileChoice(string Name, bool IsDefault);
}
