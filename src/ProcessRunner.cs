using System.Diagnostics;

internal static class ProcessRunner
{
    public static async Task<(bool Success, string StdOut, string StdErr)> TryGetCommandOutputAsync(string fileName, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach(string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if(process is null)
            {
                return (false, String.Empty, "Unable to start process.");
            }

            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync());
            return (process.ExitCode == 0, await stdOutTask, await stdErrTask);
        }
        catch(Exception ex)
        {
            return (false, String.Empty, ex.Message);
        }
    }

    public static async Task<(bool Success, string StdErr)> CommandSucceedsAsync(string fileName, IReadOnlyList<string> args, string? onFailurePrefix = null)
    {
        var command = await TryGetCommandOutputAsync(fileName, args);
        bool success = command.Success;
        string stdErr = command.StdErr;

        if(!success && !String.IsNullOrWhiteSpace(onFailurePrefix))
        {
            AppIO.WriteError(onFailurePrefix);
            if(!String.IsNullOrWhiteSpace(stdErr))
            {
                AppIO.WriteError(stdErr.Trim());
            }
        }

        return (success, stdErr);
    }

    public static async Task<bool> TryOpenDirectoryAsync(string directoryPath, bool isWindows, string? onFailurePrefix = null)
    {
        if(isWindows)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = directoryPath,
                    UseShellExecute = true
                });

                return true;
            }
            catch(Exception ex)
            {
                if(!String.IsNullOrWhiteSpace(onFailurePrefix))
                {
                    AppIO.WriteError(onFailurePrefix);
                }

                AppIO.WriteError(ex.Message);
                return false;
            }
        }

        var openResult = await CommandSucceedsAsync("xdg-open", [directoryPath], onFailurePrefix);
        return openResult.Success;
    }

    public static async Task<int> RunAttachedProcessAsync(string fileName, IReadOnlyList<string> args, string? workDir = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
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

        try
        {
            using var process = Process.Start(psi);
            if(process is null)
            {
                AppIO.WriteError($"Failed to start process '{fileName}'.");
                return 1;
            }

            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to run '{fileName}': {ex.Message}");
            return 1;
        }
        finally
        {
            RestoreTerminalState();
        }
    }

    private static void RestoreTerminalState()
    {
        if(Console.IsOutputRedirected)
        {
            return;
        }

        const string reset = "\x1b[0m\x1b[?25h\x1b[?7h\r\n";

        try
        {
            Console.Out.Write(reset);
            Console.Out.Flush();
        }
        catch
        {
        }
    }
}
