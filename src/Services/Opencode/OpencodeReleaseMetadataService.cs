using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

namespace OpencodeWrap.Services.Opencode;

internal sealed record OpencodePackagePin(string Name, string Version);

internal sealed record OpencodeNpmPackageAsset(
    string PackageName,
    string Version,
    string DownloadUrl,
    string Integrity);

internal sealed record ResolvedOpencodeRelease(
    string PackageName,
    string Version,
    OpencodeNpmPackageAsset CliPackage,
    Dictionary<string, OpencodeNpmPackageAsset> PlatformPackages);

internal sealed record ResolvedOpencodeBinaryAsset(
    string Target,
    string ExecutableFileName,
    OpencodeNpmPackageAsset Asset);

internal sealed partial class OpencodeReleaseMetadataService : Singleton
{
    private const string NPM_REGISTRY_ROOT = "https://registry.npmjs.org";
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
    private static readonly OpencodePackagePin _packagePin = LoadPackagePin();

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly OcwHostPathService _hostPathService;

    [Inject]
    private readonly FileLockService _fileLockService;

    internal static OpencodePackagePin PackagePin => _packagePin;

    public async Task<(bool Success, ResolvedOpencodeRelease Release)> TryResolvePinnedAsync()
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
        if(cachedRelease is not null && IsExpectedPinnedRelease(cachedRelease))
        {
            _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, $"reusing cached OpenCode V2 package metadata for {_packagePin.Name}@{_packagePin.Version}", LogLevel.Information);
            return (true, cachedRelease);
        }

        _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, $"resolving pinned OpenCode V2 package {_packagePin.Name}@{_packagePin.Version}", LogLevel.Information);
        var (success, release) = await TryFetchPinnedReleaseAsync();
        if(!success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, $"Failed to resolve pinned OpenCode V2 package {_packagePin.Name}@{_packagePin.Version}.");
            return (false, emptyRelease);
        }

        await TryWriteCachedReleaseAsync(paths.OpencodePackageCachePath, release);
        _deferredSessionLogService.Write(LogCategories.OPENCODE_VERSION, $"resolved pinned OpenCode V2 package {_packagePin.Name}@{release.Version}", LogLevel.Information);
        return (true, release);
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
        return TryResolveBinaryAsset(release, BuildTargetCandidates(os, arch, isMusl, needsBaseline), os, LogCategories.OPENCODE_HOST);
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
            BuildTargetCandidates("linux", normalizedArchitecture, isMusl, normalizedArchitecture == "x64" && needsBaseline),
            "linux",
            LogCategories.OPENCODE_RUNTIME);
    }

    internal static IReadOnlyList<string> BuildTargetCandidates(string os, string arch, bool isMusl, bool needsBaseline)
    {
        if(arch == "arm64")
        {
            return os == "linux"
                ? isMusl
                    ? [$"{os}-{arch}-musl", $"{os}-{arch}"]
                    : [$"{os}-{arch}", $"{os}-{arch}-musl"]
                : [$"{os}-{arch}"];
        }

        if(arch != "x64")
        {
            return [];
        }

        string preferred = needsBaseline ? $"{os}-{arch}-baseline" : $"{os}-{arch}";
        string fallback = needsBaseline ? $"{os}-{arch}" : $"{os}-{arch}-baseline";
        if(os != "linux")
        {
            return [preferred, fallback];
        }

        string preferredLibcSuffix = isMusl ? "-musl" : String.Empty;
        string fallbackLibcSuffix = isMusl ? String.Empty : "-musl";
        return
        [
            preferred + preferredLibcSuffix,
            fallback + preferredLibcSuffix,
            preferred + fallbackLibcSuffix,
            fallback + fallbackLibcSuffix
        ];
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

        string name = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? String.Empty
            : String.Empty;
        string version = root.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString() ?? String.Empty
            : String.Empty;

        if(!String.Equals(name, expectedName, StringComparison.Ordinal))
        {
            errorMessage = $"npm returned package '{name}' instead of '{expectedName}'.";
            return false;
        }

        if(!String.Equals(version, expectedVersion, StringComparison.Ordinal))
        {
            errorMessage = $"npm returned {name}@{version} instead of the pinned version {expectedVersion}.";
            return false;
        }

        if(!root.TryGetProperty("dist", out var distElement) || distElement.ValueKind != JsonValueKind.Object)
        {
            errorMessage = $"npm metadata for {name}@{version} did not include dist metadata.";
            return false;
        }

        string downloadUrl = distElement.TryGetProperty("tarball", out var tarballElement)
            ? tarballElement.GetString() ?? String.Empty
            : String.Empty;
        string integrity = distElement.TryGetProperty("integrity", out var integrityElement)
            ? integrityElement.GetString() ?? String.Empty
            : String.Empty;

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
        IReadOnlyList<string> targets,
        string operatingSystem,
        string logCategory)
    {
        var emptyAsset = CreateEmptyBinaryAsset();
        string executableFileName = operatingSystem == "windows"
            ? "opencode2.exe"
            : "opencode2";

        foreach(string target in targets)
        {
            string packageName = $"{release.PackageName}-{target}";
            if(release.PlatformPackages.TryGetValue(packageName, out var package)
                && String.Equals(package.Version, release.Version, StringComparison.Ordinal))
            {
                return (true, new ResolvedOpencodeBinaryAsset(target, executableFileName, package));
            }
        }

        _deferredSessionLogService.WriteErrorOrConsole(
            logCategory,
            $"No matching OpenCode V2 npm platform package was found for targets: {String.Join(", ", targets)}.");
        return (false, emptyAsset);
    }

    private async Task<(bool Success, ResolvedOpencodeRelease Release)> TryFetchPinnedReleaseAsync()
    {
        var emptyRelease = CreateEmptyRelease();
        var (cliSuccess, cliRoot) = await TryFetchPackageMetadataAsync(_packagePin.Name, _packagePin.Version);
        if(!cliSuccess)
        {
            return (false, emptyRelease);
        }

        if(!TryParsePackageAsset(cliRoot, _packagePin.Name, _packagePin.Version, out var cliPackage, out string cliError))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, cliError);
            return (false, emptyRelease);
        }

        if(!TryReadPinnedPlatformDependencies(cliRoot, out var platformPackageNames, out string dependencyError))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, dependencyError);
            return (false, emptyRelease);
        }

        var packageTasks = platformPackageNames.Select(TryFetchPinnedPlatformPackageAsync).ToArray();
        var packageResults = await Task.WhenAll(packageTasks);
        if(packageResults.Any(result => !result.Success))
        {
            return (false, emptyRelease);
        }

        var platformPackages = packageResults.ToDictionary(
            result => result.Asset.PackageName,
            result => result.Asset,
            StringComparer.Ordinal);

        return (true, new ResolvedOpencodeRelease(_packagePin.Name, _packagePin.Version, cliPackage, platformPackages));
    }

    private async Task<(bool Success, OpencodeNpmPackageAsset Asset)> TryFetchPinnedPlatformPackageAsync(string packageName)
    {
        var emptyAsset = new OpencodeNpmPackageAsset("", "", "", "");
        var (success, root) = await TryFetchPackageMetadataAsync(packageName, _packagePin.Version);
        if(!success)
        {
            return (false, emptyAsset);
        }

        if(!TryParsePackageAsset(root, packageName, _packagePin.Version, out var asset, out string errorMessage))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, errorMessage);
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
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, $"npm metadata lookup for {packageName}@{version} failed with HTTP {(int) response.StatusCode} {response.ReasonPhrase}.");
                return (false, default);
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return (true, document.RootElement.Clone());
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_VERSION, $"Failed to fetch npm metadata for {packageName}@{version}: {ex.Message}");
            return (false, default);
        }
    }

    private bool TryReadPinnedPlatformDependencies(JsonElement cliRoot, out List<string> packageNames, out string errorMessage)
    {
        packageNames = [];
        errorMessage = String.Empty;
        if(!cliRoot.TryGetProperty("optionalDependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Object)
        {
            errorMessage = $"npm metadata for {_packagePin.Name}@{_packagePin.Version} did not include platform optional dependencies.";
            return false;
        }

        string platformPackagePrefix = $"{_packagePin.Name}-";
        foreach(var dependency in dependencies.EnumerateObject())
        {
            if(!dependency.Name.StartsWith(platformPackagePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string dependencyVersion = dependency.Value.GetString() ?? String.Empty;
            if(!String.Equals(dependencyVersion, _packagePin.Version, StringComparison.Ordinal))
            {
                errorMessage = $"{_packagePin.Name}@{_packagePin.Version} references {dependency.Name}@{dependencyVersion}; every platform package must match the pinned CLI version.";
                return false;
            }

            packageNames.Add(dependency.Name);
        }

        packageNames.Sort(StringComparer.Ordinal);
        string[] requiredPackageNames = [.. _requiredPlatformTargets.Select(target => $"{_packagePin.Name}-{target}")];
        if(!packageNames.SequenceEqual(requiredPackageNames, StringComparer.Ordinal))
        {
            errorMessage = $"npm metadata for {_packagePin.Name}@{_packagePin.Version} did not contain the complete pinned platform package set.";
            return false;
        }

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
        try
        {
            string json = JsonSerializer.Serialize(release, OpencodeJsonContext.Default.ResolvedOpencodeRelease);
            await File.WriteAllTextAsync(cachePath, json);
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_VERSION, $"Failed to write cached OpenCode V2 package metadata '{cachePath}': {ex.Message}");
        }
    }

    private static bool IsExpectedPinnedRelease(ResolvedOpencodeRelease release)
        => String.Equals(release.PackageName, _packagePin.Name, StringComparison.Ordinal)
            && String.Equals(release.Version, _packagePin.Version, StringComparison.Ordinal)
            && IsExpectedPackageAsset(release.CliPackage, _packagePin.Name)
            && release.PlatformPackages is not null
            && release.PlatformPackages.Count == _requiredPlatformTargets.Length
            && _requiredPlatformTargets.All(target =>
            {
                string packageName = $"{_packagePin.Name}-{target}";
                return release.PlatformPackages.TryGetValue(packageName, out var package)
                    && IsExpectedPackageAsset(package, packageName);
            });

    private static bool IsExpectedPackageAsset(OpencodeNpmPackageAsset? asset, string expectedName)
        => asset is not null
            && String.Equals(asset.PackageName, expectedName, StringComparison.Ordinal)
            && String.Equals(asset.Version, _packagePin.Version, StringComparison.Ordinal)
            && Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri)
            && downloadUri.Scheme == Uri.UriSchemeHttps
            && OpencodePackageArtifactService.TryGetSha512Digest(asset.Integrity, out _);

    private static OpencodePackagePin LoadPackagePin()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("OpencodeV2Package.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The embedded OpenCode V2 package pin could not be loaded.");
        var pin = JsonSerializer.Deserialize(stream, OpencodeJsonContext.Default.OpencodePackagePin)
            ?? throw new InvalidOperationException("The embedded OpenCode V2 package pin is invalid.");

        if(String.IsNullOrWhiteSpace(pin.Name) || String.IsNullOrWhiteSpace(pin.Version))
        {
            throw new InvalidOperationException("The embedded OpenCode V2 package pin must include a package name and exact version.");
        }

        return pin;
    }

    private static ResolvedOpencodeRelease CreateEmptyRelease()
        => new("", "", new OpencodeNpmPackageAsset("", "", "", ""), []);

    private static ResolvedOpencodeBinaryAsset CreateEmptyBinaryAsset()
        => new("", "", new OpencodeNpmPackageAsset("", "", "", ""));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ocw/opencode2-package-metadata");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
