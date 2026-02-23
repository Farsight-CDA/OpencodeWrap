using System.CommandLine;

internal sealed class OpencodeWrapRootCommand : RootCommand
{
    private readonly OpencodeWrapServices _services;
    private int _cleanupStarted;
    private string? _containerName;

    public OpencodeWrapRootCommand(OpencodeWrapServices services)
        : base("Run opencode in Docker and manage persisted Opencode state. Any command not matched to an ocw subcommand is forwarded to opencode in the container.")
    {
        _services = services;
        Add(new RunCliCommand(_services));
        Add(new DataCliCommand(_services.Volume));
        Add(new ProfileCliCommand(_services.Profiles, _services.Image, _services.Host));
    }

    public static Task<int> ExecuteOpencodeAsync(
        OpencodeWrapServices services,
        IReadOnlyList<string> opencodeArgs,
        string? requestedProfileName,
        bool includeProfileConfig,
        bool disableWorkspaceMount = false)
    {
        return new OpencodeWrapRootCommand(services).ExecuteOpencodeAsync(opencodeArgs, requestedProfileName, includeProfileConfig, disableWorkspaceMount);
    }

    private async Task<int> ExecuteOpencodeAsync(IReadOnlyList<string> opencodeArgs, string? requestedProfileName, bool includeProfileConfig, bool disableWorkspaceMount)
    {
        var profileResolution = await _services.Profiles.TryResolveProfileAsync(includeProfileConfig ? requestedProfileName : null);
        if(!profileResolution.Success)
        {
            return 1;
        }

        var profile = profileResolution.Profile;

        if(!await AppIO.WithStatusAsync("Checking Docker volume...", () => _services.Volume.EnsureVolumeReadyAsync()))
        {
            return 1;
        }

        var imageResult = await _services.Image.TryEnsureImageAsync(profile.DockerfilePath);
        if(!imageResult.Success)
        {
            return 1;
        }

        string imageTag = imageResult.ImageTag;

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

        string? userSpec = await _services.Host.GetContainerUserSpecAsync();
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
            "-e", $"XDG_STATE_HOME={OpencodeWrapConstants.CONTAINER_XDG_STATE_HOME}"
        ]);

        if(!disableWorkspaceMount)
        {
            runArgs.AddRange(["--mount", VolumeStateService.BuildBindMount(hostWorkDir!, containerWorkDir)]);
        }

        if(includeProfileConfig)
        {
            runArgs.AddRange(["--mount", VolumeStateService.BuildBindMount(profile.DirectoryPath, OpencodeWrapConstants.CONTAINER_HOST_CONFIG_SOURCE) + ",readonly"]);
        }

        runArgs.AddRange(
        [
            "--mount", VolumeStateService.BuildVolumeMount(OpencodeWrapConstants.XDG_VOLUME_NAME, OpencodeWrapConstants.CONTAINER_XDG_ROOT),
            "-w", containerWorkDir,
            imageTag,
            "bash",
            "-lc",
            OpencodeWrapConstants.CONTAINER_COMMAND,
            "--"
        ]);

        foreach(string arg in opencodeArgs)
        {
            runArgs.Add(arg);
        }

        int exitCode = await ProcessRunner.RunAttachedProcessAsync("docker", runArgs);
        await CleanupContainerAsync(force: false);
        return exitCode;
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
        if(String.IsNullOrWhiteSpace(directoryName) || directoryName.Contains('/') || directoryName.Contains('\\'))
        {
            return OpencodeWrapConstants.CONTAINER_WORKSPACE;
        }

        return $"{OpencodeWrapConstants.CONTAINER_WORKSPACE}/{directoryName}";
    }

    private void RegisterCleanupHandlers()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _ = CleanupContainerAsync(force: true);
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _ = CleanupContainerAsync(force: true);
    }

    private async Task CleanupContainerAsync(bool force)
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

        _ = await ProcessRunner.CommandSucceedsAsync("docker", args);
    }
}
