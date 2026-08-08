using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;

namespace OpencodeWrap.Services.Opencode;

internal sealed record OpencodeNpmPackageAsset(
    string PackageName,
    string Version,
    string DownloadUrl,
    string Integrity);

internal sealed record ResolvedOpencodeRelease(
    string PackageName,
    string Version,
    Dictionary<string, OpencodeNpmPackageAsset> PlatformPackages);

internal sealed record ResolvedOpencodeBinaryAsset(
    string ExecutableFileName,
    OpencodeNpmPackageAsset Asset);

internal sealed partial class OpencodeReleaseMetadataService : Singleton
{
    private const string NPM_REGISTRY_ROOT = "https://registry.npmjs.org";
    private const string OPENCODE_V2_PACKAGE_NAME = "@opencode-ai/cli";
    private const string OPENCODE_V2_DIST_TAG = "next";
    private static readonly string[] _requiredPlatformTargets =
    [
        "darwin-arm64",
        "darwin-x64",
        "darwin-x64-baseline",
        "linux-arm64",
        "linux-arm64-musl",
        "linux-x64",
        "linux-x64-baseline",
        "linux-x64-baseline-musl",
        "linux-x64-musl",
        "windows-arm64",
        "windows-x64",
        "windows-x64-baseline"
    ];
    private static readonly HttpClient _httpClient = CreateHttpClient();

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly OcwHostPathService _hostPathService;

    [Inject]
    private readonly FileLockService _fileLockService;

