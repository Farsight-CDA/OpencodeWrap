using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace OpencodeWrap.Services.Opencode;

internal sealed partial class OpencodePackageArtifactService : Singleton
{
    private static readonly HttpClient _httpClient = CreateHttpClient();

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<bool> TryDownloadVerifiedAsync(
        OpencodeNpmPackageAsset asset,
        string destinationPath,
        string logCategory)
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

            if(!FileMatchesIntegrity(destinationPath, asset.Integrity))
            {
                _deferredSessionLogService.WriteErrorOrConsole(logCategory, $"Integrity validation failed for {asset.PackageName}@{asset.Version}.");
                return false;
            }

            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(logCategory, $"Failed to download {asset.PackageName}@{asset.Version}: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryExtractExecutableAsync(
        string archivePath,
        string executableFileName,
        string destinationPath,
        string logCategory)
    {
        try
        {
            await ExtractNpmExecutableAsync(archivePath, executableFileName, destinationPath);
            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(logCategory, $"Failed to extract '{executableFileName}' from OpenCode V2 package '{archivePath}': {ex.Message}");
            return false;
        }
    }

    internal static async Task ExtractNpmExecutableAsync(
        string archivePath,
        string executableFileName,
        string destinationPath)
    {
        string expectedEntryName = $"package/bin/{executableFileName}";
        await using var archiveStream = File.OpenRead(archivePath);
        await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        TarEntry? entry;
        while((entry = tarReader.GetNextEntry()) is not null)
        {
            string entryName = entry.Name.Replace('\\', '/').TrimStart('/');
            if(!String.Equals(entryName, expectedEntryName, StringComparison.Ordinal))
            {
                continue;
            }

            if(entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink
                || entry.DataStream is null)
            {
                throw new InvalidDataException($"Archive entry '{expectedEntryName}' is not a regular file.");
            }

            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if(!String.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await entry.DataStream.CopyToAsync(destination);
            await destination.FlushAsync();
            return;
        }

        throw new InvalidDataException($"Archive did not contain '{expectedEntryName}'.");
    }

    internal static bool FileMatchesIntegrity(string filePath, string integrity)
    {
        if(!TryGetSha512Digest(integrity, out var expectedDigest))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        byte[] actualDigest = SHA512.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest);
    }

    internal static bool TryGetSha512Digest(string? integrity, out byte[] digest)
    {
        digest = [];
        foreach(string token in (integrity ?? String.Empty).Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string prefix = "sha512-";
            if(!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                byte[] candidate = Convert.FromBase64String(token[prefix.Length..]);
                if(candidate.Length == SHA512.HashSizeInBytes)
                {
                    digest = candidate;
                    return true;
                }
            }
            catch(FormatException)
            {
            }
        }

        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ocw/opencode2-artifact-install");
        return client;
    }
}
