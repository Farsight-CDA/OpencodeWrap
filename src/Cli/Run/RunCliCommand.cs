using Spectre.Console;
using System.CommandLine;

namespace OpencodeWrap.Cli.Run;

internal sealed class RunCliCommand : Command
{
    private readonly OpencodeLauncherService _launcherService;
    private readonly ProfileService _profileService;
    private readonly BuiltInProfileTemplateService _builtInProfileTemplateService;
    private readonly Option<string?> _profileOption;
    private readonly Option<string?> _mountModeOption;
    private readonly Option<string[]> _resourceDirOption;
    private readonly Option<bool> _verboseOption;

    public RunCliCommand(OpencodeLauncherService launcherService, ProfileService profileService, BuiltInProfileTemplateService builtInProfileTemplateService)
        : base("run", "Run opencode with a selected profile, including its config and bin directory.")
    {
        _launcherService = launcherService;
        _profileService = profileService;
        _builtInProfileTemplateService = builtInProfileTemplateService;
        _profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Profile name from a directory under $HOME/.opencode-wrap/profiles."
        };
        _mountModeOption = new Option<string?>("--mount-mode")
        {
            Description = "Workspace mount mode: mount (default), readonly-mount, or no-mount (starts in /workspace)."
        };
        _resourceDirOption = new Option<string[]>("--resource-dir", "-r")
        {
            Description = "Additional host directory to mount read-only. Repeat the option to mount multiple directories."
        };
        _verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Show deferred debug session logs after the interactive session exits."
        };

        Add(_profileOption);
        Add(_mountModeOption);
        Add(_resourceDirOption);
        Add(_verboseOption);

        SetAction(async parseResult =>
        {
            string? profile = parseResult.GetValue(_profileOption);
            string mountModeInput = parseResult.GetValue(_mountModeOption) ?? "mount";
            string[] resourceDirs = parseResult.GetValue(_resourceDirOption) ?? [];
            bool verbose = parseResult.GetValue(_verboseOption);
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

            return await _launcherService.ExecuteAsync([], requestedProfileName: profile, includeProfileConfig: true, workspaceMountMode: mountMode, extraReadonlyMountDirs: resourceDirs, verboseSessionLogs: verbose);
        });
    }

    private RunSelection? PromptForRunSelection(WorkspaceMountMode defaultMountMode, IReadOnlyList<string> initialResourceDirectories)
    {
        var (success, catalog) = _profileService.TryLoadProfileCatalog();
        if(!success)
        {
            return null;
        }

        var profileNames = new HashSet<string>(catalog.ProfileDirectories.Keys, StringComparer.OrdinalIgnoreCase);
        foreach(var builtInProfile in _builtInProfileTemplateService.BuiltInProfiles)
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

        while(true)
        {
            RenderRunSelectionScreen(profileChoices, selectedIndex, mountMode, currentWorkspacePath, selectedResourceDirectories);
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
                    string? inputPath = PromptForResourceDirectory(currentWorkspacePath, selectedResourceDirectories);
                    if(inputPath is null)
                    {
                        break;
                    }

                    if(!TryNormalizeResourceDirectory(inputPath, out string normalizedPath, out _))
                    {
                        break;
                    }

                    if(!seenResourceDirectories.Add(normalizedPath))
                    {
                        break;
                    }

                    selectedResourceDirectories.Add(normalizedPath);
                    break;
                case ConsoleKey.Backspace:
                case ConsoleKey.D:
                    if(selectedResourceDirectories.Count == 0)
                    {
                        break;
                    }

                    string removedDirectory = selectedResourceDirectories[^1];
                    selectedResourceDirectories.RemoveAt(selectedResourceDirectories.Count - 1);
                    seenResourceDirectories.Remove(removedDirectory);
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
        List<string> selectedResourceDirectories)
    {
        AnsiConsole.Clear();
        AppIO.WriteHeader("Run Setup", "Choose profile, mount mode, and optional resource directories.");

        string mountModeLabel = mountMode switch
        {
            WorkspaceMountMode.ReadOnly => "[gold1]Read-only mount[/]",
            WorkspaceMountMode.None => "[yellow]Mount disabled[/]",
            _ => "[deepskyblue1]Read-write mount[/]"
        };

        var profileTable = new Table()
            .Border(TableBorder.None)
            .Expand();
        profileTable.AddColumn(new TableColumn(""));
        for(int i = 0; i < profileChoices.Count; i++)
        {
            var choice = profileChoices[i];
            bool isSelected = i == selectedIndex;
            string cursor = isSelected ? "[deepskyblue1]>[/]" : " ";
            string escapedName = Markup.Escape(choice.Name);
            string defaultTag = choice.IsDefault ? " [grey](default)[/]" : "";
            string label = isSelected
                ? $"[bold deepskyblue1]{escapedName}[/]{defaultTag}"
                : $"{escapedName}{defaultTag}";

            profileTable.AddRow($"{cursor} {label}");
        }

        var profilePanel = new Panel(profileTable)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("[bold]Profile[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0)
        };

        var runtimeGrid = new Grid();
        runtimeGrid.AddColumn(new GridColumn().Width(12));
        runtimeGrid.AddColumn();
        runtimeGrid.AddRow("[grey]Mode[/]", mountModeLabel);
        runtimeGrid.AddRow("[grey]Workspace[/]", $"[deepskyblue1]{Markup.Escape(currentWorkspacePath)}[/]");

        var runtimePanel = new Panel(runtimeGrid)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("[bold]Runtime[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0)
        };

        var resourceTable = new Table()
            .Border(TableBorder.None)
            .Expand();
        resourceTable.AddColumn(new TableColumn(""));
        if(selectedResourceDirectories.Count == 0)
        {
            resourceTable.AddRow("[grey](none selected)[/]");
        }
        else
        {
            foreach(string resourceDirectory in selectedResourceDirectories)
            {
                resourceTable.AddRow($"[deepskyblue1]+[/] {Markup.Escape(resourceDirectory)}");
            }
        }

        var resourcesPanel = new Panel(resourceTable)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("[bold]Read-only Resource Dirs[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0)
        };

        var rightColumn = new Rows(runtimePanel, resourcesPanel);
        AnsiConsole.Write(new Columns(profilePanel, rightColumn)
        {
            Expand = true
        });

        AnsiConsole.Write(CreateKeyHelpPanel(
            ("Up/Down", "Select profile"),
            ("Space", "Toggle mount mode"),
            ("R", "Add resource directory"),
            ("Backspace / D", "Remove last directory"),
            ("Enter", "Continue"),
            ("Esc", "Cancel")));
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

    private static string? PromptForResourceDirectory(string workspacePath, List<string> selectedResourceDirectories)
    {
        string startDirectory = selectedResourceDirectories.Count > 0
            ? selectedResourceDirectories[^1]
            : Directory.GetParent(workspacePath)?.FullName ?? workspacePath;

        if(!Directory.Exists(startDirectory))
        {
            startDirectory = Directory.GetCurrentDirectory();
        }

        return BrowseForResourceDirectory(startDirectory);
    }

    private static string? BrowseForResourceDirectory(string initialDirectory)
    {
        string currentDirectory;
        try
        {
            currentDirectory = Path.GetFullPath(initialDirectory);
        }
        catch(Exception)
        {
            currentDirectory = Directory.GetCurrentDirectory();
        }

        int selectedIndex = 0;
        bool selectingDrive = false;

        while(true)
        {
            List<ExplorerEntry> entries = [];
            if(selectingDrive)
            {
                var drives = GetAvailableDriveRoots();
                foreach(string drive in drives)
                {
                    entries.Add(new ExplorerEntry(drive, ExplorerEntryType.Drive, drive));
                }
            }
            else
            {
                var childDirectories = GetChildDirectories(currentDirectory);
                if(Directory.GetParent(currentDirectory) is not null)
                {
                    entries.Add(new ExplorerEntry(".. (parent)", ExplorerEntryType.Parent));
                }
                else if(OperatingSystem.IsWindows())
                {
                    entries.Add(new ExplorerEntry(".. (drives)", ExplorerEntryType.Parent));
                }

                foreach(string childDirectory in childDirectories)
                {
                    entries.Add(new ExplorerEntry(Path.GetFileName(childDirectory), ExplorerEntryType.Child, childDirectory));
                }
            }

            if(selectedIndex >= entries.Count)
            {
                selectedIndex = entries.Count - 1;
            }

            if(selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            RenderResourceDirectoryExplorer(currentDirectory, selectingDrive, entries, selectedIndex);

            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
            if(keyInfo is null)
            {
                continue;
            }

            switch(keyInfo.Value.Key)
            {
                case ConsoleKey.UpArrow:
                    if(entries.Count > 0)
                    {
                        selectedIndex = selectedIndex <= 0 ? entries.Count - 1 : selectedIndex - 1;
                    }

                    break;
                case ConsoleKey.DownArrow:
                    if(entries.Count > 0)
                    {
                        selectedIndex = selectedIndex >= entries.Count - 1 ? 0 : selectedIndex + 1;
                    }

                    break;
                case ConsoleKey.Backspace:
                case ConsoleKey.LeftArrow:
                    if(selectingDrive)
                    {
                        break;
                    }

                    if(TryMoveToParentDirectory(currentDirectory, out string? parentDirectory))
                    {
                        currentDirectory = parentDirectory;
                        selectedIndex = 0;
                    }
                    else if(OperatingSystem.IsWindows())
                    {
                        selectingDrive = true;
                        selectedIndex = 0;
                    }

                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.RightArrow:
                    if(selectingDrive)
                    {
                        if(entries.Count == 0)
                        {
                            break;
                        }

                        var selectedDriveEntry = entries[selectedIndex];
                        if(selectedDriveEntry.Path is not null)
                        {
                            currentDirectory = selectedDriveEntry.Path;
                            selectingDrive = false;
                            selectedIndex = 0;
                        }

                        break;
                    }

                    if(entries.Count == 0)
                    {
                        return currentDirectory;
                    }

                    var selectedEntry = entries[selectedIndex];
                    if(selectedEntry.EntryType is ExplorerEntryType.Parent)
                    {
                        if(TryMoveToParentDirectory(currentDirectory, out string? nextParentDirectory))
                        {
                            currentDirectory = nextParentDirectory;
                            selectedIndex = 0;
                        }
                        else if(OperatingSystem.IsWindows())
                        {
                            selectingDrive = true;
                            selectedIndex = 0;
                        }
                    }
                    else if(selectedEntry.Path is not null)
                    {
                        currentDirectory = selectedEntry.Path;
                        selectedIndex = 0;
                    }

                    break;
                case ConsoleKey.A:
                    if(selectingDrive)
                    {
                        break;
                    }

                    return currentDirectory;
                case ConsoleKey.Escape:
                    AnsiConsole.Clear();
                    return null;
            }
        }
    }

    private static void RenderResourceDirectoryExplorer(
        string currentDirectory,
        bool selectingDrive,
        IReadOnlyList<ExplorerEntry> entries,
        int selectedIndex)
    {
        AnsiConsole.Clear();
        AppIO.WriteHeader("Resource Browser", "Pick additional read-only mounts.");

        string locationLine = selectingDrive
            ? "[deepskyblue1](drive selection)[/]"
            : $"[deepskyblue1]{Markup.Escape(currentDirectory)}[/]";
        AnsiConsole.Write(new Panel(locationLine)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("[bold]Location[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0)
        });

        if(selectingDrive)
        {
            AnsiConsole.MarkupLine("[grey]Choose a drive, then browse and press A to add the current folder.[/]");
        }

        var entryTable = new Table()
            .Border(TableBorder.None)
            .Expand();
        entryTable.AddColumn(new TableColumn(""));
        if(entries.Count == 0)
        {
            entryTable.AddRow("[grey](no subdirectories)[/]");
        }
        else
        {
            for(int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                bool isSelected = i == selectedIndex;
                string cursor = isSelected ? "[deepskyblue1]>[/]" : " ";
                string label = entry.EntryType switch
                {
                    ExplorerEntryType.Parent => $"[grey]..[/] [silver]{Markup.Escape(entry.Label)}[/]",
                    ExplorerEntryType.Drive => $"[deepskyblue1]{Markup.Escape(entry.Label)}[/] [grey](drive)[/]",
                    _ => Markup.Escape(entry.Label)
                };
                if(isSelected)
                {
                    label = $"[bold deepskyblue1]{label}[/]";
                }

                entryTable.AddRow($"{cursor} {label}");
            }
        }

        AnsiConsole.Write(new Panel(entryTable)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("[bold]Directories[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0)
        });

        AnsiConsole.Write(CreateKeyHelpPanel(
            ("Up/Down", "Navigate"),
            ("Enter / Right", "Open selected"),
            ("Left / Backspace", "Go up"),
            ("A", "Add current directory"),
            ("Esc", "Cancel")));
    }

    private static Panel CreateKeyHelpPanel(params (string Key, string Action)[] shortcuts)
    {
        var keyGrid = new Grid();
        keyGrid.AddColumn(new GridColumn().NoWrap());
        keyGrid.AddColumn();

        foreach(var (key, action) in shortcuts)
        {
            keyGrid.AddRow($"[deepskyblue1]{Markup.Escape(key)}[/]", $"[grey]{Markup.Escape(action)}[/]");
        }

        return new Panel(keyGrid)
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("[bold]Controls[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0)
        };
    }

    private static List<string> GetChildDirectories(string directory)
    {
        try
        {
            var children = Directory
                .GetDirectories(directory)
                .OrderBy(path => path, GetHostPathComparer())
                .ToList();
            return children;
        }
        catch(UnauthorizedAccessException)
        {
            return [];
        }
        catch(IOException)
        {
            return [];
        }
    }

    private static List<string> GetAvailableDriveRoots()
    {
        if(!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            var drives = DriveInfo
                .GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => Path.GetFullPath(drive.RootDirectory.FullName))
                .OrderBy(path => path, GetHostPathComparer())
                .ToList();
            return drives;
        }
        catch(IOException)
        {
            return [];
        }
        catch(UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryMoveToParentDirectory(string directory, out string parentDirectory)
    {
        var parent = Directory.GetParent(directory);
        if(parent is null)
        {
            parentDirectory = directory;
            return false;
        }

        parentDirectory = parent.FullName;
        return true;
    }

    private static bool TryNormalizeResourceDirectory(string requestedDirectory, out string normalizedPath, out string errorMessage)
    {
        if(String.IsNullOrWhiteSpace(requestedDirectory))
        {
            normalizedPath = "";
            errorMessage = "--resource-dir cannot be empty.";
            return false;
        }

        normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory));
        if(!Directory.Exists(normalizedPath))
        {
            errorMessage = $"Resource directory not found: '{normalizedPath}'.";
            return false;
        }

        errorMessage = "";
        return true;
    }

    private static StringComparer GetHostPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record RunSelection(string ProfileName, WorkspaceMountMode MountMode, IReadOnlyList<string> ResourceDirectories);
    private sealed record ProfileChoice(string Name, bool IsDefault);
    private sealed record ExplorerEntry(string Label, ExplorerEntryType EntryType, string? Path = null);

    private enum ExplorerEntryType
    {
        Parent,
        Drive,
        Child
    }
}
