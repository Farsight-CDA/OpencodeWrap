using System.Diagnostics;

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
        var psi = CreateProcessStartInfo(fileName, args, captureOutput: captureOutput, workDir: workDir);

        try
        {
            return await RunProcessAsync(psi, captureOutput: captureOutput);
        }
        catch(Exception ex)
        {
            return new ProcessRunResult(false, 1, "", ex.Message);
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string fileName, IReadOnlyList<string> args, bool captureOutput, string? workDir = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            RedirectStandardInput = false
        };

        if(!String.IsNullOrWhiteSpace(workDir))
        {
            psi.WorkingDirectory = workDir;
        }

        foreach(string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static async Task<ProcessRunResult> RunProcessAsync(ProcessStartInfo psi, bool captureOutput)
    {
        using var process = Process.Start(psi);
        if(process is null)
        {
            return new ProcessRunResult(false, 1, "", "Unable to start process.");
        }

        if(!captureOutput)
        {
            await process.WaitForExitAsync();
            return new ProcessRunResult(true, process.ExitCode, "", "");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync());

        string stdOut = await stdOutTask;
        string stdErr = await stdErrTask;
        return new ProcessRunResult(true, process.ExitCode, stdOut, stdErr);
    }
}
