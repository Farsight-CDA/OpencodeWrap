using OpencodeWrap.Services.Runtime.Infrastructure;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.CommandLine;
using System.Text;

namespace OpencodeWrap.Cli.Run;

internal sealed class RunCliCommand : Command
{
    private readonly OpencodeLauncherService _launcherService;
    private readonly ProfileService _profileService;
    private readonly BuiltInProfileTemplateService _builtInProfileTemplateService;
    private readonly DockerHostService _dockerHostService;
    private readonly Option<bool> _verboseOption;

    public RunCliCommand(OpencodeLauncherService launcherService, ProfileService profileService, BuiltInProfileTemplateService builtInProfileTemplateService, DockerHostService dockerHostService)
        : base("run", "Run opencode with a profile selected from the interactive setup menu.")
    {
        _launcherService = launcherService;
        _profileService = profileService;
        _builtInProfileTemplateService = builtInProfileTemplateService;
        _dockerHostService = dockerHostService;
        _verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Show deferred debug session logs after the interactive session exits."
        };

        Add(_verboseOption);

        SetAction(async parseResult =>
        {
            bool verbose = parseResult.GetValue(_verboseOption);
            var selection = await PromptForRunSelectionAsync();
            return selection is null
                ? 1
                : await _launcherService.ExecuteAsync([], requestedProfileName: selection.ProfileName, includeProfileConfig: true, workspaceMountMode: selection.MountMode, extraReadonlyMountDirs: selection.ResourceDirectories, dockerNetworkMode: ResolveDockerNetworkModeArgument(selection.NetworkMode), dockerNetworks: selection.NetworkNames, verboseSessionLogs: verbose);
        });
    }

    private async Task<RunSelection?> PromptForRunSelectionAsync()
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
            AppIO.WriteError("Interactive profile selection is unavailable in this terminal. Run `ocw run` from an interactive shell.");
            return null;
        }

        var (networkListSuccess, availableNetworkNames) = await _dockerHostService.TryListNetworkNamesAsync();
        if(!networkListSuccess)
        {
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

        var mountMode = WorkspaceMountMode.ReadWrite;
        var selectedResourceDirectories = new List<string>();
        var seenResourceDirectories = new HashSet<string>(GetHostPathComparer());

        var selectedTab = RunSelectionTab.Profile;
        int selectedResourceIndex = selectedResourceDirectories.Count > 0 ? 1 : 0;
        int selectedNetworkIndex = 0;
        var selectedNetworkMode = DockerNetworkMode.Bridge;
        var activeNetworkNames = new HashSet<string>(StringComparer.Ordinal);
        bool showingControls = false;

        while(true)
        {
            RenderRunSelectionScreen(profileChoices, selectedIndex, selectedTab, mountMode, currentWorkspacePath, selectedResourceDirectories, selectedResourceIndex, availableNetworkNames, selectedNetworkIndex, selectedNetworkMode, activeNetworkNames, showingControls);
            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
            if(keyInfo is null)
            {
                continue;
            }

            if(showingControls)
            {
                if(!IsHelpKey(keyInfo.Value))
                {
                    showingControls = false;
                }

                continue;
            }

            if(IsHelpKey(keyInfo.Value))
            {
                showingControls = true;
                continue;
            }

            switch(keyInfo.Value.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.W:
                    switch(selectedTab)
                    {
                        case RunSelectionTab.Profile:
                            selectedIndex = selectedIndex <= 0 ? profileChoices.Count - 1 : selectedIndex - 1;
                            break;
                        case RunSelectionTab.Resources:
                            int resourceEntryCount = selectedResourceDirectories.Count + 1;
                            selectedResourceIndex = selectedResourceIndex <= 0 ? resourceEntryCount - 1 : selectedResourceIndex - 1;
                            break;
                        case RunSelectionTab.Networks:
                            int networkEntryCount = GetNetworkEntryCount(availableNetworkNames, selectedNetworkMode);
                            selectedNetworkIndex = selectedNetworkIndex <= 0 ? networkEntryCount - 1 : selectedNetworkIndex - 1;
                            break;
                    }

                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.S:
                    switch(selectedTab)
                    {
                        case RunSelectionTab.Profile:
                            selectedIndex = selectedIndex >= profileChoices.Count - 1 ? 0 : selectedIndex + 1;
                            break;
                        case RunSelectionTab.Resources:
                            int resourceEntryCount = selectedResourceDirectories.Count + 1;
                            selectedResourceIndex = selectedResourceIndex >= resourceEntryCount - 1 ? 0 : selectedResourceIndex + 1;
                            break;
                        case RunSelectionTab.Networks:
                            int networkEntryCount = GetNetworkEntryCount(availableNetworkNames, selectedNetworkMode);
                            selectedNetworkIndex = selectedNetworkIndex >= networkEntryCount - 1 ? 0 : selectedNetworkIndex + 1;
                            break;
                    }

                    break;
                case ConsoleKey.LeftArrow:
                case ConsoleKey.A:
                    selectedTab = CycleInteractiveTab(selectedTab, movingRight: false);
                    break;
                case ConsoleKey.RightArrow:
                case ConsoleKey.D:
                    selectedTab = CycleInteractiveTab(selectedTab, movingRight: true);
                    break;
                case ConsoleKey.M:
                    mountMode = CycleInteractiveMountMode(mountMode);
                    break;
                case ConsoleKey.Spacebar:
                    if(selectedTab is RunSelectionTab.Networks)
                    {
                        if(selectedNetworkIndex == 0)
                        {
                            selectedNetworkMode = CycleDockerNetworkMode(selectedNetworkMode);
                            if(!DoesNetworkModeSupportAdditionalNetworks(selectedNetworkMode))
                            {
                                activeNetworkNames.Clear();
                                selectedNetworkIndex = 0;
                            }

                            break;
                        }

                        if(!DoesNetworkModeSupportAdditionalNetworks(selectedNetworkMode) || availableNetworkNames.Count == 0)
                        {
                            break;
                        }

                        string selectedNetwork = availableNetworkNames[selectedNetworkIndex - 1];
                        if(!activeNetworkNames.Add(selectedNetwork))
                        {
                            activeNetworkNames.Remove(selectedNetwork);
                        }

                        break;
                    }

                    if(selectedTab is RunSelectionTab.Resources && selectedResourceIndex == 0)
                    {
                        string? inputPath = PromptForResourceDirectory(currentWorkspacePath, selectedResourceDirectories);
                        if(inputPath is null)
                        {
                            break;
                        }

                        if(!TryNormalizeResourceDirectory(inputPath, out string normalizedPath))
                        {
                            break;
                        }

                        if(!seenResourceDirectories.Add(normalizedPath))
                        {
                            break;
                        }

                        selectedResourceDirectories.Add(normalizedPath);
                        selectedResourceIndex = selectedResourceDirectories.Count;
                    }

                    break;
                case ConsoleKey.Backspace:
                case ConsoleKey.Delete:
                    if(selectedTab is not RunSelectionTab.Resources || selectedResourceIndex == 0)
                    {
                        break;
                    }

                    int resourceDirectoryIndex = selectedResourceIndex - 1;
                    string removedDirectory = selectedResourceDirectories[resourceDirectoryIndex];
                    selectedResourceDirectories.RemoveAt(resourceDirectoryIndex);
                    seenResourceDirectories.Remove(removedDirectory);
                    if(selectedResourceIndex > selectedResourceDirectories.Count)
                    {
                        selectedResourceIndex = selectedResourceDirectories.Count;
                    }

                    break;
                case ConsoleKey.Enter:
                    AnsiConsole.Clear();
                    return new RunSelection(
                        profileChoices[selectedIndex].Name,
                        mountMode,
                        selectedResourceDirectories,
                        selectedNetworkMode,
                        [.. availableNetworkNames.Where(activeNetworkNames.Contains)]);
                case ConsoleKey.Escape:
                    AnsiConsole.Clear();
                    return null;
            }
        }
    }

    private static WorkspaceMountMode CycleInteractiveMountMode(WorkspaceMountMode mountMode) => mountMode switch
    {
        WorkspaceMountMode.ReadWrite => WorkspaceMountMode.None,
        _ => WorkspaceMountMode.ReadWrite
    };

    private static void RenderRunSelectionScreen(
        IReadOnlyList<ProfileChoice> profileChoices,
        int selectedIndex,
        RunSelectionTab selectedTab,
        WorkspaceMountMode mountMode,
        string currentWorkspacePath,
        List<string> selectedResourceDirectories,
        int selectedResourceIndex,
        IReadOnlyList<string> availableNetworkNames,
        int selectedNetworkIndex,
        DockerNetworkMode selectedNetworkMode,
        HashSet<string> activeNetworkNames,
        bool showingControls)
    {
        if(showingControls)
        {
            RenderControlsScreen(
                "Run Setup Controls",
                "Press any key to return.",
                ("Left / Right", "Switch tabs"),
                ("A / D", "Switch tabs"),
                ("Up / Down", "Move selection"),
                ("W / S", "Move selection"),
                ("M", "Toggle workspace mount"),
                ("Space", "Add resource (resource tab)"),
                ("Space", "Cycle mode / toggle network"),
                ("Backspace / Delete", "Remove selected resource"),
                ("Enter", "Start session"),
                ("Esc", "Cancel"),
                ("?", "Show controls"));
            return;
        }

        AnsiConsole.Clear();
        AppIO.WriteHeader("Run Setup");

        string mountModeLabel = mountMode switch
        {
            WorkspaceMountMode.None => "[yellow]✗ Mount disabled[/]",
            _ => "[green]✓ Read-write mount[/]"
        };

        var mountGrid = new Grid();
        mountGrid.AddColumn(new GridColumn().Width(12));
        mountGrid.AddColumn();
        mountGrid.AddRow("[grey58]Mode[/]", mountModeLabel);
        mountGrid.AddRow("[grey58]Workspace[/]", $"[white]{Markup.Escape(currentWorkspacePath)}[/]");

        var mountPanel = new Panel(mountGrid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DodgerBlue1),
            Header = new PanelHeader("[bold dodgerblue1]Primary Mount[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0)
        };
        AnsiConsole.Write(mountPanel);

        AnsiConsole.Write(CreateTabStrip(selectedTab, selectedResourceDirectories.Count, activeNetworkNames.Count));
        AnsiConsole.WriteLine();

        var activeContent = selectedTab switch
        {
            RunSelectionTab.Resources => CreateResourceSelectionContent(selectedResourceDirectories, selectedResourceIndex),
            RunSelectionTab.Networks => CreateNetworkSelectionContent(availableNetworkNames, selectedNetworkIndex, selectedNetworkMode, activeNetworkNames),
            _ => CreateProfileSelectionContent(profileChoices, selectedIndex)
        };

        // Wrap content in a panel for consistent styling
        var contentPanel = new Panel(activeContent)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey39),
            Padding = new Padding(2, 1),
            Height = 16
        };
        AnsiConsole.Write(contentPanel);

        // Footer with key hints
        AnsiConsole.WriteLine();
        var footerGrid = new Grid();
        footerGrid.AddColumn(new GridColumn().Width(20));
        footerGrid.AddColumn(new GridColumn().Width(20));
        footerGrid.AddColumn(new GridColumn().Width(20));
        footerGrid.AddColumn(new GridColumn());

        string tabHint = selectedTab switch
        {
            RunSelectionTab.Profile => "[grey]Enter[/] [dodgerblue1]start[/] | [grey]↑↓[/] [dodgerblue1]navigate[/] | [grey]←→[/] [dodgerblue1]tabs[/]",
            RunSelectionTab.Resources => "[grey]Enter[/] [dodgerblue1]start[/] | [grey]Space[/] [dodgerblue1]add[/] | [grey]Del[/] [dodgerblue1]remove[/] | [grey]←→[/] [dodgerblue1]tabs[/]",
            RunSelectionTab.Networks => "[grey]Enter[/] [dodgerblue1]start[/] | [grey]Space[/] [dodgerblue1]toggle[/] | [grey]←→[/] [dodgerblue1]tabs[/]",
            _ => ""
        };

        footerGrid.AddRow(
            "[grey]ESC[/] [dodgerblue1]cancel[/]",
            "[grey]?[/] [dodgerblue1]help[/]",
            "[grey]M[/] [dodgerblue1]mount mode[/]",
            tabHint
        );

        var footerPanel = new Panel(footerGrid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey23),
            Padding = new Padding(1, 0)
        };
        AnsiConsole.Write(footerPanel);
    }

    private static Panel CreateTabStrip(RunSelectionTab selectedTab, int resourceCount, int activeNetworkCount)
    {
        var tabGrid = new Grid();
        tabGrid.AddColumn();
        tabGrid.AddColumn();
        tabGrid.AddColumn();

        string profileTab = selectedTab is RunSelectionTab.Profile
            ? "[white on dodgerblue1] 👤 Profile Selection [/]"
            : "[grey70 on grey19] 👤 Profile Selection [/]";

        string resourceLabel = resourceCount == 0
            ? "📁 Resource Directories"
            : $"📁 Resource Directories ([green]{resourceCount}[/])";
        string resourceTab = selectedTab is RunSelectionTab.Resources
            ? $"[white on dodgerblue1] {resourceLabel} [/]"
            : $"[grey70 on grey19] {resourceLabel} [/]";

        string networkLabel = activeNetworkCount == 0
            ? "🌐 Docker Networks"
            : $"🌐 Docker Networks ([green]{activeNetworkCount}[/])";
        string networkTab = selectedTab is RunSelectionTab.Networks
            ? $"[white on dodgerblue1] {networkLabel} [/]"
            : $"[grey70 on grey19] {networkLabel} [/]";

        tabGrid.AddRow(profileTab, resourceTab, networkTab);

        return new Panel(tabGrid)
        {
            Border = BoxBorder.None,
            Padding = new Padding(0)
        };
    }

    private static Markup CreateProfileSelectionContent(IReadOnlyList<ProfileChoice> profileChoices, int selectedIndex)
    {
        var content = new StringBuilder();

        if(profileChoices.Count == 0)
        {
            content.AppendLine("[grey](no profiles available)[/]");
            return new Markup(content.ToString());
        }

        content.AppendLine("[grey58]Select a profile to run with opencode:[/]");
        content.AppendLine();

        for(int i = 0; i < profileChoices.Count; i++)
        {
            var choice = profileChoices[i];
            bool isSelected = i == selectedIndex;
            string escapedName = Markup.Escape(choice.Name);

            if(isSelected)
            {
                content.Append($"[dodgerblue1]▶[/] [bold white]{escapedName}[/]");
                content.AppendLine(" [grey]<--[/]");
            }
            else
            {
                content.AppendLine($"  [grey70]{escapedName}[/]");
            }
        }

        return new Markup(content.ToString());
    }

    private static Markup CreateResourceSelectionContent(List<string> selectedResourceDirectories, int selectedResourceIndex)
    {
        var content = new StringBuilder();
        content.AppendLine("[grey58]Add read-only resource directories to mount in the container:[/]");
        content.AppendLine();

        // Add button
        if(selectedResourceIndex == 0)
        {
            content.AppendLine("[green]▶[/] [bold white]+ Add resource directory[/] [grey]<--[/]");
        }
        else
        {
            content.AppendLine("  [grey70]+ Add resource directory[/]");
        }

        // Visual separator between add button and actual entries
        content.AppendLine("  [grey50]─────────────────────────────────────[/]");

        if(selectedResourceDirectories.Count == 0)
        {
            content.AppendLine("[grey]  (no directories selected)[/]");
        }
        else
        {
            content.AppendLine();
            for(int i = 0; i < selectedResourceDirectories.Count; i++)
            {
                bool isSelected = selectedResourceIndex == i + 1;
                string path = Markup.Escape(selectedResourceDirectories[i]);

                if(isSelected)
                {
                    content.AppendLine($"[red]▶[/] [bold white]{path}[/] [grey](press Del to remove)[/]");
                }
                else
                {
                    content.AppendLine($"  [grey70]{path}[/]");
                }
            }
        }

        return new Markup(content.ToString());
    }

    private static Markup CreateNetworkSelectionContent(IReadOnlyList<string> availableNetworkNames, int selectedNetworkIndex, DockerNetworkMode selectedNetworkMode, HashSet<string> activeNetworkNames)
    {
        var content = new StringBuilder();
        content.AppendLine("[grey58]Configure Docker networking for the container:[/]");
        content.AppendLine();

        // Network mode
        string modeLabel = GetDockerNetworkModeLabel(selectedNetworkMode);
        string modeDisplay = selectedNetworkMode switch
        {
            DockerNetworkMode.Host => "[yellow]host[/]",
            _ => "[green]bridge[/]"
        };

        if(selectedNetworkIndex == 0)
        {
            content.AppendLine($"[dodgerblue1]▶[/] [bold white]Mode:[/] {modeDisplay} [grey](press Space to cycle)[/]");
        }
        else
        {
            content.AppendLine($"  [grey70]Mode:[/] {modeDisplay}");
        }

        content.AppendLine();

        if(!DoesNetworkModeSupportAdditionalNetworks(selectedNetworkMode))
        {
            content.AppendLine($"[grey]  Additional networks are disabled in {modeLabel} mode.[/]");
            return new Markup(content.ToString());
        }

        if(availableNetworkNames.Count == 0)
        {
            content.AppendLine("[grey]  (no additional Docker networks found)[/]");
            return new Markup(content.ToString());
        }

        content.AppendLine("[grey58]Additional networks (Space to toggle):[/]");
        for(int i = 0; i < availableNetworkNames.Count; i++)
        {
            string networkName = availableNetworkNames[i];
            bool isActive = activeNetworkNames.Contains(networkName);
            bool isSelected = selectedNetworkIndex == i + 1;
            string checkbox = isActive ? "[green]☑[/]" : "[grey]☐[/]";

            if(isSelected)
            {
                content.AppendLine($"[dodgerblue1]▶[/] {checkbox} [bold white]{Markup.Escape(networkName)}[/]");
            }
            else
            {
                content.AppendLine($"  {checkbox} [grey70]{Markup.Escape(networkName)}[/]");
            }
        }

        return new Markup(content.ToString());
    }

    private static RunSelectionTab CycleInteractiveTab(RunSelectionTab selectedTab, bool movingRight) => (selectedTab, movingRight) switch
    {
        (RunSelectionTab.Profile, true) => RunSelectionTab.Resources,
        (RunSelectionTab.Resources, true) => RunSelectionTab.Networks,
        (RunSelectionTab.Networks, true) => RunSelectionTab.Profile,
        (RunSelectionTab.Profile, false) => RunSelectionTab.Networks,
        (RunSelectionTab.Resources, false) => RunSelectionTab.Profile,
        _ => RunSelectionTab.Resources
    };

    private static DockerNetworkMode CycleDockerNetworkMode(DockerNetworkMode networkMode) => networkMode switch
    {
        DockerNetworkMode.Bridge => DockerNetworkMode.Host,
        _ => DockerNetworkMode.Bridge
    };

    private static bool DoesNetworkModeSupportAdditionalNetworks(DockerNetworkMode networkMode) => networkMode is DockerNetworkMode.Bridge;

    private static string GetDockerNetworkModeLabel(DockerNetworkMode networkMode) => networkMode switch
    {
        DockerNetworkMode.Host => "host",
        _ => "bridge"
    };

    private static string? ResolveDockerNetworkModeArgument(DockerNetworkMode networkMode) => networkMode switch
    {
        DockerNetworkMode.Host => "host",
        _ => null
    };

    private static int GetNetworkEntryCount(IReadOnlyList<string> availableNetworkNames, DockerNetworkMode networkMode)
        => !DoesNetworkModeSupportAdditionalNetworks(networkMode)
            ? 1
            : availableNetworkNames.Count + 1;

    private static bool IsHelpKey(ConsoleKeyInfo keyInfo) => keyInfo.KeyChar == '?';

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
        bool showingControls = false;

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

            RenderResourceDirectoryExplorer(currentDirectory, selectingDrive, entries, selectedIndex, showingControls);

            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
            if(keyInfo is null)
            {
                continue;
            }

            if(showingControls)
            {
                if(!IsHelpKey(keyInfo.Value))
                {
                    showingControls = false;
                }

                continue;
            }

            if(IsHelpKey(keyInfo.Value))
            {
                showingControls = true;
                continue;
            }

            switch(keyInfo.Value.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.W:
                    if(entries.Count > 0)
                    {
                        selectedIndex = selectedIndex <= 0 ? entries.Count - 1 : selectedIndex - 1;
                    }

                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.S:
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
                case ConsoleKey.Spacebar:
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
                        break;
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
                case ConsoleKey.Enter:
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
        int selectedIndex,
        bool showingControls)
    {
        if(showingControls)
        {
            RenderControlsScreen(
                "Resource Browser Controls",
                "Press any key to return.",
                ("Up / Down", "Navigate"),
                ("W / S", "Navigate"),
                ("Space / Right", "Open selected"),
                ("Left / Backspace", "Go up"),
                ("Enter", "Add current directory"),
                ("Esc", "Cancel"),
                ("?", "Show controls"));
            return;
        }

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
            AnsiConsole.MarkupLine("[grey]Choose a drive, then browse and press Enter to add the current folder.[/]");
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
    }

    private static void RenderControlsScreen(string title, string subtitle, params (string Key, string Action)[] shortcuts)
    {
        AnsiConsole.Clear();
        AppIO.WriteHeader(title, subtitle);
        AnsiConsole.Write(CreateKeyHelpPanel(shortcuts));
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

    private static bool TryNormalizeResourceDirectory(string requestedDirectory, out string normalizedPath)
    {
        if(String.IsNullOrWhiteSpace(requestedDirectory))
        {
            normalizedPath = "";
            return false;
        }

        normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory));
        return Directory.Exists(normalizedPath);
    }

    private static StringComparer GetHostPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record RunSelection(string ProfileName, WorkspaceMountMode MountMode, IReadOnlyList<string> ResourceDirectories, DockerNetworkMode NetworkMode, IReadOnlyList<string> NetworkNames);
    private sealed record ProfileChoice(string Name, bool IsDefault);
    private sealed record ExplorerEntry(string Label, ExplorerEntryType EntryType, string? Path = null);

    private enum RunSelectionTab
    {
        Profile,
        Resources,
        Networks
    }

    private enum DockerNetworkMode
    {
        Bridge,
        Host
    }

    private enum ExplorerEntryType
    {
        Parent,
        Drive,
        Child
    }
}
