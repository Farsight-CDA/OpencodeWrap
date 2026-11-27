using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Docker;

internal sealed partial class VolumeStateService : Singleton
{
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const string WSL_LOCALHOST_DEVICE_PREFIX = @"\??\UNC\wsl.localhost\";
    private const string WSL_DEVICE_PREFIX = @"\??\UNC\wsl$\";

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public Task<DockerResult<DockerCompleted>> EnsureVolumeReadyAsync()
        => EnsureVolumeAsync(OpencodeWrapConstants.XDG_VOLUME_NAME);

    public async Task<DockerResult<DockerCompleted>> ValidateImportTargetStateAsync(bool force)
    {
        if(force)
        {
            return new DockerValue<DockerCompleted>(new DockerCompleted());
        }

        var content = await TryVolumeHasAnyContentAsync(OpencodeWrapConstants.XDG_VOLUME_NAME);
        if(content is not DockerValue<bool> hasContent)
        {
            return content.Select(_ => new DockerCompleted());
        }

        if(!hasContent.Value)
        {
            return new DockerValue<DockerCompleted>(new DockerCompleted());
        }

        string message = $"Docker volume '{OpencodeWrapConstants.XDG_VOLUME_NAME}' already contains data. Use -f or --force to replace it.";
        _deferredSessionLogService.WriteErrorOrConsole("docker", message);
        return new DockerFailure(message);
    }

    public async Task<DockerResult<DockerCompleted>> ImportStateFromRootDirectoryAsync(string sourceRoot)
    {
        string sourceShare = Path.Combine(sourceRoot, ".local", "share", "opencode");
        string sourceState = Path.Combine(sourceRoot, ".local", "state", "opencode");
        if(!Directory.Exists(sourceShare) && !Directory.Exists(sourceState))
        {
            string message = $"Import source must contain at least one of '{sourceShare}' or '{sourceState}'.";
            _deferredSessionLogService.WriteErrorOrConsole("docker", message);
            return new DockerFailure(message);
        }

        var reset = await ResetNamedVolumeAsync();
        return reset is not DockerValue<DockerVolumeReset>
            ? reset.Select(_ => new DockerCompleted())
            : await CopyHostXdgDirectoryToVolumeAsync(sourceRoot, OpencodeWrapConstants.XDG_VOLUME_NAME);
    }

    public Task<DockerResult<DockerCompleted>> ExportVolumeToHostArchiveAsync(string destinationArchive)
        => CreateVolumeArchiveOnHostAsync(OpencodeWrapConstants.XDG_VOLUME_NAME, destinationArchive);

    public async Task<DockerResult<DockerVolumeReset>> ResetNamedVolumeAsync()
    {
        string volumeName = OpencodeWrapConstants.XDG_VOLUME_NAME;
        var inspect = await DockerCommand.InspectAsync("volume", volumeName);
        if(inspect is DockerNotFound)
        {
            return new DockerValue<DockerVolumeReset>(DockerVolumeReset.AlreadyAbsent);
        }

        if(inspect is not DockerValue<DockerOutput>)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to inspect Docker volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", inspect.Require().ErrorDetails);
            return inspect.Require().Select(_ => DockerVolumeReset.Removed);
        }

