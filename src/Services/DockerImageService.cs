using System.Security.Cryptography;

internal sealed class DockerImageService
{
    public async Task<(bool Success, string ImageTag)> TryEnsureImageAsync(string dockerfilePath)
    {
        string imageTag = "opencode-wrap:unavailable";

        if(!File.Exists(dockerfilePath))
        {
            AppIO.WriteError($"Dockerfile not found: '{dockerfilePath}'.");
            return (false, imageTag);
        }

        imageTag = await BuildImageTagAsync(dockerfilePath);

        var inspect = await ProcessRunner.CommandSucceedsAsync("docker", ["image", "inspect", imageTag]);
        if(inspect.Success)
        {
            return (true, imageTag);
        }

        AppIO.WriteInfo($"Docker image '{imageTag}' not found. Building it now...");
        return await TryBuildImageAsync(dockerfilePath, imageTag, noCache: false);
    }

    public async Task<(bool Success, string ImageTag)> TryBuildImageAsync(string dockerfilePath, bool noCache)
    {
        string imageTag = "opencode-wrap:unavailable";

        if(!File.Exists(dockerfilePath))
        {
            AppIO.WriteError($"Dockerfile not found: '{dockerfilePath}'.");
            return (false, imageTag);
        }

        imageTag = await BuildImageTagAsync(dockerfilePath);
        return await TryBuildImageAsync(dockerfilePath, imageTag, noCache);
    }

    private static async Task<(bool Success, string ImageTag)> TryBuildImageAsync(string dockerfilePath, string imageTag, bool noCache)
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

        bool built = await ProcessRunner.RunAttachedProcessAsync("docker", buildArgs, buildContextDirectory) == 0;
        if(!built)
        {
            AppIO.WriteError($"Failed to build Docker image '{imageTag}'.");
            return (false, imageTag);
        }

        return (true, imageTag);
    }

    private static async Task<string> BuildImageTagAsync(string dockerfilePath)
    {
        byte[] bytes = await File.ReadAllBytesAsync(dockerfilePath);
        byte[] hashBytes = SHA256.HashData(bytes);
        string hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return $"opencode-wrap:{hash[..12]}";
    }

}
