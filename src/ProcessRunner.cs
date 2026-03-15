using CliWrap;
using CliWrap.Buffered;
using CliCommand = CliWrap.Command;

namespace OpencodeWrap;

internal static class ProcessRunner
{
    internal readonly record struct ProcessRunResult(bool Started, int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => Started && ExitCode == 0;
    }

    public static bool CommandSucceedsBlocking(string fileName, IReadOnlyList<string> args)
    {
        try
        {
            return RunAsync(fileName, args).GetAwaiter().GetResult().Success;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> args, bool captureOutput = true, string? workDir = null)
    {
        try
        {
            var command = CreateCommand(fileName, args, workDir);

            if(!captureOutput)
            {
                return await RunAttachedAsync(fileName, args, workDir);
            }

            var bufferedResult = await command.ExecuteBufferedAsync();
            return new ProcessRunResult(true, bufferedResult.ExitCode, bufferedResult.StandardOutput, bufferedResult.StandardError);
        }
        catch(Exception ex)
        {
            return new ProcessRunResult(false, 1, "", ex.Message);
        }
    }

    public static async Task<ProcessRunResult> RunAttachedAsync(string fileName, IReadOnlyList<string> args, string? workDir = null)
    {
        try
        {
            var result = await CreateCommand(fileName, args, workDir)
                .WithStandardInputPipe(PipeSource.FromStream(Console.OpenStandardInput()))
                .WithStandardOutputPipe(PipeTarget.ToStream(Console.OpenStandardOutput()))
                .WithStandardErrorPipe(PipeTarget.ToStream(Console.OpenStandardError()))
                .ExecuteAsync();

            return new ProcessRunResult(true, result.ExitCode, "", "");
        }
        catch(Exception ex)
        {
            return new ProcessRunResult(false, 1, "", ex.Message);
        }
    }

    private static CliCommand CreateCommand(string fileName, IReadOnlyList<string> args, string? workDir)
    {
        var command = CliWrap.Cli.Wrap(fileName)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None);

        if(String.IsNullOrWhiteSpace(workDir))
        {
            return command;
        }

        return command.WithWorkingDirectory(workDir);
    }
}
