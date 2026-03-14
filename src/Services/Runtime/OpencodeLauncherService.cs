using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Runtime;

internal sealed class OpencodeLauncherService(
    DockerHostService hostService,
    VolumeStateService volumeService,
    SessionStagingService sessionStagingService,
    InteractiveDockerRunnerService interactiveDockerRunnerService)
{
    private static readonly TimeSpan _watchdogReadyTimeout = TimeSpan.FromSeconds(2);
    private static readonly string _containerCommand = BuildContainerCommand();

    private readonly DockerHostService _hostService = hostService;
    private readonly VolumeStateService _volumeService = volumeService;
    private readonly SessionStagingService _sessionStagingService = sessionStagingService;
    private readonly InteractiveDockerRunnerService _interactiveDockerRunnerService = interactiveDockerRunnerService;

    private int _cleanupStarted;
    private string? _containerName;
    private string? _hostSessionDirectory;
    private readonly List<PosixSignalRegistration> _signalRegistrations = [];

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> opencodeArgs,
        string? requestedProfileName,
        bool includeProfileConfig,
        WorkspaceMountMode workspaceMountMode = WorkspaceMountMode.ReadWrite,
        IReadOnlyList<string>? extraReadonlyMountDirs = null)
    {
        var (success, profile) = await ProfileService.TryResolveProfileAsync(includeProfileConfig ? requestedProfileName : null);
        if(!success)
        {
            return 1;
        }

        try
        {
            if(!await AppIO.RunWithLoadingStateAsync("Checking Docker volume...", _volumeService.EnsureVolumeReadyAsync))
            {
                return 1;
            }

            var (imageReady, imageTag) = await DockerImageService.TryEnsureImageAsync(profile.DockerfilePath);
            if(!imageReady)
            {
                return 1;
            }

            string? hostWorkDir = null;
            string containerWorkDir;
            if(workspaceMountMode == WorkspaceMountMode.None)
            {
                containerWorkDir = OpencodeWrapConstants.CONTAINER_HOME;
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

            _containerName = $"opencode-wrap-{Guid.NewGuid():N}"[..27];
            if(!_sessionStagingService.TryCreateSession(_containerName, out var session))
            {
                return 1;
            }

            _hostSessionDirectory = session.HostSessionDirectory;
            RegisterCleanupHandlers();
            await EnsureCleanupWatchdogAsync(_containerName, session.HostSessionDirectory);

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
                "-e", $"HOME={OpencodeWrapConstants.CONTAINER_HOME}",
                "-e", $"XDG_CONFIG_HOME={OpencodeWrapConstants.CONTAINER_XDG_CONFIG_HOME}",
                "-e", $"XDG_DATA_HOME={OpencodeWrapConstants.CONTAINER_XDG_DATA_HOME}",
                "-e", $"XDG_STATE_HOME={OpencodeWrapConstants.CONTAINER_XDG_STATE_HOME}",
                "-e", $"OCW_PROFILE_ROOT={OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}",
                "-e", $"OCW_HOST_CONFIG_SOURCE={OpencodeWrapConstants.CONTAINER_HOST_CONFIG_SOURCE}"
            ]);

            runArgs.AddRange(BuildTerminalEnvironmentArgs());

            if(workspaceMountMode != WorkspaceMountMode.None)
            {
                string workspaceMount = VolumeStateService.BuildBindMount(hostWorkDir!, containerWorkDir);
                if(workspaceMountMode == WorkspaceMountMode.ReadOnly)
                {
                    workspaceMount += ",readonly";
                }

                runArgs.AddRange(["--mount", workspaceMount]);
            }

            runArgs.AddRange(["--mount", VolumeStateService.BuildBindMount(session.HostPasteDirectory, session.ContainerPasteDirectory) + ",readonly"]);

            foreach(var mount in additionalReadonlyMounts)
            {
                runArgs.AddRange(["--mount", VolumeStateService.BuildBindMount(mount.HostPath, mount.ContainerPath) + ",readonly"]);
            }

            if(includeProfileConfig)
            {
                runArgs.AddRange(["--mount", VolumeStateService.BuildBindMount(profile.DirectoryPath, OpencodeWrapConstants.CONTAINER_PROFILE_ROOT) + ",readonly"]);
            }

            runArgs.AddRange(
            [
                "--mount", VolumeStateService.BuildVolumeMount(OpencodeWrapConstants.XDG_VOLUME_NAME, OpencodeWrapConstants.CONTAINER_XDG_ROOT),
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
                AppIO.TryDeleteDirectory(_hostSessionDirectory);
            }

            if(profile.CleanupDirectoryPath is not null)
            {
                AppIO.TryDeleteDirectory(profile.CleanupDirectoryPath);
            }
        }
    }

    private static string BuildContainerCommand()
    {
        string profileEntrypointPath = $"{OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}/{OpencodeWrapConstants.PROFILE_ENTRYPOINT_FILE_NAME}";
        return "set -e; "
            + "mkdir -p \"$XDG_CONFIG_HOME\" \"$XDG_DATA_HOME/opencode\" \"$XDG_STATE_HOME/opencode\" \"$HOME/.local/bin\"; "
            + "rm -rf \"$XDG_CONFIG_HOME/opencode\"; "
            + "mkdir -p \"$XDG_CONFIG_HOME/opencode\"; "
            + "if [ -d \"$OCW_HOST_CONFIG_SOURCE\" ]; then cp -a \"$OCW_HOST_CONFIG_SOURCE\"/. \"$XDG_CONFIG_HOME/opencode/\"; fi; "
            + "export PATH=\"/opt/opencode/.opencode/bin:/opt/opencode/.local/share/opencode/bin:/opt/opencode/.local/bin:$HOME/.opencode/bin:$XDG_DATA_HOME/opencode/bin:$HOME/.local/bin:$PATH\"; "
            + $"if [ -f \"{profileEntrypointPath}\" ]; then exec bash \"{profileEntrypointPath}\" \"$@\"; fi; "
            + "exec opencode \"$@\"";
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

        char[] sanitizedChars = baseName
            .Select(ch => Char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')
            .ToArray();
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

        InteractiveDockerRunnerService.RestoreTerminalStateIfNeeded();

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
