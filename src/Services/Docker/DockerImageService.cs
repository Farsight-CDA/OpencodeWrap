using System.Security.Cryptography;
using System.Text;

namespace OpencodeWrap.Services.Docker;

internal sealed record DockerImageInfo(string Id, string Os, string Architecture);
internal sealed record ResolvedDockerImage(string Tag, DockerImageInfo Info);

internal sealed partial class DockerImageService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly SessionOutputService _sessionOutputService;

    public async Task<DockerResult<ResolvedDockerImage>> TryEnsureImageAsync(string dockerfilePath)
    {
        if(!File.Exists(dockerfilePath))
        {
            string message = $"Dockerfile not found: '{dockerfilePath}'.";
            _deferredSessionLogService.WriteErrorOrConsole("docker", message);
            return new DockerFailure(message);
        }

        string imageTag = await BuildImageTagAsync(dockerfilePath);

        var inspect = await TryInspectImageAsync(imageTag);
        if(inspect is DockerValue<DockerImageInfo> image)
        {
            return new DockerValue<ResolvedDockerImage>(new ResolvedDockerImage(imageTag, image.Value));
        }

        if(inspect is not DockerNotFound)
        {
            var error = inspect.Require();
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to inspect Docker image '{imageTag}'.");
            _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", error.ErrorDetails);
            return error.Select(info => new ResolvedDockerImage(imageTag, info));
        }

        _sessionOutputService.WriteInfo(LogCategories.DOCKER, $"Docker image '{imageTag}' not found. Building it now...");
        var build = await TryBuildImageAsync(dockerfilePath, imageTag, noCache: false, _deferredSessionLogService);
        return build switch
        {
            DockerValue<string> => await InspectBuiltImageAsync(),
            DockerUnavailable unavailable => unavailable,
            DockerFailure failure => failure
        };

        async Task<DockerResult<ResolvedDockerImage>> InspectBuiltImageAsync()
        {
            var builtImage = (await TryInspectImageAsync(imageTag)).Require();
            if(builtImage is not DockerValue<DockerImageInfo>)
            {
                _deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to inspect built Docker image '{imageTag}'.");
                _deferredSessionLogService.WriteErrorDetailsOrConsole("docker", builtImage.ErrorDetails);
            }

            return builtImage.Select(info => new ResolvedDockerImage(imageTag, info));
        }
    }

    public async Task<DockerResult<string>> TryBuildImageAsync(string dockerfilePath, bool noCache)
    {
        if(!File.Exists(dockerfilePath))
        {
            string message = $"Dockerfile not found: '{dockerfilePath}'.";
            _deferredSessionLogService.WriteErrorOrConsole("docker", message);
            return new DockerFailure(message);
        }

        string imageTag = await BuildImageTagAsync(dockerfilePath);
        return await TryBuildImageAsync(dockerfilePath, imageTag, noCache, _deferredSessionLogService);
    }

    private static async Task<DockerResult<string>> TryBuildImageAsync(string dockerfilePath, string imageTag, bool noCache, DeferredSessionLogService deferredSessionLogService)
    {
        string buildContextDirectory = Path.GetDirectoryName(dockerfilePath) ?? AppContext.BaseDirectory;
        var buildArgs = new List<string>
        {
            "build"
        };

        if(noCache)
        {
            buildArgs.Add("--no-cache");
        }

        buildArgs.AddRange(
        [
            "-f", dockerfilePath,
            "-t", imageTag,
            "."
        ]);

        var build = await DockerCommand.RunAsync(buildArgs, captureOutput: false, workDir: buildContextDirectory);
        if(build is not DockerValue<DockerOutput>)
        {
            deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to build Docker image '{imageTag}'.");
            deferredSessionLogService.WriteErrorDetailsOrConsole("docker", build.ErrorDetails);
        }

        return build.Select(_ => imageTag);
    }

    public async Task<DockerInspection<DockerImageInfo>> TryInspectImageAsync(string imageTag)
    {
        if(String.IsNullOrWhiteSpace(imageTag))
        {
            return new DockerFailure("A Docker image tag is required.");
        }

        var inspect = await DockerCommand.InspectAsync("image", imageTag, "{{.Id}}|{{.Os}}|{{.Architecture}}");
        return inspect switch
        {
            DockerValue<DockerOutput> output => ParseImageInfo(output.Value.StdOut),
            DockerNotFound missing => missing,
            DockerUnavailable unavailable => unavailable,
            DockerFailure failure => failure
        };
    }

    private static DockerInspection<DockerImageInfo> ParseImageInfo(string output)
    {
        string[] parts = output
            .Trim()
            .Split('|', StringSplitOptions.TrimEntries);
        if(parts.Length != 3 || parts.Any(String.IsNullOrWhiteSpace))
        {
            return new DockerFailure("Docker image inspection returned invalid ID/platform metadata.");
        }

        return new DockerValue<DockerImageInfo>(new DockerImageInfo(parts[0], parts[1], parts[2]));
    }

    private static async Task<string> BuildImageTagAsync(string dockerfilePath)
    {
        string buildContextDirectory = Path.GetDirectoryName(dockerfilePath) ?? AppContext.BaseDirectory;
        string hash = await ComputeDirectoryHashAsync(buildContextDirectory);
        return $"opencode-wrap:{hash[..12]}";
    }

    private static async Task<string> ComputeDirectoryHashAsync(string directoryPath)
    {
        string[] filePaths = [.. Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(string filePath in filePaths)
        {
            string relativePath = Path.GetRelativePath(directoryPath, filePath).Replace('\\', '/');
            AppendString(hash, relativePath);
            byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
            hash.AppendData(fileBytes);
        }

        byte[] hashBytes = hash.GetHashAndReset();
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([(byte) '\n']);
    }
}
