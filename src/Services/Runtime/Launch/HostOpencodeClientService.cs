using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace OpencodeWrap.Services.Runtime.Launch;

internal sealed partial class HostOpencodeClientService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly SessionOutputService _sessionOutputService;

    public async Task<int> RunAsync(string executablePath, string serverUrl, string serverPassword, string tuiChannel)
    {
        if(String.IsNullOrWhiteSpace(executablePath))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, "Managed host OpenCode executable path was not resolved.");
            return 1;
        }

        if(String.IsNullOrWhiteSpace(serverUrl))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, "OpenCode V2 server URL was not resolved.");
            return 1;
        }

        if(String.IsNullOrWhiteSpace(tuiChannel)
            || tuiChannel.Any(value => !Char.IsAsciiLetterOrDigit(value) && value is not '.' and not '_' and not '-'))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, "Host OpenCode V2 TUI channel was not resolved.");
            return 1;
        }

        _deferredSessionLogService.Write(LogCategories.CLIENT, $"launching managed OpenCode V2 host client '{executablePath}' against '{serverUrl}'", LogLevel.Information);

        var startInfo = BuildStartInfo(executablePath, serverUrl, serverPassword, tuiChannel);

        try
        {
            var process = await _sessionOutputService.RunWithLoadingStateAsync(
                LogCategories.CLIENT,
                "Launching OpenCode terminal...",
                () => Task.FromResult(Process.Start(startInfo)));
            if(process is null)
            {
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, "Failed to start the managed host `opencode2 --server` process.");
                return 1;
            }

            using(process)
            {
                await process.WaitForExitAsync();
                return process.ExitCode;
            }
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, $"Failed to start the managed host `opencode2 --server` process: {ex.Message}");
            return 1;
        }
        finally
        {
            CleanupTuiChannel(tuiChannel);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string executablePath, string serverUrl, string serverPassword, string tuiChannel)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        startInfo.ArgumentList.Add("--server");
        startInfo.ArgumentList.Add(serverUrl);

        string noProxyValue = MergeNoProxy(Environment.GetEnvironmentVariable("NO_PROXY"));
        startInfo.Environment["NO_PROXY"] = noProxyValue;
        startInfo.Environment["no_proxy"] = noProxyValue;
        startInfo.Environment[OpencodeWrapConstants.OPENCODE_PASSWORD_ENVIRONMENT_VARIABLE] = serverPassword;
        startInfo.Environment[OpencodeWrapConstants.OPENCODE_DISABLE_AUTOUPDATE_ENVIRONMENT_VARIABLE] = "1";
        startInfo.Environment["OPENCODE_TUI_CHANNEL"] = tuiChannel;
        return startInfo;
    }

    private static void CleanupTuiChannel(string tuiChannel)
    {
        try
        {
            string? stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if(String.IsNullOrWhiteSpace(stateHome))
            {
                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                stateHome = Path.Combine(userHome, ".local", "state");
            }

            string channelDirectory = Path.GetFullPath(Path.Combine(stateHome, "opencode", tuiChannel));
            AppIO.TryDeleteDirectory(channelDirectory);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string MergeNoProxy(string? existingValue)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach(string value in (existingValue ?? String.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if(seen.Add(value))
            {
                values.Add(value);
            }
        }

        const string loopbackAddress = "127.0.0.1";
        if(seen.Add(loopbackAddress))
        {
            values.Add(loopbackAddress);
        }

        return String.Join(',', values);
    }
}
