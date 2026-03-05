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
                    string? inputPath = PromptForResourceDirectory(currentWorkspacePath, selectedResourceDirectories);
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

        AnsiConsole.MarkupLine("[grey](Use [blue]<up>/<down>[/] to select profile, [blue]<space>[/] to toggle mount mode, [blue]r[/] to browse/add resource dir, [blue]<backspace>[/] or [blue]d[/] to remove last, [green]<enter>[/] to continue, [red]<esc>[/] to cancel)[/]");
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

    private static string? PromptForResourceDirectory(string workspacePath, IReadOnlyList<string> selectedResourceDirectories)
    {
        string startDirectory = workspacePath;
        if(selectedResourceDirectories.Count > 0)
        {
            startDirectory = selectedResourceDirectories[^1];
        }

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

        string? statusMessage = null;
        int selectedIndex = 0;
        bool selectingDrive = false;

        while(true)
        {
            List<ExplorerEntry> entries = [];
            string? loadError;

            if(selectingDrive)
            {
                List<string> drives = GetAvailableDriveRoots(out loadError);
                foreach(string drive in drives)
                {
                    entries.Add(new ExplorerEntry(drive, ExplorerEntryType.Drive, drive));
                }
            }
            else
            {
                List<string> childDirectories = GetChildDirectories(currentDirectory, out loadError);
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

            RenderResourceDirectoryExplorer(currentDirectory, selectingDrive, entries, selectedIndex, statusMessage ?? loadError);
            statusMessage = null;

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
                        statusMessage = "Select a drive to continue browsing.";
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
                    else
                    {
                        statusMessage = "No parent directory available.";
                    }

                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.RightArrow:
                    if(selectingDrive)
                    {
                        if(entries.Count == 0)
                        {
                            statusMessage = "No drives are available.";
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
                        statusMessage = "Select a drive first.";
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
        int selectedIndex,
        string? statusMessage)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("Browse resource directory");
        if(selectingDrive)
        {
            AnsiConsole.MarkupLine("[grey]Current:[/] [deepskyblue1](drive selection)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[grey]Current:[/] {Markup.Escape(currentDirectory)}");
        }

        if(!String.IsNullOrWhiteSpace(statusMessage))
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(statusMessage)}[/]");
        }

        AnsiConsole.MarkupLine("[grey](Use [blue]<up>/<down>[/] to navigate, [blue]<enter>[/] or [blue]<right>[/] to open, [blue]<left>[/] or [blue]<backspace>[/] for parent/drive selection, [green]a[/] to add current dir, [red]<esc>[/] to cancel)[/]");
        AnsiConsole.WriteLine();

        if(entries.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey](no subdirectories)[/]");
            return;
        }

        for(int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool isSelected = i == selectedIndex;
            string cursor = isSelected ? "[green]>[/]" : " ";
            string label = entry.EntryType switch
            {
                ExplorerEntryType.Parent => Markup.Escape(entry.Label),
                ExplorerEntryType.Drive => Markup.Escape($"{entry.Label} (drive)"),
                _ => Markup.Escape(entry.Label)
            };

            if(isSelected)
            {
                label = $"[deepskyblue1]{label}[/]";
            }

            AnsiConsole.MarkupLine($"{cursor} {label}");
        }
    }

    private static List<string> GetChildDirectories(string directory, out string? errorMessage)
    {
        try
        {
            var children = Directory
                .GetDirectories(directory)
                .OrderBy(path => path, GetHostPathComparer())
                .ToList();
            errorMessage = null;
            return children;
        }
        catch(Exception ex) when(ex is UnauthorizedAccessException or IOException)
        {
            errorMessage = $"Cannot list subdirectories: {ex.Message}";
            return [];
        }
    }

    private static List<string> GetAvailableDriveRoots(out string? errorMessage)
    {
        if(!OperatingSystem.IsWindows())
        {
            errorMessage = "Drive selection is only available on Windows.";
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
            errorMessage = drives.Count == 0 ? "No ready drives were found." : null;
            return drives;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            errorMessage = $"Cannot list drives: {ex.Message}";
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
    private sealed record ExplorerEntry(string Label, ExplorerEntryType EntryType, string? Path = null);

    private enum ExplorerEntryType
    {
        Parent,
        Drive,
        Child
    }
}
