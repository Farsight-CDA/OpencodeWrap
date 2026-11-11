using System.Text.Json;

namespace OpencodeWrap.Services.Runtime.Lifecycle;

internal sealed partial class OpencodeBackendReadinessService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<bool> WaitUntilReadyAsync(string containerName, Func<CancellationToken, Task<bool>> waitUntilReady)
    {
        using var cancellation = new CancellationTokenSource();
        var containerExitTask = DockerCommand.RunAsync(["wait", containerName], cancellation.Token);
        var readinessTask = waitUntilReady(cancellation.Token);
        try
        {
            await Task.WhenAny(containerExitTask, readinessTask);
            if(containerExitTask.IsCompleted)
            {
                await cancellation.CancelAsync();
                var result = await containerExitTask;
                if(result is DockerValue<DockerOutput> output)
                {
                    await WriteContainerExitAsync(containerName, output.Value.StdOut.Trim());
                }
                else
                {
                    _deferredSessionLogService.WriteErrorOrConsole(LogCategories.STARTUP, $"Failed to monitor OpenCode backend container '{containerName}' during startup.");
                    _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", result.ErrorDetails);
                }

                return false;
            }

            return await readinessTask;
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await Task.WhenAll(containerExitTask, readinessTask);
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task WriteContainerExitAsync(string containerName, string exitCode)
    {
        _deferredSessionLogService.WriteErrorOrConsole(LogCategories.STARTUP, $"OpenCode backend container '{containerName}' exited during startup with exit code {exitCode}.");
        var inspection = await DockerCommand.InspectAsync("container", containerName, "{{json .State}}");
        if(inspection is not DockerValue<DockerOutput> output)
        {
            _deferredSessionLogService.WriteWarningOrConsole("docker", $"Could not inspect backend container '{containerName}'; OOM status is unknown.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", inspection.Require().ErrorDetails);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(output.Value.StdOut);
            var state = document.RootElement;
            string status = state.GetProperty("Status").GetString() ?? String.Empty;
            bool oomKilled = state.GetProperty("OOMKilled").GetBoolean();
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Backend container state: {status}; OOMKilled={oomKilled.ToString().ToLowerInvariant()}.");
            string? error = state.GetProperty("Error").GetString();
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", error);
        }
        catch(Exception ex) when(ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _deferredSessionLogService.WriteWarningOrConsole("docker", $"Could not read backend container '{containerName}' state; OOM status is unknown.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", ex.Message);
        }
    }
}
