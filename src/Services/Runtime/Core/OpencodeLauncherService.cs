using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpencodeWrap.Services.Runtime.Core;

internal sealed partial class OpencodeLauncherService : Singleton
{
    private static readonly TimeSpan _watchdogReadyTimeout = TimeSpan.FromSeconds(2);
    private static readonly string _containerCommand = BuildContainerCommand();

    [Inject] private readonly DockerHostService _hostService;
    [Inject] private readonly VolumeStateService _volumeService;
    [Inject] private readonly ProfileService _profileService;
    [Inject] private readonly DockerImageService _dockerImageService;
    [Inject] private readonly ManagedHostOpencodeService _managedHostOpencodeService;
    [Inject] private readonly OpencodeReleaseMetadataService _opencodeReleaseMetadataService;
    [Inject] private readonly OpencodeRuntimeImageService _opencodeRuntimeImageService;
    [Inject] private readonly SessionStagingService _sessionStagingService;
    [Inject] private readonly SessionAddonService _sessionAddonService;
    [Inject] private readonly DeferredSessionLogService _deferredSessionLogService;
    [Inject] private readonly SessionOutputService _sessionOutputService;
    [Inject] private readonly LocalPortReservationService _localPortReservationService;
    [Inject] private readonly HostOpencodeClientService _hostOpencodeClientService;
    [Inject] private readonly OpencodeCatalogReadinessService _opencodeCatalogReadinessService;
    [Inject] private readonly OpencodeLocationProxyService _opencodeLocationProxyService;
    [Inject] private readonly OpencodeServeHealthcheckService _opencodeServeHealthcheckService;
    [Inject] private readonly RuntimeAgentInstructionsService _runtimeAgentInstructionsService;
    [Inject] private readonly RunMenuDefaultsService _runMenuDefaultsService;

