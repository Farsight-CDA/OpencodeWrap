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
    private readonly RunMenuDefaultsService _runMenuDefaultsService;
    private readonly SessionAddonService _sessionAddonService;
    private readonly Option<bool> _verboseOption;

    public RunCliCommand(OpencodeLauncherService launcherService, ProfileService profileService, BuiltInProfileTemplateService builtInProfileTemplateService, DockerHostService dockerHostService, RunMenuDefaultsService runMenuDefaultsService, SessionAddonService sessionAddonService)
        : base("run", "Resolve the latest OpenCode release, run `opencode serve` in Docker, then launch the selected TUI, web, or desktop client after interactive setup.")
    {
        _launcherService = launcherService;
        _profileService = profileService;
        _builtInProfileTemplateService = builtInProfileTemplateService;
        _dockerHostService = dockerHostService;
        _runMenuDefaultsService = runMenuDefaultsService;
        _sessionAddonService = sessionAddonService;
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
                : await _launcherService.ExecuteAsync([], requestedProfileName: selection.ProfileName, includeProfileConfig: true, runtimeMode: OpencodeRuntimeMode.HostAttachToServe, runUiMode: selection.UiMode, workspaceMountMode: selection.MountMode, extraReadonlyMountDirs: selection.ResourceDirectories, sessionAddons: selection.SessionAddonNames, dockerNetworkMode: selection.NetworkMode, dockerNetworks: selection.NetworkNames, verboseSessionLogs: verbose);
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
            AppIO.WriteError("`ocw run` now asks for a UI mode on every run. Launch it from an interactive shell; scripted UI selection is not available yet.");
            return null;
        }

        if(!_runMenuDefaultsService.TryLoadDefaults(out var runMenuDefaults))
        {
            return null;
        }

        if(!_sessionAddonService.TryLoadCatalog(out var addonCatalog))
        {
            return null;
        }

        var (networkListSuccess, availableNetworkNames) = await _dockerHostService.TryListNetworkNamesAsync();
        if(!networkListSuccess)
        {
            return null;
        }

        var desktopAppStatus = await _dockerHostService.GetOpenCodeDesktopAppStatusAsync();

        string defaultProfileName = profileNames.Contains(runMenuDefaults.DefaultProfileName ?? "")
            ? runMenuDefaults.DefaultProfileName!
            : catalog.DefaultProfileName;

        List<ProfileChoice> profileChoices = [.. profileNames
            .OrderByDescending(name => String.Equals(name, defaultProfileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ProfileChoice(name, String.Equals(name, defaultProfileName, StringComparison.OrdinalIgnoreCase)))];

        string currentWorkspacePath = Path.GetFullPath(Directory.GetCurrentDirectory());
        int selectedIndex = profileChoices.FindIndex(choice => choice.IsDefault);
        if(selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        var mountMode = WorkspaceMountMode.ReadWrite;
        var selectedResourceDirectories = new List<string>();
        var seenResourceDirectories = new HashSet<string>(GetHostPathComparer());
        var defaultResourceDirectories = new HashSet<string>(GetHostPathComparer());

        foreach(string savedDirectory in runMenuDefaults.ResourceDirectories)
        {
            if(!TryNormalizeResourceDirectory(savedDirectory, out string normalizedPath) || !seenResourceDirectories.Add(normalizedPath))
            {
                continue;
            }

            selectedResourceDirectories.Add(normalizedPath);
            defaultResourceDirectories.Add(normalizedPath);
        }

        List<string> availableAddonNames = [.. addonCatalog.Addons.Keys.OrderBy(name => name, GetHostPathComparer())];
        var activeAddonNames = new HashSet<string>(GetHostPathComparer());
        var defaultAddonNames = new HashSet<string>(GetHostPathComparer());
        var savedAddonNames = new HashSet<string>(runMenuDefaults.SessionAddons, GetHostPathComparer());
        foreach(string addonName in availableAddonNames)
        {
            if(!savedAddonNames.Contains(addonName))
            {
                continue;
            }

            activeAddonNames.Add(addonName);
            defaultAddonNames.Add(addonName);
        }

        var selectedTab = RunSelectionTab.Profile;
        var defaultUiMode = runMenuDefaults.DefaultUiMode;
        var uiChoices = BuildUiChoices(desktopAppStatus, defaultUiMode);
        int selectedUiIndex = GetInitialSelectableUiIndex(uiChoices);
        int selectedResourceIndex = selectedResourceDirectories.Count > 0 ? 1 : 0;
        int selectedAddonIndex = 0;
        int selectedNetworkIndex = 0;
        bool hostNetworkAvailable = true;
        DockerNetworkMode? defaultNetworkMode = ParseSavedDockerNetworkMode(runMenuDefaults.DefaultDockerNetworkMode, hostNetworkAvailable);
        var selectedNetworkMode = defaultNetworkMode ?? DockerNetworkMode.Bridge;
        var activeNetworkNames = new HashSet<string>(StringComparer.Ordinal);
        var defaultNetworkNames = new HashSet<string>(StringComparer.Ordinal);
        foreach(string networkName in availableNetworkNames.Where(name => runMenuDefaults.DockerNetworks.Contains(name, StringComparer.Ordinal)))
        {
            defaultNetworkNames.Add(networkName);
        }

        if(DoesNetworkModeSupportAdditionalNetworks(selectedNetworkMode))
        {
            activeNetworkNames.UnionWith(defaultNetworkNames);
        }

        bool showingControls = false;

        while(true)
        {
            RenderRunSelectionScreen(profileChoices, selectedIndex, uiChoices, selectedUiIndex, selectedTab, mountMode, currentWorkspacePath, selectedResourceDirectories, defaultResourceDirectories, selectedResourceIndex, addonCatalog.AddonsRoot, availableAddonNames, defaultAddonNames, selectedAddonIndex, activeAddonNames, availableNetworkNames, defaultNetworkNames, selectedNetworkIndex, selectedNetworkMode, defaultNetworkMode, activeNetworkNames, showingControls, hostNetworkAvailable, showWindowsHostNetworkingHint: _dockerHostService.IsWindows);
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

            if(IsDefaultToggleKey(keyInfo.Value))
            {
                switch(selectedTab)
                {
                    case RunSelectionTab.Profile:
                        string selectedProfileName = profileChoices[selectedIndex].Name;
                        if(TrySaveRunMenuDefaults(selectedProfileName, defaultUiMode, selectedResourceDirectories, defaultResourceDirectories, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
                        {
                            profileChoices = [.. profileChoices.Select(choice => choice with
                            {
                                IsDefault = String.Equals(choice.Name, selectedProfileName, StringComparison.OrdinalIgnoreCase)
                            })];
                        }

                        break;
                    case RunSelectionTab.Ui:
                        var selectedUiMode = uiChoices[selectedUiIndex].Mode;
                        if(TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), selectedUiMode, selectedResourceDirectories, defaultResourceDirectories, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
                        {
                            defaultUiMode = selectedUiMode;
                            uiChoices = [.. uiChoices.Select(choice => choice with
                            {
                                IsDefault = choice.Mode == selectedUiMode
                            })];
                        }

                        break;
                    case RunSelectionTab.Resources:
                        if(selectedResourceIndex > 0)
                        {
                            string selectedResourceDirectory = selectedResourceDirectories[selectedResourceIndex - 1];
                            bool resourceWasDefault = defaultResourceDirectories.Contains(selectedResourceDirectory);
                            if(resourceWasDefault)
                            {
                                defaultResourceDirectories.Remove(selectedResourceDirectory);
                            }
                            else
                            {
                                defaultResourceDirectories.Add(selectedResourceDirectory);
                            }

                            if(!TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), defaultUiMode, selectedResourceDirectories, defaultResourceDirectories, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
                            {
                                if(resourceWasDefault)
                                {
                                    defaultResourceDirectories.Add(selectedResourceDirectory);
                                }
                                else
                                {
                                    defaultResourceDirectories.Remove(selectedResourceDirectory);
                                }
                            }
                        }

                        break;
                    case RunSelectionTab.Addons:
                        if(availableAddonNames.Count > 0)
                        {
                            string selectedAddonName = availableAddonNames[selectedAddonIndex];
                            bool addonWasDefault = defaultAddonNames.Contains(selectedAddonName);
                            bool addonWasActive = activeAddonNames.Contains(selectedAddonName);
                            if(addonWasDefault)
                            {
                                defaultAddonNames.Remove(selectedAddonName);
                            }
                            else
                            {
                                defaultAddonNames.Add(selectedAddonName);
                                activeAddonNames.Add(selectedAddonName);
                            }

                            if(!TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), defaultUiMode, selectedResourceDirectories, defaultResourceDirectories, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
                            {
                                if(addonWasDefault)
                                {
                                    defaultAddonNames.Add(selectedAddonName);
                                }
                                else
                                {
                                    defaultAddonNames.Remove(selectedAddonName);
                                    if(!addonWasActive)
                                    {
                                        activeAddonNames.Remove(selectedAddonName);
                                    }
                                }
                            }
                        }

                        break;
                    case RunSelectionTab.Networks:
                        if(selectedNetworkIndex == 0)
                        {
                            DockerNetworkMode? previousDefaultNetworkMode = defaultNetworkMode;
                            defaultNetworkMode = defaultNetworkMode == selectedNetworkMode
                                ? null
                                : selectedNetworkMode;

                            if(!TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), defaultUiMode, selectedResourceDirectories, defaultResourceDirectories, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
                            {
                                defaultNetworkMode = previousDefaultNetworkMode;
                            }
                        }
                        else if(DoesNetworkModeSupportAdditionalNetworks(selectedNetworkMode) && availableNetworkNames.Count > 0)
                        {
                            string selectedNetworkName = availableNetworkNames[selectedNetworkIndex - 1];
                            bool networkWasDefault = defaultNetworkNames.Contains(selectedNetworkName);
                            bool networkWasActive = activeNetworkNames.Contains(selectedNetworkName);
                            if(networkWasDefault)
                            {
                                defaultNetworkNames.Remove(selectedNetworkName);
                            }
                            else
                            {
                                defaultNetworkNames.Add(selectedNetworkName);
                                activeNetworkNames.Add(selectedNetworkName);
                            }

                            if(!TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), defaultUiMode, selectedResourceDirectories, defaultResourceDirectories, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
                            {
                                if(networkWasDefault)
                                {
                                    defaultNetworkNames.Add(selectedNetworkName);
                                }
                                else
                                {
                                    defaultNetworkNames.Remove(selectedNetworkName);
                                    if(!networkWasActive)
                                    {
                                        activeNetworkNames.Remove(selectedNetworkName);
                                    }
                                }
                            }
                        }

                        break;
                }

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
                        case RunSelectionTab.Ui:
                            selectedUiIndex = MoveUiSelection(uiChoices, selectedUiIndex, movingForward: false);
                            break;
                        case RunSelectionTab.Resources:
                            int resourceEntryCount = selectedResourceDirectories.Count + 1;
                            selectedResourceIndex = selectedResourceIndex <= 0 ? resourceEntryCount - 1 : selectedResourceIndex - 1;
                            break;
                        case RunSelectionTab.Addons:
                            if(availableAddonNames.Count > 0)
                            {
                                selectedAddonIndex = selectedAddonIndex <= 0 ? availableAddonNames.Count - 1 : selectedAddonIndex - 1;
                            }

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
                        case RunSelectionTab.Ui:
                            selectedUiIndex = MoveUiSelection(uiChoices, selectedUiIndex, movingForward: true);
                            break;
                        case RunSelectionTab.Resources:
                            int resourceEntryCount = selectedResourceDirectories.Count + 1;
                            selectedResourceIndex = selectedResourceIndex >= resourceEntryCount - 1 ? 0 : selectedResourceIndex + 1;
                            break;
                        case RunSelectionTab.Addons:
                            if(availableAddonNames.Count > 0)
                            {
                                selectedAddonIndex = selectedAddonIndex >= availableAddonNames.Count - 1 ? 0 : selectedAddonIndex + 1;
                            }

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
                    if(selectedTab is RunSelectionTab.Addons)
                    {
                        if(availableAddonNames.Count == 0)
                        {
                            break;
                        }

                        string selectedAddonName = availableAddonNames[selectedAddonIndex];
                        if(!activeAddonNames.Add(selectedAddonName))
                        {
                            activeAddonNames.Remove(selectedAddonName);
                        }

                        break;
                    }

                    if(selectedTab is RunSelectionTab.Networks)
                    {
                        if(selectedNetworkIndex == 0)
                        {
                            selectedNetworkMode = CycleDockerNetworkMode(selectedNetworkMode, hostNetworkAvailable);
                            if(!DoesNetworkModeSupportAdditionalNetworks(selectedNetworkMode))
                            {
                                activeNetworkNames.Clear();
                                selectedNetworkIndex = 0;
                            }
                            else
                            {
                                activeNetworkNames.Clear();
                                activeNetworkNames.UnionWith(defaultNetworkNames);
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
                    if(defaultResourceDirectories.Remove(removedDirectory))
                    {
                        _ = TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), defaultUiMode, selectedResourceDirectories, defaultResourceDirectories, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames);
                    }

                    if(selectedResourceIndex > selectedResourceDirectories.Count)
                    {
                        selectedResourceIndex = selectedResourceDirectories.Count;
                    }

                    break;
                case ConsoleKey.Enter:
                    if(!uiChoices[selectedUiIndex].IsSelectable)
                    {
                        break;
                    }

                    AnsiConsole.Clear();
                    return new RunSelection(
                        profileChoices[selectedIndex].Name,
                        uiChoices[selectedUiIndex].Mode,
                        mountMode,
                        selectedResourceDirectories,
                        [.. availableAddonNames.Where(activeAddonNames.Contains)],
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
        List<UiChoice> uiChoices,
        int selectedUiIndex,
        RunSelectionTab selectedTab,
        WorkspaceMountMode mountMode,
        string currentWorkspacePath,
        List<string> selectedResourceDirectories,
        HashSet<string> defaultResourceDirectories,
        int selectedResourceIndex,
        string addonsRootPath,
        IReadOnlyList<string> availableAddonNames,
        HashSet<string> defaultAddonNames,
        int selectedAddonIndex,
        HashSet<string> activeAddonNames,
        IReadOnlyList<string> availableNetworkNames,
        HashSet<string> defaultNetworkNames,
        int selectedNetworkIndex,
        DockerNetworkMode selectedNetworkMode,
        DockerNetworkMode? defaultNetworkMode,
        HashSet<string> activeNetworkNames,
        bool showingControls,
        bool hostNetworkAvailable,
        bool showWindowsHostNetworkingHint)
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
                ("Space", "Toggle addon (addons tab)"),
                ("Space", "Cycle mode / toggle network"),
                ("+", "Save default / toggle saved"),
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

        AnsiConsole.Write(CreateTabStrip(selectedTab, selectedResourceDirectories.Count, activeAddonNames.Count, activeNetworkNames.Count, selectedNetworkMode, profileChoices[selectedIndex], uiChoices[selectedUiIndex]));
        AnsiConsole.WriteLine();

        var activeContent = selectedTab switch
        {
            RunSelectionTab.Ui => CreateUiSelectionContent(uiChoices, selectedUiIndex),
            RunSelectionTab.Resources => CreateResourceSelectionContent(selectedResourceDirectories, defaultResourceDirectories, selectedResourceIndex),
            RunSelectionTab.Addons => CreateAddonSelectionContent(addonsRootPath, availableAddonNames, defaultAddonNames, selectedAddonIndex, activeAddonNames),
            RunSelectionTab.Networks => CreateNetworkSelectionContent(availableNetworkNames, defaultNetworkNames, selectedNetworkIndex, selectedNetworkMode, defaultNetworkMode, activeNetworkNames, hostNetworkAvailable, showWindowsHostNetworkingHint),
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
            RunSelectionTab.Profile => "[grey]Enter[/] [dodgerblue1]start[/] | [grey]+[/] [dodgerblue1]default[/] | [grey]↑↓[/] [dodgerblue1]navigate[/] | [grey]←→[/] [dodgerblue1]tabs[/]",
            RunSelectionTab.Ui => "[grey]Enter[/] [dodgerblue1]start[/] | [grey]+[/] [dodgerblue1]default[/] | [grey]↑↓[/] [dodgerblue1]choose ui[/] | [grey]←→[/] [dodgerblue1]tabs[/]",
            RunSelectionTab.Resources => "[grey]Enter[/] [dodgerblue1]start[/] | [grey]Space[/] [dodgerblue1]add[/] | [grey]+[/] [dodgerblue1]default[/] | [grey]Del[/] [dodgerblue1]remove[/]",
            RunSelectionTab.Addons => "[grey]Enter[/] [dodgerblue1]start[/] | [grey]Space[/] [dodgerblue1]toggle[/] | [grey]+[/] [dodgerblue1]default[/] | [grey]←→[/] [dodgerblue1]tabs[/]",
            RunSelectionTab.Networks => "[grey]Enter[/] [dodgerblue1]start[/] | [grey]Space[/] [dodgerblue1]toggle[/] | [grey]+[/] [dodgerblue1]default[/] | [grey]←→[/] [dodgerblue1]tabs[/]",
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

    private static Panel CreateTabStrip(RunSelectionTab selectedTab, int resourceCount, int activeAddonCount, int activeNetworkCount, DockerNetworkMode selectedNetworkMode, ProfileChoice selectedProfileChoice, UiChoice selectedUiChoice)
    {
        var tabGrid = new Grid();
        tabGrid.AddColumn();
        tabGrid.AddColumn();
        tabGrid.AddColumn();
        tabGrid.AddColumn();
        tabGrid.AddColumn();

        string profileLabel = $"👤 Profile: {Markup.Escape(selectedProfileChoice.Name)}";
        string profileTab = selectedTab is RunSelectionTab.Profile
            ? $"[white on dodgerblue1] {profileLabel} [/]"
            : $"[grey70 on grey19] {profileLabel} [/]";

        string uiLabel = selectedUiChoice.Mode switch
        {
            RunUiMode.Web => "UI: Web",
            RunUiMode.Desktop => "UI: Desktop",
            _ => "UI: TUI"
        };
        if(selectedUiChoice.IsUnavailable)
        {
            uiLabel += " ([yellow]unavailable[/])";
        }

        string uiTab = selectedTab is RunSelectionTab.Ui
            ? $"[white on dodgerblue1] {uiLabel} [/]"
            : $"[grey70 on grey19] {uiLabel} [/]";

        string resourceLabel = resourceCount == 0
            ? "📁 Resources"
            : $"📁 Resources ([green]{resourceCount}[/])";
        string resourceTab = selectedTab is RunSelectionTab.Resources
            ? $"[white on dodgerblue1] {resourceLabel} [/]"
            : $"[grey70 on grey19] {resourceLabel} [/]";

        string addonLabel = activeAddonCount == 0
            ? "🧩 Addons"
            : $"🧩 Addons ([green]{activeAddonCount}[/])";
        string addonTab = selectedTab is RunSelectionTab.Addons
            ? $"[white on dodgerblue1] {addonLabel} [/]"
            : $"[grey70 on grey19] {addonLabel} [/]";

        string networkModeLabel = GetDockerNetworkModeLabel(selectedNetworkMode);
        string networkLabel = activeNetworkCount == 0
            ? $"🌐 Networks: {networkModeLabel}"
            : $"🌐 Networks: {networkModeLabel} ([green]{activeNetworkCount}[/])";
        string networkTab = selectedTab is RunSelectionTab.Networks
            ? $"[white on dodgerblue1] {networkLabel} [/]"
            : $"[grey70 on grey19] {networkLabel} [/]";

        tabGrid.AddRow(profileTab, uiTab, resourceTab, addonTab, networkTab);

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
            string defaultMarker = choice.IsDefault ? " [yellow]★[/]" : "";

            if(isSelected)
            {
                content.Append($"[dodgerblue1]▶[/] [bold white]{escapedName}[/]");
                content.Append(defaultMarker);
                content.AppendLine(" [grey]<--[/]");
            }
            else
            {
                content.AppendLine($"  [grey70]{escapedName}[/]{defaultMarker}");
            }
        }

        return new Markup(content.ToString());
    }

    private static Markup CreateUiSelectionContent(List<UiChoice> uiChoices, int selectedUiIndex)
    {
        var content = new StringBuilder();

        for(int i = 0; i < uiChoices.Count; i++)
        {
            var choice = uiChoices[i];
            bool isSelected = i == selectedUiIndex;
            bool isDefault = choice.IsDefault;
            string labelStyle = !choice.IsSelectable ? "grey35" : choice.IsUnavailable ? "red" : "white";
            string label = Markup.Escape(choice.Label);
            string description = Markup.Escape(choice.Description);
            string defaultMarker = isDefault ? " [yellow]★[/]" : "";

            if(isSelected)
            {
                content.Append($"[dodgerblue1]▶[/] [bold {labelStyle}]{label}[/]{defaultMarker}");
            }
            else
            {
                content.Append($"  [{(!choice.IsSelectable ? "grey35" : choice.IsUnavailable ? "red" : "grey70")}]{label}[/]{defaultMarker}");
            }

            content.AppendLine();
            content.Append($"    [grey]-[/] [{(choice.IsSelectable ? (isSelected ? "grey70" : "grey58") : "grey50")}]{description}[/]");

            if(!String.IsNullOrWhiteSpace(choice.Detail))
            {
                if(choice.IsUnavailable)
                {
                    content.AppendLine();
                    content.Append($"      [red](error)[/]: [red]{Markup.Escape(choice.Detail!)}[/]");
                }
                else
                {
                    content.Append($" [grey]({Markup.Escape(choice.Detail!)})[/]");
                }
            }

            content.AppendLine();

            if(i < uiChoices.Count - 1)
            {
                content.AppendLine();
            }
        }

        return new Markup(content.ToString());
    }

    private static Markup CreateResourceSelectionContent(List<string> selectedResourceDirectories, HashSet<string> defaultResourceDirectories, int selectedResourceIndex)
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
                string resourceDirectory = selectedResourceDirectories[i];
                string path = Markup.Escape(resourceDirectory);
                string defaultMarker = defaultResourceDirectories.Contains(resourceDirectory) ? " [yellow]★[/]" : "";

                if(isSelected)
                {
                    content.AppendLine($"[red]▶[/] [bold white]{path}[/]{defaultMarker} [grey](press Del to remove)[/]");
                }
                else
                {
                    content.AppendLine($"  [grey70]{path}[/]{defaultMarker}");
                }
            }
        }

        return new Markup(content.ToString());
    }

    private static Markup CreateAddonSelectionContent(string addonsRootPath, IReadOnlyList<string> availableAddonNames, HashSet<string> defaultAddonNames, int selectedAddonIndex, HashSet<string> activeAddonNames)
    {
        var content = new StringBuilder();
        content.AppendLine("[grey58]Overlay selected addon directories into the session profile before launch:[/]");
        content.AppendLine();

        if(availableAddonNames.Count == 0)
        {
            content.AppendLine($"[grey]Create addon folders under {Markup.Escape(addonsRootPath)} to make them available here.[/]");
            content.AppendLine("[grey]Conflicting files stop launch, except AGENTS.md which is merged.[/]");
            return new Markup(content.ToString());
        }

        content.AppendLine("[grey58]Space toggles an addon for this run. + saves it as a default.[/]");
        content.AppendLine();

        for(int i = 0; i < availableAddonNames.Count; i++)
        {
            string addonName = availableAddonNames[i];
            bool isActive = activeAddonNames.Contains(addonName);
            bool isSelected = selectedAddonIndex == i;
            string checkbox = isActive ? "[green]☑[/]" : "[grey]☐[/]";
            string defaultMarker = defaultAddonNames.Contains(addonName) ? " [yellow]★[/]" : "";

            if(isSelected)
            {
                content.AppendLine($"[dodgerblue1]▶[/] {checkbox} [bold white]{Markup.Escape(addonName)}[/]{defaultMarker}");
            }
            else
            {
                content.AppendLine($"  {checkbox} [grey70]{Markup.Escape(addonName)}[/]{defaultMarker}");
            }
        }

        return new Markup(content.ToString());
    }

    private static Markup CreateNetworkSelectionContent(IReadOnlyList<string> availableNetworkNames, HashSet<string> defaultNetworkNames, int selectedNetworkIndex, DockerNetworkMode selectedNetworkMode, DockerNetworkMode? defaultNetworkMode, HashSet<string> activeNetworkNames, bool hostNetworkAvailable, bool showWindowsHostNetworkingHint)
    {
        var content = new StringBuilder();
        content.AppendLine("[grey58]Configure Docker networking for the container:[/]");
        content.AppendLine();

        // Network mode
        string modeLabel = GetDockerNetworkModeLabel(selectedNetworkMode);
        string defaultModeMarker = defaultNetworkMode == selectedNetworkMode ? " [yellow]★[/]" : "";
        string modeDisplay = selectedNetworkMode switch
        {
            DockerNetworkMode.Host => "[yellow]host[/]",
            _ => "[green]bridge[/]"
        };

        if(selectedNetworkIndex == 0)
        {
            content.AppendLine($"[dodgerblue1]▶[/] [bold white]Mode:[/] {modeDisplay}{defaultModeMarker}");
        }
        else
        {
            content.AppendLine($"  [grey70]Mode:[/] {modeDisplay}{defaultModeMarker}");
        }

        if(!hostNetworkAvailable)
        {
            content.AppendLine("[grey]  Host mode is unavailable on this host.[/]");
        }
        else if(showWindowsHostNetworkingHint)
        {
            content.AppendLine("[grey]  Windows host mode requires Docker Desktop host networking to be enabled.[/]");
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

        content.AppendLine("[grey58]Additional networks (Space to toggle, + to save as default):[/]");
        for(int i = 0; i < availableNetworkNames.Count; i++)
        {
            string networkName = availableNetworkNames[i];
            bool isActive = activeNetworkNames.Contains(networkName);
            bool isSelected = selectedNetworkIndex == i + 1;
            string checkbox = isActive ? "[green]☑[/]" : "[grey]☐[/]";
            string defaultMarker = defaultNetworkNames.Contains(networkName) ? " [yellow]★[/]" : "";

            if(isSelected)
            {
                content.AppendLine($"[dodgerblue1]▶[/] {checkbox} [bold white]{Markup.Escape(networkName)}[/]{defaultMarker}");
            }
            else
            {
                content.AppendLine($"  {checkbox} [grey70]{Markup.Escape(networkName)}[/]{defaultMarker}");
            }
        }

        return new Markup(content.ToString());
    }

    internal static RunSelectionTab CycleInteractiveTab(RunSelectionTab selectedTab, bool movingRight) => (selectedTab, movingRight) switch
    {
        (RunSelectionTab.Profile, true) => RunSelectionTab.Ui,
        (RunSelectionTab.Ui, true) => RunSelectionTab.Resources,
        (RunSelectionTab.Resources, true) => RunSelectionTab.Addons,
        (RunSelectionTab.Addons, true) => RunSelectionTab.Networks,
        (RunSelectionTab.Networks, true) => RunSelectionTab.Profile,
        (RunSelectionTab.Profile, false) => RunSelectionTab.Networks,
        (RunSelectionTab.Ui, false) => RunSelectionTab.Profile,
        (RunSelectionTab.Resources, false) => RunSelectionTab.Ui,
        (RunSelectionTab.Addons, false) => RunSelectionTab.Resources,
        _ => RunSelectionTab.Addons
    };

    internal static List<UiChoice> BuildUiChoices(OpenCodeDesktopAppStatus desktopAppStatus, RunUiMode? defaultUiMode)
    {
        string desktopDetail = desktopAppStatus.Availability switch
        {
            OpenCodeDesktopAvailability.UnsupportedAttachContract => desktopAppStatus.Detail ?? "installed, but backend handoff is not supported yet",
            _ => "app not detected on this host"
        };
        bool desktopSelectable = desktopAppStatus.Availability switch
        {
            OpenCodeDesktopAvailability.NotDetected => false,
            OpenCodeDesktopAvailability.UnsupportedAttachContract => false,
            _ => true
        };

        return
        [
            new UiChoice(RunUiMode.Tui, "TUI", "Attach the OCW-managed terminal client.", IsDefault: defaultUiMode is RunUiMode.Tui),
            new UiChoice(RunUiMode.Web, "Web", "Open the local browser UI and print the URL.", IsDefault: defaultUiMode is RunUiMode.Web),
            new UiChoice(RunUiMode.Desktop, "Desktop", "Open the installed OpenCode desktop app.", desktopDetail, IsUnavailable: true, desktopSelectable, defaultUiMode is RunUiMode.Desktop)
        ];
    }

    internal static int GetInitialSelectableUiIndex(IReadOnlyList<UiChoice> uiChoices)
    {
        for(int i = 0; i < uiChoices.Count; i++)
        {
            if(uiChoices[i].IsSelectable && uiChoices[i].IsDefault)
            {
                return i;
            }
        }

        for(int i = 0; i < uiChoices.Count; i++)
        {
            if(uiChoices[i].IsSelectable)
            {
                return i;
            }
        }

        return 0;
    }

    internal static int MoveUiSelection(IReadOnlyList<UiChoice> uiChoices, int selectedUiIndex, bool movingForward)
    {
        if(uiChoices.Count == 0)
        {
            return 0;
        }

        int nextIndex = selectedUiIndex;
        for(int i = 0; i < uiChoices.Count; i++)
        {
            nextIndex = movingForward
                ? nextIndex >= uiChoices.Count - 1 ? 0 : nextIndex + 1
                : nextIndex <= 0 ? uiChoices.Count - 1 : nextIndex - 1;

            if(uiChoices[nextIndex].IsSelectable)
            {
                return nextIndex;
            }
        }

        return selectedUiIndex;
    }

    private static DockerNetworkMode CycleDockerNetworkMode(DockerNetworkMode networkMode, bool hostNetworkAvailable)
        => !hostNetworkAvailable
            ? DockerNetworkMode.Bridge
            : networkMode switch
            {
                DockerNetworkMode.Bridge => DockerNetworkMode.Host,
                _ => DockerNetworkMode.Bridge
            };

    private static bool DoesNetworkModeSupportAdditionalNetworks(DockerNetworkMode networkMode)
        => networkMode.SupportsAdditionalNetworks();

    private static string GetDockerNetworkModeLabel(DockerNetworkMode networkMode)
        => networkMode.GetLabel();

    private static DockerNetworkMode? ParseSavedDockerNetworkMode(DockerNetworkMode? persistedValue, bool hostNetworkAvailable)
        => persistedValue is DockerNetworkMode.Host && !hostNetworkAvailable
            ? null
            : persistedValue;

    private static int GetNetworkEntryCount(IReadOnlyList<string> availableNetworkNames, DockerNetworkMode networkMode)
        => !DoesNetworkModeSupportAdditionalNetworks(networkMode)
            ? 1
            : availableNetworkNames.Count + 1;

    private static bool IsHelpKey(ConsoleKeyInfo keyInfo) => keyInfo.KeyChar == '?';

    private static bool IsDefaultToggleKey(ConsoleKeyInfo keyInfo)
        => keyInfo.KeyChar == '+' || keyInfo.Key is ConsoleKey.Add or ConsoleKey.OemPlus;

    private bool TrySaveRunMenuDefaults(
        string defaultProfileName,
        RunUiMode? defaultUiMode,
        IReadOnlyList<string> selectedResourceDirectories,
        HashSet<string> defaultResourceDirectories,
        IReadOnlyList<string> availableAddonNames,
        HashSet<string> defaultAddonNames,
        DockerNetworkMode? defaultNetworkMode,
        IReadOnlyList<string> availableNetworkNames,
        HashSet<string> defaultNetworkNames)
    {
        List<string> resourceDirectories = [.. selectedResourceDirectories.Where(defaultResourceDirectories.Contains)];
        List<string> sessionAddons = [.. availableAddonNames.Where(defaultAddonNames.Contains)];
        List<string> dockerNetworks = [.. availableNetworkNames.Where(defaultNetworkNames.Contains)];

        return _runMenuDefaultsService.TrySaveDefaults(new RunMenuDefaults(defaultProfileName, defaultUiMode, defaultNetworkMode, resourceDirectories, sessionAddons, dockerNetworks));
    }

    private static string GetSelectedDefaultProfileName(IReadOnlyList<ProfileChoice> profileChoices)
        => profileChoices.FirstOrDefault(choice => choice.IsDefault)?.Name ?? profileChoices[0].Name;

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

    private sealed record RunSelection(string ProfileName, RunUiMode UiMode, WorkspaceMountMode MountMode, IReadOnlyList<string> ResourceDirectories, IReadOnlyList<string> SessionAddonNames, DockerNetworkMode NetworkMode, IReadOnlyList<string> NetworkNames);
    private sealed record ProfileChoice(string Name, bool IsDefault);
    internal sealed record UiChoice(RunUiMode Mode, string Label, string Description, string? Detail = null, bool IsUnavailable = false, bool IsSelectable = true, bool IsDefault = false);
    private sealed record ExplorerEntry(string Label, ExplorerEntryType EntryType, string? Path = null);

    internal enum RunSelectionTab
    {
        Profile,
        Ui,
        Resources,
        Addons,
        Networks
    }

    private enum ExplorerEntryType
    {
        Parent,
        Drive,
        Child
    }
}
