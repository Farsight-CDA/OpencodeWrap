using Microsoft.Extensions.Logging;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpencodeWrap.Services.Opencode;

internal sealed record ManagedHostOpencodeMetadata(
    string Version,
    string Target,
    string AssetName,
    string AssetUrl,
    string? Sha256,
    string ExecutableRelativePath,
    DateTimeOffset InstalledAtUtc);

internal sealed partial class ManagedHostOpencodeService : Singleton
{
    private static readonly HttpClient _httpClient = CreateHttpClient();

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly OcwHostPathService _hostPathService;

    [Inject]
    private readonly FileLockService _fileLockService;

    [Inject]
    private readonly OpencodeReleaseMetadataService _releaseMetadataService;

    [Inject]
    private readonly SessionStagingService _sessionStagingService;

    public async Task<(bool Success, string ExecutablePath)> EnsureLatestAsync(LatestOpencodeRelease release)
    {
        var emptyResult = (false, String.Empty);
        if(!_hostPathService.TryGetPaths(out var paths))
        {
            return emptyResult;
        }

        await using var hostLock = await _fileLockService.AcquireAsync(paths.OpencodeHostLockPath, LogCategories.OPENCODE_HOST, "managed host OpenCode");
        return hostLock is null ? ((bool Success, string ExecutablePath)) emptyResult : await EnsureLatestLockedAsync(paths, release);
    }