        var remove = await DockerCommand.RunAsync(["volume", "rm", "-f", volumeName]);
        if(remove is not DockerValue<DockerOutput>)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to remove Docker volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", remove.ErrorDetails);
        }

        return remove.Select(_ => DockerVolumeReset.Removed);
    }

    public string BuildBindMount(string source, string target)
        => $"type=bind,{EscapeMountField($"src={ResolveBindSource(source)}")},{EscapeMountField($"dst={target}")}";

    public string BuildVolumeMount(string source, string target)
        => $"type=volume,{EscapeMountField($"src={source}")},{EscapeMountField($"dst={target}")}";

    private static unsafe string ResolveBindSource(string source)
    {
        string fullPath = Path.GetFullPath(source);
        if(!OperatingSystem.IsWindows() || fullPath.Length < 3 || fullPath[1] != ':')
        {
            return fullPath;
        }

        string drive = fullPath[..2];
        Span<char> buffer = stackalloc char[512];
        while(true)
        {
            uint length;
            fixed(char* target = buffer)
            {
                length = QueryDosDevice(drive, target, (uint) buffer.Length);
            }

            if(length != 0)
            {
                // The first string is the current mapping; subsequent strings are prior mappings.
                var deviceTarget = buffer[..(int) length];
                deviceTarget = deviceTarget[..deviceTarget.IndexOf('\0')];
                if(deviceTarget.StartsWith(WSL_LOCALHOST_DEVICE_PREFIX, StringComparison.OrdinalIgnoreCase))
                {
                    deviceTarget = deviceTarget[WSL_LOCALHOST_DEVICE_PREFIX.Length..];
                }
                else if(deviceTarget.StartsWith(WSL_DEVICE_PREFIX, StringComparison.OrdinalIgnoreCase))
                {
                    deviceTarget = deviceTarget[WSL_DEVICE_PREFIX.Length..];
                }
                else
                {
                    return fullPath;
                }

                // Docker Desktop recognizes WSL bind sources in the \\wsl$\ form.
                return Path.Join(String.Concat(@"\\wsl$\", deviceTarget), fullPath.AsSpan(3));
            }

            if(Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER)
            {
                return fullPath;
            }

            buffer = new char[buffer.Length * 2];
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint QueryDosDevice(string deviceName, char* targetPath, uint maxChars);

    private static string EscapeMountField(string field)
        => field.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;

    private async Task<DockerResult<DockerCompleted>> EnsureVolumeAsync(string volumeName)
    {
        var inspect = await DockerCommand.InspectAsync("volume", volumeName);
        if(inspect is DockerValue<DockerOutput>)
        {
            return new DockerValue<DockerCompleted>(new DockerCompleted());
        }

        if(inspect is not DockerNotFound)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to inspect Docker volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", inspect.Require().ErrorDetails);
            return inspect.Require().Select(_ => new DockerCompleted());
        }

        var create = await DockerCommand.RunAsync(["volume", "create", volumeName]);
        if(create is not DockerValue<DockerOutput>)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to create Docker volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", create.ErrorDetails);
        }

        return create.Select(_ => new DockerCompleted());
    }

    private async Task<DockerResult<DockerCompleted>> CopyHostXdgDirectoryToVolumeAsync(string sourceRootDirectory, string volumeName)
    {
        var result = await DockerCommand.RunAsync(
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
                copy_dir() {
                    src_dir="$1"
                    dst_dir="$2"
                    mkdir -p "$dst_dir"
                    if [ ! -d "$src_dir" ]; then
                        return 0
                    fi
                    cp -a "$src_dir"/. "$dst_dir/"
                }

                rm -rf /target/* /target/.[!.]* /target/..?* 2>/dev/null || true
                mkdir -p /target/.local/share /target/.local/state
                copy_dir /source/.local/share/opencode /target/.local/share/opencode
                copy_dir /source/.local/state/opencode /target/.local/state/opencode
                """
            ]);

        if(result is not DockerValue<DockerOutput>)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to import state from '{sourceRootDirectory}' into volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.ErrorDetails);
        }

        return result.Select(_ => new DockerCompleted());
    }

    private async Task<DockerResult<DockerCompleted>> CreateVolumeArchiveOnHostAsync(string volumeName, string destinationArchive)
    {
        string destinationDirectory = Path.GetDirectoryName(destinationArchive) ?? "";
        string archiveFileName = Path.GetFileName(destinationArchive);
        if(String.IsNullOrWhiteSpace(destinationDirectory) || String.IsNullOrWhiteSpace(archiveFileName))
        {
            string message = $"Invalid export archive path '{destinationArchive}'.";
            _deferredSessionLogService.WriteErrorOrConsole("docker", message);
            return new DockerFailure(message);
        }

        var result = await DockerCommand.RunAsync(
        [
            "run", "--rm",
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

                        try:
                            if entry.is_dir(follow_symlinks=True):
                                stack.append((entry.path, archive_entry))
                                continue

                            if not entry.is_file(follow_symlinks=True):
                                continue
                        except (FileNotFoundError, PermissionError, OSError):
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

        if(result is not DockerValue<DockerOutput>)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to create export archive '{destinationArchive}' from Docker volume '{volumeName}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.ErrorDetails);
            return result.Select(_ => new DockerCompleted());
        }

        if(!File.Exists(destinationArchive))
        {
            string message = $"Docker reported success but export archive '{destinationArchive}' was not created.";
            _deferredSessionLogService.WriteErrorOrConsole("docker", message);
            return new DockerFailure(message);
        }

        return new DockerValue<DockerCompleted>(new DockerCompleted());
    }

    private async Task<DockerResult<bool>> TryVolumeHasAnyContentAsync(string volumeName)
    {
        var result = await DockerCommand.RunAsync(
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

        if(result is not DockerValue<DockerOutput> output)
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to inspect Docker volume '{volumeName}' before import.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.ErrorDetails);

            return result.Select(_ => false);
        }

        return output.Value.StdOut.Trim() switch
        {
            "has-data" => new DockerValue<bool>(true),
            "empty" => new DockerValue<bool>(false),
            _ => InvalidContentResult()
        };

        DockerFailure InvalidContentResult()
        {
            string message = $"Docker returned an invalid content check for volume '{volumeName}'.";
            _deferredSessionLogService.WriteErrorOrConsole("docker", message);
            return new DockerFailure(message);
        }
    }
}
