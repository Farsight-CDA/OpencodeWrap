using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpencodeWrap.Services.Docker;

internal sealed partial class DockerHostService : Singleton
{
    private static readonly FrozenSet<string> _reservedNetworkModeNames =
        new[] { "bridge", "host", "none" }.ToFrozenSet(StringComparer.Ordinal);
    private const string DOCKER_DESKTOP_HOST_NETWORKING_SETTINGS_KEY = "HostNetworkingEnabled";
    private const string DOCKER_DESKTOP_LEGACY_HOST_NETWORKING_SETTINGS_KEY = "hostNetworkingEnabled";

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool IsLinux { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool IsMacOS { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public bool IsUnixLike => IsLinux || IsMacOS;

    public DockerDesktopHostNetworkingState GetDockerDesktopHostNetworkingState()
    {
        if(!IsWindows)
        {
            return DockerDesktopHostNetworkingState.NotApplicable;
        }

        string appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if(String.IsNullOrWhiteSpace(appDataDirectory))
        {
            return DockerDesktopHostNetworkingState.Unknown;
        }

        string settingsStorePath = Path.Combine(appDataDirectory, "Docker", "settings-store.json");
        if(!File.Exists(settingsStorePath))
        {
            return DockerDesktopHostNetworkingState.Unknown;
        }

        try
        {
            using var stream = File.OpenRead(settingsStorePath);
            using var document = JsonDocument.Parse(stream);
            if(document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return DockerDesktopHostNetworkingState.Unknown;
            }

            if(TryReadHostNetworkingSetting(document.RootElement, out bool enabled))
            {
                return enabled
                    ? DockerDesktopHostNetworkingState.Enabled
                    : DockerDesktopHostNetworkingState.Disabled;
            }
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.DOCKER, $"Failed to read Docker Desktop settings from '{settingsStorePath}': {ex.Message}");
        }

        return DockerDesktopHostNetworkingState.Unknown;
    }

    public async Task<bool> EnsureHostAndDockerAsync()
    {
        if(!IsWindows && !IsLinux && !IsMacOS)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Unsupported host OS. Only Windows, Linux, and macOS are supported.");
            return false;
        }

        var dockerCheck = await ProcessRunner.RunAsync("docker", ["version"]);
        if(dockerCheck.Success)
        {
            return true;
        }

        _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Docker is required but is not functional on this host.");
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
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Failed to resolve current Unix user ID.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole(LogCategories.DOCKER, uidResult.StdErr);

            return (false, "");
        }

        var gidResult = await ProcessRunner.RunAsync("id", ["-g"]);
        if(!gidResult.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Failed to resolve current Unix group ID.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole(LogCategories.DOCKER, gidResult.StdErr);

            return (false, "");
        }

        string uid = uidResult.StdOut.Trim();
        string gid = gidResult.StdOut.Trim();
        if(uid.Length == 0 || gid.Length == 0)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Unix user ID/group ID cannot be empty.");
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
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Failed to list Docker networks.");
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

    public async Task<OpenCodeDesktopAppStatus> GetOpenCodeDesktopAppStatusAsync()
    {
        string? launchTarget = IsWindows
            ? GetWindowsDesktopAppPath()
            : IsMacOS
                ? GetMacDesktopAppPath()
                : await GetLinuxDesktopAppTargetAsync();

        return String.IsNullOrWhiteSpace(launchTarget)
            ? OpenCodeDesktopAppStatus.NotDetected
            : new OpenCodeDesktopAppStatus(
            OpenCodeDesktopAvailability.UnsupportedAttachContract,
            launchTarget,
            "Current OpenCode desktop builds do not yet expose a supported way to attach an existing OCW-managed backend session.");
    }

