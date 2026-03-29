using Microsoft.Extensions.Logging;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace OpencodeWrap.Services.Opencode;

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

        string leaseDirectoryPath = GetLeaseVersionRoot(paths, release.Version);
        string leasePath = Path.Combine(leaseDirectoryPath, $"{sessionId}.lease");
        try
        {
            Directory.CreateDirectory(leaseDirectoryPath);
            await File.WriteAllTextAsync(leasePath, executablePath);
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
        _sessionStagingService.CleanupStaleSessions();
        RemoveOrphanedLeaseFiles(paths);

        var (success, asset) = await _releaseMetadataService.TryResolveCurrentHostBinaryAsync(release);
        if(!success)
        {
            return (false, String.Empty);
        }

        var binaryAsset = asset;
        CleanupUnusedInstalledVersions(paths, [release.Version]);

        string versionRoot = GetVersionRoot(paths, release.Version);
        if(TryGetInstalledExecutablePath(versionRoot, binaryAsset.ExecutableFileName, out string installedExecutablePath))
        {
            _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, $"reusing managed host OpenCode {release.Version}", LogLevel.Information);
            return (true, installedExecutablePath);
        }

        if(Directory.Exists(versionRoot) && HasActiveLeaseForVersion(paths, release.Version))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Managed host OpenCode {release.Version} is in use by another active session and cannot be reinstalled in place.");
            return (false, String.Empty);
        }

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

            if(!TryInstallVersion(extractedRoot, versionRoot, release.Version))
            {
                return (false, String.Empty);
            }

            if(!TryGetInstalledExecutablePath(versionRoot, binaryAsset.ExecutableFileName, out string finalExecutablePath))
            {
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Managed OpenCode {release.Version} was installed, but '{binaryAsset.ExecutableFileName}' was not found under '{versionRoot}'.");
                return (false, String.Empty);
            }

            _deferredSessionLogService.Write(LogCategories.OPENCODE_HOST, $"managed host OpenCode {release.Version} installed at '{finalExecutablePath}'", LogLevel.Information);
            return (true, finalExecutablePath);
        }
        finally
        {
            AppIO.TryDeleteDirectory(temporaryRoot);
        }
    }

    private void RemoveOrphanedLeaseFiles(OcwHostPaths paths)
    {
        string[] leaseFiles;
        try
        {
            leaseFiles = Directory.Exists(paths.OpencodeLeasesRoot)
                ? Directory.GetFiles(paths.OpencodeLeasesRoot, "*.lease", SearchOption.AllDirectories)
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

        CleanupEmptyLeaseDirectories(paths.OpencodeLeasesRoot);
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

    private bool TryInstallVersion(string extractedRoot, string versionRoot, string version)
    {
        try
        {
            if(Directory.Exists(versionRoot))
            {
                AppIO.TryDeleteDirectory(versionRoot);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(versionRoot)!);
            Directory.Move(extractedRoot, versionRoot);
            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_HOST, $"Failed to install managed host OpenCode {version}: {ex.Message}");
            return false;
        }
    }

    private void CleanupUnusedInstalledVersions(OcwHostPaths paths, IReadOnlyCollection<string> preserveVersions)
    {
        string[] versionDirectories;
        try
        {
            versionDirectories = Directory.Exists(paths.OpencodeVersionsRoot)
                ? Directory.GetDirectories(paths.OpencodeVersionsRoot, "*", SearchOption.TopDirectoryOnly)
                : [];
        }
        catch
        {
            return;
        }

        var activeVersions = new HashSet<string>(EnumerateActiveLeaseVersions(paths), StringComparer.OrdinalIgnoreCase);
        foreach(string preservedVersion in preserveVersions)
        {
            activeVersions.Add(GetVersionDirectoryName(preservedVersion));
        }

        foreach(string versionDirectory in versionDirectories)
        {
            string versionDirectoryName = Path.GetFileName(versionDirectory);
            if(String.IsNullOrWhiteSpace(versionDirectoryName)
                || activeVersions.Contains(versionDirectoryName))
            {
                continue;
            }

            AppIO.TryDeleteDirectory(versionDirectory);
        }
    }

    private IEnumerable<string> EnumerateActiveLeaseVersions(OcwHostPaths paths)
    {
        string[] leaseDirectories;
        try
        {
            leaseDirectories = Directory.Exists(paths.OpencodeLeasesRoot)
                ? Directory.GetDirectories(paths.OpencodeLeasesRoot, "*", SearchOption.TopDirectoryOnly)
                : [];
        }
        catch
        {
            yield break;
        }

        foreach(string leaseDirectory in leaseDirectories)
        {
            string versionDirectoryName = Path.GetFileName(leaseDirectory);
            if(String.IsNullOrWhiteSpace(versionDirectoryName))
            {
                continue;
            }

            string[] leaseFiles;
            try
            {
                leaseFiles = Directory.GetFiles(leaseDirectory, "*.lease", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            if(leaseFiles.Length > 0)
            {
                yield return versionDirectoryName;
            }
        }
    }

    private void CleanupEmptyLeaseDirectories(string leasesRoot)
    {
        string[] leaseDirectories;
        try
        {
            leaseDirectories = Directory.Exists(leasesRoot)
                ? Directory.GetDirectories(leasesRoot, "*", SearchOption.TopDirectoryOnly)
                : [];
        }
        catch
        {
            return;
        }

        foreach(string leaseDirectory in leaseDirectories)
        {
            try
            {
                if(!Directory.EnumerateFileSystemEntries(leaseDirectory).Any())
                {
                    Directory.Delete(leaseDirectory);
                }
            }
            catch
            {
                // Best effort lease directory cleanup only.
            }
        }
    }

    private async ValueTask ReleaseLeaseAsync(string leasePath)
    {
        TryDeleteLeaseFile(leasePath);
        string? leaseDirectory = Path.GetDirectoryName(leasePath);
        if(!String.IsNullOrWhiteSpace(leaseDirectory))
        {
            TryDeleteDirectoryIfEmpty(leaseDirectory);
        }

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

    private static string GetVersionRoot(OcwHostPaths paths, string version)
        => Path.Combine(paths.OpencodeVersionsRoot, GetVersionDirectoryName(version));

    private static string GetLeaseVersionRoot(OcwHostPaths paths, string version)
        => Path.Combine(paths.OpencodeLeasesRoot, GetVersionDirectoryName(version));

    private static string GetVersionDirectoryName(string version)
    {
        string normalizedVersion = String.IsNullOrWhiteSpace(version)
            ? "unknown"
            : version.Trim();

        foreach(char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            normalizedVersion = normalizedVersion.Replace(invalidCharacter, '-');
        }

        return normalizedVersion;
    }

    private bool HasActiveLeaseForVersion(OcwHostPaths paths, string version)
    {
        string leaseVersionRoot = GetLeaseVersionRoot(paths, version);
        if(!Directory.Exists(leaseVersionRoot))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(leaseVersionRoot, "*.lease", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetInstalledExecutablePath(string versionRoot, string executableFileName, out string executablePath)
    {
        if(!Directory.Exists(versionRoot))
        {
            executablePath = String.Empty;
            return false;
        }

        return TryFindExecutable(versionRoot, executableFileName, out executablePath);
    }

    private void TryDeleteDirectoryIfEmpty(string directoryPath)
    {
        try
        {
            if(Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath);
            }
        }
        catch
        {
            // Best effort lease directory cleanup only.
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
