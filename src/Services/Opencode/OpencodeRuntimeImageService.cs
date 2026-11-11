using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace OpencodeWrap.Services.Opencode;

internal sealed partial class OpencodeRuntimeImageService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly FileLockService _fileLockService;

    [Inject]
    private readonly OcwHostPathService _hostPathService;

    public async Task<DockerResult<string>> TryEnsureRuntimeImageAsync(ResolvedDockerImage baseImage, ResolvedOpencodeRelease release)
    {
        if(!_hostPathService.TryGetPaths(out var paths))
        {
            return new DockerFailure("Failed to resolve host paths for the OpenCode runtime image.");
        }

        var image = baseImage.Info;
        if(!String.Equals(image.Os, "linux", StringComparison.OrdinalIgnoreCase))
        {
            string message = $"Unsupported base image OS '{image.Os}'. Only Linux profile images can host OpenCode runtime images.";
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, message);
            return new DockerFailure(message);
        }

        string runtimeKey = $"{image.Id}|{release.PackageName}|{release.Version}|{BuildRuntimeDockerfile()}";
        string runtimeHash = ComputeSha256(runtimeKey)[..12];
        string imageTag = $"opencode-wrap-runtime:{runtimeHash}";
        string runtimeLockPath = Path.Combine(paths.LocksRoot, $"opencode-runtime-{runtimeHash}.lock");

        await using var runtimeLock = await _fileLockService.AcquireAsync(runtimeLockPath, LogCategories.OPENCODE_RUNTIME, $"OpenCode runtime image '{imageTag}'");
        if(runtimeLock is null)
        {
            return new DockerFailure($"Failed to acquire the runtime image lock for '{imageTag}'.");
        }

        var inspect = await DockerCommand.InspectAsync("image", imageTag);
        if(inspect is DockerValue<DockerOutput>)
        {
            _deferredSessionLogService.Write(LogCategories.OPENCODE_RUNTIME, $"reusing runtime image '{imageTag}' for OpenCode {release.Version}", LogLevel.Information);
            return new DockerValue<string>(imageTag);
        }

        if(inspect is not DockerNotFound)
        {
            var error = inspect.Require();
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Failed to inspect runtime image '{imageTag}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole(LogCategories.OPENCODE_RUNTIME, error.ErrorDetails);
            return error.Select(_ => imageTag);
        }

        _deferredSessionLogService.Write(LogCategories.OPENCODE_RUNTIME, $"building runtime image '{imageTag}' for OpenCode {release.Version}", LogLevel.Information);
        return await TryBuildRuntimeImageAsync(baseImage.Tag, imageTag, release);
    }

    private async Task<DockerResult<string>> TryBuildRuntimeImageAsync(
        string baseImageRef,
        string imageTag,
        ResolvedOpencodeRelease release
    )
    {
        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"ocw-runtime-image-{Guid.NewGuid():N}");
        string buildContextDirectory = Path.Combine(temporaryRoot, "context");
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
                "--build-arg", $"BASE_IMAGE={baseImageRef}",
                "--build-arg", $"OPENCODE_PACKAGE={release.PackageName}",
                "--build-arg", $"OPENCODE_VERSION={release.Version}",
                "."
            };

            var buildResult = await DockerCommand.RunAsync(buildArgs, captureOutput: false, workDir: buildContextDirectory);
            if(buildResult is not DockerValue<DockerOutput>)
            {
                _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OPENCODE_RUNTIME, $"Failed to build runtime image '{imageTag}'.");
                _deferredSessionLogService.WriteErrorDetailsOrConsole(LogCategories.OPENCODE_RUNTIME, buildResult.ErrorDetails);
            }

            return buildResult.Select(_ => imageTag);
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
        FROM ${BASE_IMAGE} AS opencode-install
        ARG OPENCODE_PACKAGE
        ARG OPENCODE_VERSION
        ENV OPENCODE_DISABLE_AUTOUPDATE=1 npm_config_cache=/tmp/ocw-opencode/cache
        # Invoke the upstream installer explicitly across npm lifecycle-policy versions.
        RUN npm install --prefix /tmp/ocw-opencode \
                --omit=optional --ignore-scripts --no-audit --no-fund \
                "${OPENCODE_PACKAGE}@${OPENCODE_VERSION}" \
            && npm --prefix "/tmp/ocw-opencode/node_modules/$OPENCODE_PACKAGE" run postinstall \
            && cp /tmp/ocw-opencode/node_modules/.bin/opencode2 /tmp/ocw-opencode2
        FROM ${BASE_IMAGE}
        ARG OPENCODE_VERSION
        ENV OPENCODE_DISABLE_AUTOUPDATE=1
        LABEL org.opencontainers.image.title="ocw-opencode2-runtime"
        LABEL org.opencontainers.image.version="$OPENCODE_VERSION"
        COPY --from=opencode-install --chmod=0755 /tmp/ocw-opencode2 /opt/opencode/bin/opencode2
        RUN test "$(/opt/opencode/bin/opencode2 --version)" = "opencode2 v${OPENCODE_VERSION}"
        """;

    private static string ComputeSha256(string value)
    {
        byte[] input = Encoding.UTF8.GetBytes(value);
        byte[] hashBytes = SHA256.HashData(input);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
