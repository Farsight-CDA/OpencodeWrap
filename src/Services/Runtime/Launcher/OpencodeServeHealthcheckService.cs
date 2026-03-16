using Microsoft.Extensions.Logging;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class OpencodeServeHealthcheckService : Singleton
{
    private static readonly TimeSpan _readinessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(2);
    private static readonly StringComparer _urlComparer = StringComparer.OrdinalIgnoreCase;

    [Inject] private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<string?> WaitUntilReadyAsync(string attachUrl, string? dockerNetworkMode, bool isWindows)
    {
        if(!Uri.TryCreate(attachUrl, UriKind.Absolute, out Uri? attachUri))
        {
            _deferredSessionLogService.WriteErrorOrConsole("startup", $"Invalid attach URL '{attachUrl}'.");
            return null;
        }

        List<(string AttachUrl, Uri HealthUri)> probeTargets = BuildProbeTargets(attachUri, dockerNetworkMode, isWindows);
        var primaryTarget = probeTargets[0];
        if(probeTargets.Count == 1)
        {
            _deferredSessionLogService.Write("startup", $"waiting for backend readiness at '{primaryTarget.HealthUri}'", LogLevel.Information);
        }
        else
        {
            string fallbackTargets = String.Join(", ", probeTargets.Skip(1).Select(target => $"'{target.HealthUri}'"));
            _deferredSessionLogService.Write("startup", $"waiting for backend readiness at '{primaryTarget.HealthUri}' (fallbacks: {fallbackTargets})", LogLevel.Information);
        }

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
            foreach(var probeTarget in probeTargets)
            {
                try
                {
                    using var response = await httpClient.GetAsync(probeTarget.HealthUri, HttpCompletionOption.ResponseHeadersRead);
                    if(response.IsSuccessStatusCode)
                    {
                        _deferredSessionLogService.Write("startup", $"backend reported ready at '{probeTarget.HealthUri}'", LogLevel.Information);
                        return probeTarget.AttachUrl;
                    }

                    lastFailureDetail = $"HTTP {(int) response.StatusCode} {response.ReasonPhrase}";
                }
                catch(Exception ex)
                {
                    lastFailureDetail = ex.Message;
                }
            }

            await Task.Delay(_pollInterval);
        }

        _deferredSessionLogService.WriteErrorOrConsole("startup", $"OpenCode backend did not become ready at '{primaryTarget.HealthUri}' within {_readinessTimeout.TotalSeconds:F0}s.");
        if(!String.IsNullOrWhiteSpace(lastFailureDetail))
        {
            _deferredSessionLogService.WriteErrorOrConsole("startup", lastFailureDetail);
        }

        return null;
    }

    private static List<(string AttachUrl, Uri HealthUri)> BuildProbeTargets(Uri attachUri, string? dockerNetworkMode, bool isWindows)
    {
        var probeTargets = new List<(string AttachUrl, Uri HealthUri)>();
        var seenAttachUrls = new HashSet<string>(_urlComparer);

        void AddProbeTarget(Uri candidateAttachUri)
        {
            string candidateAttachUrl = candidateAttachUri.GetLeftPart(UriPartial.Authority);
            if(!seenAttachUrls.Add(candidateAttachUrl))
            {
                return;
            }

            probeTargets.Add((candidateAttachUrl, new Uri(candidateAttachUri, "/global/health")));
        }

        AddProbeTarget(attachUri);

        if(isWindows
            && String.Equals(dockerNetworkMode, "host", StringComparison.OrdinalIgnoreCase)
            && TryBuildAlternateLoopbackUri(attachUri, out Uri alternateAttachUri))
        {
            AddProbeTarget(alternateAttachUri);
        }

        return probeTargets;
    }

    private static bool TryBuildAlternateLoopbackUri(Uri attachUri, out Uri alternateAttachUri)
    {
        alternateAttachUri = attachUri;

        string? alternateHost = attachUri.Host switch
        {
            "127.0.0.1" => "localhost",
            "localhost" => "127.0.0.1",
            _ => null
        };

        if(alternateHost is null)
        {
            return false;
        }

        alternateAttachUri = new UriBuilder(attachUri)
        {
            Host = alternateHost
        }.Uri;
        return true;
    }
}
