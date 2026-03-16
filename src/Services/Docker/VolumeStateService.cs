namespace OpencodeWrap.Services.Docker;

internal sealed partial class VolumeStateService : Singleton
{
    [Inject]
    private readonly DockerHostService _hostService;

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<bool> EnsureVolumeReadyAsync()
    {
        if (!await _hostService.EnsureHostAndDockerAsync())
        {
            return false;
        }

        if (!await EnsureVolumeAsync(OpencodeWrapConstants.XDG_VOLUME_NAME))
        {
            return false;
        }

        if (!_hostService.IsUnixLike)
        {
            return true;
        }

        var (success, userSpec) = await _hostService.TryGetUnixUserSpecAsync();
        return success && await EnsureVolumeIsWritableAsync(OpencodeWrapConstants.XDG_VOLUME_NAME, userSpec);
    }

    public async Task<bool> ValidateImportTargetStateAsync(bool force)
    {
        if (force)
        {
            return true;
        }

        var (success, hasState) = await TryVolumeHasImportedStateAsync(OpencodeWrapConstants.XDG_VOLUME_NAME);
        if (!success)
        {
            return false;
        }

        if (!hasState)
        {
            return true;
        }

        _deferredSessionLogService.WriteErrorOrConsole("docker", $"Docker volume '{OpencodeWrapConstants.XDG_VOLUME_NAME}' already contains imported state. Use -f or --force to overwrite it.");
        return false;
    }

    public async Task<bool> ImportStateFromRootDirectoryAsync(string sourceRoot)
    {
        string sourceShare = Path.Combine(sourceRoot, ".local", "share", "opencode");
        string sourceState = Path.Combine(sourceRoot, ".local", "state", "opencode");
        if (!Directory.Exists(sourceShare) && !Directory.Exists(sourceState))
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Import source must contain at least one of '{sourceShare}' or '{sourceState}'.");
            return false;
        }

        if (!await CopyHostXdgDirectoryToVolumeAsync(sourceRoot, OpencodeWrapConstants.XDG_VOLUME_NAME))
        {
            return false;
        }

        if (!_hostService.IsUnixLike)
        {
            return true;
        }

