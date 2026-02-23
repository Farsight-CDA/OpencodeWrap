internal sealed class VolumeStateService
{
    private readonly DockerHostService _hostService;

    public VolumeStateService(DockerHostService hostService)
    {
        _hostService = hostService;
    }

    public async Task<bool> EnsureVolumeReadyAsync()
    {
        if(!await _hostService.EnsureHostAndDockerAsync())
        {
            return false;
        }

        if(!await EnsureVolumeAsync(OpencodeWrapConstants.XDG_VOLUME_NAME))
        {
            return false;
        }

        if(!_hostService.IsLinux)
        {
            return true;
        }

        var userSpecResult = await _hostService.TryGetLinuxUserSpecAsync();
        if(!userSpecResult.Success)
        {
            return false;
        }

        return await EnsureVolumeIsWritableAsync(OpencodeWrapConstants.XDG_VOLUME_NAME, userSpecResult.UserSpec);
    }

    public async Task<bool> ValidateImportTargetStateAsync(bool force)
    {
        if(force)
        {
            return true;
        }

        var volumeState = await TryVolumeHasImportedStateAsync(OpencodeWrapConstants.XDG_VOLUME_NAME);
        if(!volumeState.Success)
        {
            return false;
        }

        if(!volumeState.HasState)
        {
            return true;
        }

        AppIO.WriteError($"Docker volume '{OpencodeWrapConstants.XDG_VOLUME_NAME}' already contains imported state. Use -f or --force to overwrite it.");
        return false;
    }

    public async Task<bool> ImportStateFromRootDirectoryAsync(string sourceRoot)
    {
        string sourceShare = Path.Combine(sourceRoot, ".local", "share", "opencode");
        string sourceState = Path.Combine(sourceRoot, ".local", "state", "opencode");
        if(!Directory.Exists(sourceShare) && !Directory.Exists(sourceState))
        {
            AppIO.WriteError($"Import source must contain at least one of '{sourceShare}' or '{sourceState}'.");
            return false;
        }

        if(!await CopyHostXdgDirectoryToVolumeAsync(sourceRoot, OpencodeWrapConstants.XDG_VOLUME_NAME))
        {
            return false;
        }

        if(!_hostService.IsLinux)
        {
            return true;
        }

        var userSpecResult = await _hostService.TryGetLinuxUserSpecAsync();
        if(!userSpecResult.Success)
        {
            return false;
        }

        return await EnsureVolumeIsWritableAsync(OpencodeWrapConstants.XDG_VOLUME_NAME, userSpecResult.UserSpec);
    }

    public Task<bool> ExportVolumeSubdirectoryToHostDirectoryAsync(string sourceSubdirectory, string destinationDirectory)
    {
        return CopyVolumeSubdirectoryToHostDirectoryAsync(OpencodeWrapConstants.XDG_VOLUME_NAME, sourceSubdirectory, destinationDirectory);
    }

    public async Task<(bool Success, bool Removed)> ResetNamedVolumeAsync()
    {
        if(!await _hostService.EnsureHostAndDockerAsync())
        {
            return (false, false);
        }

        string volumeName = OpencodeWrapConstants.XDG_VOLUME_NAME;
        var inspect = await ProcessRunner.CommandSucceedsAsync("docker", ["volume", "inspect", volumeName]);
        if(!inspect.Success)
        {
            return (true, false);
        }

        var remove = await ProcessRunner.CommandSucceedsAsync("docker", ["volume", "rm", "-f", volumeName]);
        if(!remove.Success)
        {
            AppIO.WriteError($"Failed to remove Docker volume '{volumeName}'.");
            if(!String.IsNullOrWhiteSpace(remove.StdErr))
            {
                AppIO.WriteError(remove.StdErr.Trim());
            }

            return (false, false);
        }

        return (true, true);
    }

    public static string BuildBindMount(string source, string target)
    {
        return $"type=bind,src={Path.GetFullPath(source)},dst={target}";
    }

    public static string BuildVolumeMount(string source, string target)
    {
        return $"type=volume,src={source},dst={target}";
    }

    private async Task<bool> EnsureVolumeAsync(string volumeName)
    {
        var inspect = await ProcessRunner.CommandSucceedsAsync("docker", ["volume", "inspect", volumeName]);
        if(inspect.Success)
        {
            return true;
        }

        var create = await ProcessRunner.CommandSucceedsAsync("docker", ["volume", "create", volumeName]);
        if(!create.Success)
        {
            AppIO.WriteError($"Failed to create Docker volume '{volumeName}'.");
            if(!String.IsNullOrWhiteSpace(create.StdErr))
            {
                AppIO.WriteError(create.StdErr.Trim());
            }

            return false;
        }

        return true;
    }

    private async Task<bool> EnsureVolumeIsWritableAsync(string volumeName, string userSpec)
    {
        var result = await ProcessRunner.CommandSucceedsAsync(
            "docker",
            [
                "run",
                "--rm",
                "--user", "root",
                "--mount", BuildVolumeMount(volumeName, "/target"),
                "ubuntu:24.04",
                "bash",
                "-lc",
                $"set -e; mkdir -p /target; chown -R {userSpec} /target; chmod -R u+rwX /target"
            ],
            onFailurePrefix: $"Failed to set permissions on Docker volume '{volumeName}'.");

        return result.Success;
    }

    private async Task<bool> CopyHostXdgDirectoryToVolumeAsync(string sourceRootDirectory, string volumeName)
    {
        var result = await ProcessRunner.CommandSucceedsAsync(
            "docker",
            [
                "run",
                "--rm",
                "--user", "root",
                "--mount", BuildBindMount(sourceRootDirectory, "/source") + ",readonly",
                "--mount", BuildVolumeMount(volumeName, "/target"),
                "ubuntu:24.04",
                "bash",
                "-lc",
                "set -e; mkdir -p /target/.local/share /target/.local/state; rm -rf /target/.local/share/opencode /target/.local/state/opencode /target/share/opencode /target/state/opencode; if [ -d /source/.local/share/opencode ]; then cp -a /source/.local/share/opencode /target/.local/share/opencode; fi; if [ -d /source/.local/state/opencode ]; then cp -a /source/.local/state/opencode /target/.local/state/opencode; fi; find /target/.local/share/opencode /target/.local/state/opencode -type f -name '*-shm' -delete 2>/dev/null || true; find /target/.local/share/opencode /target/.local/state/opencode -type f -name '*-wal' -delete 2>/dev/null || true"
            ],
            onFailurePrefix: $"Failed to import state from '{sourceRootDirectory}' into volume '{volumeName}'.");

        return result.Success;
    }

    private async Task<bool> CopyVolumeSubdirectoryToHostDirectoryAsync(string volumeName, string sourceSubdirectory, string destinationDirectory)
    {
        var runArgs = new List<string> { "run", "--rm" };

        string? userSpec = await _hostService.GetContainerUserSpecAsync();
        if(userSpec is not null)
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
            $"set -e; rm -rf /target/* /target/.[!.]* /target/..?* 2>/dev/null || true; mkdir -p /target; source_dir=/source/{sourceSubdirectory}; legacy_dir=/source/{GetLegacyVolumeSubdirectory(sourceSubdirectory)}; if [ -d \"$source_dir\" ]; then cp -a \"$source_dir\"/. /target/; elif [ -d \"$legacy_dir\" ]; then cp -a \"$legacy_dir\"/. /target/; fi; find /target -type f -name '*-shm' -delete 2>/dev/null || true; find /target -type f -name '*-wal' -delete 2>/dev/null || true"
        ]);

        var result = await ProcessRunner.CommandSucceedsAsync(
            "docker",
            runArgs,
            onFailurePrefix: $"Failed to export state from volume '{volumeName}/{sourceSubdirectory}' to '{destinationDirectory}'.");

        return result.Success;
    }

    private static string GetLegacyVolumeSubdirectory(string sourceSubdirectory)
    {
        return sourceSubdirectory switch
        {
            OpencodeWrapConstants.VOLUME_SHARE_SUBDIRECTORY => "share/opencode",
            OpencodeWrapConstants.VOLUME_STATE_SUBDIRECTORY => "state/opencode",
            _ => sourceSubdirectory
        };
    }

    private async Task<(bool Success, bool HasState)> TryVolumeHasImportedStateAsync(string volumeName)
    {
        var result = await ProcessRunner.TryGetCommandOutputAsync(
            "docker",
            [
                "run",
                "--rm",
                "--user", "root",
                "--mount", BuildVolumeMount(volumeName, "/target"),
                "ubuntu:24.04",
                "bash",
                "-lc",
                "set -e; has_data=0; for dir in /target/.local/share/opencode /target/.local/state/opencode /target/share/opencode /target/state/opencode; do if [ -d \"$dir\" ] && [ \"$(find \"$dir\" -mindepth 1 -print -quit 2>/dev/null)\" ]; then has_data=1; break; fi; done; if [ \"$has_data\" -eq 1 ]; then printf 'has-data'; else printf 'empty'; fi"
            ]);

        if(!result.Success)
        {
            AppIO.WriteError($"Failed to inspect Docker volume '{volumeName}' before import.");
            if(!String.IsNullOrWhiteSpace(result.StdErr))
            {
                AppIO.WriteError(result.StdErr.Trim());
            }

            return (false, false);
        }

        bool hasState = String.Equals(result.StdOut.Trim(), "has-data", StringComparison.Ordinal);
        return (true, hasState);
    }
}
