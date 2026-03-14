namespace OpencodeWrap.Services.Runtime;

internal sealed partial class DeferredSessionLogService : Singleton
{
    private readonly Lock _sync = new();
    private SessionBuffer? _currentSession;

    public enum Importance
    {
        Verbose,
        Significant
    }

    public SessionScope BeginSession()
    {
        lock(_sync)
        {
            SessionBuffer? previousSession = _currentSession;
            var session = new SessionBuffer();
            _currentSession = session;
            return new SessionScope(this, previousSession, session);
        }
    }

    public void Write(string category, string message, Importance importance = Importance.Verbose)
    {
        SessionBuffer? session;

        lock(_sync)
        {
            session = _currentSession;
        }

        if(session is null)
        {
            return;
        }

        session.Write(category, message, importance);
    }

    internal sealed class SessionScope : IDisposable
    {
        private readonly SessionBuffer? _previousSession;
        private readonly SessionBuffer _session;
        private readonly DeferredSessionLogService _owner;
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

    internal sealed class SessionBuffer
    {
        private readonly Lock _sync = new();
        private readonly List<SessionLogEntry> _entries = [];
        private bool _hasSignificantEntries;

        public void Write(string category, string message, Importance importance)
        {
            try
            {
                lock(_sync)
                {
                    _entries.Add(new SessionLogEntry($"{DateTime.UtcNow:O} [{category}] {message}"));
                    _hasSignificantEntries |= importance == Importance.Significant;
                }
            }
            catch
            {
                // Deferred logging must remain best effort.
            }
        }

        public void FlushToConsole()
        {
            SessionLogEntry[] entries;

            lock(_sync)
            {
                if(_entries.Count == 0 || !_hasSignificantEntries)
                {
                    _entries.Clear();
                    _hasSignificantEntries = false;
                    return;
                }

                entries = [.. _entries];
                _entries.Clear();
                _hasSignificantEntries = false;
            }

            try
            {
                Console.WriteLine();
                Console.WriteLine("ocw session log:");
                foreach(SessionLogEntry entry in entries)
                {
                    Console.WriteLine(entry.Text);
                }
            }
            catch
            {
                foreach(SessionLogEntry entry in entries)
                {
                    AppIO.WriteInfo(entry.Text);
                }
            }
        }

        private readonly record struct SessionLogEntry(string Text);
    }
}
