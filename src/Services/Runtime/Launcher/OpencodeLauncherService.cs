using Microsoft.Extensions.Logging;
using OpencodeWrap.Services.Runtime.Infrastructure;
using OpencodeWrap.Services.Runtime.Launcher;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Runtime;

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
    [Inject] private readonly RunUiLauncherService _runUiLauncherService;
    [Inject] private readonly SessionStagingService _sessionStagingService;
    [Inject] private readonly DeferredSessionLogService _deferredSessionLogService;
    [Inject] private readonly SessionOutputService _sessionOutputService;
    [Inject] private readonly LocalPortReservationService _localPortReservationService;
    [Inject] private readonly HostOpencodeAttachService _hostOpencodeAttachService;
    [Inject] private readonly OpencodeServeHealthcheckService _opencodeServeHealthcheckService;
    [Inject] private readonly RuntimeAgentInstructionsService _runtimeAgentInstructionsService;

    private int _cleanupStarted;
    private string? _containerName;
    private string? _hostSessionDirectory;
    private readonly List<PosixSignalRegistration> _signalRegistrations = [];

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> opencodeArgs,
        string? requestedProfileName,
        bool includeProfileConfig,
        OpencodeRuntimeMode runtimeMode,
        RunUiMode runUiMode = RunUiMode.Tui,
        WorkspaceMountMode workspaceMountMode = WorkspaceMountMode.ReadWrite,
        IReadOnlyList<string>? extraReadonlyMountDirs = null,
        DockerNetworkMode dockerNetworkMode = DockerNetworkMode.Bridge,
        IReadOnlyList<string>? dockerNetworks = null,
        bool verboseSessionLogs = false)
    {
        bool useServeSession = runtimeMode is OpencodeRuntimeMode.HostAttachToServe;
        bool useManagedHostClient = useServeSession && runUiMode is RunUiMode.Tui;
        var sessionLog = includeProfileConfig
            ? _deferredSessionLogService.BeginSession(verboseSessionLogs ? LogLevel.Debug : LogLevel.Information)
            : null;
        ReservedLocalPort? reservedPort = null;
        ManagedHostOpencodeService.ManagedHostOpencodeLease? hostLease = null;
        string? managedHostExecutablePath = null;
        string? profileCleanupDirectoryPath = null;
        try
        {
            LogStartupPhase("starting ocw run startup", LogLevel.Debug);

            if(!await _sessionOutputService.RunWithLoadingStateAsync(LogCategories.STARTUP, "Checking Docker volume...", _volumeService.EnsureVolumeReadyAsync))
            {
                return 1;
            }

            LogStartupPhase("docker volume ready", LogLevel.Debug);

            string? hostWorkDir = null;
            string containerWorkDir;
            if(workspaceMountMode == WorkspaceMountMode.None)
            {
                containerWorkDir = OpencodeWrapConstants.CONTAINER_WORKSPACE;
            }
            else
            {
                hostWorkDir = Path.GetFullPath(Directory.GetCurrentDirectory());
                if(!Directory.Exists(hostWorkDir))
                {
                    _deferredSessionLogService.WriteErrorOrConsole("startup", $"Workspace directory not found: '{hostWorkDir}'.");
                    return 1;
                }

                containerWorkDir = ResolveContainerWorkspacePath(hostWorkDir);
            }

            if(!TryResolveAdditionalReadonlyMounts(extraReadonlyMountDirs, out var additionalReadonlyMounts))
            {
                return 1;
            }

            DockerNetworkMode selectedDockerNetworkMode = dockerNetworkMode;

            if(!TryNormalizeDockerNetworks(dockerNetworks, out var selectedDockerNetworks))
            {
                return 1;
            }

            if(selectedDockerNetworks.Count > 0 && !DockerNetworkModeSupportsAdditionalNetworks(selectedDockerNetworkMode))
            {
                _deferredSessionLogService.WriteErrorOrConsole("startup", $"Docker network mode '{selectedDockerNetworkMode.GetLabel()}' does not support additional network attachments.");
                return 1;
            }

            string runtimeAgentInstructions = _runtimeAgentInstructionsService.Build(containerWorkDir, workspaceMountMode, additionalReadonlyMounts);

            _containerName = $"opencode-wrap-{Guid.NewGuid():N}"[..27];

            var (releaseResolved, latestRelease) = await _opencodeReleaseMetadataService.TryResolveLatestAsync();
            if(!releaseResolved)
            {
                return 1;
            }

            LogStartupPhase($"resolved latest OpenCode version {latestRelease.Version}", LogLevel.Debug);

            var (success, profile) = await _profileService.TryResolveProfileAsync(includeProfileConfig ? requestedProfileName : null);
            if(!success)
            {
                return 1;
            }

            profileCleanupDirectoryPath = profile.CleanupDirectoryPath;
            var imageBuildProfile = profile;

            LogStartupPhase($"resolved profile '{profile.Name}' from '{profile.DirectoryPath}'", LogLevel.Debug);

            string? globalAgentInstructions = null;
            if(includeProfileConfig && !TryReadGlobalAgentInstructions(out globalAgentInstructions))
            {
                return 1;
            }

            if(!_sessionStagingService.TryCreateSession(_containerName, out var session))
            {
                return 1;
            }

            _hostSessionDirectory = session.HostSessionDirectory;

            if(!TryPrepareSessionProfile(profile, session.HostSessionDirectory, includeProfileConfig, globalAgentInstructions, runtimeAgentInstructions, out profile))
            {
                return 1;
            }

            RegisterCleanupHandlers();
            await EnsureCleanupWatchdogAsync(_containerName, session.HostSessionDirectory);
            LogStartupPhase("cleanup watchdog ready", LogLevel.Debug);
            _deferredSessionLogService.Write("session", $"prepared runtime session '{session.SessionId}' at '{session.HostSessionDirectory}'", LogLevel.Information);
            LogStartupPhase($"runtime session '{session.SessionId}' finalized at '{session.HostSessionDirectory}'", LogLevel.Debug);

            session = session with
            {
                UiMode = runUiMode
            };

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
                    HostPort = port,
                    ContainerPort = port,
                    AttachUrl = $"http://{ResolveAttachHostname(selectedDockerNetworkMode, _hostService.IsWindows)}:{port.ToString(CultureInfo.InvariantCulture)}"
                };
                LogStartupPhase($"prepared attach port {port} at '{session.AttachUrl}'", LogLevel.Debug);

                if(useManagedHostClient)
                {
                    var (leaseAcquired, lease) = await _managedHostOpencodeService.TryAcquireLeaseAsync(session.SessionId, latestRelease);
                    if(!leaseAcquired || lease is null)
                    {
                        return 1;
                    }

                    hostLease = lease;
                    managedHostExecutablePath = lease.ExecutablePath;
                    LogStartupPhase("managed host OpenCode lease acquired", LogLevel.Debug);
                }
            }

            var (baseImageReady, baseImageTag) = await _dockerImageService.TryEnsureImageAsync(imageBuildProfile.DockerfilePath);
            if(!baseImageReady)
            {
                return 1;
            }

            LogStartupPhase($"base profile image ready: '{baseImageTag}'", LogLevel.Debug);

            var (runtimeImageReady, runtimeImageTag) = await _opencodeRuntimeImageService.TryEnsureRuntimeImageAsync(baseImageTag, latestRelease);
            if(!runtimeImageReady)
            {
                return 1;
            }

            LogStartupPhase($"OpenCode runtime image ready: '{runtimeImageTag}'", LogLevel.Debug);

            string containerXdgConfigHome = includeProfileConfig
                ? OpencodeWrapConstants.CONTAINER_PROFILE_ROOT
                : OpencodeWrapConstants.CONTAINER_XDG_CONFIG_HOME;

            string? userSpec = await _hostService.GetContainerUserSpecAsync();
            var containerArgs = new List<string>();

            if(userSpec is not null)
            {
                containerArgs.AddRange(["--user", userSpec]);
            }

            containerArgs.AddRange(
            [
                "--name", _containerName,
                "-e", $"XDG_CONFIG_HOME={containerXdgConfigHome}",
                "-e", $"XDG_DATA_HOME={OpencodeWrapConstants.CONTAINER_XDG_DATA_HOME}",
                "-e", $"XDG_STATE_HOME={OpencodeWrapConstants.CONTAINER_XDG_STATE_HOME}",
                "-e", $"OCW_PROFILE_ROOT={OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}"
            ]);

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
                string portMapping = $"127.0.0.1:{session.HostPort!.Value.ToString(CultureInfo.InvariantCulture)}:{session.ContainerPort!.Value.ToString(CultureInfo.InvariantCulture)}";
                containerArgs.AddRange(["-p", portMapping]);
            }

            if(workspaceMountMode != WorkspaceMountMode.None)
            {
                string workspaceMount = _volumeService.BuildBindMount(hostWorkDir!, containerWorkDir);
                containerArgs.AddRange(["--mount", workspaceMount]);
            }

            foreach(var (hostPath, containerPath) in additionalReadonlyMounts)
            {
                containerArgs.AddRange(["--mount", $"{_volumeService.BuildBindMount(hostPath, containerPath)},readonly"]);
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
                containerArgs.Add(session.ContainerPort!.Value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                foreach(string arg in opencodeArgs)
                {
                    containerArgs.Add(arg);
                }
            }

            bool requiresPrecreatedContainer = selectedDockerNetworks.Count > 0;
            string startupDescription = useServeSession
                ? requiresPrecreatedContainer ? "creating backend container with additional networks" : "starting backend container"
                : requiresPrecreatedContainer ? "creating docker container with additional networks" : "starting attached docker process";
            LogStartupPhase(startupDescription, LogLevel.Debug);

            reservedPort?.Dispose();
            reservedPort = null;

            if(useServeSession)
            {
                bool backendStarted = requiresPrecreatedContainer
                    ? (await CreateConnectAndStartContainerAsync(containerArgs, selectedDockerNetworks, startDetached: true)).Success
                    : await StartBackendContainerAsync(containerArgs);
                if(!backendStarted)
                {
                    CleanupContainer(force: true);
                    return 1;
                }

                string? readyAttachUrl = await _opencodeServeHealthcheckService.WaitUntilReadyAsync(session.AttachUrl!, selectedDockerNetworkMode, _hostService.IsWindows);
                if(readyAttachUrl is null)
                {
                    await WriteContainerLogsAsync(_containerName!);
                    CleanupContainer(force: true);
                    return 1;
                }

                if(!String.Equals(readyAttachUrl, session.AttachUrl, StringComparison.OrdinalIgnoreCase))
                {
                    session = session with
                    {
                        AttachUrl = readyAttachUrl
                    };
                }

                var launchResult = await _runUiLauncherService.LaunchAsync(runUiMode, session.AttachUrl!, containerWorkDir, managedHostExecutablePath);
                if(!launchResult.Success)
                {
                    if(!String.IsNullOrWhiteSpace(session.AttachUrl))
                    {
                        _sessionOutputService.WriteInfo(LogCategories.ATTACH, $"Local session URL: {session.AttachUrl}");
                    }

                    CleanupContainer(force: true);
                    return launchResult.ExitCode;
                }

                if(launchResult.WaitForBackendShutdown)
                {
                    int backendExitCode = await WaitForContainerExitAsync(_containerName!);
                    CleanupContainer(force: false);
                    return backendExitCode;
                }

                CleanupContainer(force: true);
                return launchResult.ExitCode;
            }

            int exitCode = requiresPrecreatedContainer
                ? (await CreateConnectAndStartContainerAsync(containerArgs, selectedDockerNetworks, startDetached: false)).ExitCode
                : await StartContainerAsync(containerArgs);
            CleanupContainer(force: false);
            return exitCode;
        }
        finally
        {
            reservedPort?.Dispose();

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

            sessionLog?.FlushToConsole();
            sessionLog?.Dispose();
        }

        void LogStartupPhase(string message, LogLevel level)
            => _deferredSessionLogService.Write("startup", message, level);
    }

    private static string BuildContainerCommand()
    {
        string profileEntrypointPath = $"{OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}/{OpencodeWrapConstants.PROFILE_ENTRYPOINT_FILE_NAME}";
        string profileBinPath = $"{OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}/{OpencodeWrapConstants.PROFILE_BIN_DIRECTORY_NAME}";
        string ocwBinPath = OpencodeWrapConstants.CONTAINER_TOOL_BIN_ROOT;
        return $$"""
        set -e
        mkdir -p "$XDG_CONFIG_HOME" "$XDG_CONFIG_HOME/opencode" "$XDG_DATA_HOME/opencode" "$XDG_STATE_HOME/opencode" "{{ocwBinPath}}"
        export PATH="/opt/opencode/bin:$XDG_DATA_HOME/opencode/bin:{{ocwBinPath}}:{{profileBinPath}}${HOME:+:$HOME/.local/share/opencode/bin:$HOME/.local/bin}:$PATH"
        if [ -f "{{profileEntrypointPath}}" ]; then printf '[ocw] starting profile entrypoint...\n' >&2; exec bash "{{profileEntrypointPath}}" "$@"; fi
        printf '[ocw] launching opencode...\n' >&2
        exec opencode "$@"
        """;
    }

    private static async Task<int> StartContainerAsync(IReadOnlyList<string> containerArgs)
    {
        List<string> runArgs = ["run", "--rm", .. containerArgs];
        var result = await ProcessRunner.RunAttachedAsync("docker", runArgs);
        return !result.Started ? 1 : result.ExitCode;
    }

    private async Task<bool> StartBackendContainerAsync(IReadOnlyList<string> containerArgs)
    {
        List<string> runArgs = ["run", "-d", "--rm", .. containerArgs];
        var runResult = await ProcessRunner.RunAsync("docker", runArgs);
        if(runResult.Success)
        {
            return true;
        }

        WriteSessionError("docker", "Failed to start OpenCode backend container.");
        WriteSessionErrorDetails("docker", runResult.StdErr);
        return false;
    }

    private async Task<int> WaitForContainerExitAsync(string containerName)
    {
        var waitResult = await ProcessRunner.RunAsync("docker", ["wait", containerName]);
        if(!waitResult.Started)
        {
            _deferredSessionLogService.WriteWarningOrConsole("docker", $"Failed to wait for backend container '{containerName}' to exit.");
            WriteSessionErrorDetails("docker", waitResult.StdErr);
            return 1;
        }

        string exitCodeText = FirstNonEmptyLine(waitResult.StdOut, waitResult.StdErr);
        return Int32.TryParse(exitCodeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int exitCode)
            ? exitCode
            : waitResult.ExitCode;
    }

    private async Task<ProcessRunner.ProcessRunResult> CreateConnectAndStartContainerAsync(
        IReadOnlyList<string> containerArgs,
        IReadOnlyList<string> selectedDockerNetworks,
        bool startDetached)
    {
        List<string> createArgs = ["create", .. containerArgs];
        var createResult = await ProcessRunner.RunAsync("docker", createArgs);
        if(!createResult.Success)
        {
            WriteSessionError("docker", "Failed to create Docker container.");
            WriteSessionErrorDetails("docker", createResult.StdErr);
            return new ProcessRunner.ProcessRunResult(false, 1, "", createResult.StdErr);
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
            return new ProcessRunner.ProcessRunResult(false, 1, "", connectResult.StdErr);
        }

        var startResult = startDetached
            ? await ProcessRunner.RunAsync("docker", ["start", _containerName!])
            : await ProcessRunner.RunAttachedAsync("docker", ["start", "-ai", _containerName!]);
        if(startResult.Success)
        {
            return startResult;
        }

        WriteSessionError("docker", startDetached ? "Failed to start OpenCode backend container." : "Failed to start attached Docker process.");
        WriteSessionErrorDetails("docker", startResult.StdErr);
        _ = ProcessRunner.CommandSucceedsBlocking("docker", ["rm", "-f", _containerName!]);
        return startResult;
    }

    private void WriteSessionError(string category, string message)
        => _deferredSessionLogService.WriteErrorOrConsole(category, message);

    private void WriteSessionErrorDetails(string category, string? detail)
        => _deferredSessionLogService.WriteErrorDetailsOrConsole(category, detail);

    private bool TryReadGlobalAgentInstructions(out string? globalAgentInstructions)
    {
        globalAgentInstructions = null;
        if(!_hostService.TryEnsureGlobalConfigDirectory(out string configDirectoryPath))
        {
            return false;
        }

        string agentsPath = Path.Combine(configDirectoryPath, OpencodeWrapConstants.HOST_GLOBAL_AGENTS_FILE_NAME);
        if(!File.Exists(agentsPath))
        {
            return true;
        }

        try
        {
            globalAgentInstructions = File.ReadAllText(agentsPath);
            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole("profile", $"Failed to read global AGENTS file '{agentsPath}': {ex.Message}");
            return false;
        }
    }

    private bool TryPrepareSessionProfile(
        ResolvedProfile profile,
        string sessionDirectoryPath,
        bool includeProfileConfig,
        string? globalAgentInstructions,
        string runtimeAgentInstructions,
        out ResolvedProfile sessionProfile)
    {
        string sessionProfileDirectoryPath = Path.Combine(sessionDirectoryPath, "profile");
        string sessionConfigDirectoryPath = Path.Combine(sessionProfileDirectoryPath, OpencodeWrapConstants.PROFILE_OPENCODE_DIRECTORY_NAME);

        try
        {
            if(!String.Equals(profile.DirectoryPath, sessionProfileDirectoryPath, StringComparison.Ordinal))
            {
                AppIO.TryDeleteDirectory(sessionProfileDirectoryPath);
                Directory.CreateDirectory(sessionProfileDirectoryPath);
                CopyDirectoryContents(profile.DirectoryPath, sessionProfileDirectoryPath);
            }

            Directory.CreateDirectory(sessionConfigDirectoryPath);

            string agentsPath = Path.Combine(sessionConfigDirectoryPath, OpencodeWrapConstants.HOST_GLOBAL_AGENTS_FILE_NAME);
            SessionProfileAgentsFile.EnsureForLaunch(agentsPath, includeProfileConfig, globalAgentInstructions, runtimeAgentInstructions);

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
            if(!String.Equals(profile.DirectoryPath, sessionProfileDirectoryPath, StringComparison.Ordinal))
            {
                AppIO.TryDeleteDirectory(sessionProfileDirectoryPath);
            }

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

    private bool TryResolveAdditionalReadonlyMounts(
        IReadOnlyList<string>? requestedDirectories,
        out List<(string HostPath, string ContainerPath)> mounts)
    {
        mounts = [];
        if(requestedDirectories is null || requestedDirectories.Count == 0)
        {
            return true;
        }

        var seenHostPaths = new HashSet<string>(GetHostPathComparer());
        var seenContainerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach(string requestedDirectory in requestedDirectories)
        {
            if(String.IsNullOrWhiteSpace(requestedDirectory))
            {
                _deferredSessionLogService.WriteErrorOrConsole("startup", "Resource directory cannot be empty.");
                return false;
            }

            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory));
            if(!Directory.Exists(fullPath))
            {
                _deferredSessionLogService.WriteErrorOrConsole("startup", $"Resource directory not found: '{fullPath}'.");
                return false;
            }

            if(!seenHostPaths.Add(fullPath))
            {
                continue;
            }

            string containerName = BuildUniqueContainerResourceDirectoryName(fullPath, seenContainerNames);
            mounts.Add((fullPath, $"{OpencodeWrapConstants.CONTAINER_RESOURCE_ROOT}/{containerName}"));
        }

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

    private static bool DockerNetworkModeSupportsAdditionalNetworks(DockerNetworkMode dockerNetworkMode)
        => dockerNetworkMode.SupportsAdditionalNetworks();

    private static string BuildUniqueContainerResourceDirectoryName(string hostPath, HashSet<string> seenContainerNames)
    {
        string baseName = Path.GetFileName(hostPath);
        if(String.IsNullOrWhiteSpace(baseName))
        {
            baseName = "resource";
        }

        char[] sanitizedChars = [.. baseName.Select(ch => Char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')];
        string sanitizedName = new string(sanitizedChars).Trim('-', '.', '_');
        if(String.IsNullOrWhiteSpace(sanitizedName))
        {
            sanitizedName = "resource";
        }

        string candidateName = sanitizedName;
        int suffix = 2;
        while(!seenContainerNames.Add(candidateName))
        {
            candidateName = $"{sanitizedName}-{suffix}";
            suffix++;
        }

        return candidateName;
    }

    private static StringComparer GetHostPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

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

    private static string ResolveAttachHostname(DockerNetworkMode dockerNetworkMode, bool isWindows)
        => dockerNetworkMode.IsHost() && isWindows
            ? "localhost"
            : "127.0.0.1";

    private static string FirstNonEmptyLine(params string[] values)
    {
        foreach(string value in values)
        {
            if(String.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string firstLine = value
                .Replace("\r", String.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? String.Empty;
            if(!String.IsNullOrWhiteSpace(firstLine))
            {
                return firstLine;
            }
        }

        return String.Empty;
    }

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
