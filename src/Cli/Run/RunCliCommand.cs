using Spectre.Console;
using System.CommandLine;

namespace OpencodeWrap.Cli.Run;

internal sealed class RunCliCommand : Command
{
    private readonly OpencodeLauncherService _launcherService;
    private readonly Option<string?> _profileOption;
    private readonly Option<string?> _mountModeOption;
    private readonly Option<string[]> _resourceDirOption;

    public RunCliCommand(OpencodeLauncherService launcherService)
        : base("run", "Run opencode with a selected profile config.")
    {
        _launcherService = launcherService;
        _profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Profile name from a directory under $HOME/.opencode-wrap."
        };
        _mountModeOption = new Option<string?>("--mount-mode")
        {
            Description = "Workspace mount mode: mount (default), readonly-mount, or no-mount."
        };
        _resourceDirOption = new Option<string[]>("--resource-dir", "-r")
        {
            Description = "Additional host directory to mount read-only. Repeat the option to mount multiple directories."
        };

        Add(_profileOption);
        Add(_mountModeOption);
        Add(_resourceDirOption);

        SetAction(async parseResult =>
        {
            string? profile = parseResult.GetValue(_profileOption);
            string mountModeInput = parseResult.GetValue(_mountModeOption) ?? "mount";
            string[] resourceDirs = parseResult.GetValue(_resourceDirOption) ?? [];
            if(!TryParseMountMode(mountModeInput, out WorkspaceMountMode mountMode))
            {
                AppIO.WriteError($"Invalid --mount-mode value '{mountModeInput}'. Expected one of: mount, readonly-mount, no-mount.");
                return 1;
            }

            if(String.IsNullOrWhiteSpace(profile))
            {
                RunSelection? selection = PromptForRunSelection(defaultMountMode: mountMode);
                if(selection is null)
                {
                    return 1;
                }

                profile = selection.ProfileName;
                mountMode = selection.MountMode;
            }

            return await _launcherService.ExecuteAsync([], requestedProfileName: profile, includeProfileConfig: true, workspaceMountMode: mountMode, extraReadonlyMountDirs: resourceDirs);
        });
    }

    private static RunSelection? PromptForRunSelection(WorkspaceMountMode defaultMountMode)
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

        WorkspaceMountMode mountMode = defaultMountMode;

        while(true)
        {
            RenderRunSelectionScreen(profileChoices, selectedIndex, mountMode, currentWorkspacePath);
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
                    mountMode = CycleMountMode(mountMode);
                    break;
                case ConsoleKey.Enter:
                    AnsiConsole.Clear();
                    return new RunSelection(profileChoices[selectedIndex].Name, mountMode);
                case ConsoleKey.Escape:
                    AnsiConsole.Clear();
                    return null;
            }
        }
    }

    private static WorkspaceMountMode CycleMountMode(WorkspaceMountMode mountMode) => mountMode switch
    {
        WorkspaceMountMode.ReadWrite => WorkspaceMountMode.ReadOnly,
        WorkspaceMountMode.ReadOnly => WorkspaceMountMode.None,
        _ => WorkspaceMountMode.ReadWrite
    };

    private static bool TryParseMountMode(string value, out WorkspaceMountMode mountMode)
    {
        switch(value.Trim().ToLowerInvariant())
        {
            case "mount":
                mountMode = WorkspaceMountMode.ReadWrite;
                return true;
            case "readonly-mount":
                mountMode = WorkspaceMountMode.ReadOnly;
                return true;
            case "no-mount":
                mountMode = WorkspaceMountMode.None;
                return true;
            default:
                mountMode = WorkspaceMountMode.ReadWrite;
                return false;
        }
    }

    private static void RenderRunSelectionScreen(IReadOnlyList<ProfileChoice> profileChoices, int selectedIndex, WorkspaceMountMode mountMode, string currentWorkspacePath)
    {
        AnsiConsole.Clear();

        string mountModeLabel = mountMode switch
        {
            WorkspaceMountMode.ReadOnly => $"[gold1]{Markup.Escape($"Readonly Mount[{currentWorkspacePath}]")}[/]",
            WorkspaceMountMode.None => "[yellow]No Mount[/]",
            _ => $"[deepskyblue1]{Markup.Escape($"Mount[{currentWorkspacePath}]")}[/]"
        };

        AnsiConsole.MarkupLine("Select a profile");
        AnsiConsole.MarkupLine($"[grey]Mount mode:[/] {mountModeLabel}");
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

    private sealed record RunSelection(string ProfileName, WorkspaceMountMode MountMode);
    private sealed record ProfileChoice(string Name, bool IsDefault);
}