    public async Task<(bool Success, ManagedHostOpencodeLease? Lease)> TryAcquireLeaseAsync(string sessionId, LatestOpencodeRelease release)
    {
        if(String.IsNullOrWhiteSpace(sessionId))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.ATTACH, "Runtime session id was not resolved for the managed host OpenCode lease.");
            return (false, null);
        }

        if(!_hostPathService.TryGetPaths(out var paths))
        {
            return (false, null);
        }

        await using var hostLock = await _fileLockService.AcquireAsync(paths.OpencodeHostLockPath, LogCategories.OPENCODE_HOST, "managed host OpenCode");
        if(hostLock is null)
        {
            return (false, null);
        }

        var (success, executablePath) = await EnsureLatestLockedAsync(paths, release);
        if(!success)
        {
            return (false, null);
        }

        string leasePath = Path.Combine(paths.OpencodeLeasesRoot, $"{sessionId}.lease");
        try
        {
            await File.WriteAllTextAsync(leasePath, release.Version);
            _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, $"created managed host OpenCode lease '{leasePath}'", LogLevel.Information);
            return (true, new ManagedHostOpencodeLease(this, leasePath, executablePath));
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Failed to create managed host OpenCode lease '{leasePath}': {ex.Message}");
            return (false, null);
        }
    }

    private async Task<(bool Success, string ExecutablePath)> EnsureLatestLockedAsync(OcwHostPaths paths, LatestOpencodeRelease release)
    {
        if(TryReadMetadata(paths.OpencodeMetadataPath, out var metadata)
            && metadata is not null
            && IsInstalledVersionCurrent(paths, metadata, release.Version, out string executablePath))
        {
            _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, $"reusing managed host OpenCode {metadata.Version}", LogLevel.Information);
            return (true, executablePath);
        }

        var (success, asset) = await _releaseMetadataService.TryResolveCurrentHostBinaryAsync(release);
        if(!success)
        {
            return (false, String.Empty);
        }

        var binaryAsset = asset;
        _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, $"installing managed host OpenCode {release.Version} from '{binaryAsset.Asset.Name}'", LogLevel.Information);

        string temporaryRoot = Path.Combine(paths.OpencodeRoot, $"install-{Guid.NewGuid():N}");
        string archivePath = Path.Combine(temporaryRoot, binaryAsset.Asset.Name);
        string extractedRoot = Path.Combine(temporaryRoot, "extracted");

        try
        {
            Directory.CreateDirectory(temporaryRoot);
            Directory.CreateDirectory(extractedRoot);

            if(!await TryDownloadArtifactAsync(binaryAsset.Asset, archivePath))
            {
                return (false, String.Empty);
            }

            if(!await TryExtractArtifactAsync(archivePath, extractedRoot))
            {
                return (false, String.Empty);
            }

            if(!TryFindExecutable(extractedRoot, binaryAsset.ExecutableFileName, out string extractedExecutablePath))
            {
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Managed OpenCode artifact '{binaryAsset.Asset.Name}' did not contain '{binaryAsset.ExecutableFileName}'.");
                return (false, String.Empty);
            }

            EnsureExecutablePermissions(extractedExecutablePath);
            if(!await ValidateExecutableAsync(extractedExecutablePath))
            {
                return (false, String.Empty);
            }

            await WaitForLeaseDrainAsync(paths);

            string executableRelativePath = Path.GetRelativePath(extractedRoot, extractedExecutablePath);
            if(!await TryReplaceCurrentInstallAsync(paths, extractedRoot, new ManagedHostOpencodeMetadata(
                release.Version,
                binaryAsset.Target,
                binaryAsset.Asset.Name,
                binaryAsset.Asset.DownloadUrl,
                binaryAsset.Asset.Sha256,
                executableRelativePath.Replace('\\', '/'),
                DateTimeOffset.UtcNow)))
            {
                return (false, String.Empty);
            }

            string finalExecutablePath = Path.Combine(paths.OpencodeCurrentRoot, executableRelativePath);
            _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, $"managed host OpenCode {release.Version} installed at '{finalExecutablePath}'", LogLevel.Information);
            return (true, finalExecutablePath);
        }
        finally
        {
            AppIO.TryDeleteDirectory(temporaryRoot);
        }
    }

    private async Task WaitForLeaseDrainAsync(OcwHostPaths paths)
    {
        bool waitingLogged = false;
        while(true)
        {
            _sessionStagingService.CleanupStaleSessions();
            RemoveOrphanedLeaseFiles(paths);

            string[] leaseFiles;
            try
            {
                leaseFiles = Directory.Exists(paths.OpencodeLeasesRoot)
                    ? Directory.GetFiles(paths.OpencodeLeasesRoot, "*.lease", SearchOption.TopDirectoryOnly)
                    : [];
            }
            catch
            {
                leaseFiles = [];
            }

            if(leaseFiles.Length == 0)
            {
                if(waitingLogged)
                {
                    _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, "active managed host OpenCode leases drained", LogLevel.Information);
                }

                return;
            }

            if(!waitingLogged)
            {
                _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, "waiting for active managed host OpenCode leases to drain", LogLevel.Information);
                waitingLogged = true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }

    private void RemoveOrphanedLeaseFiles(OcwHostPaths paths)
    {
        string[] leaseFiles;
        try
        {
            leaseFiles = Directory.Exists(paths.OpencodeLeasesRoot)
                ? Directory.GetFiles(paths.OpencodeLeasesRoot, "*.lease", SearchOption.TopDirectoryOnly)
                : [];
        }
        catch
        {
            return;
        }

        foreach(string leasePath in leaseFiles)
        {
            string sessionId = Path.GetFileNameWithoutExtension(leasePath);
            if(String.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            string sessionDirectory = Path.Combine(paths.SessionsRoot, sessionId);
            if(Directory.Exists(sessionDirectory))
            {
                continue;
            }

            TryDeleteLeaseFile(leasePath);
        }
    }

    private async Task<bool> TryDownloadArtifactAsync(OpencodeReleaseAsset asset, string destinationPath)
    {
        try
        {
            using var response = await _httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync();
            await using(var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(destination);
                await destination.FlushAsync();
            }

            if(!String.IsNullOrWhiteSpace(asset.Sha256) && !FileMatchesSha256(destinationPath, asset.Sha256))
            {
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Checksum validation failed for '{asset.Name}'.");
                return false;
            }

            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Failed to download managed OpenCode artifact '{asset.Name}': {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryExtractArtifactAsync(string archivePath, string destinationDirectory)
    {
        try
        {
            if(archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
                return true;
            }

            if(archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var archiveStream = File.OpenRead(archivePath);
                await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzipStream, destinationDirectory, overwriteFiles: true);
                return true;
            }

            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Unsupported managed OpenCode archive format '{archivePath}'.");
            return false;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Failed to extract managed OpenCode artifact '{archivePath}': {ex.Message}");
            return false;
        }
    }

    private bool TryFindExecutable(string extractedRoot, string executableFileName, out string executablePath)
    {
        executablePath = String.Empty;
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(extractedRoot, executableFileName, SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Failed to scan extracted managed OpenCode files in '{extractedRoot}': {ex.Message}");
            return false;
        }

        executablePath = candidates.FirstOrDefault() ?? String.Empty;
        return !String.IsNullOrWhiteSpace(executablePath);
    }

    private static void EnsureExecutablePermissions(string executablePath)
    {
        if(OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
        }
    }

    private async Task<bool> ValidateExecutableAsync(string executablePath)
    {
        var versionResult = await ProcessRunner.RunAsync(executablePath, ["--version"]);
        if(!versionResult.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, DescribeProcessFailure($"'{executablePath} --version'", versionResult));
            return false;
        }

        var attachHelpResult = await ProcessRunner.RunAsync(executablePath, ["attach", "--help"]);
        if(!attachHelpResult.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, DescribeProcessFailure($"'{executablePath} attach --help'", attachHelpResult));
            return false;
        }

        return true;
    }

    private async Task<bool> TryReplaceCurrentInstallAsync(OcwHostPaths paths, string extractedRoot, ManagedHostOpencodeMetadata metadata)
    {
        string backupRoot = Path.Combine(paths.OpencodeRoot, $"backup-{Guid.NewGuid():N}");
        bool movedCurrentAside = false;

        try
        {
            AppIO.TryDeleteDirectory(backupRoot);
            if(Directory.Exists(paths.OpencodeCurrentRoot))
            {
                Directory.Move(paths.OpencodeCurrentRoot, backupRoot);
                movedCurrentAside = true;
            }

            Directory.Move(extractedRoot, paths.OpencodeCurrentRoot);
            string metadataJson = JsonSerializer.Serialize(metadata, OpencodeJsonContext.Default.ManagedHostOpencodeMetadata);
            await File.WriteAllTextAsync(paths.OpencodeMetadataPath, metadataJson);
            AppIO.TryDeleteDirectory(backupRoot);
            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Failed to replace the managed host OpenCode installation: {ex.Message}");

            try
            {
                if(Directory.Exists(paths.OpencodeCurrentRoot))
                {
                    AppIO.TryDeleteDirectory(paths.OpencodeCurrentRoot);
                }

                if(movedCurrentAside && Directory.Exists(backupRoot))
                {
                    Directory.Move(backupRoot, paths.OpencodeCurrentRoot);
                }
            }
            catch
            {
            }

            return false;
        }
    }

    private bool TryReadMetadata(string metadataPath, out ManagedHostOpencodeMetadata? metadata)
    {
        metadata = null;
        if(!File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(metadataPath);
            metadata = JsonSerializer.Deserialize(json, OpencodeJsonContext.Default.ManagedHostOpencodeMetadata);
            return metadata is not null;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_HOST, $"Ignoring invalid managed host OpenCode metadata '{metadataPath}': {ex.Message}");
            return false;
        }
    }

    private static bool IsInstalledVersionCurrent(OcwHostPaths paths, ManagedHostOpencodeMetadata metadata, string expectedVersion, out string executablePath)
    {
        executablePath = Path.Combine(paths.OpencodeCurrentRoot, metadata.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return String.Equals(metadata.Version, expectedVersion, StringComparison.OrdinalIgnoreCase)
            && File.Exists(executablePath);
    }

    private async ValueTask ReleaseLeaseAsync(string leasePath)
    {
        TryDeleteLeaseFile(leasePath);
        await ValueTask.CompletedTask;
    }

    private void TryDeleteLeaseFile(string leasePath)
    {
        try
        {
            if(File.Exists(leasePath))
            {
                File.Delete(leasePath);
                _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, $"removed managed host OpenCode lease '{leasePath}'", LogLevel.Information);
            }
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteWarningOrConsole(LogCategories.OPENCODE_HOST, $"Failed to remove managed host OpenCode lease '{leasePath}': {ex.Message}");
        }
    }

    private static bool FileMatchesSha256(string filePath, string expectedSha256)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        string actualSha256 = Convert.ToHexString(hash).ToLowerInvariant();
        return String.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeProcessFailure(string commandDescription, ProcessRunner.ProcessRunResult result)
    {
        string detail = FirstNonEmptyLine(result.StdErr, result.StdOut);
        return !result.Started
            ? String.IsNullOrWhiteSpace(detail)
                ? $"{commandDescription} could not start"
                : $"{commandDescription} could not start: {detail}"
            : String.IsNullOrWhiteSpace(detail)
            ? $"{commandDescription} exited with code {result.ExitCode}"
            : $"{commandDescription} exited with code {result.ExitCode}: {detail}";
    }

    private static string FirstNonEmptyLine(params string[] values)
    {
        foreach(string value in values)
        {
            if(String.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string firstLine = value
                .Replace("\r", String.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? String.Empty;
            if(!String.IsNullOrWhiteSpace(firstLine))
            {
                return firstLine;
            }
        }

        return String.Empty;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ocw/managed-host-install");
        return client;
    }

    internal sealed class ManagedHostOpencodeLease : IAsyncDisposable
    {
        private readonly ManagedHostOpencodeService _owner;
        private readonly string _leasePath;
        private bool _disposed;

        internal ManagedHostOpencodeLease(ManagedHostOpencodeService owner, string leasePath, string executablePath)
        {
            _owner = owner;
            _leasePath = leasePath;
            ExecutablePath = executablePath;
        }

        public string ExecutablePath { get; }

        public async ValueTask DisposeAsync()
        {
            if(_disposed)
            {
                return;
            }

            _disposed = true;
            await _owner.ReleaseLeaseAsync(_leasePath);
        }
    }
}