    public async Task<HostLaunchResult> TryOpenUrlAsync(string url)
    {
        if(String.IsNullOrWhiteSpace(url))
        {
            return HostLaunchResult.Fail("URL was not provided.");
        }

        try
        {
            if(IsWindows)
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                return process is null
                    ? HostLaunchResult.Fail($"Failed to open '{url}' in the default browser.")
                    : HostLaunchResult.Ok();
            }

            string command = IsMacOS ? "open" : "xdg-open";
            var result = await ProcessRunner.RunAsync(command, [url]);
            return result.Success
                ? HostLaunchResult.Ok()
                : HostLaunchResult.Fail(DescribeHostLaunchFailure(command, result, url));
        }
        catch(Exception ex)
        {
            return HostLaunchResult.Fail($"Failed to open '{url}' in the default browser: {ex.Message}");
        }
    }

    public Task<HostLaunchResult> TryLaunchOpenCodeDesktopAsync(OpenCodeDesktopAppStatus desktopStatus)
    {
        if(desktopStatus.Availability is OpenCodeDesktopAvailability.NotDetected)
        {
            return Task.FromResult(HostLaunchResult.Fail("OpenCode desktop was not detected on this host."));
        }

        string launchTarget = desktopStatus.LaunchTarget ?? "this host";
        return Task.FromResult(HostLaunchResult.Fail($"Detected OpenCode desktop at '{launchTarget}', but current OpenCode desktop builds do not yet expose a supported attach-to-existing-server launch contract for OCW."));
    }

    public bool TryEnsureGlobalConfigDirectory(out string configDirectory)
    {
        configDirectory = "";

        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(String.IsNullOrWhiteSpace(homeDirectory))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, "Unable to resolve host home directory.");
            return false;
        }

        configDirectory = Path.Combine(homeDirectory, OpencodeWrapConstants.HOST_GLOBAL_CONFIG_DIRECTORY_NAME);

        try
        {
            Directory.CreateDirectory(configDirectory);
            string globalAgentsPath = Path.Combine(configDirectory, OpencodeWrapConstants.HOST_GLOBAL_AGENTS_FILE_NAME);
            if(!File.Exists(globalAgentsPath))
            {
                File.WriteAllText(globalAgentsPath, String.Empty);
            }

            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, $"Failed to create host config directory '{configDirectory}': {ex.Message}");
            return false;
        }
    }

    private void WriteDockerErrorDetails(string? dockerError)
    {
        if(String.IsNullOrWhiteSpace(dockerError))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.DOCKER, "install Docker and ensure the daemon is running.");
            return;
        }

        string details = dockerError.Trim();
        _deferredSessionLogService.WriteErrorOrConsole(LogCategories.DOCKER, details);

        if(IsUnixLike && (details.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || details.Contains("got permission denied", StringComparison.OrdinalIgnoreCase)))
        {
            if(IsLinux)
            {
                _deferredSessionLogService.WriteWarningOrConsole(LogCategories.DOCKER, "your user may not have access to /var/run/docker.sock.");
                _deferredSessionLogService.WriteWarningOrConsole(LogCategories.DOCKER, "add your user to the docker group or run with appropriate privileges.");
            }
            else if(IsMacOS)
            {
                _deferredSessionLogService.WriteWarningOrConsole(LogCategories.DOCKER, "ensure Docker Desktop is running and your shell has access to the Docker socket.");
            }

            return;
        }

        if(details.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.DOCKER, "start the Docker daemon and try again.");
        }
    }

    private static bool TryReadHostNetworkingSetting(JsonElement rootElement, out bool enabled)
    {
        enabled = false;

        if(rootElement.TryGetProperty(DOCKER_DESKTOP_HOST_NETWORKING_SETTINGS_KEY, out var currentValue)
            && currentValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            enabled = currentValue.GetBoolean();
            return true;
        }

        if(rootElement.TryGetProperty(DOCKER_DESKTOP_LEGACY_HOST_NETWORKING_SETTINGS_KEY, out var legacyValue)
            && legacyValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            enabled = legacyValue.GetBoolean();
            return true;
        }

        return false;
    }

    private string? GetWindowsDesktopAppPath()
    {
        var candidates = new List<string>();

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if(!String.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "Programs", "OpenCode", "OpenCode.exe"));
            candidates.Add(Path.Combine(localAppData, "Programs", "OpenCode Desktop", "OpenCode.exe"));
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if(!String.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "OpenCode", "OpenCode.exe"));
            candidates.Add(Path.Combine(programFiles, "OpenCode Desktop", "OpenCode.exe"));
        }

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if(!String.IsNullOrWhiteSpace(programFilesX86))
        {
            candidates.Add(Path.Combine(programFilesX86, "OpenCode", "OpenCode.exe"));
            candidates.Add(Path.Combine(programFilesX86, "OpenCode Desktop", "OpenCode.exe"));
        }

        string? detectedPath = candidates.FirstOrDefault(File.Exists);
        return !String.IsNullOrWhiteSpace(detectedPath) ? detectedPath : GetWindowsDesktopStartMenuShortcutPath();
    }

    private string? GetWindowsDesktopStartMenuShortcutPath()
    {
        string[] startMenuDirectories =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        ];

        foreach(string directory in startMenuDirectories.Where(path => !String.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                string? shortcutPath = Directory
                    .EnumerateFiles(directory, "*.lnk", SearchOption.AllDirectories)
                    .FirstOrDefault(IsOpenCodeDesktopStartMenuShortcut);
                if(!String.IsNullOrWhiteSpace(shortcutPath))
                {
                    return shortcutPath;
                }
            }
            catch(Exception)
            {
            }
        }

        return null;
    }

    private static bool IsOpenCodeDesktopStartMenuShortcut(string path)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        return fileName.StartsWith("OpenCode", StringComparison.OrdinalIgnoreCase);
    }

    private string? GetMacDesktopAppPath()
    {
        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            "/Applications/OpenCode.app",
            String.IsNullOrWhiteSpace(homeDirectory) ? String.Empty : Path.Combine(homeDirectory, "Applications", "OpenCode.app")
        ];

        return candidates.FirstOrDefault(path => !String.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }

    private async Task<string?> GetLinuxDesktopAppTargetAsync()
    {
        string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] pathCandidates =
        [
            String.IsNullOrWhiteSpace(homeDirectory) ? String.Empty : Path.Combine(homeDirectory, ".local", "bin", "opencode-desktop"),
            "/usr/local/bin/opencode-desktop",
            "/usr/bin/opencode-desktop",
            String.IsNullOrWhiteSpace(homeDirectory) ? String.Empty : Path.Combine(homeDirectory, "Applications", "OpenCode.AppImage")
        ];

        string? resolvedPath = pathCandidates.FirstOrDefault(path => !String.IsNullOrWhiteSpace(path) && File.Exists(path));
        if(!String.IsNullOrWhiteSpace(resolvedPath))
        {
            return resolvedPath;
        }

        foreach(string commandName in new[] { "opencode-desktop", "OpenCode" })
        {
            var result = await ProcessRunner.RunAsync("which", [commandName]);
            if(result.Success)
            {
                string resolved = result.StdOut.Trim();
                if(resolved.Length > 0)
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    private static string DescribeHostLaunchFailure(string command, ProcessRunner.ProcessRunResult result, string target)
    {
        string detail = FirstNonEmptyLine(result.StdErr, result.StdOut);
        return !result.Started
            ? String.IsNullOrWhiteSpace(detail)
                ? $"Failed to start '{command}' for '{target}'."
                : $"Failed to start '{command}' for '{target}': {detail}"
            : String.IsNullOrWhiteSpace(detail)
            ? $"'{command}' exited with code {result.ExitCode} while opening '{target}'."
            : $"'{command}' exited with code {result.ExitCode} while opening '{target}': {detail}";
    }

    private static string FirstNonEmptyLine(params string[] values)
    {
        foreach(string value in values)
        {
            if(String.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string firstLine = value
                .Replace("\r", String.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? String.Empty;
            if(!String.IsNullOrWhiteSpace(firstLine))
            {
                return firstLine;
            }
        }

        return String.Empty;
    }
}

internal enum DockerDesktopHostNetworkingState
{
    NotApplicable,
    Unknown,
    Disabled,
    Enabled
}

internal enum OpenCodeDesktopAvailability
{
    NotDetected,
    UnsupportedAttachContract
}

internal sealed record OpenCodeDesktopAppStatus(OpenCodeDesktopAvailability Availability, string? LaunchTarget, string? Detail)
{
    public static OpenCodeDesktopAppStatus NotDetected { get; } = new(OpenCodeDesktopAvailability.NotDetected, null, null);
}

internal sealed record HostLaunchResult(bool Success, string? ErrorMessage)
{
    public static HostLaunchResult Ok() => new(true, null);
    public static HostLaunchResult Fail(string errorMessage) => new(false, errorMessage);
}
