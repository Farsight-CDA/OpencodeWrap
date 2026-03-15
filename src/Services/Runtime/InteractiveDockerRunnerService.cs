using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class InteractiveDockerRunnerService : Singleton
{
    private static readonly byte[] _bracketedPasteEnableSequence = "\u001b[?2004h"u8.ToArray();
    internal const string STARTUP_READY_MARKER = "__OCW_READY_EEB6BB76D6E84A0FBC7A385C3D1AE7E5__";
    internal const string STARTUP_READY_MARKER_ENV_VAR = "OCW_STARTUP_READY_MARKER";

    [Inject]
    private readonly PastedImagePathService _pastedImagePathService;

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

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
            var startupInputGate = new StartupInputGate();
            var startupProgress = new StartupProgressTracker();
            var startupFailureTranscript = new StartupFailureTranscript();
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
            var startupInputGate = new StartupInputGate();
            var startupProgress = new StartupProgressTracker();
            var startupFailureTranscript = new StartupFailureTranscript();
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

    private void WriteRelayError(string message)
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

    private static string DescribeProcessFailure(string commandDescription, ProcessRunner.ProcessRunResult result)
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

    private static async Task PumpStreamAsync(Stream source, Stream destination, StartupOutputFilter? outputFilter, StartupFailureTranscript? startupFailureTranscript, CancellationToken cancellationToken)
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

    private static async Task ReportStartupProgressAsync(StartupProgressTracker tracker, Stream destination, Func<bool> hasExited, CancellationToken cancellationToken)
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

    private static void WriteRelayBytes(Stream relayInput, byte[] bytes)
    {
        if(bytes.Length == 0)
        {
            return;
        }

        relayInput.Write(bytes, 0, bytes.Length);
        relayInput.Flush();
    }

    private sealed class StartupInputGate
    {
        private int _ready;

        public bool IsOpen()
            => Volatile.Read(ref _ready) != 0;

        public bool CanForwardInput()
            => IsOpen();

        public void Open()
            => Interlocked.Exchange(ref _ready, 1);

        public StartupOutputFilter CreateOutputFilter(StartupProgressTracker tracker)
            => new(this, tracker);
    }

    private sealed class StartupOutputFilter(StartupInputGate inputGate, StartupProgressTracker tracker)
    {
        private static readonly byte[] _markerBytes = Encoding.ASCII.GetBytes(STARTUP_READY_MARKER);

        private readonly StartupInputGate _inputGate = inputGate;
        private readonly StartupProgressTracker _tracker = tracker;
        private readonly List<byte> _startupBytes = [];
        private bool _startupComplete;

        public ReadOnlyMemory<byte> Filter(ReadOnlySpan<byte> buffer)
        {
            if(buffer.Length == 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            if(_startupComplete)
            {
                if(buffer.Length > 0)
                {
                    _tracker.MarkVisibleOutput();
                }

                return buffer.ToArray();
            }

            _startupBytes.AddRange(buffer.ToArray());

            if(_inputGate.IsOpen())
            {
                _startupComplete = true;
                return ReleaseStartupBytes();
            }

            byte[] current = [.. _startupBytes];
            int markerIndex = current.AsSpan().IndexOf(_markerBytes);
            if(markerIndex >= 0)
            {
                _startupBytes.Clear();
                _startupComplete = true;
                _inputGate.Open();
                _tracker.MarkContainerReady();

                int trailingLength = current.Length - markerIndex - _markerBytes.Length;
                if(trailingLength > 0)
                {
                    _tracker.MarkVisibleOutput();
                    return current.AsMemory(markerIndex + _markerBytes.Length, trailingLength);
                }

                return ReadOnlyMemory<byte>.Empty;
            }

            return ReadOnlyMemory<byte>.Empty;
        }

        public ReadOnlyMemory<byte> Flush()
        {
            if(_startupBytes.Count == 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            _startupComplete = true;
            return ReleaseStartupBytes();
        }

        private ReadOnlyMemory<byte> ReleaseStartupBytes()
        {
            if(_startupBytes.Count == 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            byte[] bytes = [.. _startupBytes];
            _startupBytes.Clear();
            if(bytes.Length > 0)
            {
                _tracker.MarkVisibleOutput();
            }

            return bytes;
        }
    }

    private sealed class StartupProgressTracker
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _containerReadyElapsedTicks = -1;
        private int _visibleOutput;

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public bool IsContainerReady()
            => Volatile.Read(ref _containerReadyElapsedTicks) >= 0;

        public bool HasVisibleOutput()
            => Volatile.Read(ref _visibleOutput) != 0;

        public void MarkContainerReady()
        {
            long elapsedTicks = _stopwatch.ElapsedTicks;
            Interlocked.CompareExchange(ref _containerReadyElapsedTicks, elapsedTicks, -1);
        }

        public void MarkVisibleOutput()
            => Interlocked.Exchange(ref _visibleOutput, 1);

        public TimeSpan ElapsedSinceContainerReady()
        {
            long readyElapsedTicks = Volatile.Read(ref _containerReadyElapsedTicks);
            if(readyElapsedTicks < 0)
            {
                return TimeSpan.Zero;
            }

            long currentElapsedTicks = _stopwatch.ElapsedTicks;
            long deltaTicks = Math.Max(0, currentElapsedTicks - readyElapsedTicks);
            return TimeSpan.FromSeconds(deltaTicks / (double) Stopwatch.Frequency);
        }
    }

    private sealed class StartupFailureTranscript
    {
        private const int MAX_CAPTURED_BYTES = 32 * 1024;

        private readonly Lock _sync = new();
        private readonly List<byte> _capturedBytes = [];
        private bool _truncated;
        private int _inputObserved;

        public void Capture(ReadOnlySpan<byte> bytes)
        {
            if(bytes.Length == 0 || Volatile.Read(ref _inputObserved) != 0)
            {
                return;
            }

            lock(_sync)
            {
                if(Volatile.Read(ref _inputObserved) != 0)
                {
                    return;
                }

                _capturedBytes.AddRange(bytes.ToArray());
                int overflow = _capturedBytes.Count - MAX_CAPTURED_BYTES;
                if(overflow > 0)
                {
                    _capturedBytes.RemoveRange(0, overflow);
                    _truncated = true;
                }
            }
        }

        public void MarkInputObserved()
            => Interlocked.Exchange(ref _inputObserved, 1);

        public void WriteToDeferredLogIfRelevant(int exitCode, DeferredSessionLogService deferredSessionLogService)
        {
            if(exitCode == 0 || Volatile.Read(ref _inputObserved) != 0)
            {
                return;
            }

            byte[] capturedBytes;
            bool truncated;
            lock(_sync)
            {
                if(_capturedBytes.Count == 0)
                {
                    return;
                }

                capturedBytes = [.. _capturedBytes];
                truncated = _truncated;
            }

            if(truncated)
            {
                deferredSessionLogService.Write("container", $"captured container output was truncated to the most recent {MAX_CAPTURED_BYTES} bytes", Microsoft.Extensions.Logging.LogLevel.Warning);
            }

            foreach(string line in EnumerateCapturedLines(capturedBytes))
            {
                deferredSessionLogService.Write("container", line, Microsoft.Extensions.Logging.LogLevel.Error);
            }
        }

        private static IEnumerable<string> EnumerateCapturedLines(byte[] bytes)
        {
            string text = Encoding.UTF8.GetString(bytes)
                .Replace(STARTUP_READY_MARKER, String.Empty, StringComparison.Ordinal)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            string cleaned = StripAnsiSequences(text);
            foreach(string line in cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if(!String.IsNullOrWhiteSpace(line))
                {
                    yield return line;
                }
            }
        }

        private static string StripAnsiSequences(string value)
        {
            if(String.IsNullOrEmpty(value))
            {
                return String.Empty;
            }

            var builder = new StringBuilder(value.Length);

            for(int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if(current != '\u001b')
                {
                    builder.Append(current);
                    continue;
                }

                if(index + 1 >= value.Length)
                {
                    break;
                }

                char next = value[index + 1];
                if(next == '[')
                {
                    index += 2;
                    while(index < value.Length)
                    {
                        char terminator = value[index];
                        if(terminator >= '@' && terminator <= '~')
                        {
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                if(next == ']')
                {
                    index += 2;
                    while(index < value.Length)
                    {
                        if(value[index] == '\u0007')
                        {
                            break;
                        }

                        if(value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '\\')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                index++;
            }

            return builder.ToString();
        }
    }

    private static partial class WindowsConsoleInput
    {
        private const int STD_INPUT_HANDLE = -10;
        private static readonly IntPtr _inputHandle = GetStdHandle(STD_INPUT_HANDLE);

        public static bool TryRead(byte[] buffer, out int bytesRead)
        {
            bytesRead = 0;
            if(!OperatingSystem.IsWindows() || _inputHandle == IntPtr.Zero || _inputHandle == new IntPtr(-1))
            {
                return false;
            }

            if(!ReadFile(_inputHandle, buffer, buffer.Length, out int read, IntPtr.Zero))
            {
                return false;
            }

            bytesRead = read;
            return true;
        }

        public static void DiscardPendingInput()
        {
            if(!OperatingSystem.IsWindows() || _inputHandle == IntPtr.Zero || _inputHandle == new IntPtr(-1))
            {
                return;
            }

            _ = FlushConsoleInputBuffer(_inputHandle);
        }
    }

    private static string QuoteForShell(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private sealed class BracketedPasteRelay(
        InteractiveSessionContext session,
        string? hostWorkingDirectory,
        PastedImagePathService pastedImagePathService,
        INormalInputInterceptor? normalInputInterceptor = null)
    {
        private static readonly byte[] _pasteStartMarker = "\u001b[200~"u8.ToArray();
        private static readonly byte[] _pasteEndMarker = "\u001b[201~"u8.ToArray();

        private readonly InteractiveSessionContext _session = session;
        private readonly string? _hostWorkingDirectory = hostWorkingDirectory;
        private readonly PastedImagePathService _pastedImagePathService = pastedImagePathService;
        private readonly INormalInputInterceptor? _normalInputInterceptor = normalInputInterceptor;
        private readonly List<byte> _pasteBuffer = [];
        private readonly List<byte> _startMarkerCandidate = [];
        private readonly List<byte> _endMarkerCandidate = [];
        private bool _insidePaste;

        public void Forward(ReadOnlySpan<byte> buffer, Stream relayInput)
        {
            if(buffer.Length == 0)
            {
                return;
            }

            var current = PrepareCurrentBuffer(buffer);
            int offset = 0;

            while(offset < current.Length)
            {
                if(_insidePaste)
                {
                    int endMarkerIndex = current[offset..].IndexOf(_pasteEndMarker);
                    if(endMarkerIndex >= 0)
                    {
                        if(endMarkerIndex > 0)
                        {
                            _pasteBuffer.AddRange(current.Slice(offset, endMarkerIndex).ToArray());
                        }

                        FlushNormalInput(relayInput);
                        WriteBytes(relayInput, _pasteStartMarker);
                        WriteBytes(relayInput, RewritePasteBytes(_pasteBuffer));
                        WriteBytes(relayInput, _pasteEndMarker);
                        _pasteBuffer.Clear();
                        _insidePaste = false;
                        offset += endMarkerIndex + _pasteEndMarker.Length;
                        continue;
                    }

                    int endTrailingPrefixLength = GetTrailingPrefixLength(current[offset..], _pasteEndMarker);
                    int pasteLength = current.Length - offset - endTrailingPrefixLength;
                    if(pasteLength > 0)
                    {
                        _pasteBuffer.AddRange(current.Slice(offset, pasteLength).ToArray());
                    }

                    if(endTrailingPrefixLength > 0)
                    {
                        _endMarkerCandidate.AddRange(current[^endTrailingPrefixLength..].ToArray());
                    }

                    return;
                }

                int startMarkerIndex = current[offset..].IndexOf(_pasteStartMarker);
                if(startMarkerIndex >= 0)
                {
                    if(startMarkerIndex > 0)
                    {
                        WriteNormalInput(relayInput, current.Slice(offset, startMarkerIndex).ToArray());
                    }

                    FlushNormalInput(relayInput);
                    _insidePaste = true;
                    offset += startMarkerIndex + _pasteStartMarker.Length;
                    continue;
                }

                int trailingPrefixLength = GetTrailingPrefixLength(current[offset..], _pasteStartMarker);
                int forwardLength = current.Length - offset - trailingPrefixLength;
                if(forwardLength > 0)
                {
                    WriteNormalInput(relayInput, current.Slice(offset, forwardLength).ToArray());
                }

                if(trailingPrefixLength > 0)
                {
                    _startMarkerCandidate.AddRange(current[^trailingPrefixLength..].ToArray());
                }

                return;
            }
        }

        public void Flush(Stream relayInput)
        {
            if(_insidePaste)
            {
                if(_endMarkerCandidate.Count > 0)
                {
                    _pasteBuffer.AddRange(_endMarkerCandidate);
                    _endMarkerCandidate.Clear();
                }

                WriteBytes(relayInput, _pasteStartMarker);
                WriteBytes(relayInput, [.. _pasteBuffer]);
                _pasteBuffer.Clear();
                _insidePaste = false;
            }

            if(_startMarkerCandidate.Count > 0)
            {
                WriteNormalInput(relayInput, [.. _startMarkerCandidate]);
                _startMarkerCandidate.Clear();
            }

            FlushNormalInput(relayInput);
        }

        private ReadOnlySpan<byte> PrepareCurrentBuffer(ReadOnlySpan<byte> buffer)
        {
            if(_insidePaste && _endMarkerCandidate.Count > 0)
            {
                byte[] combined = new byte[_endMarkerCandidate.Count + buffer.Length];
                _endMarkerCandidate.CopyTo(combined, 0);
                buffer.CopyTo(combined.AsSpan(_endMarkerCandidate.Count));
                _endMarkerCandidate.Clear();
                return combined;
            }

            if(!_insidePaste && _startMarkerCandidate.Count > 0)
            {
                byte[] combined = new byte[_startMarkerCandidate.Count + buffer.Length];
                _startMarkerCandidate.CopyTo(combined, 0);
                buffer.CopyTo(combined.AsSpan(_startMarkerCandidate.Count));
                _startMarkerCandidate.Clear();
                return combined;
            }

            return buffer;
        }

        private static int GetTrailingPrefixLength(ReadOnlySpan<byte> buffer, byte[] marker)
        {
            int maxPrefixLength = Math.Min(marker.Length - 1, buffer.Length);
            for(int prefixLength = maxPrefixLength; prefixLength > 0; prefixLength--)
            {
                if(buffer[^prefixLength..].SequenceEqual(marker.AsSpan(0, prefixLength)))
                {
                    return prefixLength;
                }
            }

            return 0;
        }

        private byte[] RewritePasteBytes(List<byte> pasteBytes)
        {
            string pastedText = Encoding.UTF8.GetString([.. pasteBytes]);
            var rewriteResult = _pastedImagePathService.RewritePaste(pastedText, _session, _hostWorkingDirectory);
            return Encoding.UTF8.GetBytes(rewriteResult.Text);
        }

        private static void WriteBytes(Stream relayInput, byte[] bytes)
        {
            if(bytes.Length == 0)
            {
                return;
            }

            WriteRelayBytes(relayInput, bytes);
        }

        private void WriteNormalInput(Stream relayInput, byte[] bytes)
        {
            if(_normalInputInterceptor is null)
            {
                WriteBytes(relayInput, bytes);
                return;
            }

            _normalInputInterceptor.Forward(bytes, relayInput);
        }

        private void FlushNormalInput(Stream relayInput)
            => _normalInputInterceptor?.Flush(relayInput);
    }

    private interface INormalInputInterceptor
    {
        void Forward(ReadOnlySpan<byte> bytes, Stream relayInput);
        void Flush(Stream relayInput);
    }

    private sealed class WindowsPlainTextPasteInterceptor(
        InteractiveSessionContext session,
        string? hostWorkingDirectory,
        PastedImagePathService pastedImagePathService) : INormalInputInterceptor
    {
        private readonly InteractiveSessionContext _session = session;
        private readonly string? _hostWorkingDirectory = hostWorkingDirectory;
        private readonly PastedImagePathService _pastedImagePathService = pastedImagePathService;

        public void Forward(ReadOnlySpan<byte> bytes, Stream relayInput)
        {
            if(bytes.Length == 0)
            {
                return;
            }

            if(bytes.Length > 1 && TryEmitDirectInputRewrite(bytes, relayInput))
            {
                return;
            }

            WriteForwardedBytes(relayInput, bytes.ToArray());
        }

        public void Flush(Stream relayInput)
        {
        }

        private bool TryEmitDirectInputRewrite(ReadOnlySpan<byte> bytes, Stream relayInput)
        {
            string inputText;

            try
            {
                inputText = Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return false;
            }

            if(!_pastedImagePathService.CanRewritePaste(inputText, _hostWorkingDirectory))
            {
                return false;
            }

            var rewriteResult = _pastedImagePathService.RewritePaste(inputText, _session, _hostWorkingDirectory);
            if(!rewriteResult.Rewritten)
            {
                return false;
            }

            WriteForwardedBytes(relayInput, Encoding.UTF8.GetBytes(rewriteResult.Text));
            return true;
        }

        private static void WriteForwardedBytes(Stream relayInput, byte[] bytes)
            => WriteRelayBytes(relayInput, bytes);
    }

    private sealed partial class UnixTerminalModeScope : IDisposable
    {
        private static readonly byte[] _bracketedPasteDisableSequence = "\u001b[?2004l"u8.ToArray();
        private const int STDIN_FILE_DESCRIPTOR = 0;
        private const int TCIFLUSH = 0;
        private static readonly Lock _sync = new();
        private static string? _activeState;
        private bool _disposed;

        public static bool TryEnter(out UnixTerminalModeScope? scope, out string? failureReason)
        {
            scope = null;
            failureReason = null;

            var readStateResult = ProcessRunner.RunAsync("stty", ["-g"]).GetAwaiter().GetResult();
            if(!readStateResult.Success || String.IsNullOrWhiteSpace(readStateResult.StdOut))
            {
                failureReason = DescribeProcessFailure("`stty -g`", readStateResult);
                return false;
            }

            string savedState = readStateResult.StdOut.Trim();
            var rawModeResult = ProcessRunner.RunAsync("stty", ["raw", "-echo"]).GetAwaiter().GetResult();
            if(!rawModeResult.Success)
            {
                failureReason = DescribeProcessFailure("`stty raw -echo`", rawModeResult);
                return false;
            }

            lock(_sync)
            {
                _activeState = savedState;
            }

            scope = new UnixTerminalModeScope();
            return true;
        }

        public static void RestoreActiveState()
        {
            string? savedState;

            lock(_sync)
            {
                savedState = _activeState;
                _activeState = null;
            }

            if(String.IsNullOrWhiteSpace(savedState))
            {
                return;
            }

            TryWriteControlSequence(_bracketedPasteDisableSequence);
            _ = ProcessRunner.CommandSucceedsBlocking("stty", [savedState]);
        }

        public static void DiscardPendingInput()
            => _ = tcflush(STDIN_FILE_DESCRIPTOR, TCIFLUSH);

        public void Dispose()
        {
            if(_disposed)
            {
                return;
            }

            _disposed = true;
            RestoreActiveState();
        }

        private static void TryWriteControlSequence(byte[] sequence)
        {
            try
            {
                using var standardOutput = Console.OpenStandardOutput();
                standardOutput.Write(sequence, 0, sequence.Length);
                standardOutput.Flush();
            }
            catch
            {
                // Best effort terminal restoration only.
            }
        }
    }
}
