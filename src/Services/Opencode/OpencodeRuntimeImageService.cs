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

    public async Task<(bool Success, string ImageTag)> TryEnsureRuntimeImageAsync(string baseImageTag, LatestOpencodeRelease release)
    {
        string unavailableTag = "opencode-wrap-runtime:unavailable";
        if(String.IsNullOrWhiteSpace(baseImageTag))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OpencodeRuntime, "Base profile image tag was not resolved.");
            return (false, unavailableTag);
        }

        if(!_hostPathService.TryGetPaths(out OcwHostPaths paths))
        {
            return (false, unavailableTag);
        }

        var inspectBaseImage = await _dockerImageService.TryInspectImageAsync(baseImageTag);
        if(!inspectBaseImage.Success)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OpencodeRuntime, $"Failed to inspect base profile image '{baseImageTag}'.");
            return (false, unavailableTag);
        }

        if(!String.Equals(inspectBaseImage.Image.Os, "linux", StringComparison.OrdinalIgnoreCase))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OpencodeRuntime, $"Unsupported base image OS '{inspectBaseImage.Image.Os}'. Only Linux profile images can host OpenCode runtime images.");
            return (false, unavailableTag);
        }

        var resolvedBinary = _releaseMetadataService.TryResolveLinuxRuntimeBinary(release, inspectBaseImage.Image.Architecture);
        if(!resolvedBinary.Success)
        {
            return (false, unavailableTag);
        }

        string runtimeKey = $"{inspectBaseImage.Image.Id}|{release.Version}|{resolvedBinary.Asset.Asset.Name}";
        string runtimeHash = ComputeSha256(runtimeKey)[..12];
        string imageTag = $"opencode-wrap-runtime:{runtimeHash}";
        string runtimeLockPath = Path.Combine(paths.LocksRoot, $"opencode-runtime-{runtimeHash}.lock");

        await using var runtimeLock = await _fileLockService.AcquireAsync(runtimeLockPath, LogCategories.OpencodeRuntime, $"OpenCode runtime image '{imageTag}'");
        if(runtimeLock is null)
        {
            return (false, unavailableTag);
        }

        if(await _dockerImageService.ImageExistsAsync(imageTag))
        {
            _deferredSessionLogService.Write(LogCategories.OpencodeRuntime, $"reusing runtime image '{imageTag}' for OpenCode {release.Version}", LogLevel.Information);
            return (true, imageTag);
        }

        _deferredSessionLogService.Write(LogCategories.OpencodeRuntime, $"building runtime image '{imageTag}' for OpenCode {release.Version}", LogLevel.Information);
        return await TryBuildRuntimeImageAsync(baseImageTag, imageTag, release, resolvedBinary.Asset);
    }

    private async Task<(bool Success, string ImageTag)> TryBuildRuntimeImageAsync(
        string baseImageTag,
        string imageTag,
        LatestOpencodeRelease release,
        ResolvedOpencodeBinaryAsset binaryAsset)
    {
        string buildContextDirectory = Path.Combine(Path.GetTempPath(), $"ocw-runtime-image-{Guid.NewGuid():N}");
        string dockerfilePath = Path.Combine(buildContextDirectory, "Dockerfile");

        try
        {
            Directory.CreateDirectory(buildContextDirectory);
            await File.WriteAllTextAsync(dockerfilePath, BuildRuntimeDockerfile());

            var buildArgs = new List<string>
            {
                "build",
                "-f", dockerfilePath,
                "-t", imageTag,
                "--build-arg", $"BASE_IMAGE={baseImageTag}",
                "--build-arg", $"OPENCODE_URL={binaryAsset.Asset.DownloadUrl}",
                "--build-arg", $"OPENCODE_SHA256={binaryAsset.Asset.Sha256 ?? String.Empty}",
                "--build-arg", $"OPENCODE_VERSION={release.Version}",
                "."
            };

            var buildResult = await ProcessRunner.RunAsync("docker", buildArgs, captureOutput: false, workDir: buildContextDirectory);
            if(!buildResult.Success)
            {
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OpencodeRuntime, $"Failed to build runtime image '{imageTag}'.");
                return (false, imageTag);
            }

            return (true, imageTag);
        }
        finally
        {
            AppIO.TryDeleteDirectory(buildContextDirectory);
        }
    }

    private static string BuildRuntimeDockerfile()
        => """
        ARG BASE_IMAGE=ubuntu:24.04
        FROM ubuntu:24.04 AS opencode-install
        ARG OPENCODE_URL
        ARG OPENCODE_SHA256
        RUN apt-get update \
            && apt-get install -y --no-install-recommends ca-certificates coreutils curl tar \
            && rm -rf /var/lib/apt/lists/*
        RUN rm -rf /opt/opencode \
            && mkdir -p /opt/opencode/bin \
            && curl -fsSL "$OPENCODE_URL" -o /tmp/opencode.tar.gz \
            && if [ -n "$OPENCODE_SHA256" ]; then printf '%s  %s\n' "$OPENCODE_SHA256" /tmp/opencode.tar.gz | sha256sum -c -; fi \
            && tar -xzf /tmp/opencode.tar.gz -C /opt/opencode/bin \
            && chmod 755 /opt/opencode/bin/opencode \
            && rm -f /tmp/opencode.tar.gz

        FROM ${BASE_IMAGE}
        ARG OPENCODE_VERSION
        LABEL org.opencontainers.image.title="ocw-opencode-runtime"
        LABEL org.opencontainers.image.version="$OPENCODE_VERSION"
        COPY --from=opencode-install /opt/opencode /opt/opencode
        """;

    private static string ComputeSha256(string value)
    {
        byte[] input = Encoding.UTF8.GetBytes(value);
        byte[] hashBytes = SHA256.HashData(input);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
