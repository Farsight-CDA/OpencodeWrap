using Microsoft.Extensions.Logging;
using OpencodeWrap.Services.Runtime.Infrastructure;
using OpencodeWrap.Services.Runtime.Launcher;
using System.Runtime.InteropServices;
using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class OpencodeLauncherService : Singleton
{
    private static readonly TimeSpan _watchdogReadyTimeout = TimeSpan.FromSeconds(2);
    private static readonly string _containerCommand = BuildContainerCommand();

    [Inject] private readonly DockerHostService _hostService;
    [Inject] private readonly VolumeStateService _volumeService;
    [Inject] private readonly ProfileService _profileService;
    [Inject] private readonly DockerImageService _dockerImageService;
    [Inject] private readonly SessionStagingService _sessionStagingService;
    [Inject] private readonly InteractiveDockerRunnerService _interactiveDockerRunnerService;
    [Inject] private readonly DeferredSessionLogService _deferredSessionLogService;

    private int _cleanupStarted;
    private string? _containerName;
    private string? _hostSessionDirectory;
    private readonly List<PosixSignalRegistration> _signalRegistrations = [];

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> opencodeArgs,
        string? requestedProfileName,
        bool includeProfileConfig,
        WorkspaceMountMode workspaceMountMode = WorkspaceMountMode.ReadWrite,
        IReadOnlyList<string>? extraReadonlyMountDirs = null,
        string? dockerNetworkMode = null,
        IReadOnlyList<string>? dockerNetworks = null,
        bool verboseSessionLogs = false)
    {
        bool useInteractiveRelay = includeProfileConfig;
        var sessionLog = includeProfileConfig
            ? _deferredSessionLogService.BeginSession(verboseSessionLogs ? LogLevel.Debug : LogLevel.Information)
            : null;
        try
        {
            LogStartupPhase("starting ocw run startup", LogLevel.Debug);

            if(!await AppIO.RunWithLoadingStateAsync("Checking Docker volume...", _volumeService.EnsureVolumeReadyAsync))
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

            if(!TryNormalizeDockerNetworkMode(dockerNetworkMode, out string? selectedDockerNetworkMode))
            {
                return 1;
            }

            if(!TryNormalizeDockerNetworks(dockerNetworks, out var selectedDockerNetworks))
            {
                return 1;
            }

            if(selectedDockerNetworks.Count > 0 && !DockerNetworkModeSupportsAdditionalNetworks(selectedDockerNetworkMode))
            {
                _deferredSessionLogService.WriteErrorOrConsole("startup", $"Docker network mode '{selectedDockerNetworkMode}' does not support additional network attachments.");
                return 1;
            }

            string runtimeAgentInstructions = BuildRuntimeAgentInstructions(containerWorkDir, workspaceMountMode, additionalReadonlyMounts);

            _containerName = $"opencode-wrap-{Guid.NewGuid():N}"[..27];
            if(!_sessionStagingService.TryCreateSession(_containerName, out var session))
            {
                return 1;
            }

            _hostSessionDirectory = session.HostSessionDirectory;
            _deferredSessionLogService.Write("session", $"created runtime session '{session.SessionId}' at '{session.HostSessionDirectory}'", LogLevel.Information);
            LogStartupPhase($"runtime session '{session.SessionId}' prepared", LogLevel.Debug);

            var (success, profile) = await _profileService.TryResolveProfileAsync(includeProfileConfig ? requestedProfileName : null, session.HostSessionDirectory);
            if(!success)
            {
                return 1;
            }

            LogStartupPhase($"resolved profile '{profile.Name}' from '{profile.DirectoryPath}'", LogLevel.Debug);

            if(!TryPrepareSessionProfile(profile, session.HostSessionDirectory, runtimeAgentInstructions, out profile))
            {
                return 1;
            }

            LogStartupPhase("session profile prepared", LogLevel.Debug);

            RegisterCleanupHandlers();
            await EnsureCleanupWatchdogAsync(_containerName, session.HostSessionDirectory);
            LogStartupPhase("cleanup watchdog ready", LogLevel.Debug);

            var (imageReady, imageTag) = await _dockerImageService.TryEnsureImageAsync(profile.DockerfilePath);
            if(!imageReady)
            {
                return 1;
            }

            LogStartupPhase($"docker image ready: '{imageTag}'", LogLevel.Debug);

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
                "-it",
                "--name", _containerName,
                "-e", $"XDG_CONFIG_HOME={containerXdgConfigHome}",
                "-e", $"XDG_DATA_HOME={OpencodeWrapConstants.CONTAINER_XDG_DATA_HOME}",
                "-e", $"XDG_STATE_HOME={OpencodeWrapConstants.CONTAINER_XDG_STATE_HOME}",
                "-e", $"OCW_PROFILE_ROOT={OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}"
            ]);

            if(useInteractiveRelay)
            {
                containerArgs.AddRange(["-e", $"{InteractiveDockerRunnerService.STARTUP_READY_MARKER_ENV_VAR}={InteractiveDockerRunnerService.STARTUP_READY_MARKER}"]);
            }

            containerArgs.AddRange(BuildTerminalEnvironmentArgs());

            if(!String.IsNullOrWhiteSpace(selectedDockerNetworkMode))
            {
                containerArgs.AddRange(["--network", selectedDockerNetworkMode]);
            }

            if(workspaceMountMode != WorkspaceMountMode.None)
            {
                string workspaceMount = _volumeService.BuildBindMount(hostWorkDir!, containerWorkDir);
                containerArgs.AddRange(["--mount", workspaceMount]);
            }

            containerArgs.AddRange(["--mount", $"{_volumeService.BuildBindMount(session.HostPasteDirectory, session.ContainerPasteDirectory)},readonly"]);

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
                imageTag,
                "bash",
                "-lc",
                _containerCommand,
                "--"
            ]);

            foreach(string arg in opencodeArgs)
            {
                containerArgs.Add(arg);
            }

            bool requiresPrecreatedContainer = selectedDockerNetworks.Count > 0;
            LogStartupPhase(requiresPrecreatedContainer ? "creating docker container with additional networks" : useInteractiveRelay ? "starting interactive docker relay" : "starting attached docker process", LogLevel.Debug);
            int exitCode = requiresPrecreatedContainer
                ? await CreateConnectAndStartContainerAsync(containerArgs, selectedDockerNetworks, useInteractiveRelay, session, hostWorkDir)
                : await StartContainerAsync(containerArgs, useInteractiveRelay, session, hostWorkDir);
            CleanupContainer(force: false);
            return exitCode;
        }
        finally
        {
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
        string startupReadyMarkerEnvVar = InteractiveDockerRunnerService.STARTUP_READY_MARKER_ENV_VAR;
        string startupReadyMarkerCheck = $"${{{startupReadyMarkerEnvVar}:-}}";
        string startupReadyMarkerValue = $"${{{startupReadyMarkerEnvVar}}}";
        return $$"""
        set -e
        mkdir -p "$XDG_CONFIG_HOME" "$XDG_CONFIG_HOME/opencode" "$XDG_DATA_HOME/opencode" "$XDG_STATE_HOME/opencode" "{{ocwBinPath}}"
        export PATH="/opt/opencode/.opencode/bin:/opt/opencode/.local/share/opencode/bin:/opt/opencode/.local/bin:$XDG_DATA_HOME/opencode/bin:{{ocwBinPath}}:{{profileBinPath}}${HOME:+:$HOME/.opencode/bin:$HOME/.local/share/opencode/bin:$HOME/.local/bin}:$PATH"
        if [ -n "{{startupReadyMarkerCheck}}" ]; then printf '%s' "{{startupReadyMarkerValue}}" >&2; fi
        if [ -f "{{profileEntrypointPath}}" ]; then printf '[ocw] starting profile entrypoint...\n' >&2; exec bash "{{profileEntrypointPath}}" "$@"; fi
        printf '[ocw] launching opencode...\n' >&2
        exec opencode "$@"
        """;
    }

    private async Task<int> StartContainerAsync(IReadOnlyList<string> containerArgs, bool useInteractiveRelay, InteractiveSessionContext session, string? hostWorkDir)
    {
        List<string> runArgs = ["run", "--rm", .. containerArgs];
        return useInteractiveRelay
            ? await _interactiveDockerRunnerService.RunDockerAsync(runArgs, session, hostWorkDir)
            : await _interactiveDockerRunnerService.RunAttachedAsync(runArgs);
    }

    private async Task<int> CreateConnectAndStartContainerAsync(
        IReadOnlyList<string> containerArgs,
        IReadOnlyList<string> selectedDockerNetworks,
        bool useInteractiveRelay,
        InteractiveSessionContext session,
        string? hostWorkDir)
    {
        List<string> createArgs = ["create", .. containerArgs];
        var createResult = await ProcessRunner.RunAsync("docker", createArgs);
        if(!createResult.Success)
        {
            WriteSessionError("docker", "Failed to create Docker container.");
            WriteSessionErrorDetails("docker", createResult.StdErr);
            return 1;
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
            return 1;
        }

        List<string> startArgs = ["start", "-ai", _containerName!];
        return useInteractiveRelay
            ? await _interactiveDockerRunnerService.RunDockerAsync(startArgs, session, hostWorkDir)
            : await _interactiveDockerRunnerService.RunAttachedAsync(startArgs);
    }

    private static string BuildRuntimeAgentInstructions(
        string containerWorkDir,
        WorkspaceMountMode workspaceMountMode,
        List<(string HostPath, string ContainerPath)> additionalReadonlyMounts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# OCW Runtime Environment");
        builder.AppendLine();
        builder.AppendLine("- You are running inside a disposable Docker container started by OpencodeWrap.");
        builder.AppendLine("- You may freely clone temporary reference repositories and create scratch files under `/tmp`.");
        builder.AppendLine($"- You may freely install transient user-space tools into writable locations such as `/tmp` and `{OpencodeWrapConstants.CONTAINER_TOOL_BIN_ROOT}`.");
        builder.AppendLine($"- Profile-local helper binaries live under `{OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}/{OpencodeWrapConstants.PROFILE_BIN_DIRECTORY_NAME}` and are added to `PATH` for profile runs.");
        builder.AppendLine("- Do not assume root access or that system-wide package installation is available.");

        if(workspaceMountMode == WorkspaceMountMode.None)
        {
            builder.AppendLine("- No workspace directory is mounted for this session.");
        }
        else
        {
            builder.AppendLine($"- The current workspace for this session is `{containerWorkDir}`.");
        }

        if(additionalReadonlyMounts.Count == 0)
        {
            builder.AppendLine($"- No additional read-only reference directories are mounted under `{OpencodeWrapConstants.CONTAINER_RESOURCE_ROOT}` for this session.");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"- Additional reference directories are mounted read-only under `{OpencodeWrapConstants.CONTAINER_RESOURCE_ROOT}`.");
        builder.AppendLine("- Treat those resource directories as reference material only and do not attempt to modify them.");
        builder.AppendLine();
        builder.AppendLine("## Current Read-Only Resource Directories");
        builder.AppendLine();

        foreach(var (hostPath, containerPath) in additionalReadonlyMounts)
        {
            builder.AppendLine($"- `{containerPath}` (host source: `{hostPath}`)");
        }

        return builder.ToString().TrimEnd();
    }

    private void WriteSessionError(string category, string message)
        => _deferredSessionLogService.WriteErrorOrConsole(category, message);

    private void WriteSessionErrorDetails(string category, string? detail)
        => _deferredSessionLogService.WriteErrorDetailsOrConsole(category, detail);

    private bool TryPrepareSessionProfile(
        ResolvedProfile profile,
        string sessionDirectoryPath,
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

            string agentsPath = Path.Combine(sessionConfigDirectoryPath, "AGENTS.md");
            if(File.Exists(agentsPath))
            {
                string existingContent = File.ReadAllText(agentsPath);
                string combinedContent = String.IsNullOrWhiteSpace(existingContent)
                    ? runtimeAgentInstructions
                    : existingContent.TrimEnd() + Environment.NewLine + Environment.NewLine + runtimeAgentInstructions;
                File.WriteAllText(agentsPath, combinedContent);
            }
            else
            {
                File.WriteAllText(agentsPath, runtimeAgentInstructions);
            }

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

    private bool TryNormalizeDockerNetworkMode(string? requestedNetworkMode, out string? normalizedNetworkMode)
    {
        if(String.IsNullOrWhiteSpace(requestedNetworkMode))
        {
            normalizedNetworkMode = null;
            return true;
        }

        switch(requestedNetworkMode.Trim().ToLowerInvariant())
        {
            case "bridge":
            case "default":
                normalizedNetworkMode = null;
                return true;
            case "host":
            case "none":
                normalizedNetworkMode = requestedNetworkMode.Trim().ToLowerInvariant();
                return true;
            default:
                _deferredSessionLogService.WriteErrorOrConsole("startup", $"Invalid Docker network mode '{requestedNetworkMode}'. Expected one of: bridge, host, none.");
                normalizedNetworkMode = null;
                return false;
        }
    }

    private static bool DockerNetworkModeSupportsAdditionalNetworks(string? dockerNetworkMode)
        => String.IsNullOrWhiteSpace(dockerNetworkMode) || String.Equals(dockerNetworkMode, "bridge", StringComparison.OrdinalIgnoreCase);

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
        yield return $"TERM={ResolveTerminalEnvironmentValue("TERM", "xterm-256color")}";

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

    private static string ResolveTerminalEnvironmentValue(string name, string fallback)
    {
        string? envValue = Environment.GetEnvironmentVariable(name);
        return String.IsNullOrWhiteSpace(envValue)
            ? fallback
            : envValue;
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

        _interactiveDockerRunnerService.RestoreTerminalStateIfNeeded();

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
}
