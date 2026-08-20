using OpencodeWrap.Services.Docker;
using OpencodeWrap.Services.Profile;
using OpencodeWrap.Services.Runtime.Core;
using OpencodeWrap.Services.Runtime.Launch;
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
        : base("run", "Launch OpenCode with interactive profile, mount, and network selection.")
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
            RunSelection? selection;
            try
            {
                selection = await PromptForRunSelectionAsync();
            }
            catch(OperationCanceledException)
            {
                return 130;
            }

            return selection is null
                ? 1
                : await _launcherService.ExecuteAsync([], requestedProfileName: selection.ProfileName, includeProfileConfig: true, runtimeMode: OpencodeRuntimeMode.HostClientToServe, workspaceMountMode: selection.MountMode, containerMounts: selection.ContainerMounts, sessionAddons: selection.SessionAddonNames, dockerNetworkMode: selection.NetworkMode, dockerNetworks: selection.NetworkNames, privileged: selection.Privileged, verboseSessionLogs: verbose);
        });
    }

    private async Task<RunSelection?> PromptForRunSelectionAsync()
    {
        using var controlCAsInputScope = ConsoleControlCAsInputScope.TryEnable();
        string currentWorkspacePath = Path.GetFullPath(Directory.GetCurrentDirectory());

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
            AppIO.WriteError("Interactive run setup is unavailable in this terminal. Launch `ocw run` from an interactive shell.");
            return null;
        }

        if(!_runMenuDefaultsService.TryLoadDefaults(out var runMenuDefaults))
        {
            return null;
        }

        if(!_runMenuDefaultsService.TryLoadWorkspaceConfig(currentWorkspacePath, out var workspaceConfig, out bool workspaceConfigExists))
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

        var (volumeListSuccess, availableVolumeNames) = await _dockerHostService.TryListVolumeNamesAsync();
        if(!volumeListSuccess)
        {
            return null;
        }

        string defaultProfileName = profileNames.Contains(runMenuDefaults.DefaultProfileName ?? "")
            ? runMenuDefaults.DefaultProfileName!
            : catalog.DefaultProfileName;
        string? configuredProfileName = profileNames.Contains(workspaceConfig.ProfileName ?? "")
            ? workspaceConfig.ProfileName
            : null;

        List<ProfileChoice> profileChoices = [.. profileNames
            .OrderByDescending(name => String.Equals(name, defaultProfileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ProfileChoice(
                name,
                String.Equals(name, defaultProfileName, StringComparison.OrdinalIgnoreCase),
                String.Equals(name, configuredProfileName, StringComparison.OrdinalIgnoreCase)))];

        int selectedIndex = profileChoices.FindIndex(choice => choice.IsConfigured);
        if(selectedIndex < 0)
        {
            selectedIndex = profileChoices.FindIndex(choice => choice.IsDefault);
        }

        if(selectedIndex < 0)
        {
            selectedIndex = 0;
        }

        WorkspaceMountMode? configuredMountMode = workspaceConfigExists ? workspaceConfig.WorkspaceMountMode : null;
        var mountMode = configuredMountMode ?? WorkspaceMountMode.ReadWrite;
        List<ContainerMount> selectedContainerMounts = [];
        var defaultContainerMounts = new HashSet<ContainerMount>();
        var configuredContainerMounts = new HashSet<ContainerMount>();
        var availableVolumeNameSet = new HashSet<string>(availableVolumeNames, StringComparer.Ordinal);
        foreach(ContainerMount savedContainerMount in runMenuDefaults.ContainerMounts)
        {
            ContainerMount normalizedContainerMount;
            switch(savedContainerMount.SourceType)
            {
                case ContainerMountSourceType.Directory:
                    if(!TryNormalizeDirectoryMountSource(savedContainerMount.Source, out string normalizedDirectorySource))
                    {
                        continue;
                    }

                    normalizedContainerMount = savedContainerMount with
                    {
                        Source = normalizedDirectorySource
                    };
                    break;
                case ContainerMountSourceType.NamedVolume:
                    string trimmedVolumeName = savedContainerMount.Source.Trim();
                    if(trimmedVolumeName.Length == 0 || !availableVolumeNameSet.Contains(trimmedVolumeName))
                    {
                        continue;
                    }

                    normalizedContainerMount = savedContainerMount with
                    {
                        Source = trimmedVolumeName
                    };
                    break;
                default:
                    continue;
            }

            if(FindConflictingContainerMountPath(selectedContainerMounts, normalizedContainerMount.ContainerPath) is not null
                || selectedContainerMounts.Contains(normalizedContainerMount))
            {
                continue;
            }

            selectedContainerMounts.Add(normalizedContainerMount);
            defaultContainerMounts.Add(normalizedContainerMount);
        }

        if(workspaceConfigExists)
        {
            foreach(ContainerMount configuredContainerMount in workspaceConfig.ContainerMounts)
            {
                ContainerMount normalizedContainerMount;
                switch(configuredContainerMount.SourceType)
                {
                    case ContainerMountSourceType.Directory:
                        if(!TryNormalizeDirectoryMountSource(configuredContainerMount.Source, out string normalizedDirectorySource))
                        {
                            continue;
                        }

                        normalizedContainerMount = configuredContainerMount with
                        {
                            Source = normalizedDirectorySource
                        };
                        break;
                    case ContainerMountSourceType.NamedVolume:
                        string trimmedVolumeName = configuredContainerMount.Source.Trim();
                        if(trimmedVolumeName.Length == 0 || !availableVolumeNameSet.Contains(trimmedVolumeName))
                        {
                            continue;
                        }

                        normalizedContainerMount = configuredContainerMount with
                        {
                            Source = trimmedVolumeName
                        };
                        break;
                    default:
                        continue;
                }

                if(selectedContainerMounts.Contains(normalizedContainerMount))
                {
                    configuredContainerMounts.Add(normalizedContainerMount);
                    continue;
                }

                if(FindConflictingContainerMountPath(selectedContainerMounts, normalizedContainerMount.ContainerPath) is { } conflictingContainerPath)
                {
                    ContainerMount? conflictingContainerMount = selectedContainerMounts.FirstOrDefault(containerMount => String.Equals(containerMount.ContainerPath, conflictingContainerPath, StringComparison.Ordinal));
                    if(conflictingContainerMount is not null)
                    {
                        selectedContainerMounts.Remove(conflictingContainerMount);
                        defaultContainerMounts.Remove(conflictingContainerMount);
                    }
                }

                if(selectedContainerMounts.Contains(normalizedContainerMount))
                {
                    configuredContainerMounts.Add(normalizedContainerMount);
                    continue;
                }

                selectedContainerMounts.Add(normalizedContainerMount);
                configuredContainerMounts.Add(normalizedContainerMount);
            }
        }

        List<string> availableAddonNames = [.. addonCatalog.Addons.Keys.OrderBy(name => name, GetHostPathComparer())];
        var activeAddonNames = new HashSet<string>(GetHostPathComparer());
        var defaultAddonNames = new HashSet<string>(GetHostPathComparer());
        var configuredAddonNames = new HashSet<string>(GetHostPathComparer());
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

        if(workspaceConfigExists)
        {
            var configuredAddonNameSet = new HashSet<string>(workspaceConfig.SessionAddons, GetHostPathComparer());
            foreach(string addonName in availableAddonNames)
            {
                if(!configuredAddonNameSet.Contains(addonName))
                {
                    continue;
                }

                activeAddonNames.Add(addonName);
                configuredAddonNames.Add(addonName);
            }
        }

        var selectedTab = RunSelectionTab.Profile;
        int selectedVolumeIndex = selectedContainerMounts.Count > 0 ? 1 : 0;
        int selectedAddonIndex = 0;
        int selectedNetworkIndex = 0;
        int selectedDockerOptionIndex = 0;
        var defaultNetworkMode = runMenuDefaults.DefaultDockerNetworkMode;
        var configuredNetworkMode = workspaceConfigExists
            ? workspaceConfig.DockerNetworkMode
            : null;
        var selectedNetworkMode = configuredNetworkMode ?? defaultNetworkMode ?? DockerNetworkMode.Bridge;
        bool privileged = workspaceConfig.Privileged ?? false;
        var activeNetworkNames = new HashSet<string>(StringComparer.Ordinal);
        var defaultNetworkNames = new HashSet<string>(StringComparer.Ordinal);
        var configuredNetworkNames = new HashSet<string>(StringComparer.Ordinal);
        foreach(string networkName in availableNetworkNames.Where(name => runMenuDefaults.DockerNetworks.Contains(name, StringComparer.Ordinal)))
        {
            defaultNetworkNames.Add(networkName);
        }

        if(workspaceConfigExists)
        {
            foreach(string networkName in availableNetworkNames.Where(name => workspaceConfig.DockerNetworks.Contains(name, StringComparer.Ordinal)))
            {
                configuredNetworkNames.Add(networkName);
            }
        }

        if(DoesNetworkModeSupportAdditionalNetworks(selectedNetworkMode))
        {
            activeNetworkNames.UnionWith(defaultNetworkNames);
            activeNetworkNames.UnionWith(configuredNetworkNames);
        }

        while(true)
        {
            bool isPrivilegedConfigured = workspaceConfig.Privileged is { } configuredPrivileged
                && configuredPrivileged == privileged;
            RenderRunSelectionScreen(profileChoices, selectedIndex, selectedTab, mountMode, configuredMountMode == mountMode, currentWorkspacePath, availableVolumeNames, selectedContainerMounts, defaultContainerMounts, configuredContainerMounts, selectedVolumeIndex, addonCatalog.AddonsRoot, availableAddonNames, defaultAddonNames, configuredAddonNames, selectedAddonIndex, activeAddonNames, availableNetworkNames, defaultNetworkNames, configuredNetworkNames, selectedNetworkIndex, selectedNetworkMode, defaultNetworkMode, configuredNetworkMode, activeNetworkNames, selectedDockerOptionIndex, privileged, isPrivilegedConfigured, showWindowsHostNetworkingHint: _dockerHostService.IsWindows);
            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
            if(keyInfo is null)
            {
                continue;
            }

            if(IsInterruptKey(keyInfo.Value))
            {
                AnsiConsole.Clear();
                throw new OperationCanceledException("Run menu interrupted by user.");
            }

            if(IsDefaultToggleKey(keyInfo.Value))
            {
                switch(selectedTab)
                {
                    case RunSelectionTab.Profile:
                        string selectedProfileName = profileChoices[selectedIndex].Name;
                        if(TrySaveRunMenuDefaults(selectedProfileName, selectedContainerMounts, defaultContainerMounts, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
                        {
                            profileChoices = [.. profileChoices.Select(choice => choice with
                            {
                                IsDefault = String.Equals(choice.Name, selectedProfileName, StringComparison.OrdinalIgnoreCase)
                            })];
                        }

                        break;
                    case RunSelectionTab.Volumes:
                        if(selectedVolumeIndex > 0)
                        {
                            ContainerMount selectedContainerMount = selectedContainerMounts[selectedVolumeIndex - 1];
                            bool mountWasDefault = defaultContainerMounts.Contains(selectedContainerMount);
                            if(mountWasDefault)
                            {
                                defaultContainerMounts.Remove(selectedContainerMount);
                            }
                            else
                            {
                                defaultContainerMounts.Add(selectedContainerMount);
                            }

                            if(!TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), selectedContainerMounts, defaultContainerMounts, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
                            {
                                if(mountWasDefault)
                                {
                                    defaultContainerMounts.Add(selectedContainerMount);
                                }
                                else
                                {
                                    defaultContainerMounts.Remove(selectedContainerMount);
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

                            if(!TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), selectedContainerMounts, defaultContainerMounts, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
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
                            var previousDefaultNetworkMode = defaultNetworkMode;
                            defaultNetworkMode = defaultNetworkMode == selectedNetworkMode
                                ? null
                                : selectedNetworkMode;

                            if(!TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), selectedContainerMounts, defaultContainerMounts, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
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

                            if(!TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), selectedContainerMounts, defaultContainerMounts, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames))
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
                    case RunSelectionTab.Docker:
                        break;
                }

                continue;
            }

            if(IsWorkspaceConfigToggleKey(keyInfo.Value))
            {
                string? configuredProfile = profileChoices.FirstOrDefault(choice => choice.IsConfigured)?.Name;
                WorkspaceMountMode? previousConfiguredMountMode = configuredMountMode;
                DockerNetworkMode? previousConfiguredNetworkMode = configuredNetworkMode;
                bool? previousConfiguredPrivileged = workspaceConfig.Privileged;
                var previousConfiguredContainerMounts = new HashSet<ContainerMount>(configuredContainerMounts);
                var previousConfiguredAddonNames = new HashSet<string>(configuredAddonNames, GetHostPathComparer());
                var previousConfiguredNetworkNames = new HashSet<string>(configuredNetworkNames, StringComparer.Ordinal);
                var previousActiveAddonNames = new HashSet<string>(activeAddonNames, GetHostPathComparer());
                var previousActiveNetworkNames = new HashSet<string>(activeNetworkNames, StringComparer.Ordinal);

                switch(selectedTab)
                {
                    case RunSelectionTab.Profile:
                        string selectedProfileName = profileChoices[selectedIndex].Name;
                        configuredProfile = String.Equals(configuredProfile, selectedProfileName, StringComparison.OrdinalIgnoreCase)
                            ? null
                            : selectedProfileName;
                        break;
                    case RunSelectionTab.Volumes:
                        if(selectedVolumeIndex == 0)
                        {
                            continue;
                        }

                        ContainerMount selectedContainerMount = selectedContainerMounts[selectedVolumeIndex - 1];
                        if(!configuredContainerMounts.Add(selectedContainerMount))
                        {
                            configuredContainerMounts.Remove(selectedContainerMount);
                        }

                        break;
                    case RunSelectionTab.Addons:
                        if(availableAddonNames.Count == 0)
                        {
                            continue;
                        }

                        string selectedAddonName = availableAddonNames[selectedAddonIndex];
                        if(!configuredAddonNames.Add(selectedAddonName))
                        {
                            configuredAddonNames.Remove(selectedAddonName);
                        }
                        else
                        {
                            activeAddonNames.Add(selectedAddonName);
                        }

                        break;
                    case RunSelectionTab.Networks:
                        if(selectedNetworkIndex == 0)
                        {
                            configuredNetworkMode = configuredNetworkMode == selectedNetworkMode
                                ? null
                                : selectedNetworkMode;
                        }
                        else if(DoesNetworkModeSupportAdditionalNetworks(selectedNetworkMode) && availableNetworkNames.Count > 0)
                        {
                            string selectedNetworkName = availableNetworkNames[selectedNetworkIndex - 1];
                            if(!configuredNetworkNames.Add(selectedNetworkName))
                            {
                                configuredNetworkNames.Remove(selectedNetworkName);
                            }
                            else
                            {
                                activeNetworkNames.Add(selectedNetworkName);
                            }
                        }

                        break;
                    case RunSelectionTab.Docker:
                        if(selectedDockerOptionIndex == 0)
                        {
                            configuredMountMode = configuredMountMode == mountMode ? null : mountMode;
                        }
                        else
                        {
                            workspaceConfig = workspaceConfig with
                            {
                                Privileged = workspaceConfig.Privileged == privileged ? null : privileged
                            };
                        }

                        break;
                }

                var updatedWorkspaceConfig = new WorkspaceRunMenuConfig(
                    configuredProfile,
                    configuredMountMode,
                    configuredNetworkMode,
                    workspaceConfig.Privileged,
                    [.. selectedContainerMounts.Where(configuredContainerMounts.Contains)],
                    [.. availableAddonNames.Where(configuredAddonNames.Contains)],
                    [.. availableNetworkNames.Where(configuredNetworkNames.Contains)]);

                if(_runMenuDefaultsService.TrySaveWorkspaceConfig(currentWorkspacePath, updatedWorkspaceConfig))
                {
                    workspaceConfig = updatedWorkspaceConfig;
                    profileChoices = [.. profileChoices.Select(choice => choice with
                    {
                        IsConfigured = String.Equals(choice.Name, configuredProfile, StringComparison.OrdinalIgnoreCase)
                    })];
                }
                else
                {
                    configuredMountMode = previousConfiguredMountMode;
                    configuredNetworkMode = previousConfiguredNetworkMode;
                    workspaceConfig = workspaceConfig with { Privileged = previousConfiguredPrivileged };
                    configuredContainerMounts = previousConfiguredContainerMounts;
                    configuredAddonNames = previousConfiguredAddonNames;
                    configuredNetworkNames = previousConfiguredNetworkNames;
                    activeAddonNames = previousActiveAddonNames;
                    activeNetworkNames = previousActiveNetworkNames;
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
                        case RunSelectionTab.Volumes:
                            int volumeEntryCount = selectedContainerMounts.Count + 1;
                            selectedVolumeIndex = selectedVolumeIndex <= 0 ? volumeEntryCount - 1 : selectedVolumeIndex - 1;
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
                        case RunSelectionTab.Docker:
                            selectedDockerOptionIndex = selectedDockerOptionIndex <= 0 ? 1 : selectedDockerOptionIndex - 1;
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
                        case RunSelectionTab.Volumes:
                            int volumeEntryCount = selectedContainerMounts.Count + 1;
                            selectedVolumeIndex = selectedVolumeIndex >= volumeEntryCount - 1 ? 0 : selectedVolumeIndex + 1;
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
                        case RunSelectionTab.Docker:
                            selectedDockerOptionIndex = selectedDockerOptionIndex >= 1 ? 0 : selectedDockerOptionIndex + 1;
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
                            selectedNetworkMode = CycleDockerNetworkMode(selectedNetworkMode);
                            if(!DoesNetworkModeSupportAdditionalNetworks(selectedNetworkMode))
                            {
                                activeNetworkNames.Clear();
                                selectedNetworkIndex = 0;
                            }
                            else
                            {
                                activeNetworkNames.Clear();
                                activeNetworkNames.UnionWith(defaultNetworkNames);
                                activeNetworkNames.UnionWith(configuredNetworkNames);
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

                    if(selectedTab is RunSelectionTab.Docker)
                    {
                        if(selectedDockerOptionIndex == 0)
                        {
                            mountMode = CycleInteractiveMountMode(mountMode);
                        }
                        else
                        {
                            privileged = !privileged;
                        }

                        break;
                    }

                    if(selectedTab is RunSelectionTab.Volumes && selectedVolumeIndex == 0)
                    {
                        TryPromptAppendContainerMount(currentWorkspacePath, availableVolumeNames, selectedContainerMounts, ref selectedVolumeIndex);
                    }

                    break;
                case ConsoleKey.Backspace:
                case ConsoleKey.Delete:
                    if(selectedTab is RunSelectionTab.Volumes && selectedVolumeIndex > 0)
                    {
                        int containerMountIndex = selectedVolumeIndex - 1;
                        ContainerMount removedContainerMount = selectedContainerMounts[containerMountIndex];
                        selectedContainerMounts.RemoveAt(containerMountIndex);
                        if(defaultContainerMounts.Remove(removedContainerMount))
                        {
                            _ = TrySaveRunMenuDefaults(GetSelectedDefaultProfileName(profileChoices), selectedContainerMounts, defaultContainerMounts, availableAddonNames, defaultAddonNames, defaultNetworkMode, availableNetworkNames, defaultNetworkNames);
                        }

                        if(configuredContainerMounts.Remove(removedContainerMount))
                        {
                            workspaceConfig = workspaceConfig with { ContainerMounts = [.. configuredContainerMounts] };
                            _ = _runMenuDefaultsService.TrySaveWorkspaceConfig(currentWorkspacePath, workspaceConfig);
                        }

                        if(selectedVolumeIndex > selectedContainerMounts.Count)
                        {
                            selectedVolumeIndex = selectedContainerMounts.Count;
                        }

                        break;
                    }

                    break;
                case ConsoleKey.Enter:
                    if(selectedTab is RunSelectionTab.Volumes && selectedVolumeIndex == 0)
                    {
                        TryPromptAppendContainerMount(currentWorkspacePath, availableVolumeNames, selectedContainerMounts, ref selectedVolumeIndex);
                        break;
                    }

                    AnsiConsole.Clear();
                    return new RunSelection(
                        profileChoices[selectedIndex].Name,
                        mountMode,
                        selectedContainerMounts,
                        [.. availableAddonNames.Where(activeAddonNames.Contains)],
                        selectedNetworkMode,
                        [.. availableNetworkNames.Where(activeNetworkNames.Contains)],
                        privileged);
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

    private static readonly Style RunMenuAccentBorderStyle = new(Color.DodgerBlue1);
    private static readonly Style RunMenuPanelBorderStyle = new(Color.Grey39);
    private static readonly Style RunMenuFooterBorderStyle = new(Color.Grey23);

    private const int RunMenuContentPanelHeight = 14;
    private const int RunMenuContentRowCount = 10;
    private const int DirectoryBrowserPanelHeight = 14;
    private const int DirectoryBrowserRowCount = 12;

    private const string RunMenuSubmenuFooterHintMarkup = "[grey]ESC[/] [dodgerblue1]menu[/] · [grey]Ctrl+C[/] [red]exit[/]";
    private const string RunMenuSubmenuConfirmFooterHintMarkup = "[grey]ESC[/] [dodgerblue1]menu[/] · [grey]←[/] [dodgerblue1]back[/] · [grey]Ctrl+C[/] [red]exit[/] · [grey]↑↓[/] [dodgerblue1]move[/] · [grey]→[/]/[grey]Enter[/] [dodgerblue1]confirm[/]";
    private const string RunMenuSubmenuTextInputFooterHintMarkup = "[grey]ESC[/] [dodgerblue1]menu[/] · [grey]←[/] [dodgerblue1]back[/] · [grey]Ctrl+C[/] [red]exit[/] · [grey]Type[/] [dodgerblue1]path[/] · [grey]Backspace[/] [dodgerblue1]erase[/] · [grey]→[/]/[grey]Enter[/] [dodgerblue1]confirm[/]";

    private static int GetRunMenuPanelWidth()
    {
        try
        {
            int consoleWidth = Console.WindowWidth;
            return Math.Max(40, consoleWidth - 4);
        }
        catch
        {
            return 80;
        }
    }

    private static string GetRunSelectionTabPanelTitle(RunSelectionTab selectedTab) => selectedTab switch
    {
        RunSelectionTab.Profile => "[bold dodgerblue1]Profile[/]",
        RunSelectionTab.Volumes => "[bold dodgerblue1]Extra mounts[/]",
        RunSelectionTab.Addons => "[bold dodgerblue1]Session addons[/]",
        RunSelectionTab.Networks => "[bold dodgerblue1]Docker networks[/]",
        RunSelectionTab.Docker => "[bold dodgerblue1]Docker options[/]",
        _ => "[bold dodgerblue1]Run setup[/]"
    };

    private static Panel CreateRunMenuContentPanel(IRenderable content, string? headerMarkup = null, int? height = null)
    {
        var paddedContent = new Padder(content)
            .PadLeft(1)
            .PadRight(1)
            .PadTop(1)
            .PadBottom(1);
        var panel = new Panel(paddedContent)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = RunMenuPanelBorderStyle,
            Width = GetRunMenuPanelWidth(),
            Padding = new Padding(0)
        };

        if(!String.IsNullOrWhiteSpace(headerMarkup))
        {
            panel.Header = new PanelHeader(headerMarkup, Justify.Left);
        }

        if(height is not null)
        {
            panel.Height = height.Value;
        }

        return panel;
    }

    private static Panel CreateRunMenuFooterPanel(string hintMarkup) => new Panel(new Markup(hintMarkup))
    {
        Border = BoxBorder.Rounded,
        BorderStyle = RunMenuFooterBorderStyle,
        Padding = new Padding(1, 1),
        Width = GetRunMenuPanelWidth()
    };

    private static void WriteRunMenuSubmenuLayout(Markup body, int bodyHeight = 14, string? bodyHeaderMarkup = null, string? footerHintMarkup = null)
    {
        AnsiConsole.Write(CreateRunMenuContentPanel(body, bodyHeaderMarkup, bodyHeight));
        AnsiConsole.WriteLine();
        AnsiConsole.Write(CreateRunMenuFooterPanel(footerHintMarkup ?? RunMenuSubmenuFooterHintMarkup));
    }

    private static void RenderRunSelectionScreen(
        IReadOnlyList<ProfileChoice> profileChoices,
        int selectedIndex,
        RunSelectionTab selectedTab,
        WorkspaceMountMode mountMode,
        bool isMountModeConfigured,
        string currentWorkspacePath,
        IReadOnlyList<string> availableVolumeNames,
        List<ContainerMount> selectedContainerMounts,
        HashSet<ContainerMount> defaultContainerMounts,
        HashSet<ContainerMount> configuredContainerMounts,
        int selectedVolumeIndex,
        string addonsRootPath,
        IReadOnlyList<string> availableAddonNames,
        HashSet<string> defaultAddonNames,
        HashSet<string> configuredAddonNames,
        int selectedAddonIndex,
        HashSet<string> activeAddonNames,
        IReadOnlyList<string> availableNetworkNames,
        HashSet<string> defaultNetworkNames,
        HashSet<string> configuredNetworkNames,
        int selectedNetworkIndex,
        DockerNetworkMode selectedNetworkMode,
        DockerNetworkMode? defaultNetworkMode,
        DockerNetworkMode? configuredNetworkMode,
        HashSet<string> activeNetworkNames,
        int selectedDockerOptionIndex,
        bool privileged,
        bool isPrivilegedConfigured,
        bool showWindowsHostNetworkingHint)
    {
        AnsiConsole.Clear();
        AppIO.WriteHeader("Run Setup");

        string mountModeLabel = mountMode switch
        {
            WorkspaceMountMode.None => "[yellow]✗ Mount disabled[/]",
            _ => "[green]✓ Read-write mount[/]"
        };
        mountModeLabel += CreateSourceMarkers(isDefault: false, isConfigured: isMountModeConfigured);

        var mountGrid = new Grid();
        mountGrid.AddColumn(new GridColumn().Width(12));
        mountGrid.AddColumn();
        mountGrid.AddRow("[grey58]Mount mode[/]", mountModeLabel);
        mountGrid.AddRow("[grey58]Host folder[/]", $"[white]{Markup.Escape(currentWorkspacePath)}[/]");

        var mountPanel = new Panel(mountGrid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = RunMenuAccentBorderStyle,
            Header = new PanelHeader("[bold dodgerblue1]Workspace[/]", Justify.Left),
            Padding = new Padding(1, 1, 1, 0),
            Width = GetRunMenuPanelWidth()
        };
        AnsiConsole.Write(mountPanel);

        AnsiConsole.Write(CreateTabStrip(selectedTab, selectedContainerMounts.Count, activeAddonNames.Count, activeNetworkNames.Count, selectedNetworkMode, privileged, profileChoices[selectedIndex]));
        AnsiConsole.WriteLine();

        var activeContent = selectedTab switch
        {
            RunSelectionTab.Volumes => CreateVolumeSelectionContent(availableVolumeNames, selectedContainerMounts, defaultContainerMounts, configuredContainerMounts, selectedVolumeIndex),
            RunSelectionTab.Addons => CreateAddonSelectionContent(addonsRootPath, availableAddonNames, defaultAddonNames, configuredAddonNames, selectedAddonIndex, activeAddonNames),
            RunSelectionTab.Networks => CreateNetworkSelectionContent(availableNetworkNames, defaultNetworkNames, configuredNetworkNames, selectedNetworkIndex, selectedNetworkMode, defaultNetworkMode, configuredNetworkMode, activeNetworkNames, showWindowsHostNetworkingHint),
            RunSelectionTab.Docker => CreateDockerSelectionContent(mountMode, isMountModeConfigured, selectedDockerOptionIndex, privileged, isPrivilegedConfigured),
            _ => CreateProfileSelectionContent(profileChoices, selectedIndex)
        };

        AnsiConsole.Write(CreateRunMenuContentPanel(activeContent, GetRunSelectionTabPanelTitle(selectedTab), RunMenuContentPanelHeight));

        AnsiConsole.WriteLine();
        string tabHint = selectedTab switch
        {
            RunSelectionTab.Profile => "[grey]Enter[/]/[grey]+[/]/[grey]#[/] · [grey]↑↓[/] · [grey]←→[/]",
            RunSelectionTab.Volumes => "[grey]Enter[/]/[grey]+[/]/[grey]#[/] · [grey]Space[/] · [grey]Del[/] · [grey]←→[/]",
            RunSelectionTab.Addons => "[grey]Enter[/]/[grey]+[/]/[grey]#[/] · [grey]Space[/] · [grey]←→[/]",
            RunSelectionTab.Networks => "[grey]Enter[/]/[grey]+[/]/[grey]#[/] · [grey]Space[/] · [grey]←→[/]",
            RunSelectionTab.Docker => "[grey]Enter[/] run · [grey]Space[/] toggle · [grey]#[/] repo · [grey]←→[/]",
            _ => ""
        };

        AnsiConsole.Write(CreateRunMenuFooterPanel($"[grey]ESC[/] · [grey]Ctrl+C[/] · [grey]M[/] workspace · {tabHint}"));
    }

    private static Panel CreateTabStrip(RunSelectionTab selectedTab, int volumeCount, int activeAddonCount, int activeNetworkCount, DockerNetworkMode selectedNetworkMode, bool privileged, ProfileChoice selectedProfileChoice)
    {
        var tabGrid = new Grid();
        tabGrid.AddColumn();
        tabGrid.AddColumn();
        tabGrid.AddColumn();
        tabGrid.AddColumn();
        tabGrid.AddColumn();

        string profileLabel = $"👤 {Markup.Escape(selectedProfileChoice.Name)}";
        string profileTab = selectedTab is RunSelectionTab.Profile
            ? $"[white on dodgerblue1] {profileLabel} [/]"
            : $"[grey70 on grey19] {profileLabel} [/]";

        string volumeLabel = volumeCount == 0
            ? "📦"
            : $"📦 ([green]{volumeCount}[/])";
        string volumeTab = selectedTab is RunSelectionTab.Volumes
            ? $"[white on dodgerblue1] {volumeLabel} [/]"
            : $"[grey70 on grey19] {volumeLabel} [/]";

        string addonLabel = activeAddonCount == 0
            ? "🧩"
            : $"🧩 ([green]{activeAddonCount}[/])";
        string addonTab = selectedTab is RunSelectionTab.Addons
            ? $"[white on dodgerblue1] {addonLabel} [/]"
            : $"[grey70 on grey19] {addonLabel} [/]";

        string networkModeLabel = GetDockerNetworkModeLabel(selectedNetworkMode);
        string networkLabel = activeNetworkCount == 0
            ? $"🌐 {networkModeLabel}"
            : $"🌐 {networkModeLabel} ([green]{activeNetworkCount}[/])";
        string networkTab = selectedTab is RunSelectionTab.Networks
            ? $"[white on dodgerblue1] {networkLabel} [/]"
            : $"[grey70 on grey19] {networkLabel} [/]";

        string dockerLabel = privileged ? "🐳 [yellow]privileged[/]" : "🐳";
        string dockerTab = selectedTab is RunSelectionTab.Docker
            ? $"[white on dodgerblue1] {dockerLabel} [/]"
            : $"[grey70 on grey19] {dockerLabel} [/]";

        tabGrid.AddRow(profileTab, volumeTab, addonTab, networkTab, dockerTab);

        return new Panel(tabGrid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = RunMenuFooterBorderStyle,
            Padding = new Padding(1, 1),
            Width = GetRunMenuPanelWidth()
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

        ListViewport viewport = CreateListViewport(profileChoices.Count, selectedIndex, RunMenuContentRowCount);
        AppendViewportOverflowIndicator(content, viewport.HiddenBeforeCount, movingUp: true);

        for(int i = viewport.StartIndex; i < viewport.EndIndex; i++)
        {
            var choice = profileChoices[i];
            bool isSelected = i == selectedIndex;
            string escapedName = Markup.Escape(choice.Name);
            string sourceMarkers = CreateSourceMarkers(choice.IsDefault, choice.IsConfigured);

            if(isSelected)
            {
                content.Append($"[dodgerblue1]▶[/] [bold white]{escapedName}[/]");
                content.Append(sourceMarkers);
                content.AppendLine();
            }
            else
            {
                content.AppendLine($"  [grey70]{escapedName}[/]{sourceMarkers}");
            }
        }

        AppendViewportOverflowIndicator(content, viewport.HiddenAfterCount, movingUp: false);

        return new Markup(content.ToString());
    }

    private static Markup CreateVolumeSelectionContent(IReadOnlyList<string> availableVolumeNames, List<ContainerMount> selectedContainerMounts, HashSet<ContainerMount> defaultContainerMounts, HashSet<ContainerMount> configuredContainerMounts, int selectedVolumeIndex)
    {
        var content = new StringBuilder();

        if(selectedVolumeIndex == 0)
        {
            content.AppendLine("[dodgerblue1]▶[/] [bold white]+ Add mount[/]");
        }
        else
        {
            content.AppendLine("  [grey70]+ Add mount[/]");
        }

        if(availableVolumeNames.Count == 0)
        {
            content.AppendLine("[grey](no named volumes)[/]");
        }

        if(selectedContainerMounts.Count == 0)
        {
            content.AppendLine("[grey](none)[/]");
        }
        else
        {
            content.AppendLine();
            int fixedRowCount = availableVolumeNames.Count == 0 ? 2 : 1;
            int mountRowCount = RunMenuContentRowCount - fixedRowCount - 1;
            int selectedMountIndex = Math.Max(0, selectedVolumeIndex - 1);
            ListViewport viewport = CreateListViewport(selectedContainerMounts.Count, selectedMountIndex, mountRowCount);
            AppendViewportOverflowIndicator(content, viewport.HiddenBeforeCount, movingUp: true);

            for(int i = viewport.StartIndex; i < viewport.EndIndex; i++)
            {
                ContainerMount containerMount = selectedContainerMounts[i];
                bool isSelected = selectedVolumeIndex == i + 1;
                string sourceMarkers = CreateSourceMarkers(defaultContainerMounts.Contains(containerMount), configuredContainerMounts.Contains(containerMount));
                string sourceTypeLabel = containerMount.SourceType is ContainerMountSourceType.NamedVolume ? "[dodgerblue1]vol[/]" : "[green]dir[/]";
                string source = Markup.Escape(containerMount.Source);
                string containerPath = Markup.Escape(containerMount.ContainerPath);
                string accessMode = containerMount.AccessMode is ContainerMountAccessMode.ReadOnly ? "[yellow]ro[/]" : "[green]rw[/]";
                string mountLine = $"{sourceTypeLabel} [white]{source}[/] [grey]->[/] [grey70]{containerPath}[/] [grey]([/]{accessMode}[grey])[/]";

                if(isSelected)
                {
                    content.AppendLine($"[dodgerblue1]▶[/] {mountLine}{sourceMarkers}");
                }
                else
                {
                    content.AppendLine($"  {mountLine}{sourceMarkers}");
                }
            }

            AppendViewportOverflowIndicator(content, viewport.HiddenAfterCount, movingUp: false);
        }

        return new Markup(content.ToString());
    }

    private static Markup CreateAddonSelectionContent(string addonsRootPath, IReadOnlyList<string> availableAddonNames, HashSet<string> defaultAddonNames, HashSet<string> configuredAddonNames, int selectedAddonIndex, HashSet<string> activeAddonNames)
    {
        var content = new StringBuilder();

        if(availableAddonNames.Count == 0)
        {
            content.AppendLine($"[grey]No addons.[/] [grey58]{Markup.Escape(addonsRootPath)}[/]");
            content.AppendLine("[grey]Conflicting files block launch except opencode/AGENTS.md, root .env, and opencode/opencode.json (merged).[/]");
            return new Markup(content.ToString());
        }

        ListViewport viewport = CreateListViewport(availableAddonNames.Count, selectedAddonIndex, RunMenuContentRowCount);
        AppendViewportOverflowIndicator(content, viewport.HiddenBeforeCount, movingUp: true);

        for(int i = viewport.StartIndex; i < viewport.EndIndex; i++)
        {
            string addonName = availableAddonNames[i];
            bool isActive = activeAddonNames.Contains(addonName);
            bool isSelected = selectedAddonIndex == i;
            string checkbox = isActive ? "[green]☑[/]" : "[grey]☐[/]";
            string sourceMarkers = CreateSourceMarkers(defaultAddonNames.Contains(addonName), configuredAddonNames.Contains(addonName));

            if(isSelected)
            {
                content.AppendLine($"[dodgerblue1]▶[/] {checkbox} [bold white]{Markup.Escape(addonName)}[/]{sourceMarkers}");
            }
            else
            {
                content.AppendLine($"  {checkbox} [grey70]{Markup.Escape(addonName)}[/]{sourceMarkers}");
            }
        }

        AppendViewportOverflowIndicator(content, viewport.HiddenAfterCount, movingUp: false);

        return new Markup(content.ToString());
    }

    private static Markup CreateNetworkSelectionContent(IReadOnlyList<string> availableNetworkNames, HashSet<string> defaultNetworkNames, HashSet<string> configuredNetworkNames, int selectedNetworkIndex, DockerNetworkMode selectedNetworkMode, DockerNetworkMode? defaultNetworkMode, DockerNetworkMode? configuredNetworkMode, HashSet<string> activeNetworkNames, bool showWindowsHostNetworkingHint)
    {
        var content = new StringBuilder();

        string modeLabel = GetDockerNetworkModeLabel(selectedNetworkMode);
        string modeSourceMarkers = CreateSourceMarkers(defaultNetworkMode == selectedNetworkMode, configuredNetworkMode == selectedNetworkMode);
        string modeDisplay = selectedNetworkMode switch
        {
            DockerNetworkMode.Host => "[yellow]host[/]",
            _ => "[green]bridge[/]"
        };

        if(selectedNetworkIndex == 0)
        {
            content.AppendLine($"[dodgerblue1]▶[/] [bold white]Network mode[/] {modeDisplay}{modeSourceMarkers}");
        }
        else
        {
            content.AppendLine($"  [grey70]Network mode[/] {modeDisplay}{modeSourceMarkers}");
        }

        if(showWindowsHostNetworkingHint && selectedNetworkMode is DockerNetworkMode.Host)
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

        content.AppendLine("  [grey50]────────────────────────────────────────[/]");
        bool hasNetworkHint = showWindowsHostNetworkingHint && selectedNetworkMode is DockerNetworkMode.Host;
        int fixedRowCount = hasNetworkHint ? 4 : 3;
        ListViewport viewport = CreateListViewport(
            availableNetworkNames.Count,
            Math.Max(0, selectedNetworkIndex - 1),
            RunMenuContentRowCount - fixedRowCount);
        AppendViewportOverflowIndicator(content, viewport.HiddenBeforeCount, movingUp: true);

        for(int i = viewport.StartIndex; i < viewport.EndIndex; i++)
        {
            string networkName = availableNetworkNames[i];
            bool isActive = activeNetworkNames.Contains(networkName);
            bool isSelected = selectedNetworkIndex == i + 1;
            string checkbox = isActive ? "[green]☑[/]" : "[grey]☐[/]";
            string sourceMarkers = CreateSourceMarkers(defaultNetworkNames.Contains(networkName), configuredNetworkNames.Contains(networkName));

            if(isSelected)
            {
                content.AppendLine($"[dodgerblue1]▶[/] {checkbox} [bold white]{Markup.Escape(networkName)}[/]{sourceMarkers}");
            }
            else
            {
                content.AppendLine($"  {checkbox} [grey70]{Markup.Escape(networkName)}[/]{sourceMarkers}");
            }
        }

        AppendViewportOverflowIndicator(content, viewport.HiddenAfterCount, movingUp: false);

        return new Markup(content.ToString());
    }

    private static Markup CreateDockerSelectionContent(WorkspaceMountMode mountMode, bool isMountModeConfigured, int selectedIndex, bool privileged, bool isPrivilegedConfigured)
    {
        string mountCheckbox = mountMode is WorkspaceMountMode.ReadWrite ? "[green]☑[/]" : "[grey]☐[/]";
        string mountState = mountMode is WorkspaceMountMode.ReadWrite ? "[green]read-write[/]" : "[yellow]disabled[/]";
        string mountCursor = selectedIndex == 0 ? "[dodgerblue1]▶[/]" : " ";
        string mountLabel = selectedIndex == 0 ? "[bold white]Workspace mount[/]" : "[grey70]Workspace mount[/]";
        string checkbox = privileged ? "[yellow]☑[/]" : "[grey]☐[/]";
        string state = privileged ? "[yellow]enabled[/]" : "[green]disabled[/]";
        string privilegedCursor = selectedIndex == 1 ? "[dodgerblue1]▶[/]" : " ";
        string privilegedLabel = selectedIndex == 1 ? "[bold white]Privileged mode[/]" : "[grey70]Privileged mode[/]";
        return new Markup(
            $"{mountCursor} {mountCheckbox} {mountLabel} {mountState}{CreateSourceMarkers(isDefault: false, isConfigured: isMountModeConfigured)}\n" +
            $"{privilegedCursor} {checkbox} {privilegedLabel} {state}{CreateSourceMarkers(isDefault: false, isConfigured: isPrivilegedConfigured)}\n\n" +
            "[grey58]Space toggles the selected option. # adds or removes it from ocw.json.[/]");
    }

    private static ListViewport CreateListViewport(int itemCount, int selectedIndex, int maxRowCount)
    {
        if(itemCount <= 0 || maxRowCount <= 0)
        {
            return new ListViewport(0, 0, 0, 0);
        }

        if(itemCount <= maxRowCount)
        {
            return new ListViewport(0, itemCount, 0, 0);
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, itemCount - 1);
        if(maxRowCount < 3)
        {
            return new ListViewport(selectedIndex, 1, 0, 0);
        }

        int edgeItemCount = maxRowCount - 1;
        if(selectedIndex < edgeItemCount)
        {
            return new ListViewport(0, edgeItemCount, 0, itemCount - edgeItemCount);
        }

        if(selectedIndex >= itemCount - edgeItemCount)
        {
            int startIndex = itemCount - edgeItemCount;
            return new ListViewport(startIndex, edgeItemCount, startIndex, 0);
        }

        int middleItemCount = maxRowCount - 2;
        int middleStartIndex = Math.Clamp(
            selectedIndex - middleItemCount / 2,
            1,
            itemCount - middleItemCount - 1);
        return new ListViewport(
            middleStartIndex,
            middleItemCount,
            middleStartIndex,
            itemCount - middleStartIndex - middleItemCount);
    }

    private static void AppendViewportOverflowIndicator(StringBuilder content, int hiddenItemCount, bool movingUp)
    {
        if(hiddenItemCount <= 0)
        {
            return;
        }

        string arrow = movingUp ? "↑" : "↓";
        content.AppendLine($"  [grey58]{arrow} {hiddenItemCount} more[/]");
    }

    internal static RunSelectionTab CycleInteractiveTab(RunSelectionTab selectedTab, bool movingRight) => (selectedTab, movingRight) switch
    {
        (RunSelectionTab.Profile, true) => RunSelectionTab.Volumes,
        (RunSelectionTab.Volumes, true) => RunSelectionTab.Addons,
        (RunSelectionTab.Addons, true) => RunSelectionTab.Networks,
        (RunSelectionTab.Networks, true) => RunSelectionTab.Docker,
        (RunSelectionTab.Docker, true) => RunSelectionTab.Profile,
        (RunSelectionTab.Profile, false) => RunSelectionTab.Docker,
        (RunSelectionTab.Volumes, false) => RunSelectionTab.Profile,
        (RunSelectionTab.Addons, false) => RunSelectionTab.Volumes,
        (RunSelectionTab.Networks, false) => RunSelectionTab.Addons,
        (RunSelectionTab.Docker, false) => RunSelectionTab.Networks,
        _ => RunSelectionTab.Docker
    };

    private static DockerNetworkMode CycleDockerNetworkMode(DockerNetworkMode networkMode) => networkMode switch
    {
        DockerNetworkMode.Bridge => DockerNetworkMode.Host,
        _ => DockerNetworkMode.Bridge
    };

    private static bool DoesNetworkModeSupportAdditionalNetworks(DockerNetworkMode networkMode)
        => networkMode.SupportsAdditionalNetworks();

    private static string GetDockerNetworkModeLabel(DockerNetworkMode networkMode)
        => networkMode.GetLabel();

    private static string CreateSourceMarkers(bool isDefault, bool isConfigured)
    {
        var markers = new StringBuilder();
        if(isDefault)
        {
            markers.Append(" [yellow]★[/]");
        }

        if(isConfigured)
        {
            markers.Append(" [deepskyblue1]◆[/]");
        }

        return markers.ToString();
    }

    private static int GetNetworkEntryCount(IReadOnlyList<string> availableNetworkNames, DockerNetworkMode networkMode)
        => !DoesNetworkModeSupportAdditionalNetworks(networkMode)
            ? 1
            : availableNetworkNames.Count + 1;

    private static bool IsInterruptKey(ConsoleKeyInfo keyInfo)
        => ((keyInfo.Modifiers & ConsoleModifiers.Control) != 0 && keyInfo.Key is ConsoleKey.C)
            || keyInfo.KeyChar == '\u0003';

    private static bool IsDefaultToggleKey(ConsoleKeyInfo keyInfo)
        => keyInfo.KeyChar == '+' || keyInfo.Key is ConsoleKey.Add or ConsoleKey.OemPlus;

    private static bool IsWorkspaceConfigToggleKey(ConsoleKeyInfo keyInfo)
        => keyInfo.KeyChar == '#';

    private bool TrySaveRunMenuDefaults(
        string defaultProfileName,
        IReadOnlyList<ContainerMount> selectedContainerMounts,
        HashSet<ContainerMount> defaultContainerMounts,
        IReadOnlyList<string> availableAddonNames,
        HashSet<string> defaultAddonNames,
        DockerNetworkMode? defaultNetworkMode,
        IReadOnlyList<string> availableNetworkNames,
        HashSet<string> defaultNetworkNames)
    {
        List<ContainerMount> containerMounts = [.. selectedContainerMounts.Where(defaultContainerMounts.Contains)];
        List<string> sessionAddons = [.. availableAddonNames.Where(defaultAddonNames.Contains)];
        List<string> dockerNetworks = [.. availableNetworkNames.Where(defaultNetworkNames.Contains)];

        return _runMenuDefaultsService.TrySaveDefaults(new RunMenuDefaults(defaultProfileName, defaultNetworkMode, containerMounts, sessionAddons, dockerNetworks));
    }

    private static string GetSelectedDefaultProfileName(IReadOnlyList<ProfileChoice> profileChoices)
        => profileChoices.FirstOrDefault(choice => choice.IsDefault)?.Name ?? profileChoices[0].Name;

    private static void TryPromptAppendContainerMount(
        string currentWorkspacePath,
        IReadOnlyList<string> availableVolumeNames,
        List<ContainerMount> selectedContainerMounts,
        ref int selectedVolumeIndex)
    {
        ContainerMount? selectedContainerMount = PromptForContainerMount(currentWorkspacePath, availableVolumeNames, selectedContainerMounts);
        if(selectedContainerMount is null)
        {
            return;
        }

        selectedContainerMounts.Add(selectedContainerMount);
        selectedVolumeIndex = selectedContainerMounts.Count;
    }

    private static ContainerMount? PromptForContainerMount(string currentWorkspacePath, IReadOnlyList<string> availableVolumeNames, IReadOnlyList<ContainerMount> selectedContainerMounts)
    {
        string workspaceContainerPath = ResolveContainerWorkspacePath(currentWorkspacePath);
        int selectedSourceTypeIndex = 0;
        int selectedVolumeIndex = 0;
        var directoryBrowserState = CreateInitialDirectoryBrowserState(currentWorkspacePath, selectedContainerMounts);
        string requestedContainerPath = "";
        ContainerMountSourceType? selectedSourceType = null;
        string? source = null;
        int? selectedAccessModeIndex = null;
        var currentPage = ContainerMountWizardPage.SourceType;

        while(true)
        {
            switch(currentPage)
            {
                case ContainerMountWizardPage.SourceType:
                    var sourceTypeResult = PromptForContainerMountSourceType(availableVolumeNames.Count > 0, selectedSourceTypeIndex);
                    selectedSourceTypeIndex = sourceTypeResult.SelectedIndex;
                    switch(sourceTypeResult.Navigation)
                    {
                        case PromptNavigation.Cancel:
                            return null;
                        case PromptNavigation.Back:
                            break;
                        case PromptNavigation.Confirm:
                            ContainerMountSourceType nextSourceType = sourceTypeResult.SourceType!.Value;
                            if(selectedSourceType != nextSourceType)
                            {
                                source = null;
                                requestedContainerPath = "";
                                selectedAccessModeIndex = null;
                            }

                            selectedSourceType = nextSourceType;
                            currentPage = ContainerMountWizardPage.Source;
                            break;
                    }

                    break;
                case ContainerMountWizardPage.Source:
                    if(selectedSourceType is null)
                    {
                        currentPage = ContainerMountWizardPage.SourceType;
                        break;
                    }

                    if(selectedSourceType.Value is ContainerMountSourceType.NamedVolume)
                    {
                        var volumeResult = PromptForDockerVolumeName(availableVolumeNames, selectedVolumeIndex);
                        selectedVolumeIndex = volumeResult.SelectedIndex;
                        switch(volumeResult.Navigation)
                        {
                            case PromptNavigation.Cancel:
                                return null;
                            case PromptNavigation.Back:
                                currentPage = ContainerMountWizardPage.SourceType;
                                break;
                            case PromptNavigation.Confirm:
                                if(!String.Equals(source, volumeResult.VolumeName, StringComparison.Ordinal))
                                {
                                    requestedContainerPath = "";
                                }

                                source = volumeResult.VolumeName;
                                currentPage = ContainerMountWizardPage.TargetPath;
                                break;
                        }

                        break;
                    }

                    var directoryResult = PromptForDirectoryMountSource(currentWorkspacePath, selectedContainerMounts, directoryBrowserState);
                    directoryBrowserState = directoryResult.State;
                    switch(directoryResult.Navigation)
                    {
                        case PromptNavigation.Cancel:
                            return null;
                        case PromptNavigation.Back:
                            currentPage = ContainerMountWizardPage.SourceType;
                            break;
                        case PromptNavigation.Confirm:
                            if(!String.Equals(source, directoryResult.DirectoryPath, StringComparison.Ordinal))
                            {
                                requestedContainerPath = "";
                            }

                            source = directoryResult.DirectoryPath;
                            currentPage = ContainerMountWizardPage.TargetPath;
                            break;
                    }

                    break;
                case ContainerMountWizardPage.TargetPath:
                    if(selectedSourceType is null || source is null)
                    {
                        currentPage = ContainerMountWizardPage.Source;
                        break;
                    }

                    if(requestedContainerPath.Length == 0)
                    {
                        requestedContainerPath = BuildDefaultContainerMountPath(source);
                    }

                    var containerPathResult = PromptForContainerMountPath(selectedSourceType.Value, source, workspaceContainerPath, selectedContainerMounts, requestedContainerPath);
                    requestedContainerPath = containerPathResult.RequestedContainerPath;
                    switch(containerPathResult.Navigation)
                    {
                        case PromptNavigation.Cancel:
                            return null;
                        case PromptNavigation.Back:
                            currentPage = ContainerMountWizardPage.Source;
                            break;
                        case PromptNavigation.Confirm:
                            currentPage = ContainerMountWizardPage.AccessMode;
                            break;
                    }

                    break;
                case ContainerMountWizardPage.AccessMode:
                    if(selectedSourceType is null || source is null || requestedContainerPath.Length == 0)
                    {
                        currentPage = ContainerMountWizardPage.TargetPath;
                        break;
                    }

                    selectedAccessModeIndex ??= selectedSourceType.Value is ContainerMountSourceType.Directory ? 0 : 1;
                    var accessModeResult = PromptForContainerMountAccessMode(selectedSourceType.Value, selectedAccessModeIndex.Value);
                    selectedAccessModeIndex = accessModeResult.SelectedIndex;
                    switch(accessModeResult.Navigation)
                    {
                        case PromptNavigation.Cancel:
                            return null;
                        case PromptNavigation.Back:
                            currentPage = ContainerMountWizardPage.TargetPath;
                            break;
                        case PromptNavigation.Confirm:
                            return new ContainerMount(selectedSourceType.Value, source, requestedContainerPath, accessModeResult.AccessMode!.Value);
                    }

                    break;
            }
        }
    }

    private static SourceTypePromptResult PromptForContainerMountSourceType(bool hasNamedVolumes, int selectedIndex)
    {
        using var controlCAsInputScope = ConsoleControlCAsInputScope.TryEnable();

        List<ContainerMountSourceTypeChoice> choices =
        [
            new(ContainerMountSourceType.Directory, "Directory", "", true),
            new(ContainerMountSourceType.NamedVolume, "Named volume", hasNamedVolumes ? "" : "No Docker named volumes on this host.", hasNamedVolumes)
        ];
        while(true)
        {
            RenderContainerMountSourceTypeSelectionScreen(choices, selectedIndex);

            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
            if(keyInfo is null)
            {
                continue;
            }

            if(IsInterruptKey(keyInfo.Value))
            {
                AnsiConsole.Clear();
                throw new OperationCanceledException("Container mount type selector interrupted by user.");
            }

            switch(keyInfo.Value.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.W:
                    selectedIndex = MoveSelectableContainerMountSourceTypeChoice(choices, selectedIndex, movingForward: false);
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.S:
                    selectedIndex = MoveSelectableContainerMountSourceTypeChoice(choices, selectedIndex, movingForward: true);
                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.Spacebar:
                    if(!choices[selectedIndex].IsSelectable)
                    {
                        break;
                    }

                    AnsiConsole.Clear();
                    return new SourceTypePromptResult(PromptNavigation.Confirm, choices[selectedIndex].SourceType, selectedIndex);
                case ConsoleKey.LeftArrow:
                    AnsiConsole.Clear();
                    return new SourceTypePromptResult(PromptNavigation.Back, null, selectedIndex);
                case ConsoleKey.RightArrow:
                    if(!choices[selectedIndex].IsSelectable)
                    {
                        break;
                    }

                    AnsiConsole.Clear();
                    return new SourceTypePromptResult(PromptNavigation.Confirm, choices[selectedIndex].SourceType, selectedIndex);
                case ConsoleKey.Escape:
                    AnsiConsole.Clear();
                    return new SourceTypePromptResult(PromptNavigation.Cancel, null, selectedIndex);
            }
        }
    }

    private static AccessModePromptResult PromptForContainerMountAccessMode(ContainerMountSourceType sourceType, int selectedIndex)
    {
        using var controlCAsInputScope = ConsoleControlCAsInputScope.TryEnable();

        List<ContainerMountAccessModeChoice> choices =
        [
            new(ContainerMountAccessMode.ReadOnly, "Read-only", ""),
            new(ContainerMountAccessMode.ReadWrite, "Read-write", "")
        ];

        while(true)
        {
            RenderContainerMountAccessModeSelectionScreen(sourceType, choices, selectedIndex);

            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
            if(keyInfo is null)
            {
                continue;
            }

            if(IsInterruptKey(keyInfo.Value))
            {
                AnsiConsole.Clear();
                throw new OperationCanceledException("Container mount access mode selector interrupted by user.");
            }

            switch(keyInfo.Value.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.W:
                    selectedIndex = selectedIndex <= 0 ? choices.Count - 1 : selectedIndex - 1;
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.S:
                    selectedIndex = selectedIndex >= choices.Count - 1 ? 0 : selectedIndex + 1;
                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.Spacebar:
                    AnsiConsole.Clear();
                    return new AccessModePromptResult(PromptNavigation.Confirm, choices[selectedIndex].AccessMode, selectedIndex);
                case ConsoleKey.LeftArrow:
                    AnsiConsole.Clear();
                    return new AccessModePromptResult(PromptNavigation.Back, null, selectedIndex);
                case ConsoleKey.RightArrow:
                    AnsiConsole.Clear();
                    return new AccessModePromptResult(PromptNavigation.Confirm, choices[selectedIndex].AccessMode, selectedIndex);
                case ConsoleKey.Escape:
                    AnsiConsole.Clear();
                    return new AccessModePromptResult(PromptNavigation.Cancel, null, selectedIndex);
            }
        }
    }

    private static VolumeNamePromptResult PromptForDockerVolumeName(IReadOnlyList<string> availableVolumeNames, int selectedIndex)
    {
        if(availableVolumeNames.Count == 0)
        {
            return new VolumeNamePromptResult(PromptNavigation.Cancel, null, 0);
        }

        using var controlCAsInputScope = ConsoleControlCAsInputScope.TryEnable();

        while(true)
        {
            RenderDockerVolumeSelectionScreen(availableVolumeNames, selectedIndex);

            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
            if(keyInfo is null)
            {
                continue;
            }

            if(IsInterruptKey(keyInfo.Value))
            {
                AnsiConsole.Clear();
                throw new OperationCanceledException("Docker volume selector interrupted by user.");
            }

            switch(keyInfo.Value.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.W:
                    selectedIndex = selectedIndex <= 0 ? availableVolumeNames.Count - 1 : selectedIndex - 1;
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.S:
                    selectedIndex = selectedIndex >= availableVolumeNames.Count - 1 ? 0 : selectedIndex + 1;
                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.Spacebar:
                    AnsiConsole.Clear();
                    return new VolumeNamePromptResult(PromptNavigation.Confirm, availableVolumeNames[selectedIndex], selectedIndex);
                case ConsoleKey.LeftArrow:
                    AnsiConsole.Clear();
                    return new VolumeNamePromptResult(PromptNavigation.Back, null, selectedIndex);
                case ConsoleKey.RightArrow:
                    AnsiConsole.Clear();
                    return new VolumeNamePromptResult(PromptNavigation.Confirm, availableVolumeNames[selectedIndex], selectedIndex);
                case ConsoleKey.Escape:
                    AnsiConsole.Clear();
                    return new VolumeNamePromptResult(PromptNavigation.Cancel, null, selectedIndex);
            }
        }
    }

    private static ContainerPathPromptResult PromptForContainerMountPath(ContainerMountSourceType sourceType, string source, string workspaceContainerPath, IReadOnlyList<ContainerMount> selectedContainerMounts, string initialContainerPath)
    {
        using var controlCAsInputScope = ConsoleControlCAsInputScope.TryEnable();

        string? validationMessage = null;
        var requestedContainerPath = new StringBuilder(initialContainerPath);

        while(true)
        {
            RenderContainerMountPathPrompt(sourceType, source, workspaceContainerPath, requestedContainerPath.ToString(), validationMessage);

            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
            if(keyInfo is null)
            {
                continue;
            }

            if(IsInterruptKey(keyInfo.Value))
            {
                AnsiConsole.Clear();
                throw new OperationCanceledException("Container mount path prompt interrupted by user.");
            }

            switch(keyInfo.Value.Key)
            {
                case ConsoleKey.Escape:
                    AnsiConsole.Clear();
                    return new ContainerPathPromptResult(PromptNavigation.Cancel, null, requestedContainerPath.ToString());
                case ConsoleKey.Backspace:
                    if(requestedContainerPath.Length > 0)
                    {
                        requestedContainerPath.Length--;
                    }

                    validationMessage = null;
                    break;
                case ConsoleKey.Enter:
                case ConsoleKey.RightArrow:
                    if(requestedContainerPath.Length == 0)
                    {
                        validationMessage = "Enter an absolute container path before continuing.";
                        break;
                    }

                    string requestedPath = requestedContainerPath.ToString();
                    if(!ContainerPathUtility.TryNormalizeAbsolutePath(requestedPath, out string normalizedContainerPath))
                    {
                        validationMessage = "Use an absolute container path other than '/' and avoid commas.";
                        break;
                    }

                    string? conflictingPath = FindConflictingContainerMountPath(selectedContainerMounts, normalizedContainerPath);
                    if(conflictingPath is not null)
                    {
                        validationMessage = $"Path '{normalizedContainerPath}' conflicts with existing mount '{conflictingPath}'.";
                        break;
                    }

                    AnsiConsole.Clear();
                    return new ContainerPathPromptResult(PromptNavigation.Confirm, normalizedContainerPath, normalizedContainerPath);
                case ConsoleKey.LeftArrow:
                    AnsiConsole.Clear();
                    return new ContainerPathPromptResult(PromptNavigation.Back, null, requestedContainerPath.ToString());
                default:
                    if(!Char.IsControl(keyInfo.Value.KeyChar))
                    {
                        requestedContainerPath.Append(keyInfo.Value.KeyChar);
                        validationMessage = null;
                    }

                    break;
            }
        }
    }

    private static void RenderContainerMountSourceTypeSelectionScreen(IReadOnlyList<ContainerMountSourceTypeChoice> choices, int selectedIndex)
    {
        AnsiConsole.Clear();
        AppIO.WriteHeader("Mount Type");

        var content = new StringBuilder();
        for(int i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            bool isSelected = i == selectedIndex;
            string labelStyle = choice.IsSelectable ? "white" : "grey35";
            string detailStyle = choice.IsSelectable ? (isSelected ? "grey70" : "grey58") : "grey50";

            if(isSelected)
            {
                content.AppendLine($"[dodgerblue1]▶[/] [bold {labelStyle}]{Markup.Escape(choice.Label)}[/]");
            }
            else
            {
                content.AppendLine($"  [{(choice.IsSelectable ? "grey70" : "grey35")}]{Markup.Escape(choice.Label)}[/]");
            }

            if(!String.IsNullOrEmpty(choice.Description))
            {
                content.AppendLine($"    [grey]-[/] [{detailStyle}]{Markup.Escape(choice.Description)}[/]");
            }

            if(i < choices.Count - 1)
            {
                content.AppendLine();
            }
        }

        WriteRunMenuSubmenuLayout(new Markup(content.ToString()), 12, "[bold dodgerblue1]Mount type[/]", RunMenuSubmenuConfirmFooterHintMarkup);
    }

    private static void RenderDockerVolumeSelectionScreen(IReadOnlyList<string> availableVolumeNames, int selectedIndex)
    {
        AnsiConsole.Clear();
        AppIO.WriteHeader("Named Volume");

        var content = new StringBuilder();
        ListViewport viewport = CreateListViewport(availableVolumeNames.Count, selectedIndex, RunMenuContentRowCount);
        AppendViewportOverflowIndicator(content, viewport.HiddenBeforeCount, movingUp: true);

        for(int i = viewport.StartIndex; i < viewport.EndIndex; i++)
        {
            string volumeName = Markup.Escape(availableVolumeNames[i]);
            if(i == selectedIndex)
            {
                content.AppendLine($"[dodgerblue1]▶[/] [bold white]{volumeName}[/]");
            }
            else
            {
                content.AppendLine($"  [grey70]{volumeName}[/]");
            }
        }

        AppendViewportOverflowIndicator(content, viewport.HiddenAfterCount, movingUp: false);

        WriteRunMenuSubmenuLayout(new Markup(content.ToString()), RunMenuContentPanelHeight, "[bold dodgerblue1]Named volume[/]", RunMenuSubmenuConfirmFooterHintMarkup);
    }

    private static void RenderContainerMountAccessModeSelectionScreen(ContainerMountSourceType sourceType, IReadOnlyList<ContainerMountAccessModeChoice> choices, int selectedIndex)
    {
        AnsiConsole.Clear();
        AppIO.WriteHeader("Access Mode");

        var content = new StringBuilder();
        for(int i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            bool isSelected = i == selectedIndex;
            if(isSelected)
            {
                content.AppendLine($"[dodgerblue1]▶[/] [bold white]{Markup.Escape(choice.Label)}[/]");
            }
            else
            {
                content.AppendLine($"  [grey70]{Markup.Escape(choice.Label)}[/]");
            }

            if(!String.IsNullOrEmpty(choice.Description))
            {
                content.AppendLine($"    [grey]-[/] [grey58]{Markup.Escape(choice.Description)}[/]");
            }

            if(i < choices.Count - 1)
            {
                content.AppendLine();
            }
        }

        WriteRunMenuSubmenuLayout(new Markup(content.ToString()), 12, "[bold dodgerblue1]Access mode[/]", RunMenuSubmenuConfirmFooterHintMarkup);
    }

    private static void RenderContainerMountPathPrompt(ContainerMountSourceType sourceType, string source, string workspaceContainerPath, string requestedContainerPath, string? validationMessage)
    {
        AnsiConsole.Clear();
        AppIO.WriteHeader("Container Mount Path");

        var summaryGrid = new Grid();
        summaryGrid.AddColumn(new GridColumn().Width(14));
        summaryGrid.AddColumn();
        summaryGrid.AddRow("[grey58]Type[/]", $"[white]{Markup.Escape(GetContainerMountSourceTypeLabel(sourceType))}[/]");
        summaryGrid.AddRow("[grey58]Source[/]", $"[white]{Markup.Escape(source)}[/]");
        summaryGrid.AddRow("[grey58]Workspace[/]", $"[white]{Markup.Escape(workspaceContainerPath)}[/]");

        AnsiConsole.Write(new Panel(summaryGrid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = RunMenuPanelBorderStyle,
            Padding = new Padding(1, 0, 1, 0),
            Header = new PanelHeader("[bold dodgerblue1]Context[/]", Justify.Left)
        });

        AnsiConsole.WriteLine();
        var promptBody = new StringBuilder();
        promptBody.AppendLine("[grey]Absolute path inside the container.[/]");
        if(!String.IsNullOrWhiteSpace(validationMessage))
        {
            promptBody.AppendLine();
            promptBody.AppendLine($"[red]{Markup.Escape(validationMessage)}[/]");
        }

        promptBody.AppendLine();
        promptBody.Append($"[grey58]Path[/] [white]{Markup.Escape(requestedContainerPath)}[/]");

        WriteRunMenuSubmenuLayout(new Markup(promptBody.ToString()), 12, "[bold dodgerblue1]Target path[/]", RunMenuSubmenuTextInputFooterHintMarkup);
    }

    private static string ResolveContainerWorkspacePath(string hostWorkDir)
    {
        string trimmedPath = Path.TrimEndingDirectorySeparator(hostWorkDir);
        string? rootPath = Path.GetPathRoot(trimmedPath);
        if(!String.IsNullOrEmpty(rootPath) && String.Equals(trimmedPath, Path.TrimEndingDirectorySeparator(rootPath), StringComparison.OrdinalIgnoreCase))
        {
            return OpencodeWrapConstants.CONTAINER_WORKSPACE;
        }

        string directoryName = Path.GetFileName(trimmedPath);
        return String.IsNullOrWhiteSpace(directoryName) || directoryName.Contains('/') || directoryName.Contains('\\')
            ? OpencodeWrapConstants.CONTAINER_WORKSPACE
            : $"{OpencodeWrapConstants.CONTAINER_WORKSPACE}/{directoryName}";
    }

    private static string BuildDefaultContainerMountPath(string source)
    {
        string sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        return $"{OpencodeWrapConstants.CONTAINER_ADDITIONAL_MOUNT_ROOT}/{sourceName}";
    }

    private static string? FindConflictingContainerMountPath(IReadOnlyList<ContainerMount> selectedContainerMounts, string candidateContainerPath)
        => selectedContainerMounts
            .Select(containerMount => containerMount.ContainerPath)
            .FirstOrDefault(existingPath => String.Equals(existingPath, candidateContainerPath, StringComparison.Ordinal));

    private static DirectoryPromptResult PromptForDirectoryMountSource(string workspacePath, IReadOnlyList<ContainerMount> selectedContainerMounts, DirectoryBrowserState? state = null)
        => BrowseForDirectoryMountSource(state ?? CreateInitialDirectoryBrowserState(workspacePath, selectedContainerMounts));

    private static DirectoryBrowserState CreateInitialDirectoryBrowserState(string workspacePath, IReadOnlyList<ContainerMount> selectedContainerMounts)
    {
        string? lastDirectoryMount = selectedContainerMounts
            .Where(mount => mount.SourceType is ContainerMountSourceType.Directory)
            .Select(mount => mount.Source)
            .LastOrDefault();

        string startDirectory = !String.IsNullOrWhiteSpace(lastDirectoryMount)
            ? lastDirectoryMount
            : Directory.GetParent(workspacePath)?.FullName ?? workspacePath;

        if(!Directory.Exists(startDirectory))
        {
            startDirectory = Directory.GetCurrentDirectory();
        }

        return new DirectoryBrowserState(startDirectory, 0, false);
    }

    private static DirectoryPromptResult BrowseForDirectoryMountSource(DirectoryBrowserState initialState)
    {
        using var controlCAsInputScope = ConsoleControlCAsInputScope.TryEnable();

        string currentDirectory;
        try
        {
            currentDirectory = Path.GetFullPath(initialState.CurrentDirectory);
        }
        catch(Exception)
        {
            currentDirectory = Directory.GetCurrentDirectory();
        }

        int selectedIndex = initialState.SelectedIndex;
        bool selectingDrive = initialState.SelectingDrive;

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

            RenderDirectoryExplorer(currentDirectory, selectingDrive, entries, selectedIndex);

            var keyInfo = AnsiConsole.Console.Input.ReadKey(intercept: true);
            if(keyInfo is null)
            {
                continue;
            }

            if(IsInterruptKey(keyInfo.Value))
            {
                AnsiConsole.Clear();
                throw new OperationCanceledException("Directory browser interrupted by user.");
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
                case ConsoleKey.LeftArrow:
                    AnsiConsole.Clear();
                    return new DirectoryPromptResult(PromptNavigation.Back, null, new DirectoryBrowserState(currentDirectory, selectedIndex, selectingDrive));
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

                    AnsiConsole.Clear();
                    return new DirectoryPromptResult(PromptNavigation.Confirm, currentDirectory, new DirectoryBrowserState(currentDirectory, selectedIndex, selectingDrive));
                case ConsoleKey.Escape:
                    AnsiConsole.Clear();
                    return new DirectoryPromptResult(PromptNavigation.Cancel, null, new DirectoryBrowserState(currentDirectory, selectedIndex, selectingDrive));
            }
        }
    }

    private static void RenderDirectoryExplorer(
        string currentDirectory,
        bool selectingDrive,
        IReadOnlyList<ExplorerEntry> entries,
        int selectedIndex)
    {
        AnsiConsole.Clear();
        AppIO.WriteHeader("Directory Browser");

        string locationLine = selectingDrive
            ? "[dodgerblue1](drive selection)[/]"
            : $"[white]{Markup.Escape(currentDirectory)}[/]";
        AnsiConsole.Write(new Panel(new Markup(locationLine))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = RunMenuAccentBorderStyle,
            Header = new PanelHeader("[bold dodgerblue1]Location[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0)
        });

        AnsiConsole.MarkupLine("[grey]Enter confirms this folder.[/]");

        var entryTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .Expand();
        entryTable.AddColumn(new TableColumn(""));
        if(entries.Count == 0)
        {
            entryTable.AddRow("[grey](no subdirectories)[/]");
        }
        else
        {
            ListViewport viewport = CreateListViewport(entries.Count, selectedIndex, DirectoryBrowserRowCount);
            if(viewport.HiddenBeforeCount > 0)
            {
                entryTable.AddRow($"  [grey58]↑ {viewport.HiddenBeforeCount} more[/]");
            }

            for(int i = viewport.StartIndex; i < viewport.EndIndex; i++)
            {
                var entry = entries[i];
                bool isSelected = i == selectedIndex;
                string cursor = isSelected ? "[dodgerblue1]▶[/]" : " ";
                string label = entry.EntryType switch
                {
                    ExplorerEntryType.Parent => $"[grey]..[/] [grey70]{Markup.Escape(entry.Label)}[/]",
                    ExplorerEntryType.Drive => $"[dodgerblue1]{Markup.Escape(entry.Label)}[/] [grey](drive)[/]",
                    _ => Markup.Escape(entry.Label)
                };
                if(isSelected)
                {
                    label = $"[bold white]{label}[/]";
                }

                entryTable.AddRow($"{cursor} {label}");
            }

            if(viewport.HiddenAfterCount > 0)
            {
                entryTable.AddRow($"  [grey58]↓ {viewport.HiddenAfterCount} more[/]");
            }
        }

        AnsiConsole.Write(new Panel(entryTable)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = RunMenuPanelBorderStyle,
            Header = new PanelHeader("[bold dodgerblue1]Browse[/]", Justify.Left),
            Padding = new Padding(1, 0, 1, 0),
            Height = DirectoryBrowserPanelHeight
        });

        AnsiConsole.WriteLine();
        AnsiConsole.Write(CreateRunMenuFooterPanel("[grey]ESC[/] [dodgerblue1]menu[/] · [grey]←[/] [dodgerblue1]back[/] · [grey]Ctrl+C[/] [red]exit[/] · [grey]↑↓[/] [dodgerblue1]move[/] · [grey]Backspace[/] [dodgerblue1]parent[/] · [grey]→[/] [dodgerblue1]browse[/] · [grey]Enter[/] [dodgerblue1]confirm[/]"));
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

    private static bool TryNormalizeDirectoryMountSource(string requestedDirectory, out string normalizedPath)
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

    private static int MoveSelectableContainerMountSourceTypeChoice(IReadOnlyList<ContainerMountSourceTypeChoice> choices, int selectedIndex, bool movingForward)
    {
        if(choices.Count == 0)
        {
            return 0;
        }

        int nextIndex = selectedIndex;
        for(int i = 0; i < choices.Count; i++)
        {
            nextIndex = movingForward
                ? nextIndex >= choices.Count - 1 ? 0 : nextIndex + 1
                : nextIndex <= 0 ? choices.Count - 1 : nextIndex - 1;

            if(choices[nextIndex].IsSelectable)
            {
                return nextIndex;
            }
        }

        return selectedIndex;
    }

    private static string GetContainerMountSourceTypeLabel(ContainerMountSourceType sourceType) => sourceType switch
    {
        ContainerMountSourceType.NamedVolume => "Named volume",
        _ => "Directory"
    };

    private enum PromptNavigation
    {
        Confirm,
        Back,
        Cancel
    }

    private enum ContainerMountWizardPage
    {
        SourceType,
        Source,
        TargetPath,
        AccessMode
    }

    private sealed class ConsoleControlCAsInputScope : IDisposable
    {
        private readonly bool _restoreTreatControlCAsInput;
        private readonly bool _previousTreatControlCAsInput;

        private ConsoleControlCAsInputScope(bool restoreTreatControlCAsInput, bool previousTreatControlCAsInput)
        {
            _restoreTreatControlCAsInput = restoreTreatControlCAsInput;
            _previousTreatControlCAsInput = previousTreatControlCAsInput;
        }

        public static ConsoleControlCAsInputScope TryEnable()
        {
            try
            {
                bool previousTreatControlCAsInput = Console.TreatControlCAsInput;
                Console.TreatControlCAsInput = true;
                return new ConsoleControlCAsInputScope(restoreTreatControlCAsInput: true, previousTreatControlCAsInput);
            }
            catch(IOException)
            {
                return new ConsoleControlCAsInputScope(restoreTreatControlCAsInput: false, previousTreatControlCAsInput: false);
            }
            catch(PlatformNotSupportedException)
            {
                return new ConsoleControlCAsInputScope(restoreTreatControlCAsInput: false, previousTreatControlCAsInput: false);
            }
        }

        public void Dispose()
        {
            if(!_restoreTreatControlCAsInput)
            {
                return;
            }

            try
            {
                Console.TreatControlCAsInput = _previousTreatControlCAsInput;
            }
            catch(IOException)
            {
                // Best effort only.
            }
            catch(PlatformNotSupportedException)
            {
                // Best effort only.
            }
        }
    }

    private sealed record RunSelection(string ProfileName, WorkspaceMountMode MountMode, IReadOnlyList<ContainerMount> ContainerMounts, IReadOnlyList<string> SessionAddonNames, DockerNetworkMode NetworkMode, IReadOnlyList<string> NetworkNames, bool Privileged);
    private sealed record ProfileChoice(string Name, bool IsDefault, bool IsConfigured);
    private sealed record ContainerMountSourceTypeChoice(ContainerMountSourceType SourceType, string Label, string Description, bool IsSelectable);
    private sealed record ContainerMountAccessModeChoice(ContainerMountAccessMode AccessMode, string Label, string Description);
    private sealed record SourceTypePromptResult(PromptNavigation Navigation, ContainerMountSourceType? SourceType, int SelectedIndex);
    private sealed record VolumeNamePromptResult(PromptNavigation Navigation, string? VolumeName, int SelectedIndex);
    private sealed record ContainerPathPromptResult(PromptNavigation Navigation, string? ContainerPath, string RequestedContainerPath);
    private sealed record AccessModePromptResult(PromptNavigation Navigation, ContainerMountAccessMode? AccessMode, int SelectedIndex);
    private sealed record DirectoryPromptResult(PromptNavigation Navigation, string? DirectoryPath, DirectoryBrowserState State);
    private sealed record DirectoryBrowserState(string CurrentDirectory, int SelectedIndex, bool SelectingDrive);
    private sealed record ExplorerEntry(string Label, ExplorerEntryType EntryType, string? Path = null);
    private readonly record struct ListViewport(int StartIndex, int ItemCount, int HiddenBeforeCount, int HiddenAfterCount)
    {
        public int EndIndex => StartIndex + ItemCount;
    }
    internal enum RunSelectionTab
    {
        Profile,
        Volumes,
        Addons,
        Networks,
        Docker
    }

    private enum ExplorerEntryType
    {
        Parent,
        Drive,
        Child
    }
}
