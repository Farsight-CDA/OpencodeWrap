using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace OpencodeWrap.Services.Docker;

internal sealed partial class SessionAddonImageService : Singleton
{
    private const string ADDON_DOCKERFILE = $$"""
        ARG BASE_IMAGE
        FROM ${BASE_IMAGE}
        RUN --mount=type=bind,source=addon,target=/tmp/ocw-addon,rw \
            cd /tmp/ocw-addon && bash ./{{OpencodeWrapConstants.ADDON_INSTALL_FILE_NAME}}

        """;

    [Inject] private readonly DeferredSessionLogService _deferredSessionLogService;
    [Inject] private readonly DockerImageService _dockerImageService;
    [Inject] private readonly FileLockService _fileLockService;
    [Inject] private readonly OcwHostPathService _hostPathService;

    public async Task<DockerResult<string>> TryEnsureImageAsync(string runtimeImageTag, IReadOnlyList<ResolvedSessionAddon> addons)
    {
        var buildAddons = addons
            .Where(static addon => ResolvedSessionAddon.HasBuildStepAt(addon.DirectoryPath))
            .OrderBy(static addon => addon.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if(buildAddons.Length == 0)
        {
            return new DockerValue<string>(runtimeImageTag);
        }

        if(!_hostPathService.TryGetPaths(out var paths))
        {
            return new DockerFailure("Failed to resolve host paths for session addon images.");
        }

        var runtimeInspection = await _dockerImageService.TryInspectImageAsync(runtimeImageTag);
        if(runtimeInspection is not DockerValue<DockerImageInfo> runtimeInfo)
        {
            return runtimeInspection.Require().Select(_ => runtimeImageTag);
        }

        var parent = new ResolvedDockerImage(runtimeImageTag, runtimeInfo.Value);
        foreach(var addon in buildAddons)
        {
            var result = await TryEnsureAddonLayerAsync(parent, addon, paths.LocksRoot);
            if(result is not DockerValue<ResolvedDockerImage> layer)
            {
                return result.Select(static image => image.Tag);
            }

            parent = layer.Value;
        }

        return new DockerValue<string>(parent.Tag);
    }

    private async Task<DockerResult<ResolvedDockerImage>> TryEnsureAddonLayerAsync(ResolvedDockerImage parent, ResolvedSessionAddon addon, string locksRoot)
    {
        string? contextDirectoryPath = null;
        try
        {
            contextDirectoryPath = Directory.CreateTempSubdirectory("ocw-addon-").FullName;
            string hash = await StageBuildContextAsync(parent.Info.Id, addon.BuildDirectoryPath, contextDirectoryPath);
            string imageTag = $"opencode-wrap-addon:{hash[..24]}";
            string lockPath = Path.Combine(locksRoot, $"opencode-addon-{hash[..24]}.lock");
            await using var imageLock = await _fileLockService.AcquireAsync(lockPath, LogCategories.DOCKER, $"addon image '{imageTag}'");
            if(imageLock is null)
            {
                return new DockerFailure($"Failed to acquire image lock for addon '{addon.Name}'.");
            }

            var existing = await _dockerImageService.TryInspectImageAsync(imageTag);
            if(existing is DockerValue<DockerImageInfo> info)
            {
                _deferredSessionLogService.Write(LogCategories.DOCKER, $"Reusing addon image '{imageTag}' for '{addon.Name}'.", LogLevel.Debug);
                return new DockerValue<ResolvedDockerImage>(new ResolvedDockerImage(imageTag, info.Value));
            }

            if(existing is not DockerNotFound)
            {
                return existing.Require().Select(value => new ResolvedDockerImage(imageTag, value));
            }

            _deferredSessionLogService.Write(LogCategories.DOCKER, $"Building addon image '{imageTag}' for '{addon.Name}'.", LogLevel.Information);
            var build = await DockerCommand.RunAsync(
                ["build", "-t", imageTag, "--build-arg", $"BASE_IMAGE={parent.Tag}", "."],
                captureOutput: false,
                workDir: contextDirectoryPath);
            if(build is not DockerValue<DockerOutput>)
            {
                return build.Select(_ => parent);
            }

            var built = await _dockerImageService.TryInspectImageAsync(imageTag);
            return built.Require().Select(value => new ResolvedDockerImage(imageTag, value));
        }
        catch(IOException ex)
        {
            return new DockerFailure($"Failed to prepare build context for addon '{addon.Name}': {ex.Message}");
        }
        catch(UnauthorizedAccessException ex)
        {
            return new DockerFailure($"Failed to prepare build context for addon '{addon.Name}': {ex.Message}");
        }
        finally
        {
            if(contextDirectoryPath is not null)
            {
                AppIO.TryDeleteDirectory(contextDirectoryPath);
            }
        }
    }

    internal static async Task<string> StageBuildContextAsync(string parentImageId, string buildDirectoryPath, string contextDirectoryPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashString(hash, parentImageId);
        AppendHashString(hash, ADDON_DOCKERFILE);

        // Hash the same snapshot Docker sees, not a mutable addon directory.
        string stagedBuildDirectoryPath = Path.Combine(contextDirectoryPath, "addon");
        await StageDirectoryAsync(new DirectoryInfo(buildDirectoryPath), stagedBuildDirectoryPath, "");
        if(!File.Exists(Path.Combine(stagedBuildDirectoryPath, OpencodeWrapConstants.ADDON_INSTALL_FILE_NAME)))
        {
            throw new IOException($"Missing '{OpencodeWrapConstants.ADDON_INSTALL_FILE_NAME}' in '{buildDirectoryPath}'.");
        }

        await File.WriteAllTextAsync(Path.Combine(contextDirectoryPath, "Dockerfile"), ADDON_DOCKERFILE);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        async Task StageDirectoryAsync(DirectoryInfo source, string destinationPath, string relativePath)
        {
            RejectLink(source);
            Directory.CreateDirectory(destinationPath);
            foreach(var entry in source.EnumerateFileSystemInfos().OrderBy(static entry => entry.Name, StringComparer.Ordinal))
            {
                RejectLink(entry);
                string childPath = relativePath + entry.Name;
                string stagedPath = Path.Combine(destinationPath, entry.Name);
                if(entry is DirectoryInfo childDirectory)
                {
                    AppendHashString(hash, childPath + "/");
                    await StageDirectoryAsync(childDirectory, stagedPath, childPath + "/");
                }
                else
                {
                    AppendHashString(hash, childPath);
                    File.Copy(entry.FullName, stagedPath);
                    await using var stagedFile = File.OpenRead(stagedPath);
                    hash.AppendData(await SHA256.HashDataAsync(stagedFile));
                }
            }
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Symbolic links and junctions are not allowed in addon build contexts: '{entry.FullName}'.");
        }
    }

    private static void AppendHashString(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }
}
