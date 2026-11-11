using CliWrap;
using CliWrap.Buffered;
using System.Text;
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

    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        bool captureOutput = true,
        string? workDir = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null
    )
        => captureOutput
            ? await RunAsync(fileName, args, CancellationToken.None, workDir, environmentVariables)
            : await RunAttachedAsync(fileName, args, workDir, environmentVariables);

    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        string? workDir = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null
    )
    {
        try
        {
            var command = CreateCommand(fileName, args, workDir, environmentVariables);
            var bufferedResult = await command.ExecuteBufferedAsync(cancellationToken);
            return new ProcessRunResult(true, bufferedResult.ExitCode, bufferedResult.StandardOutput, bufferedResult.StandardError);
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception ex)
        {
            return new ProcessRunResult(false, 1, "", ex.Message);
        }
    }

    public static async Task<ProcessRunResult> RunAttachedAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workDir = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        bool captureStandardError = false
    )
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(fileName)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = captureStandardError
                }
            };

            if(!String.IsNullOrWhiteSpace(workDir))
            {
                process.StartInfo.WorkingDirectory = workDir;
            }

            if(environmentVariables is not null)
            {
                foreach(var environmentVariable in environmentVariables)
                {
                    process.StartInfo.Environment[environmentVariable.Key] = environmentVariable.Value;
                }
            }

            foreach(string arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            if(!process.Start())
            {
                return new ProcessRunResult(false, 1, "", "Unable to start process.");
            }

            var standardError = new StringBuilder();
            var errorTask = captureStandardError ? ForwardStandardErrorAsync(process.StandardError, standardError) : Task.CompletedTask;
            await Task.WhenAll(process.WaitForExitAsync(), errorTask);
            return new ProcessRunResult(true, process.ExitCode, "", standardError.ToString());
        }
        catch(Exception ex)
        {
            return new ProcessRunResult(false, 1, "", ex.Message);
        }
    }

    public static async Task<ProcessRunResult> RunStreamingAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workDir = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null
    )
    {
        var standardError = new StringBuilder();
        try
        {
            var command = CreateCommand(fileName, args, workDir, environmentVariables)
                .WithStandardOutputPipe(PipeTarget.ToStream(Console.OpenStandardOutput()))
                .WithStandardErrorPipe(PipeTarget.Merge(PipeTarget.ToStream(Console.OpenStandardError()), PipeTarget.ToStringBuilder(standardError)));
            var result = await command.ExecuteAsync();
            return new ProcessRunResult(true, result.ExitCode, "", standardError.ToString());
        }
        catch(Exception ex)
        {
            return new ProcessRunResult(false, 1, "", ex.Message);
        }
    }

    private static async Task ForwardStandardErrorAsync(StreamReader reader, StringBuilder standardError)
    {
        char[] buffer = new char[4096];
        int count;
        while((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            standardError.Append(buffer, 0, count);
            await Console.Error.WriteAsync(buffer.AsMemory(0, count));
        }
    }

    private static CliCommand CreateCommand(
        string fileName,
        IReadOnlyList<string> args,
        string? workDir,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        var command = CliWrap.Cli.Wrap(fileName)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None);

        if(environmentVariables is not null)
        {
            command = command.WithEnvironmentVariables(environmentVariables);
        }

        return String.IsNullOrWhiteSpace(workDir) ? command : command.WithWorkingDirectory(workDir);
    }
}
