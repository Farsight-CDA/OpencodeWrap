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
        Add(new ProfileCliCommand(_services.Profiles, _services.Image));
    }

    public static Task<int> ExecuteOpencodeAsync(
        OpencodeWrapServices services,
        IReadOnlyList<string> opencodeArgs,
        string? requestedProfileName,
        bool includeProfileConfig)
    {
        return new OpencodeWrapRootCommand(services).ExecuteOpencodeAsync(opencodeArgs, requestedProfileName, includeProfileConfig);
    }

    private async Task<int> ExecuteOpencodeAsync(IReadOnlyList<string> opencodeArgs, string? requestedProfileName, bool includeProfileConfig)
    {
        var profileResolution = await _services.Profiles.TryResolveProfileAsync(includeProfileConfig ? requestedProfileName : null);
        if(!profileResolution.Success)
        {
            return 1;
        }

        ResolvedProfile profile = profileResolution.Profile;

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

        string hostWorkDir = Path.GetFullPath(Directory.GetCurrentDirectory());
        if(!Directory.Exists(hostWorkDir))
        {
            AppIO.WriteError($"Workspace directory not found: '{hostWorkDir}'.");
            return 1;
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
            "-e", $"XDG_CONFIG_HOME={OpencodeWrapConstants.CONTAINER_XDG_CONFIG_HOME}",
            "-e", $"XDG_DATA_HOME={OpencodeWrapConstants.CONTAINER_XDG_DATA_HOME}",
            "-e", $"XDG_STATE_HOME={OpencodeWrapConstants.CONTAINER_XDG_STATE_HOME}",
            "--mount", VolumeStateService.BuildBindMount(hostWorkDir, OpencodeWrapConstants.CONTAINER_WORKSPACE)
        ]);

        if(includeProfileConfig)
        {
            runArgs.AddRange(["--mount", VolumeStateService.BuildBindMount(profile.DirectoryPath, OpencodeWrapConstants.CONTAINER_HOST_CONFIG_SOURCE) + ",readonly"]);
        }

        runArgs.AddRange(
        [
            "--mount", VolumeStateService.BuildVolumeMount(OpencodeWrapConstants.XDG_VOLUME_NAME, OpencodeWrapConstants.CONTAINER_XDG_ROOT),
            "-w", OpencodeWrapConstants.CONTAINER_WORKSPACE,
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
        await CleanupContainerAsync();
        _cleanupStarted = 0;
        return exitCode;
    }

    private void RegisterCleanupHandlers()
    {
        Console.CancelKeyPress += (_, _) => _ = CleanupContainerAsync();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _ = CleanupContainerAsync();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _ = CleanupContainerAsync();
    }

    private async Task CleanupContainerAsync()
    {
        if(Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
        {
            return;
        }

        if(String.IsNullOrWhiteSpace(_containerName))
        {
            return;
        }

        _ = await ProcessRunner.CommandSucceedsAsync("docker", ["rm", "-f", _containerName]);
    }
}
