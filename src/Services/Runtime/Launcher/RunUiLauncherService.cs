using Microsoft.Extensions.Logging;
using OpencodeWrap.Services.Runtime.Infrastructure;
using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class RunUiLauncherService : Singleton
{
    [Inject]
    private readonly HostOpencodeAttachService _hostOpencodeAttachService;

    [Inject]
    private readonly DockerHostService _dockerHostService;

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly SessionOutputService _sessionOutputService;

    public async Task<RunUiLaunchResult> LaunchAsync(RunUiMode mode, string attachUrl, string workspaceDirectory, string? managedHostExecutablePath = null)
        => mode switch
        {
            RunUiMode.Web => await LaunchWebAsync(attachUrl, workspaceDirectory),
            RunUiMode.Desktop => await LaunchDesktopAsync(attachUrl),
            _ => await LaunchTuiAsync(managedHostExecutablePath, attachUrl)
        };

    private async Task<RunUiLaunchResult> LaunchTuiAsync(string? managedHostExecutablePath, string attachUrl)
    {
        int exitCode = await _hostOpencodeAttachService.RunAttachAsync(managedHostExecutablePath ?? String.Empty, attachUrl);
        return RunUiLaunchResult.Complete(exitCode);
    }

    private async Task<RunUiLaunchResult> LaunchWebAsync(string attachUrl, string workspaceDirectory)
    {
        if(String.IsNullOrWhiteSpace(attachUrl))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Attach, "OpenCode web URL was not resolved.");
            return RunUiLaunchResult.Fail();
        }

        string launchUrl = BuildWorkspaceLaunchUrl(attachUrl, workspaceDirectory);
        _deferredSessionLogService.Write(LogCategories.Attach, $"launching browser against '{launchUrl}'", LogLevel.Information);
        _sessionOutputService.WriteInfo(LogCategories.Attach, $"OpenCode web URL: {launchUrl}");

        HostLaunchResult openResult = await _dockerHostService.TryOpenUrlAsync(launchUrl);
        if(!openResult.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Attach, openResult.ErrorMessage ?? "Failed to open the local browser UI.");
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.Attach, "Open the printed URL manually, or rerun `ocw run` and choose TUI mode.");
            return RunUiLaunchResult.Fail();
        }

        _sessionOutputService.WriteInfo(LogCategories.Attach, "Browser launched. Press Ctrl+C to stop the OCW session.");
        return RunUiLaunchResult.WaitForBackend();
    }

    private async Task<RunUiLaunchResult> LaunchDesktopAsync(string attachUrl)
    {
        if(String.IsNullOrWhiteSpace(attachUrl))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Attach, "OpenCode desktop attach URL was not resolved.");
            return RunUiLaunchResult.Fail();
        }

        OpenCodeDesktopAppStatus desktopStatus = await _dockerHostService.GetOpenCodeDesktopAppStatusAsync();
        if(desktopStatus.Availability is OpenCodeDesktopAvailability.NotDetected)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Attach, "OpenCode desktop mode was selected, but no local OpenCode desktop app installation was detected.");
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.Attach, "Install the OpenCode desktop app from https://opencode.ai/download, or rerun `ocw run` and choose Web or TUI.");
            return RunUiLaunchResult.Fail();
        }

        HostLaunchResult launchResult = await _dockerHostService.TryLaunchOpenCodeDesktopAsync(attachUrl, desktopStatus);
        if(!launchResult.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.Attach, launchResult.ErrorMessage ?? "Failed to launch the OpenCode desktop app.");
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.Attach, "Desktop mode currently requires an OpenCode desktop build that can attach to an OCW-managed local backend. Use Web mode for now.");
            return RunUiLaunchResult.Fail();
        }

        return RunUiLaunchResult.Complete(0);
    }

    internal static string BuildWorkspaceLaunchUrl(string attachUrl, string workspaceDirectory)
    {
        if(String.IsNullOrWhiteSpace(attachUrl) || String.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return attachUrl;
        }

        if(!Uri.TryCreate(attachUrl, UriKind.Absolute, out Uri? baseUri))
        {
            return attachUrl;
        }

        string slug = Base64UrlEncode(workspaceDirectory);
        return new Uri(baseUri, $"/{slug}/session").ToString();
    }

    private static string Base64UrlEncode(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

internal sealed record RunUiLaunchResult(bool Success, bool WaitForBackendShutdown, int ExitCode)
{
    public static RunUiLaunchResult Complete(int exitCode) => new(true, false, exitCode);
    public static RunUiLaunchResult WaitForBackend() => new(true, true, 0);
    public static RunUiLaunchResult Fail(int exitCode = 1) => new(false, false, exitCode);
}
