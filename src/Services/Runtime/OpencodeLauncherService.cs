using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Runtime;

internal sealed class OpencodeLauncherService(DockerHostService hostService, VolumeStateService volumeService)
{
    private static readonly TimeSpan _watchdogReadyTimeout = TimeSpan.FromSeconds(2);
    private static readonly string _containerCommand = BuildContainerCommand();

    private readonly DockerHostService _hostService = hostService;
    private readonly VolumeStateService _volumeService = volumeService;

    private int _cleanupStarted;
    private string? _containerName;
    private readonly List<PosixSignalRegistration> _signalRegistrations = [];

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> opencodeArgs,
        string? requestedProfileName,
        bool includeProfileConfig,
        bool disableWorkspaceMount = false)
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
            if(disableWorkspaceMount)
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

            _containerName = $"opencode-wrap-{Guid.NewGuid():N}"[..27];
            RegisterCleanupHandlers();
            await EnsureCleanupWatchdogAsync(_containerName);

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
                "-e", "TERM=xterm-256color",
                "-e", "COLORTERM=truecolor",
                "-e", "FORCE_HYPERLINK=0",
                "-e", $"XDG_CONFIG_HOME={OpencodeWrapConstants.CONTAINER_XDG_CONFIG_HOME}",
                "-e", $"XDG_DATA_HOME={OpencodeWrapConstants.CONTAINER_XDG_DATA_HOME}",
                "-e", $"XDG_STATE_HOME={OpencodeWrapConstants.CONTAINER_XDG_STATE_HOME}",
                "-e", $"OCW_PROFILE_ROOT={OpencodeWrapConstants.CONTAINER_PROFILE_ROOT}",
                "-e", $"OCW_HOST_CONFIG_SOURCE={OpencodeWrapConstants.CONTAINER_HOST_CONFIG_SOURCE}"
            ]);

            if(!disableWorkspaceMount)
            {
                runArgs.AddRange(["--mount", VolumeStateService.BuildBindMount(hostWorkDir!, containerWorkDir)]);
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

            int exitCode = (await ProcessRunner.RunAsync("docker", runArgs, captureOutput: false)).ExitCode;
            CleanupContainer(force: false);
            return exitCode;
        }
        finally
        {
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

        if(String.IsNullOrWhiteSpace(_containerName))
        {
            return;
        }

        string[] args = force
            ? ["rm", "-f", _containerName]
            : ["rm", _containerName];

        _ = ProcessRunner.CommandSucceedsBlocking("docker", args);

        foreach(var registration in _signalRegistrations)
        {
            registration.Dispose();
        }

        _signalRegistrations.Clear();
    }

    private static async Task EnsureCleanupWatchdogAsync(string containerName)
    {
        bool watchdogReady = await ContainerCleanupWatchdog.TryStartDetachedAndWaitReadyAsync(containerName, _watchdogReadyTimeout);
        if(!watchdogReady)
        {
            AppIO.WriteWarning("cleanup watchdog failed to initialize; terminal-close cleanup may be less reliable.");
        }
    }
}
