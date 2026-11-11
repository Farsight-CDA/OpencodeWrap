namespace OpencodeWrap.Services.Docker;

internal static class DockerCommand
{
    public static async Task<DockerResult<DockerOutput>> RunAsync(
        IReadOnlyList<string> args,
        bool captureOutput = true,
        string? workDir = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null
    )
    {
        var result = captureOutput
            ? await ProcessRunner.RunAsync("docker", args, workDir: workDir, environmentVariables: environmentVariables)
            : await ProcessRunner.RunStreamingAsync("docker", args, workDir: workDir, environmentVariables: environmentVariables);
        return Classify(result);
    }

    public static async Task<DockerResult<DockerOutput>> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        => Classify(await ProcessRunner.RunAsync("docker", args, cancellationToken));

    public static async Task<DockerResult<DockerOutput>> RunAttachedAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environmentVariables = null
    )
        => Classify(await ProcessRunner.RunAttachedAsync("docker", args, environmentVariables: environmentVariables, captureStandardError: true));

    public static DockerResult<DockerOutput> RunBlocking(IReadOnlyList<string> args)
        => RunAsync(args).GetAwaiter().GetResult();

    public static async Task<DockerInspection<DockerOutput>> InspectAsync(string resourceType, string resourceName, string? format = null)
    {
        List<string> args = [resourceType, "inspect"];
        if(format is not null)
        {
            args.AddRange(["--format", format]);
        }

        args.Add(resourceName);
        return await RunAsync(args) switch
        {
            DockerValue<DockerOutput> value => value,
            DockerUnavailable unavailable => unavailable,
            DockerFailure failure when IsMissingResource(failure.Details, resourceType, resourceName) => new DockerNotFound(resourceName),
            DockerFailure failure => failure
        };
    }

    private static DockerResult<DockerOutput> Classify(ProcessRunner.ProcessRunResult result)
    {
        if(result.Success)
        {
            return new DockerValue<DockerOutput>(new DockerOutput(result.StdOut, result.StdErr));
        }

        if(!result.Started || IsUnavailable(result.StdErr))
        {
            return new DockerUnavailable(result.StdErr, result.ExitCode);
        }

        return new DockerFailure(result.StdErr, result.ExitCode);
    }

    private static bool IsUnavailable(string details)
        => details.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || details.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase)
            || details.Contains("error during connect:", StringComparison.OrdinalIgnoreCase)
            || details.Contains("failed to connect to the docker API", StringComparison.OrdinalIgnoreCase)
            || details.Contains("permission denied while trying to connect", StringComparison.OrdinalIgnoreCase)
            || details.Contains("client version", StringComparison.OrdinalIgnoreCase) && details.Contains("is too old", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingResource(string details, string resourceType, string resourceName)
    {
        // Docker has no distinct exit code for absence. Only accept its specific
        // missing-resource diagnostic; unknown failures must remain failures.
        string[] messages = resourceType switch
        {
            "volume" => [$"no such volume: {resourceName}", $"get {resourceName}: no such volume"],
            "image" => [$"no such image: {resourceName}", $"no such object: {resourceName}"],
            "container" => [$"no such container: {resourceName}", $"no such object: {resourceName}"],
            _ => []
        };
        string[] lines = details.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && lines.All(line => messages.Any(message =>
            String.Equals(line, message, StringComparison.OrdinalIgnoreCase)
            || String.Equals(line, $"Error: {message}", StringComparison.OrdinalIgnoreCase)
            || String.Equals(line, $"Error response from daemon: {message}", StringComparison.OrdinalIgnoreCase)));
    }
}
