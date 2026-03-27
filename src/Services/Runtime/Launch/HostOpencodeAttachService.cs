using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace OpencodeWrap.Services.Runtime.Launch;

internal sealed partial class HostOpencodeAttachService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly SessionOutputService _sessionOutputService;

    public async Task<int> RunAttachAsync(string executablePath, string attachUrl)
    {
        if(String.IsNullOrWhiteSpace(executablePath))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.ATTACH, "Managed host OpenCode executable path was not resolved.");
            return 1;
        }

        if(String.IsNullOrWhiteSpace(attachUrl))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.ATTACH, "OpenCode attach URL was not resolved.");
            return 1;
        }

        _deferredSessionLogService.Write(LogCategories.ATTACH, $"launching managed host client '{executablePath}' against '{attachUrl}'", LogLevel.Information);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        startInfo.ArgumentList.Add("attach");
        startInfo.ArgumentList.Add(attachUrl);

        string noProxyValue = MergeNoProxy(Environment.GetEnvironmentVariable("NO_PROXY"));
        startInfo.Environment["NO_PROXY"] = noProxyValue;
        startInfo.Environment["no_proxy"] = noProxyValue;

        try
        {
            var process = await _sessionOutputService.RunWithLoadingStateAsync(
                LogCategories.ATTACH,
                "Launching OpenCode terminal...",
                () => Task.FromResult(Process.Start(startInfo)));
            if(process is null)
            {
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.ATTACH, "Failed to start the managed host `opencode attach` process.");
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
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.ATTACH, $"Failed to start the managed host `opencode attach` process: {ex.Message}");
            return 1;
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

        foreach(string value in new[] { "localhost", "127.0.0.1" })
        {
            if(seen.Add(value))
            {
                values.Add(value);
            }
        }

        return String.Join(',', values);
    }
}
