using System.Security.Cryptography;
using System.Text;

namespace OpencodeWrap.Services.Docker;

internal sealed record DockerImageInfo(string Tag, string Id, string Os, string Architecture);

internal sealed partial class DockerImageService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly SessionOutputService _sessionOutputService;

    public async Task<(bool Success, string ImageTag)> TryEnsureImageAsync(string dockerfilePath)
    {
        string imageTag = "opencode-wrap:unavailable";

        if(!File.Exists(dockerfilePath))
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Dockerfile not found: '{dockerfilePath}'.");
            return (false, imageTag);
        }

        imageTag = await BuildImageTagAsync(dockerfilePath);

        if(await ImageExistsAsync(imageTag))
        {
            return (true, imageTag);
        }

        _sessionOutputService.WriteInfo(LogCategories.DOCKER, $"Docker image '{imageTag}' not found. Building it now...");
        return await TryBuildImageAsync(dockerfilePath, imageTag, noCache: false, _deferredSessionLogService);
    }

    public async Task<(bool Success, string ImageTag)> TryBuildImageAsync(string dockerfilePath, bool noCache)
    {
        string imageTag = "opencode-wrap:unavailable";

        if(!File.Exists(dockerfilePath))
        {
            _deferredSessionLogService.WriteErrorOrConsole("docker", $"Dockerfile not found: '{dockerfilePath}'.");
            return (false, imageTag);
        }

        imageTag = await BuildImageTagAsync(dockerfilePath);
        return await TryBuildImageAsync(dockerfilePath, imageTag, noCache, _deferredSessionLogService);
    }

    private static async Task<(bool Success, string ImageTag)> TryBuildImageAsync(string dockerfilePath, string imageTag, bool noCache, DeferredSessionLogService deferredSessionLogService)
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

        bool built = (await ProcessRunner.RunAsync("docker", buildArgs, captureOutput: false, workDir: buildContextDirectory)).Success;
        if(!built)
        {
            deferredSessionLogService.WriteErrorOrConsole("docker", $"Failed to build Docker image '{imageTag}'.");
            return (false, imageTag);
        }

        return (true, imageTag);
    }

    public async Task<bool> ImageExistsAsync(string imageTag)
        => (await ProcessRunner.RunAsync("docker", ["image", "inspect", imageTag])).Success;

    public async Task<(bool Success, DockerImageInfo Image)> TryInspectImageAsync(string imageTag)
    {
        var emptyImage = new DockerImageInfo(imageTag, "", "", "");
        if(String.IsNullOrWhiteSpace(imageTag))
        {
            return (false, emptyImage);
        }

        var inspect = await ProcessRunner.RunAsync("docker", ["image", "inspect", "--format", "{{.Id}}|{{.Os}}|{{.Architecture}}", imageTag]);
        if(!inspect.Success)
        {
            return (false, emptyImage);
        }

        string[] parts = inspect.StdOut
            .Trim()
            .Split('|', StringSplitOptions.TrimEntries);
        if(parts.Length != 3)
        {
            return (false, emptyImage);
        }

        return (true, new DockerImageInfo(imageTag, parts[0], parts[1], parts[2]));
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
