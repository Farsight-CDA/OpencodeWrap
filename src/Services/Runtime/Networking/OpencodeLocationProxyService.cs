using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace OpencodeWrap.Services.Runtime.Networking;

internal sealed partial class OpencodeLocationProxyService : Singleton
{
    private static readonly HashSet<string> _hopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<(bool Success, OpencodeLocationProxyLease? Lease)> TryStartAsync(
        string backendUrl,
        string defaultContainerDirectory,
        IReadOnlyList<OpencodeLocationMapping> mappings)
    {
        if(!Uri.TryCreate(backendUrl, UriKind.Absolute, out var backendUri))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, $"Invalid OpenCode V2 backend URL '{backendUrl}'.");
            return (false, null);
        }

        WebApplication? application = null;
        HttpClient? httpClient = null;
        try
        {
            var mapper = new OpencodeLocationMapper(defaultContainerDirectory, mappings);
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };
            httpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = []
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
            });

            application = builder.Build();
            application.UseWebSockets();
            application.Run(context => ForwardAsync(context, httpClient, backendUri, mapper));
            await application.StartAsync();

            var server = application.Services.GetRequiredService<IServer>();
            string? proxyUrl = server.Features.Get<IServerAddressesFeature>()?.Addresses
                .SingleOrDefault(address => address.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase));
            if(String.IsNullOrWhiteSpace(proxyUrl))
            {
                throw new InvalidOperationException("Kestrel did not publish a loopback proxy address.");
            }

            _deferredSessionLogService.Write(LogCategories.CLIENT, $"OpenCode V2 location proxy listening at '{proxyUrl}' for backend '{backendUrl}'", LogLevel.Information);
            return (true, new OpencodeLocationProxyLease(application, httpClient, proxyUrl));
        }
        catch(Exception ex)
        {
            if(application is not null)
            {
                try
                {
                    await application.DisposeAsync();
                }
                catch
                {
                    // Preserve the startup error below.
                }
            }

            httpClient?.Dispose();
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.CLIENT, $"Failed to start the OpenCode V2 location proxy: {ex.Message}");
            return (false, null);
        }
    }

    private static async Task ForwardAsync(HttpContext context, HttpClient httpClient, Uri backendUri, OpencodeLocationMapper mapper)
    {
        Uri upstreamUri = BuildUpstreamUri(context.Request, backendUri, mapper);
        if(context.WebSockets.IsWebSocketRequest)
        {
            await ForwardWebSocketAsync(context, upstreamUri, mapper);
            return;
        }

        using var upstreamRequest = await BuildUpstreamRequestAsync(context.Request, upstreamUri, mapper);
        using var upstreamResponse = await httpClient.SendAsync(
            upstreamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);

        context.Response.StatusCode = (int) upstreamResponse.StatusCode;
        CopyResponseHeaders(upstreamResponse, context.Response);
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        if(context.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
        var buffer = new byte[16 * 1024];
        while(true)
        {
            int count = await responseStream.ReadAsync(buffer, context.RequestAborted);
            if(count == 0)
            {
                break;
            }

            await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    private static Uri BuildUpstreamUri(HttpRequest request, Uri backendUri, OpencodeLocationMapper mapper)
    {
        string query = RewriteQuery(request, mapper);
        return new UriBuilder(backendUri)
        {
            Path = request.PathBase.Add(request.Path).Value,
            Query = query
        }.Uri;
    }

    private static string RewriteQuery(HttpRequest request, OpencodeLocationMapper mapper)
    {
        var rewritten = new List<KeyValuePair<string, string?>>();
        bool changed = false;
        foreach(var pair in request.Query)
        {
            foreach(string? value in pair.Value)
            {
                string? mapped = value;
                if(pair.Key.Equals("location[directory]", StringComparison.Ordinal)
                    || (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                        && request.Path.Equals("/api/session", StringComparison.Ordinal)
                        && pair.Key.Equals("directory", StringComparison.Ordinal)))
                {
                    mapped = mapper.Map(value ?? String.Empty);
                }

                changed |= !String.Equals(value, mapped, StringComparison.Ordinal);
                rewritten.Add(new KeyValuePair<string, string?>(pair.Key, mapped));
            }
        }

        return changed
            ? QueryString.Create(rewritten).Value?.TrimStart('?') ?? String.Empty
            : request.QueryString.Value?.TrimStart('?') ?? String.Empty;
    }

    private static async Task<HttpRequestMessage> BuildUpstreamRequestAsync(HttpRequest request, Uri upstreamUri, OpencodeLocationMapper mapper)
    {
        var upstreamRequest = new HttpRequestMessage(new HttpMethod(request.Method), upstreamUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        if(request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
        {
            byte[]? rewrittenBody = await TryRewriteJsonBodyAsync(request, mapper);
            upstreamRequest.Content = rewrittenBody is null
                ? new StreamContent(request.Body)
                : new ByteArrayContent(rewrittenBody);
        }

        foreach(var header in request.Headers)
        {
            if(_hopByHopHeaders.Contains(header.Key)
                || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("x-opencode-directory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if(!upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                upstreamRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        string directory = request.Headers.TryGetValue("x-opencode-directory", out StringValues headerDirectory)
            ? mapper.Map(DecodeDirectoryHeader(headerDirectory.ToString()))
            : mapper.DefaultContainerDirectory;
        upstreamRequest.Headers.TryAddWithoutValidation("x-opencode-directory", Uri.EscapeDataString(directory));
        return upstreamRequest;
    }

    private static async Task<byte[]?> TryRewriteJsonBodyAsync(HttpRequest request, OpencodeLocationMapper mapper)
    {
        if(request.ContentType is null
            || !request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || request.Headers.ContainsKey("Content-Encoding")
            || !ShouldInspectJsonBody(request))
        {
            return null;
        }

        using var stream = new MemoryStream();
        await request.Body.CopyToAsync(stream, request.HttpContext.RequestAborted);
        byte[] bytes = stream.ToArray();
        try
        {
            if(JsonNode.Parse(bytes) is not JsonObject root)
            {
                return bytes;
            }

            bool changed = RewriteJsonLocation(request, root, mapper);
            return changed ? Encoding.UTF8.GetBytes(root.ToJsonString()) : bytes;
        }
        catch
        {
            return bytes;
        }
    }

    private static bool ShouldInspectJsonBody(HttpRequest request)
    {
        if(!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && !request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = request.Path.Value ?? String.Empty;
        return path is "/api/session" or "/api/session/import" or "/api/pty" or "/api/shell"
            || (path.StartsWith("/api/session/", StringComparison.Ordinal) && path.EndsWith("/move", StringComparison.Ordinal))
            || (path.StartsWith("/experimental/project/", StringComparison.Ordinal) && path.EndsWith("/copy", StringComparison.Ordinal));
    }

    private static bool RewriteJsonLocation(HttpRequest request, JsonObject root, OpencodeLocationMapper mapper)
    {
        string path = request.Path.Value ?? String.Empty;
        if(path is "/api/session" or "/api/session/import")
        {
            JsonObject location;
            if(root["location"] is JsonObject existingLocation)
            {
                location = existingLocation;
            }
            else if(root["location"] is null)
            {
                location = new JsonObject();
                root["location"] = location;
            }
            else
            {
                return false;
            }

            string source = location["directory"]?.GetValue<string>() ?? mapper.DefaultContainerDirectory;
            string mapped = mapper.Map(source);
            if(location["directory"]?.GetValue<string>() == mapped)
            {
                return false;
            }

            location["directory"] = mapped;
            return true;
        }

        string propertyName = path is "/api/pty" or "/api/shell" ? "cwd" : "directory";
        if(root[propertyName] is not JsonValue value || !value.TryGetValue<string>(out string? sourceDirectory))
        {
            return false;
        }

        string mappedDirectory = mapper.Map(sourceDirectory);
        if(String.Equals(sourceDirectory, mappedDirectory, StringComparison.Ordinal))
        {
            return false;
        }

        root[propertyName] = mappedDirectory;
        return true;
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach(var header in source.Headers.Concat(source.Content.Headers))
        {
            if(!_hopByHopHeaders.Contains(header.Key))
            {
                destination.Headers[header.Key] = new StringValues(header.Value.ToArray());
            }
        }
    }

    private static async Task ForwardWebSocketAsync(HttpContext context, Uri upstreamHttpUri, OpencodeLocationMapper mapper)
    {
        var upstreamUri = new UriBuilder(upstreamHttpUri)
        {
            Scheme = upstreamHttpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws"
        }.Uri;
        using var upstream = new ClientWebSocket();
        upstream.Options.Proxy = null;

        foreach(string protocol in context.WebSockets.WebSocketRequestedProtocols)
        {
            upstream.Options.AddSubProtocol(protocol);
        }

        foreach(var header in context.Request.Headers)
        {
            if(_hopByHopHeaders.Contains(header.Key)
                || header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("x-opencode-directory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            upstream.Options.SetRequestHeader(header.Key, header.Value.ToString());
        }

        string directory = context.Request.Headers.TryGetValue("x-opencode-directory", out StringValues headerDirectory)
            ? mapper.Map(DecodeDirectoryHeader(headerDirectory.ToString()))
            : mapper.DefaultContainerDirectory;
        upstream.Options.SetRequestHeader("x-opencode-directory", Uri.EscapeDataString(directory));
        upstream.Options.SetRequestHeader("Origin", $"{upstreamHttpUri.Scheme}://{upstreamHttpUri.Authority}");

        await upstream.ConnectAsync(upstreamUri, context.RequestAborted);
        using WebSocket client = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Task upstreamPump = PumpWebSocketAsync(client, upstream, cancellation.Token);
        Task clientPump = PumpWebSocketAsync(upstream, client, cancellation.Token);
        Task completed = await Task.WhenAny(upstreamPump, clientPump);
        Task remaining = ReferenceEquals(completed, upstreamPump) ? clientPump : upstreamPump;
        if(completed.IsCompletedSuccessfully)
        {
            await Task.WhenAny(remaining, Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted));
        }

        await cancellation.CancelAsync();
        await IgnoreCancellationAsync(upstreamPump);
        await IgnoreCancellationAsync(clientPump);
    }

    private static async Task PumpWebSocketAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while(!cancellationToken.IsCancellationRequested
            && source.State is WebSocketState.Open
            && destination.State is WebSocketState.Open)
        {
            WebSocketReceiveResult result = await source.ReceiveAsync(buffer, cancellationToken);
            if(result.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseOutputAsync(
                    result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    result.CloseStatusDescription,
                    CancellationToken.None);
                return;
            }

            await destination.SendAsync(
                buffer.AsMemory(0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                cancellationToken);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch(OperationCanceledException)
        {
        }
        catch(WebSocketException)
        {
        }
    }

    private static string DecodeDirectoryHeader(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
    }
}

internal sealed record OpencodeLocationMapping(string HostPath, string ContainerPath);

internal sealed class OpencodeLocationMapper
{
    private readonly IReadOnlyList<OpencodeLocationMapping> _mappings;

    public string DefaultContainerDirectory { get; }

    public OpencodeLocationMapper(string defaultContainerDirectory, IReadOnlyList<OpencodeLocationMapping> mappings)
    {
        DefaultContainerDirectory = defaultContainerDirectory;
        _mappings = mappings
            .OrderByDescending(mapping => mapping.HostPath.Length)
            .ToArray();
    }

    public string Map(string path)
    {
        foreach(var mapping in _mappings)
        {
            bool windowsPath = IsWindowsPath(mapping.HostPath);
            string hostPath = NormalizeHostPath(mapping.HostPath, windowsPath);
            string candidatePath = windowsPath ? path.Replace('/', '\\') : path;
            var comparison = windowsPath ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if(String.Equals(candidatePath, hostPath, comparison))
            {
                return mapping.ContainerPath;
            }

            int suffixStart;
            if(hostPath.Length > 0 && IsDirectorySeparator(hostPath[^1]))
            {
                if(!candidatePath.StartsWith(hostPath, comparison))
                {
                    continue;
                }

                suffixStart = hostPath.Length;
            }
            else
            {
                if(candidatePath.Length <= hostPath.Length
                    || !candidatePath.StartsWith(hostPath, comparison)
                    || !IsDirectorySeparator(candidatePath[hostPath.Length]))
                {
                    continue;
                }

                suffixStart = hostPath.Length + 1;
            }

            string suffix = candidatePath[suffixStart..].Replace('\\', '/');
            return String.IsNullOrEmpty(suffix)
                ? mapping.ContainerPath
                : $"{mapping.ContainerPath.TrimEnd('/')}/{suffix}";
        }

        return path;
    }

    private static bool IsWindowsPath(string path)
        => (path.Length >= 3 && Char.IsAsciiLetter(path[0]) && path[1] == ':' && IsDirectorySeparator(path[2]))
            || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal);

    private static string NormalizeHostPath(string path, bool windowsPath)
    {
        string normalized = windowsPath ? path.Replace('/', '\\') : path;
        int minimumLength = windowsPath && normalized.Length >= 3 && normalized[1] == ':' ? 3 : 1;
        return normalized.Length > minimumLength ? normalized.TrimEnd('/', '\\') : normalized;
    }

    private static bool IsDirectorySeparator(char value) => value is '/' or '\\';
}

internal sealed class OpencodeLocationProxyLease : IAsyncDisposable
{
    private WebApplication? _application;
    private HttpClient? _httpClient;

    public string ServerUrl { get; }

    public OpencodeLocationProxyLease(WebApplication application, HttpClient httpClient, string serverUrl)
    {
        _application = application;
        _httpClient = httpClient;
        ServerUrl = serverUrl.TrimEnd('/');
    }

    public async ValueTask DisposeAsync()
    {
        var application = Interlocked.Exchange(ref _application, null);
        try
        {
            if(application is not null)
            {
                try
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await application.StopAsync(cancellation.Token);
                }
                catch
                {
                    // Best effort shutdown; the process is already exiting this run.
                }

                try
                {
                    await application.DisposeAsync();
                }
                catch
                {
                    // Keep the remaining OCW cleanup path running.
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _httpClient, null)?.Dispose();
        }
    }
}
