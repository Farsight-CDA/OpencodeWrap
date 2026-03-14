using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed class InteractiveDockerRunnerService(PastedImagePathService pastedImagePathService)
{
    private static readonly byte[] _bracketedPasteEnableSequence = "\u001b[?2004h"u8.ToArray();
    private readonly PastedImagePathService _pastedImagePathService = pastedImagePathService;

    public async Task<int> RunDockerAsync(IReadOnlyList<string> dockerArgs, InteractiveSessionContext? session, string? hostWorkingDirectory)
    {
        if(session is null || !CanUseRelay())
        {
            return (await ProcessRunner.RunAsync("docker", dockerArgs, captureOutput: false)).ExitCode;
        }

        if(OperatingSystem.IsWindows())
        {
            return await RunWindowsAsync(dockerArgs, session, hostWorkingDirectory);
        }

        if(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return await RunUnixAsync(dockerArgs, session, hostWorkingDirectory);
        }

        return (await ProcessRunner.RunAsync("docker", dockerArgs, captureOutput: false)).ExitCode;
    }

    public static void RestoreTerminalStateIfNeeded()
    {
        UnixTerminalModeScope.RestoreActiveState();
        WindowsConsoleModeScope.RestoreActiveState();
    }

    private async Task<int> RunUnixAsync(IReadOnlyList<string> dockerArgs, InteractiveSessionContext session, string? hostWorkingDirectory)
    {
        if(!UnixTerminalModeScope.TryEnter(out UnixTerminalModeScope? terminalModeScope) || terminalModeScope is null)
        {
            return (await ProcessRunner.RunAsync("docker", dockerArgs, captureOutput: false)).ExitCode;
        }

        using Process? relayProcess = TryStartUnixRelayProcess(dockerArgs);
        if(relayProcess is null)
        {
            terminalModeScope.Dispose();
            return (await ProcessRunner.RunAsync("docker", dockerArgs, captureOutput: false)).ExitCode;
        }

        using(terminalModeScope)
        {
            Stream standardOutput = Console.OpenStandardOutput();
            Stream standardError = Console.OpenStandardError();
            await standardOutput.WriteAsync(_bracketedPasteEnableSequence);
            await standardOutput.FlushAsync();

            using var inputCancellationSource = new CancellationTokenSource();
            var pasteRelay = new BracketedPasteRelay(_pastedImagePathService, session, hostWorkingDirectory);

            Task stdoutTask = PumpStreamAsync(relayProcess.StandardOutput.BaseStream, standardOutput, CancellationToken.None);
            Task stderrTask = PumpStreamAsync(relayProcess.StandardError.BaseStream, standardError, CancellationToken.None);
            Task inputTask = Task.Run(() => RelayInputLoop(
                relayProcess.StandardInput.BaseStream,
                pasteRelay,
                () => relayProcess.HasExited,
                inputCancellationSource.Token));

            await relayProcess.WaitForExitAsync();
            inputCancellationSource.Cancel();
            TryCloseRelayInput(relayProcess.StandardInput.BaseStream);

            await Task.WhenAll(stdoutTask, stderrTask);
            await Task.WhenAny(inputTask, Task.Delay(100));
            return relayProcess.ExitCode;
        }
    }

    private async Task<int> RunWindowsAsync(IReadOnlyList<string> dockerArgs, InteractiveSessionContext session, string? hostWorkingDirectory)
    {
        if(!WindowsConsoleModeScope.TryEnter(out WindowsConsoleModeScope? terminalModeScope, out string? consoleFailureReason) || terminalModeScope is null)
        {
            AppIO.WriteWarning($"Windows terminal relay unavailable; falling back to direct docker attach. {consoleFailureReason}");
            return (await ProcessRunner.RunAsync("docker", dockerArgs, captureOutput: false)).ExitCode;
        }

        if(!WindowsPseudoConsoleSession.TryStart("docker", dockerArgs, out WindowsPseudoConsoleSession? pseudoConsoleSession, out string? pseudoConsoleFailureReason) || pseudoConsoleSession is null)
        {
            terminalModeScope.Dispose();
            AppIO.WriteWarning($"Windows ConPTY relay failed to start; falling back to direct docker attach. {pseudoConsoleFailureReason}");
            return (await ProcessRunner.RunAsync("docker", dockerArgs, captureOutput: false)).ExitCode;
        }

        using(terminalModeScope)
        using(pseudoConsoleSession)
        {
            Stream standardOutput = Console.OpenStandardOutput();
            await standardOutput.WriteAsync(_bracketedPasteEnableSequence);
            await standardOutput.FlushAsync();

            using var inputCancellationSource = new CancellationTokenSource();
            using var resizeCancellationSource = new CancellationTokenSource();
            var plainTextInterceptor = new WindowsPlainTextPasteInterceptor(_pastedImagePathService, session, hostWorkingDirectory);
            var pasteRelay = new BracketedPasteRelay(_pastedImagePathService, session, hostWorkingDirectory, plainTextInterceptor);

            Task outputTask = PumpStreamAsync(pseudoConsoleSession.OutputStream, standardOutput, CancellationToken.None);
            Task inputTask = Task.Run(() => RelayWindowsRawInputLoop(
                pseudoConsoleSession.InputStream,
                pasteRelay,
                () => pseudoConsoleSession.HasExited,
                inputCancellationSource.Token));
            Task resizeTask = Task.Run(() => PollConsoleResizeAsync(pseudoConsoleSession, resizeCancellationSource.Token));

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

    private static bool CanUseRelay()
        => (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            && !Console.IsInputRedirected
            && !Console.IsOutputRedirected;

    private static Process? TryStartUnixRelayProcess(IReadOnlyList<string> dockerArgs)
    {
        try
        {
            return Process.Start(BuildUnixRelayStartInfo(dockerArgs));
        }
        catch
        {
            return null;
        }
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

    private static async Task PumpStreamAsync(Stream source, Stream destination, CancellationToken cancellationToken)
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

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
        }
        catch
        {
            // The interactive session may be shutting down.
        }
    }

    private static void RelayInputLoop(Stream relayInput, BracketedPasteRelay pasteRelay, Func<bool> hasExited, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];

        try
        {
            using Stream standardInput = Console.OpenStandardInput();

            while(!cancellationToken.IsCancellationRequested && !hasExited())
            {
                int bytesRead = standardInput.Read(buffer, 0, buffer.Length);
                if(bytesRead <= 0)
                {
                    break;
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

    private static void RelayWindowsRawInputLoop(Stream relayInput, BracketedPasteRelay pasteRelay, Func<bool> hasExited, CancellationToken cancellationToken)
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
        (short Columns, short Rows) lastSize = WindowsPseudoConsoleSession.GetCurrentConsoleSize();

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

    private static class WindowsConsoleInput
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);
    }

    private static string QuoteForShell(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private sealed class BracketedPasteRelay(
        PastedImagePathService pastedImagePathService,
        InteractiveSessionContext session,
        string? hostWorkingDirectory,
        INormalInputInterceptor? normalInputInterceptor = null)
    {
        private static readonly byte[] _pasteStartMarker = "\u001b[200~"u8.ToArray();
        private static readonly byte[] _pasteEndMarker = "\u001b[201~"u8.ToArray();

        private readonly PastedImagePathService _pastedImagePathService = pastedImagePathService;
        private readonly InteractiveSessionContext _session = session;
        private readonly string? _hostWorkingDirectory = hostWorkingDirectory;
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

            ReadOnlySpan<byte> current = PrepareCurrentBuffer(buffer);
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
                WriteBytes(relayInput, _pasteBuffer.ToArray());
                _pasteBuffer.Clear();
                _insidePaste = false;
            }

            if(_startMarkerCandidate.Count > 0)
            {
                WriteNormalInput(relayInput, _startMarkerCandidate.ToArray());
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
            string pastedText = Encoding.UTF8.GetString(pasteBytes.ToArray());
            PasteRewriteResult rewriteResult = _pastedImagePathService.RewritePaste(pastedText, _session, _hostWorkingDirectory);
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
        PastedImagePathService pastedImagePathService,
        InteractiveSessionContext session,
        string? hostWorkingDirectory) : INormalInputInterceptor
    {
        private const byte CTRL_V = 0x16;

        private readonly PastedImagePathService _pastedImagePathService = pastedImagePathService;
        private readonly InteractiveSessionContext _session = session;
        private readonly string? _hostWorkingDirectory = hostWorkingDirectory;

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

            int ctrlVIndex = bytes.IndexOf(CTRL_V);
            if(ctrlVIndex < 0)
            {
                ForwardOrRewriteChunk(bytes, relayInput);
                return;
            }

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
            if(!WindowsClipboardReader.TryGetText(out string clipboardText) || clipboardText.Length == 0)
            {
                return false;
            }

            PasteRewriteResult rewriteResult = _pastedImagePathService.RewritePaste(clipboardText, _session, _hostWorkingDirectory);
            string forwardedText = rewriteResult.Rewritten ? rewriteResult.Text : clipboardText;
            WriteForwardedBytes(relayInput, Encoding.UTF8.GetBytes(forwardedText));
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

            PasteRewriteResult rewriteResult = _pastedImagePathService.RewritePaste(inputText, _session, _hostWorkingDirectory);
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

    private static class WindowsClipboardReader
    {
        private const uint CF_UNICODETEXT = 13;

        public static uint GetSequenceNumber()
            => !OperatingSystem.IsWindows() ? 0 : GetClipboardSequenceNumber();

        public static bool TryGetText(out string clipboardText)
        {
            clipboardText = "";
            if(!OperatingSystem.IsWindows() || !IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
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

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);
    }

    private sealed class UnixTerminalModeScope(string savedState) : IDisposable
    {
        private static readonly byte[] _bracketedPasteDisableSequence = "\u001b[?2004l"u8.ToArray();
        private static readonly Lock _sync = new();
        private static string? _activeState;
        private readonly string _savedState = savedState;
        private bool _disposed;

        public static bool TryEnter(out UnixTerminalModeScope? scope)
        {
            scope = null;

            var readStateResult = ProcessRunner.RunAsync("stty", ["-g"]).GetAwaiter().GetResult();
            if(!readStateResult.Success || String.IsNullOrWhiteSpace(readStateResult.StdOut))
            {
                return false;
            }

            string savedState = readStateResult.StdOut.Trim();
            var rawModeResult = ProcessRunner.RunAsync("stty", ["raw", "-echo"]).GetAwaiter().GetResult();
            if(!rawModeResult.Success)
            {
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
                using Stream standardOutput = Console.OpenStandardOutput();
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
