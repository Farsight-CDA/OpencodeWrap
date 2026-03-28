using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

namespace OpencodeWrap.Services.Opencode;

internal sealed record OpencodeReleaseAsset(string Name, string DownloadUrl, string? Sha256);

internal sealed record LatestOpencodeRelease(
    string Version,
    string TagName,
    DateTimeOffset PublishedAtUtc,
    Dictionary<string, OpencodeReleaseAsset> Assets);

internal sealed record CachedLatestOpencodeRelease(DateTimeOffset ResolvedAtUtc, LatestOpencodeRelease Release);

internal sealed record ResolvedOpencodeBinaryAsset(
    string Target,
    string ExecutableFileName,
    OpencodeReleaseAsset Asset);

internal sealed partial class OpencodeReleaseMetadataService : Singleton
{
    private const string LATEST_RELEASE_ENDPOINT = "https://api.github.com/repos/anomalyco/opencode/releases/latest";
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
    private static readonly HttpClient _httpClient = CreateHttpClient();

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly OcwHostPathService _hostPathService;

    [Inject]
    private readonly FileLockService _fileLockService;

    public async Task<(bool Success, LatestOpencodeRelease Release)> TryResolveLatestAsync()
    {
        var emptyRelease = new LatestOpencodeRelease("", "", DateTimeOffset.MinValue, []);
        if(!_hostPathService.TryGetPaths(out var paths))
        {
            return (false, emptyRelease);
        }

        await using var latestLock = await _fileLockService.AcquireAsync(paths.OpencodeLatestLockPath, LogCategories.OPENCODE_VERSION, "OpenCode latest-version metadata");
        if(latestLock is null)
        {
            return (false, emptyRelease);
        }

        var cachedRelease = TryReadCachedRelease(paths.OpencodeLatestCachePath);
        if(cachedRelease is not null && DateTimeOffset.UtcNow - cachedRelease.ResolvedAtUtc <= _cacheTtl)
        {
            _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, $"reusing cached latest OpenCode version {cachedRelease.Release.Version}", LogLevel.Information);
            return (true, cachedRelease.Release);
        }

