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

        var (platformDetected, isMusl, needsBaseline) = await TryDetectRuntimePlatformAsync(baseImageTag, image.Architecture);
        if(!platformDetected)
        {
            return (false, unavailableTag);
        }

        var (assetResolved, asset) = _releaseMetadataService.TryResolveLinuxRuntimeBinary(release, image.Architecture, isMusl, needsBaseline);
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
        return await TryBuildRuntimeImageAsync(baseImageTag, imageTag, release, asset);
    }

    private async Task<(bool Success, string ImageTag)> TryBuildRuntimeImageAsync(
        string baseImageTag,
        string imageTag,
        ResolvedOpencodeRelease release,
        ResolvedOpencodeBinaryAsset binaryAsset)
    {
        string buildContextDirectory = Path.Combine(Path.GetTempPath(), $"ocw-runtime-image-{Guid.NewGuid():N}");
        string dockerfilePath = Path.Combine(buildContextDirectory, "Dockerfile");
        string archivePath = Path.Combine(buildContextDirectory, "opencode2.tgz");

        try
        {
            Directory.CreateDirectory(buildContextDirectory);
            if(!await _packageArtifactService.TryDownloadVerifiedAsync(binaryAsset.Asset, archivePath, LogCategories.OPENCODE_RUNTIME))
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
            AppIO.TryDeleteDirectory(buildContextDirectory);
        }
    }

    internal static string BuildRuntimeDockerfile()
        => """
        ARG BASE_IMAGE=ubuntu:24.04
        FROM ubuntu:24.04 AS opencode-install
        RUN apt-get update \
            && apt-get install -y --no-install-recommends tar \
            && rm -rf /var/lib/apt/lists/*
        COPY opencode2.tgz /tmp/opencode2.tgz
        RUN mkdir -p /opt/opencode/bin \
            && tar -xzf /tmp/opencode2.tgz -C /tmp package/bin/opencode2 \
            && install -m 0755 /tmp/package/bin/opencode2 /opt/opencode/bin/opencode2 \
            && rm -rf /tmp/opencode2.tgz /tmp/package

        FROM ${BASE_IMAGE}
        ARG OPENCODE_VERSION
        ENV OPENCODE_DISABLE_AUTOUPDATE=1
        LABEL org.opencontainers.image.title="ocw-opencode2-runtime"
        LABEL org.opencontainers.image.version="$OPENCODE_VERSION"
        COPY --from=opencode-install /opt/opencode /opt/opencode
        RUN actual="$(/opt/opencode/bin/opencode2 --version)" \
            && test "$actual" = "opencode2 v${OPENCODE_VERSION}"
        """;

    private async Task<(bool Success, bool IsMusl, bool NeedsBaseline)> TryDetectRuntimePlatformAsync(string imageTag, string architecture)
    {
        string? normalizedArchitecture = OpencodeReleaseMetadataService.NormalizeArchitecture(architecture);
        if(normalizedArchitecture is null)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Unsupported Docker image architecture '{architecture}'.");
            return (false, false, false);
        }

        const string platformProbe = "if [ -f /etc/alpine-release ] || (command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl); then printf musl; else printf glibc; fi; printf '|'; if grep -qw avx2 /proc/cpuinfo 2>/dev/null; then printf avx2; else printf baseline; fi";
        var probeResult = await ProcessRunner.RunAsync(
            "docker",
            ["run", "--rm", "--entrypoint", "bash", imageTag, "-lc", platformProbe]);
        if(!probeResult.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Failed to detect libc and CPU support in base profile image '{imageTag}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole(LogCategories.OPENCODE_RUNTIME, probeResult.StdErr);
            return (false, false, false);
        }

        string[] parts = probeResult.StdOut.Trim().Split('|', StringSplitOptions.TrimEntries);
        if(parts.Length != 2 || parts[0] is not ("glibc" or "musl") || parts[1] is not ("avx2" or "baseline"))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Base profile image '{imageTag}' returned an invalid runtime platform probe result.");
            return (false, false, false);
        }

        bool isMusl = parts[0] == "musl";
        bool needsBaseline = normalizedArchitecture == "x64" && parts[1] != "avx2";
        return (true, isMusl, needsBaseline);
    }

    private static string ComputeSha256(string value)
    {
        byte[] input = Encoding.UTF8.GetBytes(value);
        byte[] hashBytes = SHA256.HashData(input);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
