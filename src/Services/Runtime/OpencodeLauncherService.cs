using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class OpencodeLauncherService : Singleton
{
    private static readonly TimeSpan _watchdogReadyTimeout = TimeSpan.FromSeconds(2);
    private static readonly string _containerCommand = BuildContainerCommand();

    [Inject]
    private readonly DockerHostService _hostService;

    [Inject]
    private readonly VolumeStateService _volumeService;

    [Inject]
    private readonly ProfileService _profileService;

    [Inject]
    private readonly DockerImageService _dockerImageService;

    [Inject]
    private readonly SessionStagingService _sessionStagingService;

    [Inject]
    private readonly InteractiveDockerRunnerService _interactiveDockerRunnerService;

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

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
        bool verboseSessionLogs = false)
    {
        using var sessionLog = _deferredSessionLogService.BeginSession(verboseSessionLogs ? LogLevel.Debug : LogLevel.Information);
        try
        {
            if(!await AppIO.RunWithLoadingStateAsync("Checking Docker volume...", _volumeService.EnsureVolumeReadyAsync))
            {
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
                hostWorkDir = Path.GetFullPath(Directory.GetCurrentDirectory());
                if(!Directory.Exists(hostWorkDir))
                {
                    AppIO.WriteError($"Workspace directory not found: '{hostWorkDir}'.");
                    return 1;
                }

                containerWorkDir = ResolveContainerWorkspacePath(hostWorkDir);
            }

            if(!TryResolveAdditionalReadonlyMounts(extraReadonlyMountDirs, out var additionalReadonlyMounts))
            {
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

            var (success, profile) = await _profileService.TryResolveProfileAsync(includeProfileConfig ? requestedProfileName : null, session.HostSessionDirectory);
            if(!success)
            {
                return 1;
            }

            if(!TryPrepareSessionProfile(profile, session.HostSessionDirectory, runtimeAgentInstructions, out profile))
            {
                return 1;
            }

            RegisterCleanupHandlers();
            await EnsureCleanupWatchdogAsync(_containerName, session.HostSessionDirectory);

            var (imageReady, imageTag) = await _dockerImageService.TryEnsureImageAsync(profile.DockerfilePath);
            if(!imageReady)
            {
                return 1;
            }

            string? userSpec = await _hostService.GetContainerUserSpecAsync();
            var runArgs = new List<string> { "run" };

            if(userSpec is not null)
            {
                runArgs.AddRange(["--user", userSpec]);
            }

            runArgs.AddRange(
            [
                "--rm",
                "-it",
                "--name", _containerName,
                "-e", $"XDG_CONFIG_HOME={OpencodeWrapConstants.CONTAINER_XDG_CONFIG_HOME}",
                "-e", $"XDG_DATA_HOME={OpencodeWrapConstants.CONTAINER_XDG_DATA_HOME}",
                "-e", $"XDG_STATE_HOME={OpencodeWrapConstants.CONTAINER_XDG_STATE_HOME}",
                "-e", $"{InteractiveDockerRunnerService.STARTUP_READY_MARKER_ENV_VAR}={InteractiveDockerRunnerService.STARTUP_READY_MARKER}",
                "-e", $"OCW_PROFILE_ROOT={OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}",
                "-e", $"OCW_HOST_CONFIG_SOURCE={OpencodeWrapConstants.CONTAINER_HOST_CONFIG_SOURCE}"
            ]);

            runArgs.AddRange(BuildTerminalEnvironmentArgs());

            if(workspaceMountMode != WorkspaceMountMode.None)
            {
                string workspaceMount = _volumeService.BuildBindMount(hostWorkDir!, containerWorkDir);
                if(workspaceMountMode == WorkspaceMountMode.ReadOnly)
                {
                    workspaceMount += ",readonly";
                }

                runArgs.AddRange(["--mount", workspaceMount]);
            }

            runArgs.AddRange(["--mount", $"{_volumeService.BuildBindMount(session.HostPasteDirectory, session.ContainerPasteDirectory)},readonly"]);

            foreach(var (hostPath, containerPath) in additionalReadonlyMounts)
            {
                runArgs.AddRange(["--mount", $"{_volumeService.BuildBindMount(hostPath, containerPath)},readonly"]);
            }

            if(includeProfileConfig)
            {
                runArgs.AddRange(["--mount", $"{_volumeService.BuildBindMount(profile.DirectoryPath, OpencodeWrapConstants.CONTAINER_PROFILE_ROOT)},readonly"]);
            }

            runArgs.AddRange(
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
                runArgs.Add(arg);
            }

            int exitCode = await _interactiveDockerRunnerService.RunDockerAsync(runArgs, session, hostWorkDir);
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

            sessionLog.FlushToConsole();
        }
    }

    private static string BuildContainerCommand()
    {
        string profileEntrypointPath = $"{OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}/{OpencodeWrapConstants.PROFILE_ENTRYPOINT_FILE_NAME}";
        string ocwBinPath = $"{OpencodeWrapConstants.CONTAINER_OCW_ROOT}/bin";
        string startupReadyMarkerEnvVar = InteractiveDockerRunnerService.STARTUP_READY_MARKER_ENV_VAR;
        string startupReadyMarkerCheck = $"${{{startupReadyMarkerEnvVar}:-}}";
        string startupReadyMarkerValue = $"${{{startupReadyMarkerEnvVar}}}";
        return $$"""
        set -e
        mkdir -p "$XDG_CONFIG_HOME" "$XDG_DATA_HOME/opencode" "$XDG_STATE_HOME/opencode" "{{ocwBinPath}}"
        rm -rf "$XDG_CONFIG_HOME/opencode"
        mkdir -p "$XDG_CONFIG_HOME/opencode"
        if [ -d "$OCW_HOST_CONFIG_SOURCE" ]; then cp -a "$OCW_HOST_CONFIG_SOURCE"/. "$XDG_CONFIG_HOME/opencode/"; fi
        export PATH="/opt/opencode/.opencode/bin:/opt/opencode/.local/share/opencode/bin:/opt/opencode/.local/bin:$XDG_DATA_HOME/opencode/bin:{{ocwBinPath}}${HOME:+:$HOME/.opencode/bin:$HOME/.local/share/opencode/bin:$HOME/.local/bin}:$PATH"
        if [ -n "{{startupReadyMarkerCheck}}" ]; then printf '%s' "{{startupReadyMarkerValue}}" >&2; fi
        if [ -f "{{profileEntrypointPath}}" ]; then exec bash "{{profileEntrypointPath}}" "$@"; fi
        exec opencode "$@"
        """;
    }

    private static string BuildRuntimeAgentInstructions(
        string containerWorkDir,
        WorkspaceMountMode workspaceMountMode,
        IReadOnlyList<(string HostPath, string ContainerPath)> additionalReadonlyMounts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# OCW Runtime Environment");
        builder.AppendLine();
        builder.AppendLine("- You are running inside a disposable Docker container started by OpencodeWrap.");
        builder.AppendLine("- You may freely clone temporary reference repositories and create scratch files under `/tmp`.");
        builder.AppendLine($"- You may freely install transient user-space tools into writable locations such as `/tmp` and `{OpencodeWrapConstants.CONTAINER_OCW_ROOT}/bin`.");
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

    private static bool TryPrepareSessionProfile(
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
            AppIO.WriteError($"Failed to prepare session profile in '{sessionProfileDirectoryPath}': {ex.Message}");
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

    private static bool TryResolveAdditionalReadonlyMounts(
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
                AppIO.WriteError("--resource-dir cannot be empty.");
                return false;
            }

            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory));
            if(!Directory.Exists(fullPath))
            {
                AppIO.WriteError($"Resource directory not found: '{fullPath}'.");
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

    private static async Task EnsureCleanupWatchdogAsync(string containerName, string sessionDirectoryPath)
    {
        bool watchdogReady = await ContainerCleanupWatchdog.TryStartDetachedAndWaitReadyAsync(containerName, sessionDirectoryPath, _watchdogReadyTimeout);
        if(!watchdogReady)
        {
            AppIO.WriteWarning("cleanup watchdog failed to initialize; terminal-close cleanup may be less reliable.");
        }
    }
}
