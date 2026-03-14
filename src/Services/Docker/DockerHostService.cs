using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Docker;

internal sealed partial class DockerHostService : Singleton
{
    public bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool IsMacOS { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public bool IsUnixLike => IsLinux || IsMacOS;

    public async Task<bool> EnsureHostAndDockerAsync()
    {
        if(!IsWindows && !IsLinux && !IsMacOS)
        {
            AppIO.WriteError("Unsupported host OS. Only Windows, Linux, and macOS are supported.");
            return false;
        }

        var dockerCheck = await ProcessRunner.RunAsync("docker", ["version"]);
        if(dockerCheck.Success)
        {
            return true;
        }

        AppIO.WriteError("Docker is required but is not functional on this host.");
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
            AppIO.WriteError("Failed to resolve current Unix user ID.");
            if(!String.IsNullOrWhiteSpace(uidResult.StdErr))
            {
                AppIO.WriteError(uidResult.StdErr.Trim());
            }

            return (false, "");
        }

        var gidResult = await ProcessRunner.RunAsync("id", ["-g"]);
        if(!gidResult.Success)
        {
            AppIO.WriteError("Failed to resolve current Unix group ID.");
            if(!String.IsNullOrWhiteSpace(gidResult.StdErr))
            {
                AppIO.WriteError(gidResult.StdErr.Trim());
            }

            return (false, "");
        }

        string uid = uidResult.StdOut.Trim();
        string gid = gidResult.StdOut.Trim();
        if(uid.Length == 0 || gid.Length == 0)
        {
            AppIO.WriteError("Unix user ID/group ID cannot be empty.");
            return (false, "");
        }

        return (true, $"{uid}:{gid}");
    }

    public bool TryEnsureGlobalConfigDirectory(out string configDirectory)
    {
        configDirectory = "";

        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(String.IsNullOrWhiteSpace(homeDirectory))
        {
            AppIO.WriteError("Unable to resolve host home directory.");
            return false;
        }

        configDirectory = Path.Combine(homeDirectory, OpencodeWrapConstants.HOST_GLOBAL_CONFIG_DIRECTORY_NAME);

        try
        {
            Directory.CreateDirectory(configDirectory);
            return true;
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to create host config directory '{configDirectory}': {ex.Message}");
            return false;
        }
    }

    private void WriteDockerErrorDetails(string? dockerError)
    {
        if(String.IsNullOrWhiteSpace(dockerError))
        {
            AppIO.WriteWarning("install Docker and ensure the daemon is running.");
            return;
        }

        string details = dockerError.Trim();
        AppIO.WriteError(details);

        if(IsUnixLike && (details.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || details.Contains("got permission denied", StringComparison.OrdinalIgnoreCase)))
        {
            if(IsLinux)
            {
                AppIO.WriteWarning("your user may not have access to /var/run/docker.sock.");
                AppIO.WriteWarning("add your user to the docker group or run with appropriate privileges.");
            }
            else if(IsMacOS)
            {
                AppIO.WriteWarning("ensure Docker Desktop is running and your shell has access to the Docker socket.");
            }

            return;
        }

        if(details.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase))
        {
            AppIO.WriteWarning("start the Docker daemon and try again.");
        }
    }
}
