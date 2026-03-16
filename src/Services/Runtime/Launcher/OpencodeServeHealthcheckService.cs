using Microsoft.Extensions.Logging;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class OpencodeServeHealthcheckService : Singleton
{
    private static readonly TimeSpan _readinessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(2);

    [Inject] private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<bool> WaitUntilReadyAsync(string attachUrl)
    {
        if(!Uri.TryCreate(attachUrl, UriKind.Absolute, out Uri? attachUri))
        {
            _deferredSessionLogService.WriteErrorOrConsole("startup", $"Invalid attach URL '{attachUrl}'.");
            return false;
        }

        Uri healthUri = new(attachUri, "/global/health");
        _deferredSessionLogService.Write("startup", $"waiting for backend readiness at '{healthUri}'", LogLevel.Information);

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = _requestTimeout
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = _requestTimeout
        };

        DateTime deadlineUtc = DateTime.UtcNow + _readinessTimeout;
        string? lastFailureDetail = null;

        while(DateTime.UtcNow < deadlineUtc)
        {
            try
            {
                using var response = await httpClient.GetAsync(healthUri, HttpCompletionOption.ResponseHeadersRead);
                if(response.IsSuccessStatusCode)
                {
                    _deferredSessionLogService.Write("startup", $"backend reported ready at '{healthUri}'", LogLevel.Information);
                    return true;
                }

                lastFailureDetail = $"HTTP {(int) response.StatusCode} {response.ReasonPhrase}";
            }
            catch(Exception ex)
            {
                lastFailureDetail = ex.Message;
            }

            await Task.Delay(_pollInterval);
        }

        _deferredSessionLogService.WriteErrorOrConsole("startup", $"OpenCode backend did not become ready at '{healthUri}' within {_readinessTimeout.TotalSeconds:F0}s.");
        if(!String.IsNullOrWhiteSpace(lastFailureDetail))
        {
            _deferredSessionLogService.WriteErrorOrConsole("startup", lastFailureDetail);
        }

        return false;
    }
}
