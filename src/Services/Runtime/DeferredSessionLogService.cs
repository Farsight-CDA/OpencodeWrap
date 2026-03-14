using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class DeferredSessionLogService : Singleton
{
    private readonly Lock _sync = new();
    private SessionBuffer? _currentSession;

    public SessionScope BeginSession(LogLevel minimumLevel = LogLevel.Information)
    {
        lock(_sync)
        {
            SessionBuffer? previousSession = _currentSession;
            var session = new SessionBuffer(minimumLevel);
            _currentSession = session;
            return new SessionScope(this, previousSession, session);
        }
    }

    public void Write(string category, string message, LogLevel level = LogLevel.Information)
    {
        SessionBuffer? session;

        lock(_sync)
        {
            session = _currentSession;
        }

        session?.Write(category, message, level);
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
                    _entries.Add(new SessionLogEntry(level, $"{DateTime.UtcNow:O} [{category}] {message}"));
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

            AppIO.WriteHeader("Session Log");
            foreach(SessionLogEntry entry in entries)
            {
                AppIO.WriteLog(entry.Level, entry.Text);
            }
        }

        private readonly record struct SessionLogEntry(LogLevel Level, string Text);
    }
}
