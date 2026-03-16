using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Docker;

internal sealed partial class DockerHostService : Singleton
{
    private static readonly FrozenSet<string> _reservedNetworkModeNames =
        new[] { "bridge", "host", "none" }.ToFrozenSet(StringComparer.Ordinal);

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool IsMacOS { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public bool IsUnixLike => IsLinux || IsMacOS;

    public async Task<bool> EnsureHostAndDockerAsync()
    {
        if(!IsWindows && !IsLinux && !IsMacOS)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Docker, "Unsupported host OS. Only Windows, Linux, and macOS are supported.");
            return false;
        }

        var dockerCheck = await ProcessRunner.RunAsync("docker", ["version"]);
        if(dockerCheck.Success)
        {
            return true;
        }

            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Docker, "Docker is required but is not functional on this host.");
        WriteDockerErrorDetails(dockerCheck.StdErr);
        return false;
    }

    public async Task<string?> GetContainerUserSpecAsync() => !IsUnixLike
            ? null
            : await TryGetUnixUserSpecAsync() is { Success: true, UserSpec: string userSpec }
            ? userSpec
            : null;

    public async Task<(bool Success, string UserSpec)> TryGetUnixUserSpecAsync()
    {
        var uidResult = await ProcessRunner.RunAsync("id", ["-u"]);
        if(!uidResult.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Docker, "Failed to resolve current Unix user ID.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole(LogCategories.Docker, uidResult.StdErr);

            return (false, "");
        }

        var gidResult = await ProcessRunner.RunAsync("id", ["-g"]);
        if(!gidResult.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Docker, "Failed to resolve current Unix group ID.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole(LogCategories.Docker, gidResult.StdErr);

            return (false, "");
        }

        string uid = uidResult.StdOut.Trim();
        string gid = gidResult.StdOut.Trim();
        if(uid.Length == 0 || gid.Length == 0)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Docker, "Unix user ID/group ID cannot be empty.");
            return (false, "");
        }

        return (true, $"{uid}:{gid}");
    }

    public async Task<(bool Success, IReadOnlyList<string> NetworkNames)> TryListNetworkNamesAsync()
    {
        if(!await EnsureHostAndDockerAsync())
        {
            return (false, []);
        }

        var networkList = await ProcessRunner.RunAsync("docker", ["network", "ls", "--format", "{{.Name}}"]);
        if(!networkList.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Docker, "Failed to list Docker networks.");
            WriteDockerErrorDetails(networkList.StdErr);
            return (false, []);
        }

        string[] networkNames = [.. networkList.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !_reservedNetworkModeNames.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
        return (true, networkNames);
    }

    public bool TryEnsureGlobalConfigDirectory(out string configDirectory)
    {
        configDirectory = "";

        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(String.IsNullOrWhiteSpace(homeDirectory))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Docker, "Unable to resolve host home directory.");
            return false;
        }

        configDirectory = Path.Combine(homeDirectory, OpencodeWrapConstants.HOST_GLOBAL_CONFIG_DIRECTORY_NAME);

        try
        {
            Directory.CreateDirectory(configDirectory);
            string globalAgentsPath = Path.Combine(configDirectory, OpencodeWrapConstants.HOST_GLOBAL_AGENTS_FILE_NAME);
            if(!File.Exists(globalAgentsPath))
            {
                File.WriteAllText(globalAgentsPath, string.Empty);
            }

            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Docker, $"Failed to create host config directory '{configDirectory}': {ex.Message}");
            return false;
        }
    }

    private void WriteDockerErrorDetails(string? dockerError)
    {
        if(String.IsNullOrWhiteSpace(dockerError))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.Docker, "install Docker and ensure the daemon is running.");
            return;
        }

        string details = dockerError.Trim();
        _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Docker, details);

        if (IsUnixLike && (details.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || details.Contains("got permission denied", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsLinux)
            {
                _deferredSessionLogService.WriteWarningOrConsole(LogCategories.Docker, "your user may not have access to /var/run/docker.sock.");
                _deferredSessionLogService.WriteWarningOrConsole(LogCategories.Docker, "add your user to the docker group or run with appropriate privileges.");
            }
            else if (IsMacOS)
            {
                _deferredSessionLogService.WriteWarningOrConsole(LogCategories.Docker, "ensure Docker Desktop is running and your shell has access to the Docker socket.");
            }

            return;
        }

        if (details.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.Docker, "start the Docker daemon and try again.");
        }
    }
}
