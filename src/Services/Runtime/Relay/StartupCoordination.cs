using System.Diagnostics;
using System.Text;

namespace OpencodeWrap.Services.Runtime.Relay;

internal static class StartupCoordination
{
    public sealed class InputGate
    {
        private int _ready;

        public bool IsOpen()
            => Volatile.Read(ref _ready) != 0;

        public bool CanForwardInput()
            => IsOpen();

        public void Open()
            => Interlocked.Exchange(ref _ready, 1);

        public OutputFilter CreateOutputFilter(ProgressTracker tracker)
            => new(this, tracker);
    }

    public sealed class OutputFilter(InputGate inputGate, ProgressTracker tracker)
    {
        private static readonly byte[] _markerBytes = Encoding.ASCII.GetBytes(InteractiveDockerRunnerService.STARTUP_READY_MARKER);

        private readonly InputGate _inputGate = inputGate;
        private readonly ProgressTracker _tracker = tracker;
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

    public sealed class ProgressTracker
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

    public sealed class FailureTranscript
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
                .Replace(InteractiveDockerRunnerService.STARTUP_READY_MARKER, String.Empty, StringComparison.Ordinal)
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
}