    private int _cleanupStarted;
    private string? _containerName;
    private string? _hostSessionDirectory;
    private readonly List<PosixSignalRegistration> _signalRegistrations = [];

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> opencodeArgs,
        string? requestedProfileName,
        bool includeProfileConfig,
        OpencodeRuntimeMode runtimeMode,
        WorkspaceMountMode workspaceMountMode = WorkspaceMountMode.ReadWrite,
        IReadOnlyList<ContainerMount>? containerMounts = null,
        IReadOnlyList<string>? sessionAddons = null,
        DockerNetworkMode dockerNetworkMode = DockerNetworkMode.Bridge,
        IReadOnlyList<string>? dockerNetworks = null,
        bool privileged = false,
        bool verboseSessionLogs = false)
    {
        bool useServeSession = runtimeMode is OpencodeRuntimeMode.HostClientToServe;
        bool useServerPassword = useServeSession || UsesServerPassword(opencodeArgs);
        bool clearConsoleOnSessionLogFlush = true;
        var sessionLog = includeProfileConfig
            ? _deferredSessionLogService.BeginSession(verboseSessionLogs ? LogLevel.Debug : LogLevel.Information)
            : null;
        ReservedLocalPort? reservedPort = null;
        ManagedHostOpencodeService.ManagedHostOpencodeLease? hostLease = null;
        OpencodeLocationProxyLease? locationProxyLease = null;
        string? managedHostExecutablePath = null;
        string? profileCleanupDirectoryPath = null;
        IReadOnlyList<SessionEnvironmentVariable> sessionEnvironmentVariables = [];
        Task<(bool Success, ResolvedOpencodeRelease Release)>? packageReleaseTask = null;
        Task<(bool Success, string ImageTag)>? baseImageTask = null;
        Task<(bool Success, ManagedHostOpencodeService.ManagedHostOpencodeLease? Lease)>? hostLeaseTask = null;
        string? resolvedOpencodeVersion = null;
        string? serverPassword = null;
        try
        {
            LogStartupPhase("starting ocw run startup", LogLevel.Debug);

            if(useServerPassword)
            {
                if(!_runMenuDefaultsService.TryLoadDefaults(out var runMenuDefaults)
                    || String.IsNullOrWhiteSpace(runMenuDefaults.ServerPassword))
                {
                    return 1;
                }

                serverPassword = runMenuDefaults.ServerPassword;
            }

            if(!await _sessionOutputService.RunWithLoadingStateAsync(LogCategories.STARTUP, "Checking Docker volume...", _volumeService.EnsureVolumeReadyAsync))
            {
                return 1;
            }

            LogStartupPhase("docker volume ready", LogLevel.Debug);

            string hostClientWorkDir = Path.GetFullPath(Directory.GetCurrentDirectory());
            if(!Directory.Exists(hostClientWorkDir))
            {
                _deferredSessionLogService.WriteErrorOrConsole("startup", $"Workspace directory not found: '{hostClientWorkDir}'.");
                return 1;
            }

            string? hostWorkDir = null;
            string containerWorkDir;
            if(workspaceMountMode == WorkspaceMountMode.None)
            {
                containerWorkDir = OpencodeWrapConstants.CONTAINER_WORKSPACE;
            }
            else
            {
                hostWorkDir = hostClientWorkDir;
                containerWorkDir = ResolveContainerWorkspacePath(hostWorkDir);
            }

            if(!TryNormalizeContainerMounts(containerMounts, out var selectedContainerMounts))
            {
                return 1;
            }

            var selectedDockerNetworkMode = dockerNetworkMode;

            if(!TryNormalizeDockerNetworks(dockerNetworks, out var selectedDockerNetworks))
            {
                return 1;
            }

            if(selectedDockerNetworks.Count > 0 && !selectedDockerNetworkMode.SupportsAdditionalNetworks())
            {
                _deferredSessionLogService.WriteErrorOrConsole("startup", $"Docker network mode '{selectedDockerNetworkMode.GetLabel()}' does not support additional network attachments.");
                return 1;
            }

            string runtimeAgentInstructions = _runtimeAgentInstructionsService.Build(containerWorkDir, workspaceMountMode, selectedContainerMounts);

            _containerName = $"opencode-wrap-{Guid.NewGuid():N}"[..27];

            packageReleaseTask = BeginPackageReleaseResolutionAsync();

            var (success, profile) = await _profileService.TryResolveProfileAsync(includeProfileConfig ? requestedProfileName : null);
            if(!success)
            {
                return 1;
            }

            profileCleanupDirectoryPath = profile.CleanupDirectoryPath;
            baseImageTask = _dockerImageService.TryEnsureImageAsync(profile.DockerfilePath);

            LogStartupPhase($"resolved profile '{profile.Name}' from '{profile.DirectoryPath}'", LogLevel.Debug);

            if(!_sessionStagingService.TryCreateSession(_containerName, out var session))
            {
                return 1;
            }

            _hostSessionDirectory = session.HostSessionDirectory;

            IReadOnlyList<ResolvedSessionAddon> resolvedSessionAddons = [];
            if(includeProfileConfig && !_sessionAddonService.TryResolveAddons(sessionAddons, session.HostSessionDirectory, out resolvedSessionAddons))
            {
                return 1;
            }

            if(!TryPrepareSessionProfile(profile, session.HostSessionDirectory, includeProfileConfig, runtimeAgentInstructions, resolvedSessionAddons, out profile, out sessionEnvironmentVariables))
            {
                return 1;
            }

            RegisterCleanupHandlers();
            await EnsureCleanupWatchdogAsync(_containerName, session.HostSessionDirectory);
            LogStartupPhase("cleanup watchdog ready", LogLevel.Debug);
            _deferredSessionLogService.Write("session", $"prepared runtime session '{session.SessionId}' at '{session.HostSessionDirectory}'", LogLevel.Information);
            LogStartupPhase($"runtime session '{session.SessionId}' finalized at '{session.HostSessionDirectory}'", LogLevel.Debug);

            var (releaseResolved, packageRelease) = await packageReleaseTask;
            if(!releaseResolved)
            {
                return 1;
            }

            LogStartupPhase($"resolved OpenCode V2 session version {packageRelease.Version}", LogLevel.Debug);
            resolvedOpencodeVersion = packageRelease.Version;

            if(useServeSession)
            {
                bool useWindowsHostNetworking = _hostService.IsWindows
                    && selectedDockerNetworkMode.IsHost();
                if(useWindowsHostNetworking)
                {
                    switch(_hostService.GetDockerDesktopHostNetworkingState())
                    {
                        case DockerDesktopHostNetworkingState.Disabled:
                            _deferredSessionLogService.WriteErrorOrConsole("startup", "Docker network mode 'host' on Windows requires Docker Desktop host networking to be enabled.");
                            _deferredSessionLogService.WriteWarningOrConsole("startup", "Enable Docker Desktop Settings > Resources > Network > Enable host networking, then restart Docker Desktop and try again.");
                            return 1;
                        case DockerDesktopHostNetworkingState.Unknown:
                            _deferredSessionLogService.WriteWarningOrConsole("startup", "Could not confirm whether Docker Desktop host networking is enabled on Windows. If startup fails, enable Docker Desktop host networking and restart Docker Desktop.");
                            break;
                    }
                }

                bool portPrepared = useWindowsHostNetworking
                    ? _localPortReservationService.TrySelectUnusedTcpPort(out var allocatedPort)
                    : _localPortReservationService.TryReserveLoopbackPort(out allocatedPort);
                if(!portPrepared)
                {
                    return 1;
                }

                reservedPort = allocatedPort;
                int port = reservedPort.Port;
                session = session with
                {
                    Port = port,
                    ServerUrl = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}"
                };
                LogStartupPhase($"prepared OpenCode V2 server port {port} at '{session.ServerUrl}'", LogLevel.Debug);

                hostLeaseTask = BeginManagedHostClientPreparationAsync(session.SessionId, packageRelease);
            }

            var (baseImageReady, baseImageTag) = await baseImageTask;
            if(!baseImageReady)
            {
                return 1;
            }

            LogStartupPhase($"base profile image ready: '{baseImageTag}'", LogLevel.Debug);

            var runtimeImageTask = _opencodeRuntimeImageService.TryEnsureRuntimeImageAsync(baseImageTag, packageRelease);
            if(hostLeaseTask is not null)
            {
                await Task.WhenAll(runtimeImageTask, hostLeaseTask);

                var (leaseAcquired, lease) = await hostLeaseTask;
                if(!leaseAcquired || lease is null)
                {
                    return 1;
                }

                hostLease = lease;
                managedHostExecutablePath = lease.ExecutablePath;
                LogStartupPhase("managed host OpenCode lease acquired", LogLevel.Debug);
            }

            var (runtimeImageReady, runtimeImageTag) = await runtimeImageTask;
            if(!runtimeImageReady)
            {
                return 1;
            }

            LogStartupPhase($"OpenCode runtime image ready: '{runtimeImageTag}'", LogLevel.Debug);

            string containerXdgConfigHome = includeProfileConfig
                ? (await ResolvePersistentConfigHomeAsync(profile.ConfigDirectoryPath, resolvedOpencodeVersion)) ?? OpencodeWrapConstants.CONTAINER_PROFILE_ROOT
                : OpencodeWrapConstants.CONTAINER_XDG_CONFIG_HOME;

            if(includeProfileConfig)
            {
                LogStartupPhase($"using OpenCode config home '{containerXdgConfigHome}'", LogLevel.Debug);
            }

            string? userSpec = await _hostService.GetContainerUserSpecAsync();
            var containerArgs = new List<string>();
            IReadOnlyDictionary<string, string?>? dockerEnvironmentVariables = null;

            if(userSpec is not null)
            {
                containerArgs.AddRange(["--user", userSpec]);
            }

            if(privileged)
            {
                containerArgs.Add("--privileged");
            }

            foreach(var environmentVariable in sessionEnvironmentVariables)
            {
                containerArgs.AddRange(["-e", $"{environmentVariable.Key}={environmentVariable.Value}"]);
            }

            containerArgs.AddRange(
            [
                "--name", _containerName,
                "-e", $"XDG_CONFIG_HOME={containerXdgConfigHome}",
                "-e", $"XDG_DATA_HOME={OpencodeWrapConstants.CONTAINER_XDG_DATA_HOME}",
                "-e", $"XDG_STATE_HOME={OpencodeWrapConstants.CONTAINER_XDG_STATE_HOME}",
                "-e", $"XDG_CACHE_HOME={OpencodeWrapConstants.CONTAINER_XDG_CACHE_HOME}",
                "-e", $"OCW_PROFILE_ROOT={OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}",
                "-e", $"{OpencodeWrapConstants.OPENCODE_DISABLE_AUTOUPDATE_ENVIRONMENT_VARIABLE}=1"
            ]);

            if(useServerPassword)
            {
                containerArgs.AddRange(
                [
                    "-e", OpencodeWrapConstants.OPENCODE_PASSWORD_ENVIRONMENT_VARIABLE
                ]);
                dockerEnvironmentVariables = new Dictionary<string, string?>
                {
                    [OpencodeWrapConstants.OPENCODE_PASSWORD_ENVIRONMENT_VARIABLE] = serverPassword
                };
            }

            if(!useServeSession)
            {
                containerArgs.Insert(0, "-it");
                containerArgs.AddRange(BuildTerminalEnvironmentArgs());
            }

            if(selectedDockerNetworkMode.IsHost())
            {
                containerArgs.AddRange(["--network", selectedDockerNetworkMode.GetLabel()]);
            }

            if(useServeSession && !selectedDockerNetworkMode.IsHost())
            {
                string portValue = session.Port!.Value.ToString(CultureInfo.InvariantCulture);
                string portMapping = $"127.0.0.1:{portValue}:{portValue}";
                containerArgs.AddRange(["-p", portMapping]);
            }

            if(workspaceMountMode != WorkspaceMountMode.None)
            {
                string workspaceMount = _volumeService.BuildBindMount(hostWorkDir!, containerWorkDir);
                containerArgs.AddRange(["--mount", workspaceMount]);
            }

            foreach(var containerMount in selectedContainerMounts)
            {
                string mountArgument = containerMount.SourceType switch
                {
                    ContainerMountSourceType.NamedVolume => _volumeService.BuildVolumeMount(containerMount.Source, containerMount.ContainerPath),
                    _ => _volumeService.BuildBindMount(containerMount.Source, containerMount.ContainerPath)
                };

                if(containerMount.AccessMode is ContainerMountAccessMode.ReadOnly)
                {
                    mountArgument += ",readonly";
                }

                containerArgs.AddRange(["--mount", mountArgument]);
            }

            if(includeProfileConfig)
            {
                containerArgs.AddRange(["--mount", _volumeService.BuildBindMount(profile.DirectoryPath, OpencodeWrapConstants.CONTAINER_PROFILE_ROOT)]);
            }

            containerArgs.AddRange(
            [
                "--mount", _volumeService.BuildVolumeMount(OpencodeWrapConstants.XDG_VOLUME_NAME, OpencodeWrapConstants.CONTAINER_XDG_ROOT),
                "-w", containerWorkDir,
                runtimeImageTag,
                "bash",
                "-lc",
                _containerCommand,
                "--"
            ]);

            if(useServeSession)
            {
                containerArgs.Add("serve");
                containerArgs.Add("--hostname");
                containerArgs.Add(ResolveServeHostname(selectedDockerNetworkMode, _hostService.IsWindows));
                containerArgs.Add("--port");
                containerArgs.Add(session.Port!.Value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                foreach(string arg in opencodeArgs)
                {
                    containerArgs.Add(arg);
                }
            }

            reservedPort?.Dispose();
            reservedPort = null;

            if(useServeSession)
            {
                bool requiresPrecreatedContainer = selectedDockerNetworks.Count > 0;
                LogStartupPhase(requiresPrecreatedContainer ? "creating backend container with additional networks" : "starting backend container", LogLevel.Debug);
                bool backendStarted = requiresPrecreatedContainer
                    ? await _sessionOutputService.RunWithLoadingStateAsync(
                        LogCategories.STARTUP,
                        "Starting OpenCode backend...",
                        () => CreateConnectAndStartBackendContainerAsync(containerArgs, selectedDockerNetworks, dockerEnvironmentVariables))
                    : await _sessionOutputService.RunWithLoadingStateAsync(
                        LogCategories.STARTUP,
                        "Starting OpenCode backend...",
                        () => StartBackendContainerAsync(containerArgs, dockerEnvironmentVariables));
                if(!backendStarted)
                {
                    CleanupContainer(force: true);
                    return 1;
                }

                bool backendReady = await _sessionOutputService.RunWithLoadingStateAsync(
                    LogCategories.STARTUP,
                    "Waiting for OpenCode backend...",
                    () => _opencodeServeHealthcheckService.WaitUntilReadyAsync(session.ServerUrl!, serverPassword!, packageRelease.Version));
                if(!backendReady)
                {
                    await WriteContainerLogsAsync(_containerName!);
                    CleanupContainer(force: true);
                    return 1;
                }

                bool catalogReady = await _sessionOutputService.RunWithLoadingStateAsync(
                    LogCategories.STARTUP,
                    "Loading OpenCode catalog...",
                    () => _opencodeCatalogReadinessService.WaitUntilReadyAsync(session.ServerUrl!, serverPassword!, containerWorkDir));
                if(!catalogReady)
                {
                    await WriteContainerLogsAsync(_containerName!);
                    CleanupContainer(force: true);
                    return 1;
                }

                var locationMappings = BuildLocationMappings(hostClientWorkDir, containerWorkDir, selectedContainerMounts);
                var proxy = await _sessionOutputService.RunWithLoadingStateAsync(
                    LogCategories.STARTUP,
                    "Preparing OpenCode client connection...",
                    () => _opencodeLocationProxyService.TryStartAsync(session.ServerUrl!, containerWorkDir, locationMappings));
                if(!proxy.Success || proxy.Lease is null)
                {
                    CleanupContainer(force: true);
                    return 1;
                }

                locationProxyLease = proxy.Lease;
                int clientExitCode = await _hostOpencodeClientService.RunAsync(
                    managedHostExecutablePath ?? String.Empty,
                    locationProxyLease.ServerUrl,
                    serverPassword!,
                    $"ocw-{session.SessionId}",
                    session.HostSessionDirectory);
                if(clientExitCode != 0)
                {
                    clearConsoleOnSessionLogFlush = false;
                }

                CleanupContainer(force: true);
                return clientExitCode;
            }

            LogStartupPhase("starting attached docker process", LogLevel.Debug);
            int exitCode = await StartContainerAsync(containerArgs, dockerEnvironmentVariables);
            CleanupContainer(force: false);
            return exitCode;
        }
        finally
        {
            reservedPort?.Dispose();

            if(locationProxyLease is not null)
            {
                await locationProxyLease.DisposeAsync();
            }

            if(hostLease is not null)
            {
                await hostLease.DisposeAsync();
            }

            if(profileCleanupDirectoryPath is not null)
            {
                AppIO.TryDeleteDirectory(profileCleanupDirectoryPath);
            }

            if(_hostSessionDirectory is not null)
            {
                _deferredSessionLogService.Write("session", $"deleting runtime session directory '{_hostSessionDirectory}'", LogLevel.Information);
                AppIO.TryDeleteDirectory(_hostSessionDirectory);
            }

            sessionLog?.FlushToConsole(clearConsoleOnSessionLogFlush);
            sessionLog?.Dispose();
        }

        void LogStartupPhase(string message, LogLevel level)
            => _deferredSessionLogService.Write("startup", message, level);

        Task<(bool Success, ResolvedOpencodeRelease Release)> BeginPackageReleaseResolutionAsync()
        {
            _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, "Checking for OpenCode V2 updates...", LogLevel.Information);
            return _opencodeReleaseMetadataService.TryResolveCurrentV2Async();
        }

        Task<(bool Success, ManagedHostOpencodeService.ManagedHostOpencodeLease? Lease)> BeginManagedHostClientPreparationAsync(string sessionId, ResolvedOpencodeRelease packageRelease)
        {
            _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, "Preparing local OpenCode V2 client...", LogLevel.Information);
            return _managedHostOpencodeService.TryAcquireLeaseAsync(sessionId, packageRelease);
        }
    }

    private static string BuildContainerCommand()
    {
        string profileEntrypointPath = $"{OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}/{OpencodeWrapConstants.PROFILE_ENTRYPOINT_FILE_NAME}";
        string profileBinPath = $"{OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}/{OpencodeWrapConstants.PROFILE_BIN_DIRECTORY_NAME}";
        return $$"""
        set -e
        mkdir -p \
          "$XDG_CONFIG_HOME" \
          "$XDG_CONFIG_HOME/opencode" \
          "$XDG_DATA_HOME/opencode" \
          "$XDG_STATE_HOME/opencode" \
          "$XDG_CACHE_HOME" \
          "$XDG_CACHE_HOME/opencode"
        if [ -d "$OCW_PROFILE_ROOT/opencode" ] && [ "$XDG_CONFIG_HOME" != "{{OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}}" ]; then
          cp -a "$OCW_PROFILE_ROOT/opencode/." "$XDG_CONFIG_HOME/opencode/"
        fi
        ocw_prepended_paths="/opt/opencode/bin"
        ocw_prepended_paths="$ocw_prepended_paths:$XDG_CACHE_HOME/opencode/bin"
        ocw_prepended_paths="$ocw_prepended_paths:{{profileBinPath}}"
        export PATH="$ocw_prepended_paths:$PATH"
        if [ -f "{{profileEntrypointPath}}" ]; then printf '[ocw] starting profile entrypoint...\n' >&2; exec bash "{{profileEntrypointPath}}" "$@"; fi
        printf '[ocw] launching opencode2...\n' >&2
        exec opencode2 "$@"
        """;
    }

    private static async Task<int> StartContainerAsync(IReadOnlyList<string> containerArgs, IReadOnlyDictionary<string, string?>? dockerEnvironmentVariables)
    {
        List<string> runArgs = ["run", "--rm", .. containerArgs];
        var result = await ProcessRunner.RunAttachedAsync("docker", runArgs, environmentVariables: dockerEnvironmentVariables);
        return !result.Started ? 1 : result.ExitCode;
    }

    private async Task<bool> StartBackendContainerAsync(IReadOnlyList<string> containerArgs, IReadOnlyDictionary<string, string?>? dockerEnvironmentVariables)
    {
        List<string> runArgs = ["run", "-d", "--rm", .. containerArgs];
        var runResult = await ProcessRunner.RunAsync("docker", runArgs, environmentVariables: dockerEnvironmentVariables);
        if(runResult.Success)
        {
            return true;
        }

        WriteSessionError("docker", "Failed to start OpenCode backend container.");
        WriteSessionErrorDetails("docker", runResult.StdErr);
        return false;
    }

    private async Task<bool> CreateConnectAndStartBackendContainerAsync(
        IReadOnlyList<string> containerArgs,
        IReadOnlyList<string> selectedDockerNetworks,
        IReadOnlyDictionary<string, string?>? dockerEnvironmentVariables)
    {
        List<string> createArgs = ["create", .. containerArgs];
        var createResult = await ProcessRunner.RunAsync("docker", createArgs, environmentVariables: dockerEnvironmentVariables);
        if(!createResult.Success)
        {
            WriteSessionError("docker", "Failed to create Docker container.");
            WriteSessionErrorDetails("docker", createResult.StdErr);
            return false;
        }

        foreach(string networkName in selectedDockerNetworks)
        {
            var connectResult = await ProcessRunner.RunAsync("docker", ["network", "connect", networkName, _containerName!]);
            if(connectResult.Success)
            {
                continue;
            }

            WriteSessionError("docker", $"Failed to attach container to Docker network '{networkName}'.");
            WriteSessionErrorDetails("docker", connectResult.StdErr);

            _ = ProcessRunner.CommandSucceedsBlocking("docker", ["rm", "-f", _containerName!]);
            return false;
        }

        var startResult = await ProcessRunner.RunAsync("docker", ["start", _containerName!]);
        if(startResult.Success)
        {
            return true;
        }

        WriteSessionError("docker", "Failed to start OpenCode backend container.");
        WriteSessionErrorDetails("docker", startResult.StdErr);
        _ = ProcessRunner.CommandSucceedsBlocking("docker", ["rm", "-f", _containerName!]);
        return false;
    }

    private void WriteSessionError(string category, string message)
        => _deferredSessionLogService.WriteErrorOrConsole(category, message);

    private void WriteSessionErrorDetails(string category, string? detail)
        => _deferredSessionLogService.WriteErrorDetailsOrConsole(category, detail);

    private bool TryPrepareSessionProfile(
        ResolvedProfile profile,
        string sessionDirectoryPath,
        bool includeProfileConfig,
        string runtimeAgentInstructions,
        IReadOnlyList<ResolvedSessionAddon> sessionAddons,
        out ResolvedProfile sessionProfile,
        out IReadOnlyList<SessionEnvironmentVariable> sessionEnvironmentVariables)
    {
        sessionEnvironmentVariables = [];
        string sessionProfileDirectoryPath = Path.Combine(sessionDirectoryPath, "profile");
        string sessionConfigDirectoryPath = Path.Combine(sessionProfileDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);

        try
        {
            AppIO.TryDeleteDirectory(sessionProfileDirectoryPath);
            Directory.CreateDirectory(sessionProfileDirectoryPath);
            CopyDirectoryContents(profile.DirectoryPath, sessionProfileDirectoryPath);

            if(!TryApplySessionAddons(sessionProfileDirectoryPath, sessionAddons))
            {
                sessionProfile = profile;
                AppIO.TryDeleteDirectory(sessionProfileDirectoryPath);
                return false;
            }

            if(!SessionProfileEnvFile.TryPrepareForLaunch(profile, sessionProfileDirectoryPath, sessionAddons, out sessionEnvironmentVariables, out string? environmentErrorMessage))
            {
                _deferredSessionLogService.WriteErrorOrConsole("profile", environmentErrorMessage ?? $"Failed to prepare merged environment file in '{sessionProfileDirectoryPath}'.");
                sessionProfile = profile;
                AppIO.TryDeleteDirectory(sessionProfileDirectoryPath);
                return false;
            }

            Directory.CreateDirectory(sessionConfigDirectoryPath);

            if(!SessionProfileOpencodeConfigFile.TryPrepareForLaunch(profile, sessionProfileDirectoryPath, sessionAddons, out string? configErrorMessage))
            {
                _deferredSessionLogService.WriteErrorOrConsole("profile", configErrorMessage ?? $"Failed to prepare merged opencode config in '{sessionProfileDirectoryPath}'.");
                sessionProfile = profile;
                AppIO.TryDeleteDirectory(sessionProfileDirectoryPath);
                return false;
            }

            string agentsPath = Path.Combine(sessionConfigDirectoryPath, OpencodeWrapConstants.AGENTS_FILE_NAME);
            SessionProfileAgentsFile.EnsureForLaunch(agentsPath, includeProfileConfig, runtimeAgentInstructions);

            sessionProfile = new ResolvedProfile(
                profile.Name,
                sessionProfileDirectoryPath,
                Path.Combine(sessionProfileDirectoryPath, OpencodeWrapConstants.PROFILE_DOCKERFILE_NAME),
                ConfigDirectoryPath: sessionConfigDirectoryPath,
                CleanupDirectoryPath: null);

            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole("profile", $"Failed to prepare session profile in '{sessionProfileDirectoryPath}': {ex.Message}");
            AppIO.TryDeleteDirectory(sessionProfileDirectoryPath);

            sessionProfile = profile;
            return false;
        }
    }

    private static void CopyDirectoryContents(string sourceDirectoryPath, string destinationDirectoryPath)
    {
        foreach(string directoryPath in Directory.EnumerateDirectories(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectoryPath, directoryPath);
            Directory.CreateDirectory(Path.Combine(destinationDirectoryPath, relativePath));
        }

        foreach(string filePath in Directory.EnumerateFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectoryPath, filePath);
            string destinationPath = Path.Combine(destinationDirectoryPath, relativePath);
            string? destinationParent = Path.GetDirectoryName(destinationPath);
            if(!String.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }

            File.Copy(filePath, destinationPath, overwrite: true);
        }
    }

    private bool TryApplySessionAddons(string sessionProfileDirectoryPath, IReadOnlyList<ResolvedSessionAddon> sessionAddons)
    {
        foreach(var addon in sessionAddons)
        {
            try
            {
                foreach(string directoryPath in Directory.EnumerateDirectories(addon.DirectoryPath, "*", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(addon.DirectoryPath, directoryPath);
                    string destinationDirectoryPath = Path.Combine(sessionProfileDirectoryPath, relativePath);
                    if(File.Exists(destinationDirectoryPath))
                    {
                        WriteSessionAddonConflict(addon.Name, relativePath, "directory conflicts with existing file");
                        return false;
                    }

                    Directory.CreateDirectory(destinationDirectoryPath);
                }

                foreach(string filePath in Directory.EnumerateFiles(addon.DirectoryPath, "*", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(addon.DirectoryPath, filePath);
                    if(IsSessionEnvFile(relativePath))
                    {
                        continue;
                    }

                    if(IsSessionOpencodeConfigFile(relativePath))
                    {
                        continue;
                    }

                    string destinationPath = Path.Combine(sessionProfileDirectoryPath, relativePath);
                    if(Directory.Exists(destinationPath))
                    {
                        WriteSessionAddonConflict(addon.Name, relativePath, "file conflicts with existing directory");
                        return false;
                    }

                    if(File.Exists(destinationPath))
                    {
                        if(IsAgentsFile(relativePath))
                        {
                            SessionProfileAgentsFile.MergeIntoFile(destinationPath, filePath);
                            continue;
                        }

                        WriteSessionAddonConflict(addon.Name, relativePath, "file already exists");
                        return false;
                    }

                    string? destinationParent = Path.GetDirectoryName(destinationPath);
                    if(!String.IsNullOrWhiteSpace(destinationParent))
                    {
                        Directory.CreateDirectory(destinationParent);
                    }

                    File.Copy(filePath, destinationPath, overwrite: false);
                }
            }
            catch(Exception ex)
            {
                _deferredSessionLogService.WriteErrorOrConsole("profile", $"Failed to apply session addon '{addon.Name}' from '{addon.DirectoryPath}': {ex.Message}");
                return false;
            }
        }

        return true;
    }

    private void WriteSessionAddonConflict(string addonName, string relativePath, string reason)
        => _deferredSessionLogService.WriteErrorOrConsole("profile", $"Session addon '{addonName}' conflicts at '{NormalizeDisplayPath(relativePath)}' ({reason}). Rename or remove the conflicting file before launching.");

    private static bool IsAgentsFile(string relativePath)
        => String.Equals(
            NormalizeDisplayPath(relativePath),
            $"{OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME}/{OpencodeWrapConstants.AGENTS_FILE_NAME}",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSessionEnvFile(string relativePath)
        => String.Equals(NormalizeDisplayPath(relativePath), OpencodeWrapConstants.PROFILE_ENV_FILE_NAME, StringComparison.OrdinalIgnoreCase);

    private static bool IsSessionOpencodeConfigFile(string relativePath)
        => String.Equals(
            NormalizeDisplayPath(relativePath),
            $"{OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME}/{OpencodeWrapConstants.PROFILE_OPENCODE_CONFIG_FILE_NAME}",
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDisplayPath(string path)
        => path.Replace('\\', '/');

    private static async Task<string?> ResolvePersistentConfigHomeAsync(string? sessionConfigDirectoryPath, string? opencodeVersion)
    {
        if(String.IsNullOrWhiteSpace(sessionConfigDirectoryPath)
            || String.IsNullOrWhiteSpace(opencodeVersion)
            || !Directory.Exists(sessionConfigDirectoryPath))
        {
            return null;
        }

        string configKey = await ComputePersistentConfigKeyAsync(sessionConfigDirectoryPath, opencodeVersion);
        return $"{OpencodeWrapConstants.CONTAINER_XDG_CONFIG_HOME}/ocw/{configKey}";
    }

    private static async Task<string> ComputePersistentConfigKeyAsync(string sessionConfigDirectoryPath, string opencodeVersion)
    {
        string[] filePaths = [.. Directory.EnumerateFiles(sessionConfigDirectoryPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashString(hash, opencodeVersion);

        foreach(string filePath in filePaths)
        {
            string relativePath = NormalizeDisplayPath(Path.GetRelativePath(sessionConfigDirectoryPath, filePath));
            AppendHashString(hash, relativePath);
            hash.AppendData(await File.ReadAllBytesAsync(filePath));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..16];
    }

    private static void AppendHashString(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([(byte) '\n']);
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

    private bool TryNormalizeContainerMounts(
        IReadOnlyList<ContainerMount>? requestedMounts,
        out List<ContainerMount> normalizedMounts)
    {
        normalizedMounts = [];
        if(requestedMounts is null || requestedMounts.Count == 0)
        {
            return true;
        }

        var seenMounts = new HashSet<ContainerMount>();
        foreach(ContainerMount requestedMount in requestedMounts)
        {
            if(!TryNormalizeContainerMount(requestedMount, out var normalizedMount, out string validationMessage))
            {
                _deferredSessionLogService.WriteErrorOrConsole("startup", validationMessage);
                return false;
            }

            if(!seenMounts.Add(normalizedMount))
            {
                continue;
            }

            ContainerMount? conflictingMount = normalizedMounts.FirstOrDefault(mount => String.Equals(mount.ContainerPath, normalizedMount.ContainerPath, StringComparison.Ordinal));
            if(conflictingMount is not null)
            {
                _deferredSessionLogService.WriteErrorOrConsole("startup", $"Container mount path '{normalizedMount.ContainerPath}' conflicts with another selected mount at '{conflictingMount.ContainerPath}'.");
                return false;
            }

            normalizedMounts.Add(normalizedMount);
        }

        return true;
    }

    private bool TryNormalizeContainerMount(ContainerMount requestedMount, out ContainerMount normalizedMount, out string validationMessage)
    {
        normalizedMount = requestedMount;
        validationMessage = String.Empty;

        if(!ContainerPathUtility.TryNormalizeAbsolutePath(requestedMount.ContainerPath, out string normalizedContainerPath))
        {
            validationMessage = $"Container mount path '{requestedMount.ContainerPath}' is invalid. Use an absolute container path other than '/' and avoid commas.";
            return false;
        }

        string normalizedSource;
        switch(requestedMount.SourceType)
        {
            case ContainerMountSourceType.Directory:
                string requestedDirectory = requestedMount.Source?.Trim() ?? String.Empty;
                if(requestedDirectory.Length == 0)
                {
                    validationMessage = "Directory mounts must include a source directory.";
                    return false;
                }

                try
                {
                    normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory));
                }
                catch(Exception)
                {
                    validationMessage = $"Directory mount source '{requestedMount.Source}' is invalid.";
                    return false;
                }

                if(!Directory.Exists(normalizedSource))
                {
                    validationMessage = $"Directory mount source not found: '{normalizedSource}'.";
                    return false;
                }

                break;
            case ContainerMountSourceType.NamedVolume:
                normalizedSource = requestedMount.Source?.Trim() ?? String.Empty;
                if(normalizedSource.Length == 0)
                {
                    validationMessage = "Docker named volume mounts must include a volume name.";
                    return false;
                }

                break;
            default:
                validationMessage = $"Unsupported container mount source type '{requestedMount.SourceType}'.";
                return false;
        }

        normalizedMount = new ContainerMount(requestedMount.SourceType, normalizedSource, normalizedContainerPath, requestedMount.AccessMode);
        return true;
    }

    private bool TryNormalizeDockerNetworks(IReadOnlyList<string>? requestedNetworks, out List<string> normalizedNetworks)
    {
        normalizedNetworks = [];
        if(requestedNetworks is null || requestedNetworks.Count == 0)
        {
            return true;
        }

        var seenNetworkNames = new HashSet<string>(StringComparer.Ordinal);
        foreach(string requestedNetwork in requestedNetworks)
        {
            if(String.IsNullOrWhiteSpace(requestedNetwork))
            {
                _deferredSessionLogService.WriteErrorOrConsole("startup", "Docker network names cannot be empty.");
                return false;
            }

            string trimmedNetwork = requestedNetwork.Trim();
            if(seenNetworkNames.Add(trimmedNetwork))
            {
                normalizedNetworks.Add(trimmedNetwork);
            }
        }

        return true;
    }

    private static IReadOnlyList<OpencodeLocationMapping> BuildLocationMappings(
        string hostWorkDir,
        string containerWorkDir,
        IReadOnlyList<ContainerMount> containerMounts)
    {
        var mappings = new List<OpencodeLocationMapping>
        {
            new(hostWorkDir, containerWorkDir),
            new(ResolveHostOpencodeWorktreeRoot(), OpencodeWrapConstants.CONTAINER_OPENCODE_WORKTREE_ROOT)
        };

        mappings.AddRange(containerMounts
            .Where(mount => mount.SourceType == ContainerMountSourceType.Directory)
            .Select(mount => new OpencodeLocationMapping(mount.Source, mount.ContainerPath)));
        return mappings;
    }

    private static string ResolveHostOpencodeWorktreeRoot()
    {
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if(String.IsNullOrWhiteSpace(dataHome))
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dataHome = Path.Combine(userHome, ".local", "share");
        }

        return Path.GetFullPath(Path.Combine(dataHome, "opencode", "worktree"));
    }

    private static bool UsesServerPassword(IReadOnlyList<string> opencodeArgs)
        => opencodeArgs.Any(arg => arg == "serve");

    private static IEnumerable<string> BuildTerminalEnvironmentArgs()
    {
        yield return "-e";
        yield return $"TERM={(Environment.GetEnvironmentVariable("TERM") is { Length: > 0 } termValue ? termValue : "xterm-256color")}";

        foreach(string envName in new[] { "COLORTERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "CLICOLOR", "CLICOLOR_FORCE", "NO_COLOR" })
        {
            string? envValue = Environment.GetEnvironmentVariable(envName);
            if(String.IsNullOrWhiteSpace(envValue))
            {
                continue;
            }

            yield return "-e";
            yield return $"{envName}={envValue}";
        }

        if(String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FORCE_HYPERLINK")))
        {
            yield return "-e";
            yield return "FORCE_HYPERLINK=0";
        }
    }

    private static string ResolveServeHostname(DockerNetworkMode dockerNetworkMode, bool isWindows)
        => dockerNetworkMode.IsHost() && !isWindows
            ? "127.0.0.1"
            : "0.0.0.0";

    private void RegisterCleanupHandlers()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupContainer(force: true);
        AppDomain.CurrentDomain.UnhandledException += (_, _) => CleanupContainer(force: true);

        if(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            _signalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, HandleTerminationSignal));
            _signalRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleTerminationSignal));
        }
    }

    private void HandleTerminationSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        CleanupContainer(force: true);
        Environment.Exit(128 + (int) context.Signal);
    }

    private void CleanupContainer(bool force)
    {
        if(Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
        {
            return;
        }

        if(!String.IsNullOrWhiteSpace(_containerName))
        {
            string[] args = force
                ? ["rm", "-f", _containerName]
                : ["rm", _containerName];

            _ = ProcessRunner.CommandSucceedsBlocking("docker", args);
        }

        if(_hostSessionDirectory is not null)
        {
            AppIO.TryDeleteDirectory(_hostSessionDirectory);
        }

        foreach(var registration in _signalRegistrations)
        {
            registration.Dispose();
        }

        _signalRegistrations.Clear();
    }

    private async Task EnsureCleanupWatchdogAsync(string containerName, string sessionDirectoryPath)
    {
        bool watchdogReady = await ContainerCleanupWatchdog.TryStartDetachedAndWaitReadyAsync(containerName, sessionDirectoryPath, _watchdogReadyTimeout);
        if(!watchdogReady)
        {
            _deferredSessionLogService.WriteWarningOrConsole("startup", "cleanup watchdog failed to initialize; terminal-close cleanup may be less reliable.");
        }
    }

    private async Task WriteContainerLogsAsync(string containerName)
    {
        var logsResult = await ProcessRunner.RunAsync("docker", ["logs", containerName]);
        if(!logsResult.Started)
        {
            _deferredSessionLogService.WriteWarningOrConsole("docker", $"Failed to read backend container logs for '{containerName}'.");
            return;
        }

        if(String.IsNullOrWhiteSpace(logsResult.StdOut) && String.IsNullOrWhiteSpace(logsResult.StdErr))
        {
            _deferredSessionLogService.WriteWarningOrConsole("docker", $"Backend container '{containerName}' produced no logs before readiness timed out.");
            return;
        }

        _deferredSessionLogService.Write("docker", $"backend container logs for '{containerName}':", LogLevel.Information);
        WriteSessionErrorDetails("docker", logsResult.StdOut);
        WriteSessionErrorDetails("docker", logsResult.StdErr);
    }
}
