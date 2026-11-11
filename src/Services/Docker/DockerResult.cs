namespace OpencodeWrap.Services.Docker;

internal sealed record DockerValue<T>(T Value);
internal sealed record DockerUnavailable(string Details, int ExitCode = 1);
internal sealed record DockerFailure(string Details, int ExitCode = 1);
internal sealed record DockerNotFound(string ResourceName);
internal sealed record DockerOutput(string StdOut, string StdErr);
internal readonly record struct DockerCompleted;

internal enum DockerVolumeReset
{
    Removed,
    AlreadyAbsent
}

internal union DockerResult<T>(DockerValue<T>, DockerUnavailable, DockerFailure)
{
    public DockerResult<TResult> Select<TResult>(Func<T, TResult> selector)
        => this switch
        {
            DockerValue<T> value => new DockerValue<TResult>(selector(value.Value)),
            DockerUnavailable unavailable => unavailable,
            DockerFailure failure => failure
        };

    public string ErrorDetails => this switch
    {
        DockerUnavailable unavailable => unavailable.Details,
        DockerFailure failure => failure.Details,
        DockerValue<T> => throw new InvalidOperationException("A successful Docker result has no error details.")
    };

    public int ExitCode => this switch
    {
        DockerValue<T> => 0,
        DockerUnavailable unavailable => unavailable.ExitCode,
        DockerFailure failure => failure.ExitCode
    };
}

internal union DockerInspection<T>(DockerValue<T>, DockerNotFound, DockerUnavailable, DockerFailure)
{
    public DockerResult<T> Require()
        => this switch
        {
            DockerValue<T> value => value,
            DockerNotFound missing => new DockerFailure($"Docker resource '{missing.ResourceName}' was not found."),
            DockerUnavailable unavailable => unavailable,
            DockerFailure failure => failure
        };
}
