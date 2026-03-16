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

        var (success, hasContent) = await TryVolumeHasAnyContentAsync(OpencodeWrapConstants.XDG_VOLUME_NAME);
        if (!success)
        {
            return false;
        }

        if (!hasContent)
        {
            return true;
        }

        _deferredSessionLogService.WriteErrorOrConsole("docker", $"Docker volume '{OpencodeWrapConstants.XDG_VOLUME_NAME}' already contains data. Use -f or --force to replace it.");
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

        var (resetSuccess, _) = await ResetNamedVolumeAsync();
        if (!resetSuccess)
        {
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

    public Task<bool> ExportVolumeToHostArchiveAsync(string destinationArchive)
        => CreateVolumeArchiveOnHostAsync(OpencodeWrapConstants.XDG_VOLUME_NAME, destinationArchive);

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
                copy_filtered_dir() {
                    src_dir="$1"
                    dst_dir="$2"
                    shift 2
                    mkdir -p "$dst_dir"
                    if [ ! -d "$src_dir" ]; then
                        return 0
                    fi

                    for item in "$src_dir"/* "$src_dir"/.[!.]* "$src_dir"/..?*; do
                        if [ ! -e "$item" ]; then
                            continue
                        fi

                        name="$(basename "$item")"
                        skip=0
                        for excluded in "$@"; do
                            if [ "$name" = "$excluded" ]; then
                                skip=1
                                break
                            fi
                        done

                        if [ "$skip" -eq 1 ]; then
                            continue
                        fi

                        cp -a "$item" "$dst_dir/"
                    done
                }

                rm -rf /target/* /target/.[!.]* /target/..?* 2>/dev/null || true
                mkdir -p /target/.local/share /target/.local/state
                copy_filtered_dir /source/.local/share/opencode /target/.local/share/opencode bin messages
                copy_filtered_dir /source/.local/state/opencode /target/.local/state/opencode
                rm -f /target/.local/share/opencode/debug.log
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

    private async Task<bool> CreateVolumeArchiveOnHostAsync(string volumeName, string destinationArchive)
    {
        string destinationDirectory = Path.GetDirectoryName(destinationArchive) ?? "";
        string archiveFileName = Path.GetFileName(destinationArchive);
        if(String.IsNullOrWhiteSpace(destinationDirectory) || String.IsNullOrWhiteSpace(archiveFileName))
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Invalid export archive path '{destinationArchive}'.");
            return false;
        }

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
            "python:3.12-alpine",
            "python",
            "-c",
            """
            import os
            import sys
            import zipfile

            archive_name = sys.argv[1]
            archive_path = os.path.join('/target', archive_name)
            roots = [
                ('/source/.local/share/opencode', '.local/share/opencode'),
                ('/source/.local/state/opencode', '.local/state/opencode'),
            ]
            added_dirs = set()
            visited_dirs = set()
            excluded_paths = {
                '.local/share/opencode/bin',
                '.local/share/opencode/messages',
            }
            excluded_file_names = {
                'debug.log',
            }

            def add_dir(rel_path: str) -> None:
                normalized = rel_path.strip('/').replace(os.sep, '/')
                if not normalized:
                    return
                entry_name = normalized + '/'
                if entry_name in added_dirs:
                    return
                archive.writestr(entry_name, b'')
                added_dirs.add(entry_name)

            def add_tree(source_root: str, archive_root: str) -> None:
                stack = [(source_root, archive_root)]

                while stack:
                    current_source, current_archive = stack.pop()

                    try:
                        real_path = os.path.realpath(current_source)
                    except OSError:
                        continue

                    if real_path in visited_dirs:
                        continue
                    visited_dirs.add(real_path)
                    add_dir(current_archive)

                    try:
                        entries = sorted(os.scandir(current_source), key=lambda entry: entry.name)
                    except (FileNotFoundError, NotADirectoryError, PermissionError, OSError):
                        continue

                    for entry in reversed(entries):
                        archive_entry = current_archive + '/' + entry.name

                        if archive_entry in excluded_paths or any(archive_entry.startswith(path + '/') for path in excluded_paths):
                            continue

                        try:
                            if entry.is_dir(follow_symlinks=True):
                                stack.append((entry.path, archive_entry))
                                continue

                            if not entry.is_file(follow_symlinks=True):
                                continue
                        except (FileNotFoundError, PermissionError, OSError):
                            continue

                        if entry.name in excluded_file_names or entry.name.endswith('-shm') or entry.name.endswith('-wal'):
                            continue

                        try:
                            archive.write(entry.path, archive_entry)
                        except (FileNotFoundError, PermissionError, OSError):
                            continue

            with zipfile.ZipFile(archive_path, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
                for rel_path in [
                    '.local',
                    '.local/share',
                    '.local/share/opencode',
                    '.local/state',
                    '.local/state/opencode',
                ]:
                    add_dir(rel_path)

                for source_root, archive_root in roots:
                    if not os.path.isdir(source_root):
                        continue

                    add_tree(source_root, archive_root)
            """,
            archiveFileName
        ]);

        var result = await ProcessRunner.RunAsync("docker", runArgs);
        if(!result.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to create export archive '{destinationArchive}' from Docker volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.StdErr);
            return false;
        }

        if(!File.Exists(destinationArchive))
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Docker reported success but export archive '{destinationArchive}' was not created.");
            return false;
        }

        return true;
    }

    private async Task<(bool Success, bool HasContent)> TryVolumeHasAnyContentAsync(string volumeName)
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
                if [ "$(find /target -mindepth 1 -print -quit 2>/dev/null)" ]; then printf 'has-data'; else printf 'empty'; fi
                """
            ]);

        if (!result.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to inspect Docker volume '{volumeName}' before import.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.StdErr);

            return (false, false);
        }

        bool hasContent = String.Equals(result.StdOut.Trim(), "has-data", StringComparison.Ordinal);
        return (true, hasContent);
    }
}