    public async Task<(bool Success, ResolvedOpencodeRelease Release)> TryResolveCurrentV2Async()
    {
        var emptyRelease = CreateEmptyRelease();
        if(!_hostPathService.TryGetPaths(out var paths))
        {
            return (false, emptyRelease);
        }

        await using var packageLock = await _fileLockService.AcquireAsync(paths.OpencodePackageLockPath, LogCategories.OPENCODE_VERSION, "OpenCode V2 package metadata");
        if(packageLock is null)
        {
            return (false, emptyRelease);
        }

        var cachedRelease = TryReadCachedRelease(paths.OpencodePackageCachePath);
        _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, $"resolving current OpenCode V2 package {OPENCODE_V2_PACKAGE_NAME}@{OPENCODE_V2_DIST_TAG}", LogLevel.Information);
        var (success, release) = await TryFetchCurrentV2ReleaseAsync(cachedRelease);
        if(success)
        {
            if(!ReferenceEquals(release, cachedRelease))
            {
                await TryWriteCachedReleaseAsync(paths.OpencodePackageCachePath, release);
            }
            _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, $"resolved OpenCode V2 {release.Version} for this session", LogLevel.Information);
            return (true, release);
        }

        if(cachedRelease is not null && IsValidRelease(cachedRelease))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, $"Could not resolve the current OpenCode V2 release; using cached {cachedRelease.Version} for this session.");
            return (true, cachedRelease);
        }

        _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, $"Failed to resolve {OPENCODE_V2_PACKAGE_NAME}@{OPENCODE_V2_DIST_TAG}, and no valid cached V2 release is available.");
        return (false, emptyRelease);
    }

    public async Task<(bool Success, ResolvedOpencodeBinaryAsset Asset)> TryResolveCurrentHostBinaryAsync(ResolvedOpencodeRelease release)
    {
        var emptyAsset = CreateEmptyBinaryAsset();
        string? os = GetCurrentHostOs();
        string? arch = NormalizeArchitecture(RuntimeInformation.OSArchitecture.ToString());
        if(String.IsNullOrWhiteSpace(os) || String.IsNullOrWhiteSpace(arch))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, "Unsupported host platform for managed OpenCode V2 installation.");
            return (false, emptyAsset);
        }

        bool isMusl = os == "linux" && await IsMuslLinuxHostAsync();
        bool needsBaseline = arch == "x64" && !Avx2.IsSupported;
        return TryResolveBinaryAsset(release, BuildTarget(os, arch, isMusl, needsBaseline), os, LogCategories.OPENCODE_HOST);
    }

    public (bool Success, ResolvedOpencodeBinaryAsset Asset) TryResolveLinuxRuntimeBinary(
        ResolvedOpencodeRelease release,
        string architecture,
        bool isMusl,
        bool needsBaseline)
    {
        var emptyAsset = CreateEmptyBinaryAsset();
        string? normalizedArchitecture = NormalizeArchitecture(architecture);
        if(String.IsNullOrWhiteSpace(normalizedArchitecture))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Unsupported Docker image architecture '{architecture}'.");
            return (false, emptyAsset);
        }

        return TryResolveBinaryAsset(
            release,
            BuildTarget("linux", normalizedArchitecture, isMusl, normalizedArchitecture == "x64" && needsBaseline),
            "linux",
            LogCategories.OPENCODE_RUNTIME);
    }

    internal static string BuildTarget(string os, string arch, bool isMusl, bool needsBaseline)
    {
        var target = new StringBuilder($"{os}-{arch}");
        if(arch == "x64" && needsBaseline)
        {
            target.Append("-baseline");
        }

        if(os == "linux" && isMusl)
        {
            target.Append("-musl");
        }

        return target.ToString();
    }

    internal static bool TryParsePackageAsset(
        JsonElement root,
        string expectedName,
        string expectedVersion,
        out OpencodeNpmPackageAsset asset,
        out string errorMessage)
    {
        asset = new OpencodeNpmPackageAsset("", "", "", "");
        errorMessage = String.Empty;

        string name = TryGetStringProperty(root, "name");
        string version = TryGetStringProperty(root, "version");

        if(!String.Equals(name, expectedName, StringComparison.Ordinal))
        {
            errorMessage = $"npm returned package '{name}' instead of '{expectedName}'.";
            return false;
        }

        if(!String.Equals(version, expectedVersion, StringComparison.Ordinal))
        {
            errorMessage = $"npm returned {name}@{version} instead of the resolved version {expectedVersion}.";
            return false;
        }

        if(root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("dist", out var distElement)
            || distElement.ValueKind != JsonValueKind.Object)
        {
            errorMessage = $"npm metadata for {name}@{version} did not include dist metadata.";
            return false;
        }

        string downloadUrl = TryGetStringProperty(distElement, "tarball");
        string integrity = TryGetStringProperty(distElement, "integrity");

        if(!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri)
            || downloadUri.Scheme != Uri.UriSchemeHttps)
        {
            errorMessage = $"npm metadata for {name}@{version} contained an invalid tarball URL.";
            return false;
        }

        if(!OpencodePackageArtifactService.TryGetSha512Digest(integrity, out _))
        {
            errorMessage = $"npm metadata for {name}@{version} did not include valid SHA-512 integrity metadata.";
            return false;
        }

        asset = new OpencodeNpmPackageAsset(name, version, downloadUrl, integrity);
        return true;
    }

    private (bool Success, ResolvedOpencodeBinaryAsset Asset) TryResolveBinaryAsset(
        ResolvedOpencodeRelease release,
        string target,
        string operatingSystem,
        string logCategory)
    {
        var emptyAsset = CreateEmptyBinaryAsset();
        string executableFileName = operatingSystem == "windows"
            ? "opencode2.exe"
            : "opencode2";

        string packageName = $"{release.PackageName}-{target}";
        if(release.PlatformPackages.TryGetValue(packageName, out var package)
            && String.Equals(package.Version, release.Version, StringComparison.Ordinal))
        {
            return (true, new ResolvedOpencodeBinaryAsset(executableFileName, package));
        }

        _deferredSessionLogService.WriteErrorOrConsole(
            logCategory,
            $"OpenCode V2 npm platform package '{packageName}' was not found.");
        return (false, emptyAsset);
    }

    private async Task<(bool Success, ResolvedOpencodeRelease Release)> TryFetchCurrentV2ReleaseAsync(ResolvedOpencodeRelease? cachedRelease)
    {
        var emptyRelease = CreateEmptyRelease();
        var (cliSuccess, cliRoot) = await TryFetchPackageMetadataAsync(OPENCODE_V2_PACKAGE_NAME, OPENCODE_V2_DIST_TAG);
        if(!cliSuccess)
        {
            return (false, emptyRelease);
        }

        string resolvedVersion = TryGetStringProperty(cliRoot, "version");
        string cliError = String.Empty;
        if(String.IsNullOrWhiteSpace(resolvedVersion)
            || !TryParsePackageAsset(cliRoot, OPENCODE_V2_PACKAGE_NAME, resolvedVersion, out _, out cliError))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, String.IsNullOrWhiteSpace(cliError) ? "npm returned invalid OpenCode V2 package metadata." : cliError);
            return (false, emptyRelease);
        }

        if(!TryReadPlatformDependencies(cliRoot, resolvedVersion, out var platformPackageNames, out string dependencyError))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, dependencyError);
            return (false, emptyRelease);
        }

        if(cachedRelease is not null
            && String.Equals(cachedRelease.Version, resolvedVersion, StringComparison.Ordinal)
            && IsValidRelease(cachedRelease))
        {
            _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, $"reusing cached package metadata for OpenCode V2 {resolvedVersion}", LogLevel.Information);
            return (true, cachedRelease);
        }

        var packageTasks = platformPackageNames.Select(packageName => TryFetchPlatformPackageAsync(packageName, resolvedVersion)).ToArray();
        var packageResults = await Task.WhenAll(packageTasks);
        if(packageResults.Any(result => !result.Success))
        {
            return (false, emptyRelease);
        }

        var platformPackages = packageResults.ToDictionary(
            result => result.Asset.PackageName,
            result => result.Asset,
            StringComparer.Ordinal);

        return (true, new ResolvedOpencodeRelease(OPENCODE_V2_PACKAGE_NAME, resolvedVersion, platformPackages));
    }

    private async Task<(bool Success, OpencodeNpmPackageAsset Asset)> TryFetchPlatformPackageAsync(string packageName, string resolvedVersion)
    {
        var emptyAsset = new OpencodeNpmPackageAsset("", "", "", "");
        var (success, root) = await TryFetchPackageMetadataAsync(packageName, resolvedVersion);
        if(!success)
        {
            return (false, emptyAsset);
        }

        if(!TryParsePackageAsset(root, packageName, resolvedVersion, out var asset, out string errorMessage))
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, errorMessage);
            return (false, emptyAsset);
        }

        return (true, asset);
    }

    private async Task<(bool Success, JsonElement Root)> TryFetchPackageMetadataAsync(string packageName, string version)
    {
        try
        {
            string escapedName = Uri.EscapeDataString(packageName);
            string escapedVersion = Uri.EscapeDataString(version);
            using var response = await _httpClient.GetAsync($"{NPM_REGISTRY_ROOT}/{escapedName}/{escapedVersion}", HttpCompletionOption.ResponseHeadersRead);
            if(!response.IsSuccessStatusCode)
            {
                _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, $"npm metadata lookup for {packageName}@{version} failed with HTTP {(int) response.StatusCode} {response.ReasonPhrase}.");
                return (false, default);
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return (true, document.RootElement.Clone());
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, $"Failed to fetch npm metadata for {packageName}@{version}: {ex.Message}");
            return (false, default);
        }
    }

    private static bool TryReadPlatformDependencies(JsonElement cliRoot, string resolvedVersion, out List<string> packageNames, out string errorMessage)
    {
        packageNames = [];
        errorMessage = String.Empty;
        if(cliRoot.ValueKind != JsonValueKind.Object
            || !cliRoot.TryGetProperty("optionalDependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Object)
        {
            errorMessage = $"npm metadata for {OPENCODE_V2_PACKAGE_NAME}@{resolvedVersion} did not include platform optional dependencies.";
            return false;
        }

        foreach(string target in _requiredPlatformTargets)
        {
            string packageName = $"{OPENCODE_V2_PACKAGE_NAME}-{target}";
            if(!dependencies.TryGetProperty(packageName, out var dependency))
            {
                errorMessage = $"npm metadata for {OPENCODE_V2_PACKAGE_NAME}@{resolvedVersion} did not include required platform package {packageName}.";
                return false;
            }

            string dependencyVersion = dependency.ValueKind == JsonValueKind.String
                ? dependency.GetString() ?? String.Empty
                : String.Empty;
            if(!String.Equals(dependencyVersion, resolvedVersion, StringComparison.Ordinal))
            {
                errorMessage = $"{OPENCODE_V2_PACKAGE_NAME}@{resolvedVersion} references {packageName}@{dependencyVersion}; every platform package must match the resolved CLI version.";
                return false;
            }

            packageNames.Add(packageName);
        }

        packageNames.Sort(StringComparer.Ordinal);
        return true;
    }

    private static string? GetCurrentHostOs()
        => OperatingSystem.IsLinux()
            ? "linux"
            : OperatingSystem.IsMacOS()
                ? "darwin"
                : OperatingSystem.IsWindows()
                    ? "windows"
                    : null;

    internal static string? NormalizeArchitecture(string architecture)
        => architecture.Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" or "x86_64" => "x64",
            "arm64" or "aarch64" => "arm64",
            _ => null
        };

    private static async Task<bool> IsMuslLinuxHostAsync()
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

    private ResolvedOpencodeRelease? TryReadCachedRelease(string cachePath)
    {
        if(!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(cachePath);
            return JsonSerializer.Deserialize(json, OpencodeJsonContext.Default.ResolvedOpencodeRelease);
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, $"Ignoring invalid cached OpenCode V2 package metadata at '{cachePath}': {ex.Message}");
            return null;
        }
    }

    private async Task TryWriteCachedReleaseAsync(string cachePath, ResolvedOpencodeRelease release)
    {
        string temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(release, OpencodeJsonContext.Default.ResolvedOpencodeRelease);
            await File.WriteAllTextAsync(temporaryPath, json);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, $"Failed to write cached OpenCode V2 package metadata '{cachePath}': {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static bool IsValidRelease(ResolvedOpencodeRelease release)
        => String.Equals(release.PackageName, OPENCODE_V2_PACKAGE_NAME, StringComparison.Ordinal)
            && !String.IsNullOrWhiteSpace(release.Version)
            && release.PlatformPackages is not null
            && release.PlatformPackages.Count == _requiredPlatformTargets.Length
            && _requiredPlatformTargets.All(target =>
            {
                string packageName = $"{OPENCODE_V2_PACKAGE_NAME}-{target}";
                return release.PlatformPackages.TryGetValue(packageName, out var package)
                    && IsExpectedPackageAsset(package, packageName, release.Version);
            });

    private static bool IsExpectedPackageAsset(OpencodeNpmPackageAsset? asset, string expectedName, string expectedVersion)
        => asset is not null
            && String.Equals(asset.PackageName, expectedName, StringComparison.Ordinal)
            && String.Equals(asset.Version, expectedVersion, StringComparison.Ordinal)
            && Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri)
            && downloadUri.Scheme == Uri.UriSchemeHttps
            && OpencodePackageArtifactService.TryGetSha512Digest(asset.Integrity, out _);

    private static ResolvedOpencodeRelease CreateEmptyRelease()
        => new("", "", []);

    private static ResolvedOpencodeBinaryAsset CreateEmptyBinaryAsset()
        => new("", new OpencodeNpmPackageAsset("", "", "", ""));

    private static string TryGetStringProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? String.Empty
                : String.Empty;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ocw/opencode2-package-metadata");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