        var (success, userSpec) = await _hostService.TryGetUnixUserSpecAsync();
        return success && await EnsureVolumeIsWritableAsync(OpencodeWrapConstants.XDG_VOLUME_NAME, userSpec);
    }

    public Task<bool> ExportVolumeSubdirectoryToHostDirectoryAsync(string sourceSubdirectory, string destinationDirectory) => CopyVolumeSubdirectoryToHostDirectoryAsync(OpencodeWrapConstants.XDG_VOLUME_NAME, sourceSubdirectory, destinationDirectory);

    public async Task<(bool Success, bool Removed)> ResetNamedVolumeAsync()
    {
        if (!await _hostService.EnsureHostAndDockerAsync())
        {
            return (false, false);
        }

        string volumeName = OpencodeWrapConstants.XDG_VOLUME_NAME;
        var inspect = await ProcessRunner.RunAsync("docker", ["volume", "inspect", volumeName]);
        if (!inspect.Success)
        {
            return (true, false);
        }

        var remove = await ProcessRunner.RunAsync("docker", ["volume", "rm", "-f", volumeName]);
        if (!remove.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to remove Docker volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", remove.StdErr);

            return (false, false);
        }

        return (true, true);
    }

    public string BuildBindMount(string source, string target) => $"type=bind,src={Path.GetFullPath(source)},dst={target}";

    public string BuildVolumeMount(string source, string target) => $"type=volume,src={source},dst={target}";

    private async Task<bool> EnsureVolumeAsync(string volumeName)
    {
        var inspect = await ProcessRunner.RunAsync("docker", ["volume", "inspect", volumeName]);
        if (inspect.Success)
        {
            return true;
        }

        var create = await ProcessRunner.RunAsync("docker", ["volume", "create", volumeName]);
        if (!create.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to create Docker volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", create.StdErr);

            return false;
        }

        return true;
    }

    private async Task<bool> EnsureVolumeIsWritableAsync(string volumeName, string userSpec)
    {
        var result = await ProcessRunner.RunAsync(
            "docker",
            [
                "run",
                "--rm",
                "--user", "root",
                "--mount", BuildVolumeMount(volumeName, "/target"),
                "ubuntu:24.04",
                "bash",
                "-lc",
                $"""
                set -e
                mkdir -p /target
                chown -R {userSpec} /target
                chmod -R u+rwX /target
                """
            ]);

        if (!result.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to set permissions on Docker volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.StdErr);
        }

        return result.Success;
    }

    private async Task<bool> CopyHostXdgDirectoryToVolumeAsync(string sourceRootDirectory, string volumeName)
    {
        var result = await ProcessRunner.RunAsync(
            "docker",
            [
                "run",
                "--rm",
                "--user", "root",
                "--mount", $"{BuildBindMount(sourceRootDirectory, "/source")},readonly",
                "--mount", BuildVolumeMount(volumeName, "/target"),
                "ubuntu:24.04",
                "bash",
                "-lc",
                """
                set -e
                mkdir -p /target/.local/share /target/.local/state
                rm -rf /target/.local/share/opencode /target/.local/state/opencode
                if [ -d /source/.local/share/opencode ]; then cp -a /source/.local/share/opencode /target/.local/share/opencode; fi
                if [ -d /source/.local/state/opencode ]; then cp -a /source/.local/state/opencode /target/.local/state/opencode; fi
                find /target/.local/share/opencode /target/.local/state/opencode -type f -name '*-shm' -delete 2>/dev/null || true
                find /target/.local/share/opencode /target/.local/state/opencode -type f -name '*-wal' -delete 2>/dev/null || true
                """
            ]);

        if (!result.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to import state from '{sourceRootDirectory}' into volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.StdErr);
        }

        return result.Success;
    }

    private async Task<bool> CopyVolumeSubdirectoryToHostDirectoryAsync(string volumeName, string sourceSubdirectory, string destinationDirectory)
    {
        var runArgs = new List<string> { "run", "--rm" };

        string? userSpec = await _hostService.GetContainerUserSpecAsync();
        if (userSpec is not null)
        {
            runArgs.AddRange(["--user", userSpec]);
        }

        runArgs.AddRange(
        [
            "--mount", BuildVolumeMount(volumeName, "/source"),
            "--mount", BuildBindMount(destinationDirectory, "/target"),
            "ubuntu:24.04",
            "bash",
            "-lc",
            $"""
            set -e
            rm -rf /target/* /target/.[!.]* /target/..?* 2>/dev/null || true
            mkdir -p /target
            source_dir=/source/{sourceSubdirectory}
            if [ -d "$source_dir" ]; then cp -a "$source_dir"/. /target/; fi
            find /target -type f -name '*-shm' -delete 2>/dev/null || true
            find /target -type f -name '*-wal' -delete 2>/dev/null || true
            """
        ]);

        var result = await ProcessRunner.RunAsync(
            "docker",
            runArgs);

        if (!result.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to export state from volume '{volumeName}/{sourceSubdirectory}' to '{destinationDirectory}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.StdErr);
        }

        return result.Success;
    }
    private async Task<(bool Success, bool HasState)> TryVolumeHasImportedStateAsync(string volumeName)
    {
        var result = await ProcessRunner.RunAsync(
            "docker",
            [
                "run",
                "--rm",
                "--user", "root",
                "--mount", BuildVolumeMount(volumeName, "/target"),
                "ubuntu:24.04",
                "bash",
                "-lc",
                """
                set -e
                has_data=0
                for dir in /target/.local/share/opencode /target/.local/state/opencode; do
                    if [ -d "$dir" ] && [ "$(find "$dir" -mindepth 1 -print -quit 2>/dev/null)" ]; then
                        has_data=1
                        break
                    fi
                done
                if [ "$has_data" -eq 1 ]; then printf 'has-data'; else printf 'empty'; fi
                """
            ]);

        if (!result.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to inspect Docker volume '{volumeName}' before import.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.StdErr);

            return (false, false);
        }

        bool hasState = String.Equals(result.StdOut.Trim(), "has-data", StringComparison.Ordinal);
        return (true, hasState);
    }
}
