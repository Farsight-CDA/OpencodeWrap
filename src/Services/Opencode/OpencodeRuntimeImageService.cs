using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace OpencodeWrap.Services.Opencode;

internal sealed partial class OpencodeRuntimeImageService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly DockerImageService _dockerImageService;

    [Inject]
    private readonly FileLockService _fileLockService;

    [Inject]
    private readonly OcwHostPathService _hostPathService;

    [Inject]
    private readonly OpencodeReleaseMetadataService _releaseMetadataService;

    [Inject]
    private readonly OpencodePackageArtifactService _packageArtifactService;

    public async Task<(bool Success, string ImageTag)> TryEnsureRuntimeImageAsync(string baseImageTag, ResolvedOpencodeRelease release)
    {
        string unavailableTag = "opencode-wrap-runtime:unavailable";
        if(String.IsNullOrWhiteSpace(baseImageTag))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, "Base profile image tag was not resolved.");
            return (false, unavailableTag);
        }

        if(!_hostPathService.TryGetPaths(out var paths))
        {
            return (false, unavailableTag);
        }

        var (success, image) = await _dockerImageService.TryInspectImageAsync(baseImageTag);
        if(!success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Failed to inspect base profile image '{baseImageTag}'.");
            return (false, unavailableTag);
        }

        if(!String.Equals(image.Os, "linux", StringComparison.OrdinalIgnoreCase))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Unsupported base image OS '{image.Os}'. Only Linux profile images can host OpenCode runtime images.");
            return (false, unavailableTag);
        }

        var (libcDetected, isMusl) = await TryDetectLibcAsync(image.Id, image.Architecture);
        if(!libcDetected)
        {
            return (false, unavailableTag);
        }

        var (assetResolved, asset) = _releaseMetadataService.TryResolveLinuxRuntimeBinary(release, image.Architecture, isMusl);
        if(!assetResolved)
        {
            return (false, unavailableTag);
        }

        string runtimeKey = $"{image.Id}|{release.Version}|{asset.Asset.PackageName}|{asset.Asset.Integrity}";
        string runtimeHash = ComputeSha256(runtimeKey)[..12];
        string imageTag = $"opencode-wrap-runtime:{runtimeHash}";
        string runtimeLockPath = Path.Combine(paths.LocksRoot, $"opencode-runtime-{runtimeHash}.lock");

        await using var runtimeLock = await _fileLockService.AcquireAsync(runtimeLockPath, LogCategories.OPENCODE_RUNTIME, $"OpenCode runtime image '{imageTag}'");
        if(runtimeLock is null)
        {
            return (false, unavailableTag);
        }

        if(await _dockerImageService.ImageExistsAsync(imageTag))
        {
            _deferredSessionLogService.Write(LogCategories.OPENCODE_RUNTIME, $"reusing runtime image '{imageTag}' for OpenCode {release.Version}", LogLevel.Information);
            return (true, imageTag);
        }

        _deferredSessionLogService.Write(LogCategories.OPENCODE_RUNTIME, $"building runtime image '{imageTag}' for OpenCode {release.Version}", LogLevel.Information);
        return await TryBuildRuntimeImageAsync(image.Id, imageTag, release, asset);
    }

    private async Task<(bool Success, string ImageTag)> TryBuildRuntimeImageAsync(
        string baseImageTag,
        string imageTag,
        ResolvedOpencodeRelease release,
        ResolvedOpencodeBinaryAsset binaryAsset)
    {
        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"ocw-runtime-image-{Guid.NewGuid():N}");
        string buildContextDirectory = Path.Combine(temporaryRoot, "context");
        string dockerfilePath = Path.Combine(buildContextDirectory, "Dockerfile");
        string executablePath = Path.Combine(buildContextDirectory, "opencode2");
        string archivePath = Path.Combine(temporaryRoot, "opencode2.tgz");

        try
        {
            Directory.CreateDirectory(buildContextDirectory);
            if(!await _packageArtifactService.TryDownloadVerifiedAsync(binaryAsset.Asset, archivePath, LogCategories.OPENCODE_RUNTIME))
            {
                return (false, imageTag);
            }

            if(!await _packageArtifactService.TryExtractExecutableAsync(
                archivePath,
                binaryAsset.ExecutableFileName,
                executablePath,
                LogCategories.OPENCODE_RUNTIME))
            {
                return (false, imageTag);
            }

            await File.WriteAllTextAsync(dockerfilePath, BuildRuntimeDockerfile());

            var buildArgs = new List<string>
            {
                "build",
                "-f", dockerfilePath,
                "-t", imageTag,
                "--build-arg", $"BASE_IMAGE={baseImageTag}",
                "--build-arg", $"OPENCODE_VERSION={release.Version}",
                "."
            };

            var buildResult = await ProcessRunner.RunAsync("docker", buildArgs, captureOutput: false, workDir: buildContextDirectory);
            if(!buildResult.Success)
            {
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Failed to build runtime image '{imageTag}'.");
                return (false, imageTag);
            }

            return (true, imageTag);
        }
        finally
        {
            AppIO.TryDeleteDirectory(temporaryRoot);
        }
    }

    internal static string BuildRuntimeDockerfile()
        => """
        # check=skip=InvalidDefaultArgInFrom
        ARG BASE_IMAGE
        FROM ${BASE_IMAGE}
        ARG OPENCODE_VERSION
        ENV OPENCODE_DISABLE_AUTOUPDATE=1
        LABEL org.opencontainers.image.title="ocw-opencode2-runtime"
        LABEL org.opencontainers.image.version="$OPENCODE_VERSION"
        COPY --chmod=0755 opencode2 /opt/opencode/bin/opencode2
        RUN actual="$(/opt/opencode/bin/opencode2 --version)" \
            && test "$actual" = "opencode2 v${OPENCODE_VERSION}"
        """;

    private async Task<(bool Success, bool IsMusl)> TryDetectLibcAsync(string imageId, string architecture)
    {
        if(OpencodeReleaseMetadataService.NormalizeArchitecture(architecture) is null)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Unsupported Docker image architecture '{architecture}'.");
            return (false, false);
        }

        const string LIBC_PROBE = "if [ -f /etc/alpine-release ] || (command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl); then printf musl; else printf glibc; fi";
        var probeResult = await ProcessRunner.RunAsync(
            "docker",
            ["run", "--rm", "--entrypoint", "bash", imageId, "-lc", LIBC_PROBE]);
        if(!probeResult.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Failed to detect libc in base profile image '{imageId}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole(LogCategories.OPENCODE_RUNTIME, probeResult.StdErr);
            return (false, false);
        }

        string libc = probeResult.StdOut.Trim();
        if(libc is not ("glibc" or "musl"))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Base profile image '{imageId}' returned an invalid libc probe result.");
            return (false, false);
        }

        return (true, libc == "musl");
    }

    private static string ComputeSha256(string value)
    {
        byte[] input = Encoding.UTF8.GetBytes(value);
        byte[] hashBytes = SHA256.HashData(input);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
