using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Docker;

internal sealed class DockerHostService
{
    public bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool IsMacOS { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

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

    public async Task<string?> GetContainerUserSpecAsync() => !IsLinux
            ? null
            : await TryGetLinuxUserSpecAsync() is { Success: true, UserSpec: string userSpec }
            ? userSpec
            : null;

    public static async Task<(bool Success, string UserSpec)> TryGetLinuxUserSpecAsync()
    {
        var uidResult = await ProcessRunner.RunAsync("id", ["-u"]);
        if(!uidResult.Success)
        {
            AppIO.WriteError("Failed to resolve current Linux user ID.");
            if(!String.IsNullOrWhiteSpace(uidResult.StdErr))
            {
                AppIO.WriteError(uidResult.StdErr.Trim());
            }

            return (false, String.Empty);
        }

        var gidResult = await ProcessRunner.RunAsync("id", ["-g"]);
        if(!gidResult.Success)
        {
            AppIO.WriteError("Failed to resolve current Linux group ID.");
            if(!String.IsNullOrWhiteSpace(gidResult.StdErr))
            {
                AppIO.WriteError(gidResult.StdErr.Trim());
            }

            return (false, String.Empty);
        }

        string uid = uidResult.StdOut.Trim();
        string gid = gidResult.StdOut.Trim();
        if(uid.Length == 0 || gid.Length == 0)
        {
            AppIO.WriteError("Linux user ID/group ID cannot be empty.");
            return (false, String.Empty);
        }

        return (true, $"{uid}:{gid}");
    }

    public static bool TryEnsureGlobalConfigDirectory(out string configDirectory)
    {
        configDirectory = String.Empty;

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

        if(IsLinux && (details.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || details.Contains("got permission denied", StringComparison.OrdinalIgnoreCase)))
        {
            AppIO.WriteWarning("your user may not have access to /var/run/docker.sock.");
            AppIO.WriteWarning("add your user to the docker group or run with appropriate privileges.");
            return;
        }

        if(details.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase))
        {
            AppIO.WriteWarning("start the Docker daemon and try again.");
        }
    }
}
