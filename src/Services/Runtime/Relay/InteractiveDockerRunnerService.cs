using OpencodeWrap.Services.Runtime.Infrastructure;
using OpencodeWrap.Services.Runtime.Relay;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class InteractiveDockerRunnerService : Singleton
{
    private static readonly byte[] _bracketedPasteEnableSequence = "\u001b[?2004h"u8.ToArray();
    internal const string STARTUP_READY_MARKER = "__OCW_READY_EEB6BB76D6E84A0FBC7A385C3D1AE7E5__";
    internal const string STARTUP_READY_MARKER_ENV_VAR = "OCW_STARTUP_READY_MARKER";

    [Inject] private readonly PastedImagePathService _pastedImagePathService;
    [Inject] private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<int> RunDockerAsync(IReadOnlyList<string> dockerArgs, InteractiveSessionContext? session, string? hostWorkingDirectory)
    {
        if(session is null)
        {
            WriteRelayError("Interactive terminal relay could not start because no runtime session was created.");
            return 1;
        }

        if(TryGetRelayUnavailableReason(out string? relayUnavailableReason))
        {
            WriteRelayError($"Interactive terminal relay unavailable: {relayUnavailableReason}.");
            return 1;
        }

        return OperatingSystem.IsWindows()
            ? await RunWindowsAsync(dockerArgs, session, hostWorkingDirectory)
            : await RunUnixAsync(dockerArgs, session, hostWorkingDirectory);
    }

    public async Task<int> RunAttachedAsync(IReadOnlyList<string> dockerArgs)
    {
        var result = await ProcessRunner.RunAttachedAsync("docker", dockerArgs);
        if(!result.Started)
        {
            WriteRelayError($"Failed to start attached Docker process: {result.StdErr}");
            return 1;
        }

        return result.ExitCode;
    }

    public void RestoreTerminalStateIfNeeded()
    {
        UnixTerminalModeScope.RestoreActiveState();
        WindowsConsoleModeScope.RestoreActiveState();
    }

    private async Task<int> RunUnixAsync(IReadOnlyList<string> dockerArgs, InteractiveSessionContext session, string? hostWorkingDirectory)
    {
        if(!UnixTerminalModeScope.TryEnter(out var terminalModeScope, out string? terminalFailureReason) || terminalModeScope is null)
        {
            WriteRelayError(BuildRelayStartupError(
                "Interactive terminal relay failed to initialize the Unix terminal state",
                terminalFailureReason,
                "Run OCW from a real terminal with stdin/stdout attached."));
            return 1;
        }

        DiscardPendingHostInput();

        using var relayProcess = TryStartUnixRelayProcess(dockerArgs, out string? relayProcessFailureReason);
        if(relayProcess is null)
        {
            terminalModeScope.Dispose();
            WriteRelayError(BuildRelayStartupError(
                "Interactive terminal relay failed to start the Unix relay process",
                relayProcessFailureReason,
                "Make sure the `script` utility is installed and available on PATH."));
            return 1;
        }

        using(terminalModeScope)
        {
            var standardOutput = Console.OpenStandardOutput();
            var standardError = Console.OpenStandardError();
            await standardOutput.WriteAsync(_bracketedPasteEnableSequence);
            await standardOutput.FlushAsync();

            using var inputCancellationSource = new CancellationTokenSource();
            var startupInputGate = new StartupCoordination.InputGate();
            var startupProgress = new StartupCoordination.ProgressTracker();
            var startupFailureTranscript = new StartupCoordination.FailureTranscript();
            var pasteRelay = new BracketedPasteRelay(session, hostWorkingDirectory, _pastedImagePathService);
            var startupProgressTask = ReportStartupProgressAsync(startupProgress, standardError, () => relayProcess.HasExited, inputCancellationSource.Token);

            var stdoutTask = PumpStreamAsync(relayProcess.StandardOutput.BaseStream, standardOutput, startupInputGate.CreateOutputFilter(startupProgress), startupFailureTranscript, CancellationToken.None);
            var stderrTask = PumpStreamAsync(relayProcess.StandardError.BaseStream, standardError, startupInputGate.CreateOutputFilter(startupProgress), startupFailureTranscript, CancellationToken.None);
            var inputTask = Task.Run(() => RelayInputLoop(
                relayProcess.StandardInput.BaseStream,
                pasteRelay,
                startupInputGate.CanForwardInput,
                startupFailureTranscript.MarkInputObserved,
                () => relayProcess.HasExited,
                inputCancellationSource.Token));

            await relayProcess.WaitForExitAsync();
            int exitCode = relayProcess.ExitCode;
            inputCancellationSource.Cancel();
            TryCloseRelayInput(relayProcess.StandardInput.BaseStream);

            await Task.WhenAll(stdoutTask, stderrTask);
            startupFailureTranscript.WriteToDeferredLogIfRelevant(exitCode, _deferredSessionLogService);
            await Task.WhenAny(startupProgressTask, Task.Delay(100));
            await Task.WhenAny(inputTask, Task.Delay(100));

            return exitCode;
        }
    }

    private async Task<int> RunWindowsAsync(IReadOnlyList<string> dockerArgs, InteractiveSessionContext session, string? hostWorkingDirectory)
    {
        if(!WindowsConsoleModeScope.TryEnter(out var terminalModeScope, out string? consoleFailureReason) || terminalModeScope is null)
        {
            WriteRelayError(BuildRelayStartupError(
                "Interactive terminal relay failed to initialize the Windows console state",
                consoleFailureReason,
                "Run OCW from a supported Windows terminal with an attached console."));
            return 1;
        }

        DiscardPendingHostInput();

        if(!WindowsPseudoConsoleSession.TryStart("docker", dockerArgs, out var pseudoConsoleSession, out string? pseudoConsoleFailureReason) || pseudoConsoleSession is null)
        {
            terminalModeScope.Dispose();
            WriteRelayError(BuildRelayStartupError(
                "Interactive terminal relay failed to start the Windows ConPTY session",
                pseudoConsoleFailureReason,
                "Check that ConPTY is available and that Docker can be launched from this console session."));
            return 1;
        }

        using(terminalModeScope)
        using(pseudoConsoleSession)
        {
            var standardOutput = Console.OpenStandardOutput();
            await standardOutput.WriteAsync(_bracketedPasteEnableSequence);
            await standardOutput.FlushAsync();

            using var inputCancellationSource = new CancellationTokenSource();
            using var resizeCancellationSource = new CancellationTokenSource();
            var startupInputGate = new StartupCoordination.InputGate();
            var startupProgress = new StartupCoordination.ProgressTracker();
            var startupFailureTranscript = new StartupCoordination.FailureTranscript();
            var plainTextInterceptor = new WindowsPlainTextPasteInterceptor(session, hostWorkingDirectory, _pastedImagePathService);
            var pasteRelay = new BracketedPasteRelay(session, hostWorkingDirectory, _pastedImagePathService, plainTextInterceptor);
            var startupProgressTask = ReportStartupProgressAsync(startupProgress, standardOutput, () => pseudoConsoleSession.HasExited, inputCancellationSource.Token);

            var outputTask = PumpStreamAsync(pseudoConsoleSession.OutputStream, standardOutput, startupInputGate.CreateOutputFilter(startupProgress), startupFailureTranscript, CancellationToken.None);
            var inputTask = Task.Run(() => RelayWindowsRawInputLoop(
                pseudoConsoleSession.InputStream,
                pasteRelay,
                startupInputGate.CanForwardInput,
                startupFailureTranscript.MarkInputObserved,
                () => pseudoConsoleSession.HasExited,
                inputCancellationSource.Token));
            var resizeTask = Task.Run(() => PollConsoleResizeAsync(pseudoConsoleSession, resizeCancellationSource.Token));

            int exitCode = await pseudoConsoleSession.WaitForExitAsync();
            inputCancellationSource.Cancel();
            resizeCancellationSource.Cancel();
            pseudoConsoleSession.CloseInput();
            pseudoConsoleSession.ClosePseudoConsole();

            await outputTask;
            startupFailureTranscript.WriteToDeferredLogIfRelevant(exitCode, _deferredSessionLogService);
            await Task.WhenAny(startupProgressTask, Task.Delay(100));
            await Task.WhenAny(inputTask, Task.Delay(100));
            await Task.WhenAny(resizeTask, Task.Delay(100));
            return exitCode;
        }
    }

    internal void WriteRelayError(string message)
    {
        _deferredSessionLogService.Write("relay", message, Microsoft.Extensions.Logging.LogLevel.Error);
        AppIO.WriteError(message);
    }

    private static bool TryGetRelayUnavailableReason(out string? reason)
    {
        if(Console.IsInputRedirected)
        {
            reason = "stdin is redirected; OCW must be started from an interactive terminal";
            return true;
        }

        if(Console.IsOutputRedirected)
        {
            reason = "stdout is redirected; OCW must be started with a terminal-attached stdout";
            return true;
        }

        if(!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            reason = $"{RuntimeInformation.OSDescription} is not supported for the interactive relay";
            return true;
        }

        reason = null;
        return false;
    }

    private static void DiscardPendingHostInput()
    {
        try
        {
            if(OperatingSystem.IsWindows())
            {
                WindowsConsoleInput.DiscardPendingInput();
                return;
            }

            if(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                UnixTerminalModeScope.DiscardPendingInput();
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static Process? TryStartUnixRelayProcess(IReadOnlyList<string> dockerArgs, out string? failureReason)
    {
        failureReason = null;

        try
        {
            return Process.Start(BuildUnixRelayStartInfo(dockerArgs));
        }
        catch(Exception ex)
        {
            failureReason = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static string BuildRelayStartupError(string summary, string? detail, string? guidance = null)
    {
        var message = new StringBuilder(summary);

        if(!String.IsNullOrWhiteSpace(detail))
        {
            message.Append(". ");
            message.Append(detail.Trim().TrimEnd('.'));
        }

        if(!String.IsNullOrWhiteSpace(guidance))
        {
            message.Append(". ");
            message.Append(guidance.Trim().TrimEnd('.'));
        }

        return message.ToString();
    }

    internal static string DescribeProcessFailure(string commandDescription, ProcessRunner.ProcessRunResult result)
    {
        string output = FirstNonEmptyLine(result.StdErr, result.StdOut);

        return !result.Started
            ? String.IsNullOrWhiteSpace(output)
                ? $"{commandDescription} could not start"
                : $"{commandDescription} could not start: {output}"
            : String.IsNullOrWhiteSpace(output)
                ? $"{commandDescription} exited with code {result.ExitCode}"
                : $"{commandDescription} exited with code {result.ExitCode}: {output}";
    }

    private static string FirstNonEmptyLine(params string[] values)
    {
        foreach(string value in values)
        {
            if(String.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string line = value
                .Replace("\r", String.Empty, StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? String.Empty;

            if(!String.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return String.Empty;
    }

    private static ProcessStartInfo BuildUnixRelayStartInfo(IReadOnlyList<string> dockerArgs)
    {
        string dockerCommand = $"exec docker {String.Join(' ', dockerArgs.Select(QuoteForShell))}";

        var psi = new ProcessStartInfo("script")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if(OperatingSystem.IsLinux())
        {
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(dockerCommand);
            psi.ArgumentList.Add("/dev/null");
            return psi;
        }

        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("/dev/null");
        psi.ArgumentList.Add("/bin/sh");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(dockerCommand);
        return psi;
    }

    private static async Task PumpStreamAsync(Stream source, Stream destination, StartupCoordination.OutputFilter? outputFilter, StartupCoordination.FailureTranscript? startupFailureTranscript, CancellationToken cancellationToken)
    {
        try
        {
            byte[] buffer = new byte[16384];

            while(true)
            {
                int bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if(bytesRead <= 0)
                {
                    break;
                }

                var bytesToWrite = outputFilter?.Filter(buffer.AsSpan(0, bytesRead)) ?? buffer.AsMemory(0, bytesRead);
                if(bytesToWrite.Length == 0)
                {
                    continue;
                }

                startupFailureTranscript?.Capture(bytesToWrite.Span);
                await destination.WriteAsync(bytesToWrite, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            if(outputFilter is not null)
            {
                var trailingBytes = outputFilter.Flush();
                if(trailingBytes.Length > 0)
                {
                    startupFailureTranscript?.Capture(trailingBytes.Span);
                    await destination.WriteAsync(trailingBytes, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }
            }
        }
        catch
        {
            // The interactive session may be shutting down.
        }
    }

    private static async Task ReportStartupProgressAsync(StartupCoordination.ProgressTracker tracker, Stream destination, Func<bool> hasExited, CancellationToken cancellationToken)
    {
        bool reportedContainerWait = false;
        bool reportedUiWait = false;

        try
        {
            while(!cancellationToken.IsCancellationRequested && !hasExited() && !tracker.HasVisibleOutput())
            {
                await Task.Delay(250, cancellationToken);

                if(!reportedContainerWait && !tracker.IsContainerReady() && tracker.Elapsed >= TimeSpan.FromSeconds(1.5))
                {
                    reportedContainerWait = true;
                    await WriteStartupProgressAsync(destination, $"[ocw] waiting for container startup... {tracker.Elapsed.TotalSeconds:F1}s", cancellationToken);
                    continue;
                }

                if(!reportedUiWait && tracker.IsContainerReady() && !tracker.HasVisibleOutput() && tracker.ElapsedSinceContainerReady() >= TimeSpan.FromSeconds(1.0))
                {
                    reportedUiWait = true;
                    await WriteStartupProgressAsync(destination, $"[ocw] container ready; waiting for opencode UI... {tracker.Elapsed.TotalSeconds:F1}s", cancellationToken);
                }
            }
        }
        catch(OperationCanceledException)
        {
        }
        catch
        {
            // Startup progress logging is best effort only.
        }
    }

    private static async Task WriteStartupProgressAsync(Stream destination, string message, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message + Environment.NewLine);
        await destination.WriteAsync(bytes, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static void RelayInputLoop(Stream relayInput, BracketedPasteRelay pasteRelay, Func<bool> canForwardInput, Action onForwardedInput, Func<bool> hasExited, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];

        try
        {
            using var standardInput = Console.OpenStandardInput();

            while(!cancellationToken.IsCancellationRequested && !hasExited())
            {
                int bytesRead = standardInput.Read(buffer, 0, buffer.Length);
                if(bytesRead <= 0)
                {
                    break;
                }

                if(!canForwardInput())
                {
                    continue;
                }

                onForwardedInput();
                pasteRelay.Forward(buffer.AsSpan(0, bytesRead), relayInput);
            }

            pasteRelay.Flush(relayInput);
        }
        catch
        {
            // The relay is best effort while the child process is still running.
        }
    }

    private void RelayWindowsRawInputLoop(Stream relayInput, BracketedPasteRelay pasteRelay, Func<bool> canForwardInput, Action onForwardedInput, Func<bool> hasExited, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];

        try
        {
            while(!cancellationToken.IsCancellationRequested && !hasExited())
            {
                if(!WindowsConsoleInput.TryRead(buffer, out int bytesRead))
                {
                    Thread.Sleep(10);
                    continue;
                }

                if(bytesRead <= 0)
                {
                    continue;
                }

                if(!canForwardInput())
                {
                    continue;
                }

                onForwardedInput();
                pasteRelay.Forward(buffer.AsSpan(0, bytesRead), relayInput);
            }

            pasteRelay.Flush(relayInput);
        }
        catch
        {
            // Best effort relay only.
        }
    }

    private static async Task PollConsoleResizeAsync(WindowsPseudoConsoleSession pseudoConsoleSession, CancellationToken cancellationToken)
    {
        var lastSize = WindowsPseudoConsoleSession.GetCurrentConsoleSize();

        try
        {
            while(!cancellationToken.IsCancellationRequested && !pseudoConsoleSession.HasExited)
            {
                await Task.Delay(200, cancellationToken);
                var currentSize = WindowsPseudoConsoleSession.GetCurrentConsoleSize();
                if(currentSize == lastSize)
                {
                    continue;
                }

                pseudoConsoleSession.Resize(currentSize.Columns, currentSize.Rows);
                lastSize = currentSize;
            }
        }
        catch(OperationCanceledException)
        {
        }
        catch
        {
            // Resize forwarding is best effort only.
        }
    }

    private static void TryCloseRelayInput(Stream relayInput)
    {
        try
        {
            relayInput.Close();
        }
        catch
        {
            // Best effort shutdown only.
        }
    }

    internal static void WriteRelayBytes(Stream relayInput, byte[] bytes)
    {
        if(bytes.Length == 0)
        {
            return;
        }

        relayInput.Write(bytes, 0, bytes.Length);
        relayInput.Flush();
    }

    private static string QuoteForShell(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
