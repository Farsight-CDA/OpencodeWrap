using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpencodeWrap.Services.Runtime.Lifecycle;

internal sealed partial class OpencodeServeHealthcheckService : Singleton
{
    private static readonly TimeSpan _readinessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromMilliseconds(500);

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<bool> WaitUntilReadyAsync(string serverUrl, string serverPassword, string expectedVersion)
    {
        if(!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.STARTUP, $"Invalid OpenCode V2 server URL '{serverUrl}'.");
            return false;
        }

        Uri healthUri = new(serverUri, "/api/health");
        _deferredSessionLogService.Write(LogCategories.STARTUP, $"waiting for backend readiness at '{healthUri}'", LogLevel.Information);

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = _requestTimeout
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = _requestTimeout
        };

        var deadlineUtc = DateTime.UtcNow + _readinessTimeout;
        string? lastFailureDetail = null;

        while(DateTime.UtcNow < deadlineUtc)
        {
            try
            {
                using var request = CreateHealthRequest(serverUri, serverPassword);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                if(response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    if(TryValidateHealthResponse(json, expectedVersion, out string validationError))
                    {
                        _deferredSessionLogService.Write(LogCategories.STARTUP, $"OpenCode V2 backend {expectedVersion} reported ready at '{healthUri}'", LogLevel.Information);
                        return true;
                    }

                    lastFailureDetail = $"{healthUri}: {validationError}";
                }
                else
                {
                    lastFailureDetail = $"{healthUri}: HTTP {(int) response.StatusCode} {response.ReasonPhrase}";
                }
            }
            catch(Exception ex)
            {
                lastFailureDetail = $"{healthUri}: {ex.Message}";
            }

            await Task.Delay(_pollInterval);
        }

        _deferredSessionLogService.WriteErrorOrConsole(LogCategories.STARTUP, $"OpenCode V2 backend did not become ready at '{healthUri}' within {_readinessTimeout.TotalSeconds:F0}s.");
        if(!String.IsNullOrWhiteSpace(lastFailureDetail))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.STARTUP, lastFailureDetail);
        }

        return false;
    }

    internal static HttpRequestMessage CreateHealthRequest(Uri serverUri, string serverPassword)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(serverUri, "/api/health"));
        string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OpencodeWrapConstants.OPENCODE_BASIC_AUTH_USERNAME}:{serverPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return request;
    }

    internal static bool TryValidateHealthResponse(string json, string expectedVersion, out string errorMessage)
    {
        errorMessage = String.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if(root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("healthy", out var healthyElement)
                || healthyElement.ValueKind != JsonValueKind.True)
            {
                errorMessage = "health response did not report healthy=true";
                return false;
            }

            string version = root.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString() ?? String.Empty
                : String.Empty;
            if(!String.Equals(version, expectedVersion, StringComparison.Ordinal))
            {
                errorMessage = $"health response version '{version}' did not match session version '{expectedVersion}'";
                return false;
            }

            if(!root.TryGetProperty("pid", out var pidElement)
                || pidElement.ValueKind != JsonValueKind.Number
                || !pidElement.TryGetInt64(out long pid)
                || pid <= 0)
            {
                errorMessage = "health response did not include a positive server pid";
                return false;
            }

            return true;
        }
        catch(JsonException ex)
        {
            errorMessage = $"health response was not valid JSON: {ex.Message}";
            return false;
        }
    }
}