        _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, "refreshing latest OpenCode release metadata", LogLevel.Information);
        var (success, release) = await TryFetchLatestReleaseAsync();
        if(success)
        {
            var cacheEnvelope = new CachedLatestOpencodeRelease(DateTimeOffset.UtcNow, release);
            await TryWriteCachedReleaseAsync(paths.OpencodeLatestCachePath, cacheEnvelope);
            _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, $"resolved latest OpenCode version {release.Version}", LogLevel.Information);
            return (true, release);
        }

        if(cachedRelease is not null)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, $"Failed to refresh latest OpenCode metadata; reusing cached version {cachedRelease.Release.Version}.");
            return (true, cachedRelease.Release);
        }

        _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, "Failed to resolve the latest OpenCode release metadata.");
        return (false, emptyRelease);
    }

    public async Task<(bool Success, ResolvedOpencodeBinaryAsset Asset)> TryResolveCurrentHostBinaryAsync(LatestOpencodeRelease release)
    {
        var emptyAsset = new ResolvedOpencodeBinaryAsset("", "", new OpencodeReleaseAsset("", "", null));
        string? os = GetCurrentHostOs();
        string? arch = NormalizeArchitecture(RuntimeInformation.OSArchitecture.ToString());
        if(String.IsNullOrWhiteSpace(os) || String.IsNullOrWhiteSpace(arch))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, "Unsupported host platform for managed OpenCode installation.");
            return (false, emptyAsset);
        }

        bool isMusl = os == "linux" && await IsMuslLinuxHostAsync();
        bool needsBaseline = arch == "x64" && !Avx2.IsSupported;
        return TryResolveBinaryAsset(release, BuildTargetCandidates(os, arch, isMusl, needsBaseline), operatingSystem: os, logCategory: LogCategories.OPENCODE_HOST);
    }

    public (bool Success, ResolvedOpencodeBinaryAsset Asset) TryResolveLinuxRuntimeBinary(LatestOpencodeRelease release, string architecture)
    {
        var emptyAsset = new ResolvedOpencodeBinaryAsset("", "", new OpencodeReleaseAsset("", "", null));
        string? normalizedArchitecture = NormalizeArchitecture(architecture);
        if(String.IsNullOrWhiteSpace(normalizedArchitecture))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Unsupported Docker image architecture '{architecture}'.");
            return (false, emptyAsset);
        }

        bool needsBaseline = normalizedArchitecture == "x64" && !Avx2.IsSupported;
        return TryResolveBinaryAsset(release, BuildTargetCandidates("linux", normalizedArchitecture, isMusl: false, needsBaseline), operatingSystem: "linux", logCategory: LogCategories.OPENCODE_RUNTIME);
    }

    private (bool Success, ResolvedOpencodeBinaryAsset Asset) TryResolveBinaryAsset(LatestOpencodeRelease release, IReadOnlyList<string> targets, string operatingSystem, string logCategory)
    {
        var emptyAsset = new ResolvedOpencodeBinaryAsset("", "", new OpencodeReleaseAsset("", "", null));
        string executableFileName = operatingSystem == "windows"
            ? "opencode.exe"
            : "opencode";

        foreach(string target in targets)
        {
            string assetName = $"opencode-{target}{(operatingSystem == "linux" ? ".tar.gz" : ".zip")}";
            if(release.Assets.TryGetValue(assetName, out var asset))
            {
                return (true, new ResolvedOpencodeBinaryAsset(target, executableFileName, asset));
            }
        }

        _deferredSessionLogService.WriteErrorOrConsole(
            logCategory,
            $"No matching upstream OpenCode artifact was found for targets: {String.Join(", ", targets)}.");
        return (false, emptyAsset);
    }

    private static string? GetCurrentHostOs()
        => OperatingSystem.IsLinux()
            ? "linux"
            : OperatingSystem.IsMacOS()
                ? "darwin"
                : OperatingSystem.IsWindows()
                    ? "windows"
                    : null;

    private static string? NormalizeArchitecture(string architecture)
        => architecture.Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" or "x86_64" => "x64",
            "arm64" or "aarch64" => "arm64",
            _ => null
        };

    private static List<string> BuildTargetCandidates(string os, string arch, bool isMusl, bool needsBaseline)
    {
        var targets = new List<string>();

        void AddTarget(string value)
        {
            if(!targets.Contains(value, StringComparer.Ordinal))
            {
                targets.Add(value);
            }
        }

        if(arch == "x64")
        {
            if(isMusl)
            {
                if(needsBaseline)
                {
                    AddTarget($"{os}-{arch}-baseline-musl");
                }

                AddTarget($"{os}-{arch}-musl");
                AddTarget($"{os}-{arch}-baseline-musl");
            }
            else
            {
                if(needsBaseline)
                {
                    AddTarget($"{os}-{arch}-baseline");
                }

                AddTarget($"{os}-{arch}");
                AddTarget($"{os}-{arch}-baseline");
            }

            return targets;
        }

        AddTarget($"{os}-{arch}");
        if(isMusl)
        {
            AddTarget($"{os}-{arch}-musl");
        }

        return targets;
    }

    private async Task<bool> IsMuslLinuxHostAsync()
    {
        if(File.Exists("/etc/alpine-release"))
        {
            return true;
        }

        var lddResult = await ProcessRunner.RunAsync("ldd", ["--version"]);
        return lddResult.Started
            && (lddResult.StdOut.Contains("musl", StringComparison.OrdinalIgnoreCase)
                || lddResult.StdErr.Contains("musl", StringComparison.OrdinalIgnoreCase));
    }

    private CachedLatestOpencodeRelease? TryReadCachedRelease(string cachePath)
    {
        if(!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(cachePath);
            return JsonSerializer.Deserialize(json, OpencodeJsonContext.Default.CachedLatestOpencodeRelease);
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, $"Ignoring invalid cached OpenCode metadata at '{cachePath}': {ex.Message}");
            return null;
        }
    }

    private async Task TryWriteCachedReleaseAsync(string cachePath, CachedLatestOpencodeRelease cacheEnvelope)
    {
        try
        {
            string json = JsonSerializer.Serialize(cacheEnvelope, OpencodeJsonContext.Default.CachedLatestOpencodeRelease);
            await File.WriteAllTextAsync(cachePath, json);
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, $"Failed to write cached OpenCode metadata '{cachePath}': {ex.Message}");
        }
    }

    private async Task<(bool Success, LatestOpencodeRelease Release)> TryFetchLatestReleaseAsync()
    {
        var emptyRelease = new LatestOpencodeRelease("", "", DateTimeOffset.MinValue, []);

        try
        {
            using var response = await _httpClient.GetAsync(LATEST_RELEASE_ENDPOINT, HttpCompletionOption.ResponseHeadersRead);
            if(!response.IsSuccessStatusCode)
            {
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, $"OpenCode latest release lookup failed with HTTP {(int) response.StatusCode} {response.ReasonPhrase}.");
                return (false, emptyRelease);
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;

            string version = root.TryGetProperty("tag_name", out var tagNameElement)
                ? (tagNameElement.GetString() ?? String.Empty).Trim().TrimStart('v')
                : String.Empty;
            string tagName = tagNameElement.GetString() ?? String.Empty;

            if(String.IsNullOrWhiteSpace(version))
            {
                return (false, emptyRelease);
            }

            var publishedAtUtc = root.TryGetProperty("published_at", out var publishedAtElement)
                && publishedAtElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(publishedAtElement.GetString(), out var parsedPublishedAt)
                    ? parsedPublishedAt
                    : DateTimeOffset.UtcNow;

            var assets = new Dictionary<string, OpencodeReleaseAsset>(StringComparer.OrdinalIgnoreCase);
            if(root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach(var assetElement in assetsElement.EnumerateArray())
                {
                    string name = assetElement.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() ?? String.Empty
                        : String.Empty;
                    string downloadUrl = assetElement.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString() ?? String.Empty
                        : String.Empty;
                    string? sha256 = assetElement.TryGetProperty("digest", out var digestElement)
                        ? NormalizeSha256Digest(digestElement.GetString())
                        : null;

                    if(String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(downloadUrl))
                    {
                        continue;
                    }

                    assets[name] = new OpencodeReleaseAsset(name, downloadUrl, sha256);
                }
            }

            return (true, new LatestOpencodeRelease(version, tagName, publishedAtUtc, assets));
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, $"Failed to fetch the latest OpenCode release metadata: {ex.Message}");
            return (false, emptyRelease);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ocw/latest-metadata");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string? NormalizeSha256Digest(string? digest)
    {
        if(String.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        const string PREFIX = "sha256:";
        return digest.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase)
            ? digest[PREFIX.Length..]
            : digest;
    }
}
