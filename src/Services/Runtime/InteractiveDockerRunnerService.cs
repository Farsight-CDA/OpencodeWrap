using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class InteractiveDockerRunnerService : Singleton
{
    private static readonly byte[] _bracketedPasteEnableSequence = "\u001b[?2004h"u8.ToArray();
    internal const string STARTUP_READY_MARKER = "__OCW_READY_EEB6BB76D6E84A0FBC7A385C3D1AE7E5__";
    internal const string STARTUP_READY_MARKER_ENV_VAR = "OCW_STARTUP_READY_MARKER";

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    [Inject]
    private readonly PastedImagePathService _pastedImagePathService;

    public async Task<int> RunDockerAsync(IReadOnlyList<string> dockerArgs, InteractiveSessionContext? session, string? hostWorkingDirectory)
    {
        if(session is null)
        {
            AppIO.WriteError("Interactive terminal relay could not start because no runtime session was created.");
            return 1;
        }

        if(TryGetRelayUnavailableReason(out string? relayUnavailableReason))
        {
            AppIO.WriteError($"Interactive terminal relay unavailable: {relayUnavailableReason}.");
            return 1;
        }

        return OperatingSystem.IsWindows()
            ? await RunWindowsAsync(dockerArgs, session, hostWorkingDirectory)
            : await RunUnixAsync(dockerArgs, session, hostWorkingDirectory);
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
            AppIO.WriteError(BuildRelayStartupError(
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
            AppIO.WriteError(BuildRelayStartupError(
                "Interactive terminal relay failed to start the Unix relay process",
                relayProcessFailureReason,
                "Make sure the `script` utility is installed and available on PATH."));
            return 1;
        }

        using var sessionLog = _deferredSessionLogService.BeginSession();
        _deferredSessionLogService.Write("session", "interactive Unix relay session started");
        try
        {
            using(terminalModeScope)
            {
                var standardOutput = Console.OpenStandardOutput();
                var standardError = Console.OpenStandardError();
                await standardOutput.WriteAsync(_bracketedPasteEnableSequence);
                await standardOutput.FlushAsync();

                using var inputCancellationSource = new CancellationTokenSource();
                var startupInputGate = new StartupInputGate();
                var pasteRelay = new BracketedPasteRelay(session, hostWorkingDirectory, _deferredSessionLogService, _pastedImagePathService);

                var stdoutTask = PumpStreamAsync(relayProcess.StandardOutput.BaseStream, standardOutput, startupInputGate.CreateOutputFilter(), CancellationToken.None);
                var stderrTask = PumpStreamAsync(relayProcess.StandardError.BaseStream, standardError, startupInputGate.CreateOutputFilter(), CancellationToken.None);
                var inputTask = Task.Run(() => RelayInputLoop(
                    relayProcess.StandardInput.BaseStream,
                    pasteRelay,
                    startupInputGate.CanForwardInput,
                    () => relayProcess.HasExited,
                    inputCancellationSource.Token));

                await relayProcess.WaitForExitAsync();
                inputCancellationSource.Cancel();
                TryCloseRelayInput(relayProcess.StandardInput.BaseStream);

                await Task.WhenAll(stdoutTask, stderrTask);
                await Task.WhenAny(inputTask, Task.Delay(100));
            }

            return relayProcess.ExitCode;
        }
        finally
        {
            sessionLog.FlushToConsole();
        }
    }

    private async Task<int> RunWindowsAsync(IReadOnlyList<string> dockerArgs, InteractiveSessionContext session, string? hostWorkingDirectory)
    {
        if(!WindowsConsoleModeScope.TryEnter(out var terminalModeScope, out string? consoleFailureReason) || terminalModeScope is null)
        {
            AppIO.WriteError(BuildRelayStartupError(
                "Interactive terminal relay failed to initialize the Windows console state",
                consoleFailureReason,
                "Run OCW from a supported Windows terminal with an attached console."));
            return 1;
        }

        DiscardPendingHostInput();

        if(!WindowsPseudoConsoleSession.TryStart("docker", dockerArgs, out var pseudoConsoleSession, out string? pseudoConsoleFailureReason) || pseudoConsoleSession is null)
        {
            terminalModeScope.Dispose();
            AppIO.WriteError(BuildRelayStartupError(
                "Interactive terminal relay failed to start the Windows ConPTY session",
                pseudoConsoleFailureReason,
                "Check that ConPTY is available and that Docker can be launched from this console session."));
            return 1;
        }

        using var sessionLog = _deferredSessionLogService.BeginSession();
        _deferredSessionLogService.Write("session", "interactive Windows relay session started");
        try
        {
            using(terminalModeScope)
            using(pseudoConsoleSession)
            {
                var standardOutput = Console.OpenStandardOutput();
                await standardOutput.WriteAsync(_bracketedPasteEnableSequence);
                await standardOutput.FlushAsync();

                using var inputCancellationSource = new CancellationTokenSource();
                using var resizeCancellationSource = new CancellationTokenSource();
                var startupInputGate = new StartupInputGate();
                var plainTextInterceptor = new WindowsPlainTextPasteInterceptor(session, hostWorkingDirectory, _deferredSessionLogService, _pastedImagePathService);
                var pasteRelay = new BracketedPasteRelay(session, hostWorkingDirectory, _deferredSessionLogService, _pastedImagePathService, plainTextInterceptor);

                var outputTask = PumpStreamAsync(pseudoConsoleSession.OutputStream, standardOutput, startupInputGate.CreateOutputFilter(), CancellationToken.None);
                var inputTask = Task.Run(() => RelayWindowsRawInputLoop(
                    pseudoConsoleSession.InputStream,
                    pasteRelay,
                    startupInputGate.CanForwardInput,
                    () => pseudoConsoleSession.HasExited,
                    inputCancellationSource.Token));
                var resizeTask = Task.Run(() => PollConsoleResizeAsync(pseudoConsoleSession, resizeCancellationSource.Token));

                int exitCode = await pseudoConsoleSession.WaitForExitAsync();
                inputCancellationSource.Cancel();
                resizeCancellationSource.Cancel();
                pseudoConsoleSession.CloseInput();
                pseudoConsoleSession.ClosePseudoConsole();

                await outputTask;
                await Task.WhenAny(inputTask, Task.Delay(100));
                await Task.WhenAny(resizeTask, Task.Delay(100));
                return exitCode;
            }
        }
        finally
        {
            sessionLog.FlushToConsole();
        }
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

    private static Process? TryStartUnixRelayProcess(IReadOnlyList<string> dockerArgs)
    {
        return TryStartUnixRelayProcess(dockerArgs, out _);
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

        if(!result.Started)
        {
            return String.IsNullOrWhiteSpace(output)
                ? $"{commandDescription} could not start"
                : $"{commandDescription} could not start: {output}";
        }

        return String.IsNullOrWhiteSpace(output)
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
        string dockerCommand = "exec docker " + String.Join(' ', dockerArgs.Select(QuoteForShell));

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

    private static async Task PumpStreamAsync(Stream source, Stream destination, StartupOutputFilter? outputFilter, CancellationToken cancellationToken)
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

                ReadOnlyMemory<byte> bytesToWrite = outputFilter?.Filter(buffer.AsSpan(0, bytesRead)) ?? buffer.AsMemory(0, bytesRead);
                if(bytesToWrite.Length == 0)
                {
                    continue;
                }

                await destination.WriteAsync(bytesToWrite, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            if(outputFilter is not null)
            {
                ReadOnlyMemory<byte> trailingBytes = outputFilter.Flush();
                if(trailingBytes.Length > 0)
                {
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

    private static void RelayInputLoop(Stream relayInput, BracketedPasteRelay pasteRelay, Func<bool> canForwardInput, Func<bool> hasExited, CancellationToken cancellationToken)
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

                pasteRelay.Forward(buffer.AsSpan(0, bytesRead), relayInput);
            }

            pasteRelay.Flush(relayInput);
        }
        catch
        {
            // The relay is best effort while the child process is still running.
        }
    }

    private void RelayWindowsRawInputLoop(Stream relayInput, BracketedPasteRelay pasteRelay, Func<bool> canForwardInput, Func<bool> hasExited, CancellationToken cancellationToken)
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

                if(ContainsPotentialPasteSignal(buffer.AsSpan(0, bytesRead)))
                {
                    _deferredSessionLogService.Write("paste", $"windows raw input received; bytes={bytesRead}; preview={DescribeBytesForLog(buffer.AsSpan(0, bytesRead))}");
                }

                if(!canForwardInput())
                {
                    continue;
                }

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

        public bool CanForwardInput()
            => Volatile.Read(ref _ready) != 0;

        public void Open()
            => Interlocked.Exchange(ref _ready, 1);

        public StartupOutputFilter CreateOutputFilter()
            => new(this);
    }

    private sealed class StartupOutputFilter(StartupInputGate inputGate)
    {
        private static readonly byte[] _markerBytes = Encoding.ASCII.GetBytes(STARTUP_READY_MARKER);

        private readonly StartupInputGate _inputGate = inputGate;
        private readonly List<byte> _candidateBytes = [];

        public ReadOnlyMemory<byte> Filter(ReadOnlySpan<byte> buffer)
        {
            if(buffer.Length == 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            if(_inputGate.CanForwardInput())
            {
                return buffer.ToArray();
            }

            byte[] current = PrepareCurrentBuffer(buffer);
            int markerIndex = current.AsSpan().IndexOf(_markerBytes);
            if(markerIndex >= 0)
            {
                _inputGate.Open();

                int trailingLength = current.Length - markerIndex - _markerBytes.Length;
                if(markerIndex == 0)
                {
                    return trailingLength > 0
                        ? current.AsMemory(_markerBytes.Length, trailingLength)
                        : ReadOnlyMemory<byte>.Empty;
                }

                byte[] filtered = GC.AllocateUninitializedArray<byte>(current.Length - _markerBytes.Length);
                current.AsSpan(0, markerIndex).CopyTo(filtered);
                if(trailingLength > 0)
                {
                    current.AsSpan(markerIndex + _markerBytes.Length, trailingLength).CopyTo(filtered.AsSpan(markerIndex));
                }

                return filtered;
            }

            int trailingPrefixLength = GetTrailingPrefixLength(current, _markerBytes);
            int forwardLength = current.Length - trailingPrefixLength;
            if(trailingPrefixLength > 0)
            {
                _candidateBytes.AddRange(current.AsSpan(forwardLength, trailingPrefixLength).ToArray());
            }

            return forwardLength > 0
                ? current.AsMemory(0, forwardLength)
                : ReadOnlyMemory<byte>.Empty;
        }

        public ReadOnlyMemory<byte> Flush()
        {
            if(_candidateBytes.Count == 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }

            byte[] bytes = [.. _candidateBytes];
            _candidateBytes.Clear();
            return bytes;
        }

        private byte[] PrepareCurrentBuffer(ReadOnlySpan<byte> buffer)
        {
            if(_candidateBytes.Count == 0)
            {
                return buffer.ToArray();
            }

            byte[] combined = new byte[_candidateBytes.Count + buffer.Length];
            _candidateBytes.CopyTo(combined, 0);
            buffer.CopyTo(combined.AsSpan(_candidateBytes.Count));
            _candidateBytes.Clear();
            return combined;
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
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private sealed class BracketedPasteRelay(
        InteractiveSessionContext session,
        string? hostWorkingDirectory,
        DeferredSessionLogService deferredSessionLogService,
        PastedImagePathService pastedImagePathService,
        INormalInputInterceptor? normalInputInterceptor = null)
    {
        private static readonly byte[] _pasteStartMarker = "\u001b[200~"u8.ToArray();
        private static readonly byte[] _pasteEndMarker = "\u001b[201~"u8.ToArray();

        private readonly InteractiveSessionContext _session = session;
        private readonly string? _hostWorkingDirectory = hostWorkingDirectory;
        private readonly DeferredSessionLogService _deferredSessionLogService = deferredSessionLogService;
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

                        _deferredSessionLogService.Write("paste", $"bracketed paste end detected; bytes={_pasteBuffer.Count}");
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

                    _deferredSessionLogService.Write("paste", "bracketed paste start detected");
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

                _deferredSessionLogService.Write("paste", $"bracketed paste flushed without end marker; bytes={_pasteBuffer.Count}", DeferredSessionLogService.Importance.Significant);
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
            _deferredSessionLogService.Write("paste", $"rewriting bracketed paste bytes; chars={pastedText.Length}; preview={DescribeTextForLog(pastedText)}");
            var rewriteResult = _pastedImagePathService.RewritePaste(pastedText, _session, _hostWorkingDirectory);
            _deferredSessionLogService.Write(
                "paste",
                $"bracketed paste rewrite result; rewritten={rewriteResult.Rewritten}; preview={DescribeTextForLog(rewriteResult.Text)}",
                rewriteResult.Rewritten ? DeferredSessionLogService.Importance.Significant : DeferredSessionLogService.Importance.Verbose);
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
        DeferredSessionLogService deferredSessionLogService,
        PastedImagePathService pastedImagePathService) : INormalInputInterceptor
    {
        private const byte CTRL_V = 0x16;

        private readonly InteractiveSessionContext _session = session;
        private readonly string? _hostWorkingDirectory = hostWorkingDirectory;
        private readonly DeferredSessionLogService _deferredSessionLogService = deferredSessionLogService;
        private readonly PastedImagePathService _pastedImagePathService = pastedImagePathService;

        public void Forward(ReadOnlySpan<byte> bytes, Stream relayInput)
        {
            if(bytes.Length == 0)
            {
                return;
            }

            if(bytes.IndexOf(CTRL_V) >= 0 || LooksLikePotentialPathText(bytes))
            {
                _deferredSessionLogService.Write("paste", $"windows interceptor received input; bytes={bytes.Length}; containsCtrlV={bytes.IndexOf(CTRL_V) >= 0}; preview={DescribeBytesForLog(bytes)}");
            }

            if(bytes.Length > 1 && TryEmitDirectInputRewrite(bytes, relayInput))
            {
                return;
            }

            int ctrlVIndex = bytes.IndexOf(CTRL_V);
            if(ctrlVIndex < 0)
            {
                ForwardOrRewriteChunk(bytes, relayInput);
                return;
            }

            _deferredSessionLogService.Write("paste", $"windows interceptor detected Ctrl+V at index {ctrlVIndex}");

            if(ctrlVIndex > 0)
            {
                ForwardOrRewriteChunk(bytes[..ctrlVIndex], relayInput);
            }

            if(!TryEmitClipboardPaste(relayInput))
            {
                WriteForwardedBytes(relayInput, [CTRL_V]);
            }

            int trailingLength = bytes.Length - ctrlVIndex - 1;
            if(trailingLength > 0)
            {
                ForwardOrRewriteChunk(bytes[(ctrlVIndex + 1)..], relayInput);
            }
        }

        public void Flush(Stream relayInput)
        {
        }

        private bool TryEmitClipboardPaste(Stream relayInput)
        {
            _deferredSessionLogService.Write("paste", "paste requested via Ctrl+V");

            if(WindowsClipboardReader.TryGetText(out string clipboardText, _deferredSessionLogService) && clipboardText.Length > 0)
            {
                var rewriteResult = _pastedImagePathService.RewritePaste(clipboardText, _session, _hostWorkingDirectory);
                string forwardedText = rewriteResult.Rewritten ? rewriteResult.Text : clipboardText;
                _deferredSessionLogService.Write(
                    "paste",
                    $"clipboard text available; length={clipboardText.Length}; rewritten={rewriteResult.Rewritten}",
                    rewriteResult.Rewritten ? DeferredSessionLogService.Importance.Significant : DeferredSessionLogService.Importance.Verbose);
                WriteForwardedBytes(relayInput, Encoding.UTF8.GetBytes(forwardedText));
                return true;
            }

            if(!WindowsClipboardReader.TryGetImageData(out var clipboardImage, _deferredSessionLogService))
            {
                _deferredSessionLogService.Write("paste", "clipboard image lookup failed; forwarding raw Ctrl+V", DeferredSessionLogService.Importance.Significant);
                return false;
            }

            string stagingKey = WindowsClipboardReader.GetSequenceNumber().ToString();
            var imageRewriteResult = _pastedImagePathService.RewriteClipboardImage(clipboardImage.Bytes, clipboardImage.Extension, _session, stagingKey);
            _deferredSessionLogService.Write(
                "paste",
                $"clipboard image available; extension={clipboardImage.Extension}; bytes={clipboardImage.Bytes.Length}; rewritten={imageRewriteResult.Rewritten}",
                imageRewriteResult.Rewritten ? DeferredSessionLogService.Importance.Significant : DeferredSessionLogService.Importance.Verbose);
            if(!imageRewriteResult.Rewritten)
            {
                return false;
            }

            WriteForwardedBytes(relayInput, Encoding.UTF8.GetBytes(imageRewriteResult.Text));
            return true;
        }

        private void ForwardOrRewriteChunk(ReadOnlySpan<byte> bytes, Stream relayInput)
        {
            if(bytes.Length == 0)
            {
                return;
            }

            if(bytes.Length > 1 && TryEmitDirectInputRewrite(bytes, relayInput))
            {
                return;
            }

            if(LooksLikePotentialPathText(bytes))
            {
                _deferredSessionLogService.Write("paste", $"forwarding unrevised text chunk; preview={DescribeBytesForLog(bytes)}");
            }
            WriteForwardedBytes(relayInput, bytes.ToArray());
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

            _deferredSessionLogService.Write("paste", $"attempting direct input rewrite; preview={DescribeTextForLog(inputText)}");
            var rewriteResult = _pastedImagePathService.RewritePaste(inputText, _session, _hostWorkingDirectory);
            if(!rewriteResult.Rewritten)
            {
                _deferredSessionLogService.Write("paste", "direct input rewrite failed after candidate detection", DeferredSessionLogService.Importance.Significant);
                return false;
            }

            _deferredSessionLogService.Write("paste", $"direct input rewrite succeeded; preview={DescribeTextForLog(rewriteResult.Text)}", DeferredSessionLogService.Importance.Significant);
            WriteForwardedBytes(relayInput, Encoding.UTF8.GetBytes(rewriteResult.Text));
            return true;
        }

        private static void WriteForwardedBytes(Stream relayInput, byte[] bytes)
            => WriteRelayBytes(relayInput, bytes);
    }

    private static bool ContainsPotentialPasteSignal(ReadOnlySpan<byte> bytes)
        => bytes.IndexOf((byte)0x16) >= 0
            || LooksLikePotentialPathText(bytes);

    private static bool LooksLikePotentialPathText(ReadOnlySpan<byte> bytes)
    {
        string text;

        try
        {
            text = Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return false;
        }

        return LooksLikePotentialPathText(text);
    }

    private static bool LooksLikePotentialPathText(string text)
        => !String.IsNullOrWhiteSpace(text)
            && (text.Contains("file://", StringComparison.OrdinalIgnoreCase)
                || text.Contains('\\')
                || text.Contains('/')
                || text.Contains(":/", StringComparison.Ordinal)
                || text.Contains(":\\", StringComparison.Ordinal));

    private static string DescribeBytesForLog(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length == 0)
        {
            return "<empty>";
        }

        int previewLength = Math.Min(bytes.Length, 48);
        string textPreview;

        try
        {
            textPreview = DescribeTextForLog(Encoding.UTF8.GetString(bytes[..previewLength]));
        }
        catch
        {
            textPreview = "<non-utf8>";
        }

        string hexPreview = Convert.ToHexString(bytes[..previewLength]);
        return $"text={textPreview}; hex={hexPreview}{(bytes.Length > previewLength ? "..." : "")}";
    }

    private static string DescribeTextForLog(string text)
    {
        if(String.IsNullOrEmpty(text))
        {
            return "<empty>";
        }

        string sanitized = text.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\u001b", "\\u001b", StringComparison.Ordinal);
        return sanitized.Length <= 120 ? sanitized : sanitized[..120] + "...";
    }

    private static partial class WindowsClipboardReader
    {
        private const uint CF_BITMAP = 2;
        private const uint CF_DIB = 8;
        private const uint CF_DIBV5 = 17;
        private const uint BI_RGB = 0;
        private const uint BI_BITFIELDS = 3;
        private const uint BI_ALPHABITFIELDS = 6;
        private const uint CF_UNICODETEXT = 13;
        private const uint DIB_RGB_COLORS = 0;
        private static readonly uint _pngClipboardFormat = OperatingSystem.IsWindows() ? RegisterClipboardFormat("PNG") : 0;

        public static uint GetSequenceNumber()
            => !OperatingSystem.IsWindows() ? 0 : GetClipboardSequenceNumber();

        public static bool TryGetImageData(out ClipboardImageData imageData, DeferredSessionLogService deferredSessionLogService)
        {
            imageData = default;
            if(!OperatingSystem.IsWindows())
            {
                return false;
            }

            if(!OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            try
            {
                deferredSessionLogService.Write("paste", $"clipboard formats: {DescribeAvailableClipboardFormats()}");

                if(TryGetClipboardDataBytes(_pngClipboardFormat, out byte[] pngBytes, deferredSessionLogService) && pngBytes.Length > 0)
                {
                    deferredSessionLogService.Write("paste", $"clipboard image format PNG found; bytes={pngBytes.Length}");
                    imageData = new ClipboardImageData(".png", pngBytes);
                    return true;
                }

                if(TryGetClipboardDataBytes(CF_DIBV5, out byte[] dibV5Bytes, deferredSessionLogService)
                    && TryConvertDibToBitmapFileBytes(dibV5Bytes, out byte[] dibV5BitmapBytes))
                {
                    deferredSessionLogService.Write("paste", $"clipboard image format CF_DIBV5 found; bytes={dibV5Bytes.Length}; bmpBytes={dibV5BitmapBytes.Length}");
                    imageData = new ClipboardImageData(".bmp", dibV5BitmapBytes);
                    return true;
                }

                if(TryGetClipboardDataBytes(CF_DIB, out byte[] dibBytes, deferredSessionLogService)
                    && TryConvertDibToBitmapFileBytes(dibBytes, out byte[] dibBitmapBytes))
                {
                    deferredSessionLogService.Write("paste", $"clipboard image format CF_DIB found; bytes={dibBytes.Length}; bmpBytes={dibBitmapBytes.Length}");
                    imageData = new ClipboardImageData(".bmp", dibBitmapBytes);
                    return true;
                }

                if(TryGetClipboardBitmapFileBytes(out byte[] clipboardBitmapBytes, deferredSessionLogService))
                {
                    deferredSessionLogService.Write("paste", $"clipboard image format CF_BITMAP found; bmpBytes={clipboardBitmapBytes.Length}");
                    imageData = new ClipboardImageData(".bmp", clipboardBitmapBytes);
                    return true;
                }

                deferredSessionLogService.Write("paste", "no supported clipboard image formats found");
                return false;
            }
            finally
            {
                _ = CloseClipboard();
            }
        }

        public static bool TryGetText(out string clipboardText, DeferredSessionLogService deferredSessionLogService)
        {
            clipboardText = "";
            if(!OperatingSystem.IsWindows() || !IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                deferredSessionLogService.Write("paste", "clipboard text unavailable (CF_UNICODETEXT absent)");
                return false;
            }

            if(!OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            try
            {
                IntPtr clipboardHandle = GetClipboardData(CF_UNICODETEXT);
                if(clipboardHandle == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr lockedData = GlobalLock(clipboardHandle);
                if(lockedData == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    clipboardText = Marshal.PtrToStringUni(lockedData) ?? "";
                    deferredSessionLogService.Write("paste", $"clipboard text read; length={clipboardText.Length}");
                    return clipboardText.Length > 0;
                }
                finally
                {
                    _ = GlobalUnlock(clipboardHandle);
                }
            }
            finally
            {
                _ = CloseClipboard();
            }
        }

        private static bool TryGetClipboardDataBytes(uint format, out byte[] bytes, DeferredSessionLogService deferredSessionLogService)
        {
            bytes = [];
            if(format == 0 || !IsClipboardFormatAvailable(format))
            {
                deferredSessionLogService.Write("paste", $"clipboard format {DescribeClipboardFormat(format)} unavailable");
                return false;
            }

            IntPtr clipboardHandle = GetClipboardData(format);
            if(clipboardHandle == IntPtr.Zero)
            {
                return false;
            }

            nuint globalSize = GlobalSize(clipboardHandle);
            if(globalSize == 0 || globalSize > Int32.MaxValue)
            {
                deferredSessionLogService.Write("paste", $"clipboard format {DescribeClipboardFormat(format)} has invalid size {globalSize}");
                return false;
            }

            IntPtr lockedData = GlobalLock(clipboardHandle);
            if(lockedData == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                bytes = GC.AllocateUninitializedArray<byte>((int)globalSize);
                Marshal.Copy(lockedData, bytes, 0, bytes.Length);
                deferredSessionLogService.Write("paste", $"clipboard format {DescribeClipboardFormat(format)} copied; bytes={bytes.Length}");
                return bytes.Length > 0;
            }
            finally
            {
                _ = GlobalUnlock(clipboardHandle);
            }
        }

        private static bool TryConvertDibToBitmapFileBytes(byte[] dibBytes, out byte[] bitmapFileBytes)
        {
            bitmapFileBytes = [];
            if(dibBytes.Length < sizeof(uint)
                || !TryGetBitmapPixelDataOffset(dibBytes, out int pixelDataOffset))
            {
                return false;
            }

            bitmapFileBytes = GC.AllocateUninitializedArray<byte>(14 + dibBytes.Length);
            bitmapFileBytes[0] = (byte)'B';
            bitmapFileBytes[1] = (byte)'M';

            BinaryPrimitives.WriteUInt32LittleEndian(bitmapFileBytes.AsSpan(2, sizeof(uint)), (uint)bitmapFileBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bitmapFileBytes.AsSpan(10, sizeof(uint)), (uint)(14 + pixelDataOffset));
            dibBytes.CopyTo(bitmapFileBytes.AsSpan(14));
            return true;
        }

        private static bool TryGetClipboardBitmapFileBytes(out byte[] bitmapFileBytes, DeferredSessionLogService deferredSessionLogService)
        {
            bitmapFileBytes = [];
            if(!IsClipboardFormatAvailable(CF_BITMAP))
            {
                deferredSessionLogService.Write("paste", "clipboard format CF_BITMAP unavailable");
                return false;
            }

            IntPtr bitmapHandle = GetClipboardData(CF_BITMAP);
            deferredSessionLogService.Write("paste", $"clipboard format CF_BITMAP handle={(bitmapHandle == IntPtr.Zero ? "0" : "non-zero")}");
            return bitmapHandle != IntPtr.Zero && TryConvertBitmapHandleToBitmapFileBytes(bitmapHandle, out bitmapFileBytes, deferredSessionLogService);
        }

        private static bool TryConvertBitmapHandleToBitmapFileBytes(IntPtr bitmapHandle, out byte[] bitmapFileBytes, DeferredSessionLogService deferredSessionLogService)
        {
            bitmapFileBytes = [];

            if(GetObject(bitmapHandle, Marshal.SizeOf<BITMAP>(), out BITMAP bitmap) == 0
                || bitmap.bmWidth <= 0
                || bitmap.bmHeight == 0)
            {
                deferredSessionLogService.Write("paste", "GetObject on CF_BITMAP failed or returned invalid dimensions");
                return false;
            }

            deferredSessionLogService.Write("paste", $"CF_BITMAP dimensions width={bitmap.bmWidth}; height={bitmap.bmHeight}; bitsPixel={bitmap.bmBitsPixel}");

            int width = bitmap.bmWidth;
            int height = Math.Abs(bitmap.bmHeight);
            int stride = width * 4;
            int pixelDataSize = stride * height;

            var infoHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
                biSizeImage = (uint)pixelDataSize
            };

            byte[] pixelData = GC.AllocateUninitializedArray<byte>(pixelDataSize);
            IntPtr deviceContext = GetDC(IntPtr.Zero);
            if(deviceContext == IntPtr.Zero)
            {
                deferredSessionLogService.Write("paste", "GetDC failed for CF_BITMAP conversion");
                return false;
            }

            try
            {
                if(GetDIBits(deviceContext, bitmapHandle, 0, (uint)height, pixelData, ref infoHeader, DIB_RGB_COLORS) == 0)
                {
                    deferredSessionLogService.Write("paste", "GetDIBits failed for CF_BITMAP conversion");
                    return false;
                }
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, deviceContext);
            }

            const int bitmapFileHeaderSize = 14;
            int infoHeaderSize = Marshal.SizeOf<BITMAPINFOHEADER>();
            int pixelDataOffset = bitmapFileHeaderSize + infoHeaderSize;
            bitmapFileBytes = GC.AllocateUninitializedArray<byte>(pixelDataOffset + pixelData.Length);
            bitmapFileBytes[0] = (byte)'B';
            bitmapFileBytes[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(bitmapFileBytes.AsSpan(2, sizeof(uint)), (uint)bitmapFileBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bitmapFileBytes.AsSpan(10, sizeof(uint)), (uint)pixelDataOffset);
            MemoryMarshal.Write(bitmapFileBytes.AsSpan(bitmapFileHeaderSize, infoHeaderSize), in infoHeader);
            pixelData.CopyTo(bitmapFileBytes.AsSpan(pixelDataOffset));
            return true;
        }

        private static string DescribeAvailableClipboardFormats()
        {
            List<string> formats = [];
            uint format = 0;

            while(true)
            {
                format = EnumClipboardFormats(format);
                if(format == 0)
                {
                    break;
                }

                formats.Add(DescribeClipboardFormat(format));
            }

            return formats.Count == 0 ? "none" : String.Join(", ", formats);
        }

        private static string DescribeClipboardFormat(uint format)
        {
            if(format == 0)
            {
                return "0";
            }

            return format switch
            {
                CF_BITMAP => "CF_BITMAP",
                CF_DIB => "CF_DIB",
                CF_DIBV5 => "CF_DIBV5",
                CF_UNICODETEXT => "CF_UNICODETEXT",
                _ when format == _pngClipboardFormat => "PNG",
                _ => TryGetClipboardFormatName(format, out string? name)
                    ? $"{name}({format})"
                    : format.ToString()
            };
        }

        private static bool TryGetClipboardFormatName(uint format, out string? name)
        {
            char[] buffer = GC.AllocateUninitializedArray<char>(128);
            int nameLength = GetClipboardFormatName(format, buffer, buffer.Length);
            if(nameLength <= 0)
            {
                name = null;
                return false;
            }

            name = new string(buffer, 0, nameLength);
            return true;
        }

        private static bool TryGetBitmapPixelDataOffset(byte[] dibBytes, out int pixelDataOffset)
        {
            pixelDataOffset = 0;
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(0, sizeof(uint)));
            if(headerSize == 12)
            {
                if(dibBytes.Length < 12)
                {
                    return false;
                }

                ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dibBytes.AsSpan(10, sizeof(ushort)));
                long paletteEntryCount = bitCount <= 8 ? 1L << bitCount : 0;
                long candidateOffset = headerSize + (paletteEntryCount * 3L);
                if(candidateOffset > dibBytes.Length)
                {
                    return false;
                }

                pixelDataOffset = (int)candidateOffset;
                return true;
            }

            if(headerSize < 40 || headerSize > dibBytes.Length)
            {
                return false;
            }

            ushort infoBitCount = BinaryPrimitives.ReadUInt16LittleEndian(dibBytes.AsSpan(14, sizeof(ushort)));
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(16, sizeof(uint)));
            uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(32, sizeof(uint)));

            int colorMaskBytes = headerSize == 40
                ? compression switch
                {
                    BI_BITFIELDS => 12,
                    BI_ALPHABITFIELDS => 16,
                    _ => 0
                }
                : 0;

            long infoPaletteEntryCount = colorsUsed != 0
                ? colorsUsed
                : infoBitCount <= 8
                    ? 1L << infoBitCount
                    : 0;
            long candidatePixelOffset = headerSize + colorMaskBytes + (infoPaletteEntryCount * 4L);
            if(candidatePixelOffset > dibBytes.Length)
            {
                return false;
            }

            pixelDataOffset = (int)candidatePixelOffset;
            return true;
        }

    }

    private readonly record struct ClipboardImageData(string Extension, byte[] Bytes);

    private sealed partial class UnixTerminalModeScope(string savedState) : IDisposable
    {
        private static readonly byte[] _bracketedPasteDisableSequence = "\u001b[?2004l"u8.ToArray();
        private const int STDIN_FILE_DESCRIPTOR = 0;
        private const int TCIFLUSH = 0;
        private static readonly Lock _sync = new();
        private static string? _activeState;
        private readonly string _savedState = savedState;
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

            scope = new UnixTerminalModeScope(savedState);
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
