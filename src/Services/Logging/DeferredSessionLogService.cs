using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace OpencodeWrap.Services.Logging;

internal sealed partial class DeferredSessionLogService : Singleton
{
    private readonly Lock _sync = new();
    private SessionBuffer? _currentSession;

    public SessionScope BeginSession(LogLevel minimumLevel = LogLevel.Information)
    {
        lock(_sync)
        {
            var previousSession = _currentSession;
            var session = new SessionBuffer(minimumLevel);
            _currentSession = session;
            return new SessionScope(this, previousSession, session);
        }
    }

    public void Write(string category, string message, LogLevel level = LogLevel.Information) => GetCurrentSession()?.Write(category, message, level);

    public void WriteErrorOrConsole(string category, string message)
        => WriteOrConsole(category, message, LogLevel.Error);

    public void WriteWarningOrConsole(string category, string message)
        => WriteOrConsole(category, message, LogLevel.Warning);

    public void WriteErrorDetailsOrConsole(string category, string? detail)
    {
        if(String.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        foreach(string line in detail
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            WriteErrorOrConsole(category, line);
        }
    }

    private void WriteOrConsole(string category, string message, LogLevel level)
    {
        if(GetCurrentSession() is { } session)
        {
            session.Write(category, message, level);
            return;
        }

        AppIO.WriteLog(level, message);
    }

    private SessionBuffer? GetCurrentSession()
    {
        lock(_sync)
        {
            return _currentSession;
        }
    }

    internal sealed class SessionScope : IDisposable
    {
        private readonly DeferredSessionLogService _owner;
        private readonly SessionBuffer? _previousSession;
        private readonly SessionBuffer _session;
        private bool _disposed;

        internal SessionScope(DeferredSessionLogService owner, SessionBuffer? previousSession, SessionBuffer session)
        {
            _owner = owner;
            _previousSession = previousSession;
            _session = session;
        }

        public void FlushToConsole()
            => _session.FlushToConsole();

        internal IReadOnlyList<SessionBuffer.SessionLogEntry> GetEntriesSnapshot()
            => _session.GetEntriesSnapshot();

        public void Dispose()
        {
            if(_disposed)
            {
                return;
            }

            _disposed = true;

            lock(_owner._sync)
            {
                _owner._currentSession = _previousSession;
            }
        }
    }

    internal sealed class SessionBuffer(LogLevel minimumLevel)
    {
        private readonly Lock _sync = new();
        private readonly List<SessionLogEntry> _entries = [];
        private readonly LogLevel _minimumLevel = minimumLevel;

        public void Write(string category, string message, LogLevel level)
        {
            if(level < _minimumLevel)
            {
                return;
            }

            try
            {
                lock(_sync)
                {
                    _entries.Add(new SessionLogEntry(level, DateTime.UtcNow, category, message));
                }
            }
            catch
            {
                // Best effort deferred logging only.
            }
        }

        public void FlushToConsole()
        {
            SessionLogEntry[] entries;

            lock(_sync)
            {
                if(_entries.Count == 0)
                {
                    return;
                }

                entries = [.. _entries];
                _entries.Clear();
            }

            if(AnsiConsole.Profile.Capabilities.Interactive)
            {
                AnsiConsole.Clear();
            }

            AppIO.WriteHeader("Session Log", includeTrailingBlankLine: false);
            foreach(var entry in entries)
            {
                AppIO.WriteSessionLog(entry.Level, entry.TimestampUtc, entry.Category, entry.Message);
            }
        }

        public IReadOnlyList<SessionLogEntry> GetEntriesSnapshot()
        {
            lock(_sync)
            {
                return [.. _entries];
            }
        }

        internal readonly record struct SessionLogEntry(LogLevel Level, DateTime TimestampUtc, string Category, string Message);
    }
}