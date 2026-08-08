using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpencodeWrap.Services.Runtime.Launch;

internal sealed partial class OpencodeCatalogReadinessService : Singleton
{
    private static readonly TimeSpan _catalogTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(5);

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<bool> WaitUntilReadyAsync(string serverUrl, string serverPassword, string containerDirectory)
    {
        if(!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, $"Invalid OpenCode V2 server URL '{serverUrl}'.");
            return false;
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

        Uri agentsUri = BuildLocationUri(serverUri, "/api/agent", containerDirectory);
        var deadlineUtc = DateTime.UtcNow + _catalogTimeout;
        string? lastFailureDetail = null;

        while(DateTime.UtcNow < deadlineUtc)
        {
            try
            {
                using var request = CreateAuthenticatedRequest(agentsUri, serverPassword);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                if(response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    if(HasAgents(json))
                    {
                        _deferredSessionLogService.Write(LogCategories.CLIENT, $"OpenCode V2 catalog ready for '{containerDirectory}'", LogLevel.Information);
                        return true;
                    }

                    lastFailureDetail = $"{agentsUri}: agent catalog is still empty";
                }
                else
                {
                    lastFailureDetail = $"{agentsUri}: HTTP {(int) response.StatusCode} {response.ReasonPhrase}";
                }
            }
            catch(Exception ex)
            {
                lastFailureDetail = $"{agentsUri}: {ex.Message}";
            }

            await Task.Delay(_pollInterval);
        }

        _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, $"OpenCode V2 agent catalog did not become ready for '{containerDirectory}' within {_catalogTimeout.TotalSeconds:F0}s.");
        if(!String.IsNullOrWhiteSpace(lastFailureDetail))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, lastFailureDetail);
        }

        return false;
    }

    private static Uri BuildLocationUri(Uri serverUri, string path, string containerDirectory)
        => new(serverUri, $"{path}?location%5Bdirectory%5D={Uri.EscapeDataString(containerDirectory)}");

    private static HttpRequestMessage CreateAuthenticatedRequest(Uri uri, string serverPassword)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{OpencodeWrapConstants.OPENCODE_BASIC_AUTH_USERNAME}:{serverPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return request;
    }

    private static bool HasAgents(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0;
        }
        catch(JsonException)
        {
            return false;
        }
    }
}
