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
            if(!TryParseMountMode(mountModeInput, out var mountMode))
            {
                AppIO.WriteError($"Invalid --mount-mode value '{mountModeInput}'. Expected one of: mount, readonly-mount, no-mount.");
                return 1;
            }

            if(String.IsNullOrWhiteSpace(profile))
            {
                var selection = PromptForRunSelection(defaultMountMode: mountMode, initialResourceDirectories: resourceDirs);
                if(selection is null)
                {
                    return 1;
                }

                profile = selection.ProfileName;
                mountMode = selection.MountMode;
                resourceDirs = [.. selection.ResourceDirectories];
            }

            return await _launcherService.ExecuteAsync([], requestedProfileName: profile, includeProfileConfig: true, workspaceMountMode: mountMode, extraReadonlyMountDirs: resourceDirs);
        });
    }

    private static RunSelection? PromptForRunSelection(WorkspaceMountMode defaultMountMode, IReadOnlyList<string> initialResourceDirectories)
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

        var mountMode = defaultMountMode;
        var selectedResourceDirectories = new List<string>();
        var seenResourceDirectories = new HashSet<string>(GetHostPathComparer());
        if(!TryAddInitialResourceDirectories(initialResourceDirectories, selectedResourceDirectories, seenResourceDirectories))
        {
            return null;
        }

        string? statusMessage = null;

        while(true)
        {
            RenderRunSelectionScreen(profileChoices, selectedIndex, mountMode, currentWorkspacePath, selectedResourceDirectories, statusMessage);
            statusMessage = null;
            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
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
                case ConsoleKey.R:
                    string? inputPath = PromptForResourceDirectory();
                    if(inputPath is null)
                    {
                        statusMessage = "Add resource directory canceled.";
                        break;
                    }

                    if(!TryNormalizeResourceDirectory(inputPath, out string normalizedPath, out string errorMessage))
                    {
                        statusMessage = errorMessage;
                        break;
                    }

                    if(!seenResourceDirectories.Add(normalizedPath))
                    {
                        statusMessage = "Resource directory is already selected.";
                        break;
                    }

                    selectedResourceDirectories.Add(normalizedPath);
                    statusMessage = $"Added: {normalizedPath}";
                    break;
                case ConsoleKey.Backspace:
                case ConsoleKey.D:
                    if(selectedResourceDirectories.Count == 0)
                    {
                        statusMessage = "No resource directories to remove.";
                        break;
                    }

                    string removedDirectory = selectedResourceDirectories[^1];
                    selectedResourceDirectories.RemoveAt(selectedResourceDirectories.Count - 1);
                    seenResourceDirectories.Remove(removedDirectory);
                    statusMessage = $"Removed: {removedDirectory}";
                    break;
                case ConsoleKey.Enter:
                    AnsiConsole.Clear();
                    return new RunSelection(profileChoices[selectedIndex].Name, mountMode, selectedResourceDirectories);
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

    private static void RenderRunSelectionScreen(
        IReadOnlyList<ProfileChoice> profileChoices,
        int selectedIndex,
        WorkspaceMountMode mountMode,
        string currentWorkspacePath,
        IReadOnlyList<string> selectedResourceDirectories,
        string? statusMessage)
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
        AnsiConsole.MarkupLine("[grey]Resource dirs (read-only):[/]");
        if(selectedResourceDirectories.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey](none)[/]");
        }
        else
        {
            foreach(string resourceDirectory in selectedResourceDirectories)
            {
                AnsiConsole.MarkupLine($"  [deepskyblue1]-[/] {Markup.Escape(resourceDirectory)}");
            }
        }

        if(!String.IsNullOrWhiteSpace(statusMessage))
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(statusMessage)}[/]");
        }

        AnsiConsole.MarkupLine("[grey](Use [blue]<up>/<down>[/] to select profile, [blue]<space>[/] to toggle mount mode, [blue]r[/] to add resource dir, [blue]<backspace>[/] or [blue]d[/] to remove last, [green]<enter>[/] to continue, [red]<esc>[/] to cancel)[/]");
        AnsiConsole.WriteLine();

        for(int i = 0; i < profileChoices.Count; i++)
        {
            var choice = profileChoices[i];
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

    private static bool TryAddInitialResourceDirectories(
        IReadOnlyList<string> initialResourceDirectories,
        List<string> selectedResourceDirectories,
        HashSet<string> seenResourceDirectories)
    {
        foreach(string initialResourceDirectory in initialResourceDirectories)
        {
            if(!TryNormalizeResourceDirectory(initialResourceDirectory, out string normalizedPath, out string errorMessage))
            {
                AppIO.WriteError(errorMessage);
                return false;
            }

            if(seenResourceDirectories.Add(normalizedPath))
            {
                selectedResourceDirectories.Add(normalizedPath);
            }
        }

        return true;
    }

    private static string? PromptForResourceDirectory()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("Add resource directory");
        AnsiConsole.MarkupLine("[grey]Enter a host directory path to mount read-only. Leave empty to cancel.[/]");
        string input = AnsiConsole.Prompt(new TextPrompt<string>("[deepskyblue1]Path[/]").AllowEmpty());
        return String.IsNullOrWhiteSpace(input)
            ? null
            : input.Trim();
    }

    private static bool TryNormalizeResourceDirectory(string requestedDirectory, out string normalizedPath, out string errorMessage)
    {
        if(String.IsNullOrWhiteSpace(requestedDirectory))
        {
            normalizedPath = String.Empty;
            errorMessage = "--resource-dir cannot be empty.";
            return false;
        }

        normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory));
        if(!Directory.Exists(normalizedPath))
        {
            errorMessage = $"Resource directory not found: '{normalizedPath}'.";
            return false;
        }

        errorMessage = String.Empty;
        return true;
    }

    private static StringComparer GetHostPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record RunSelection(string ProfileName, WorkspaceMountMode MountMode, IReadOnlyList<string> ResourceDirectories);
    private sealed record ProfileChoice(string Name, bool IsDefault);
}
