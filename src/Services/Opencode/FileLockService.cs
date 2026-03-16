using Microsoft.Extensions.Logging;

namespace OpencodeWrap.Services.Opencode;

internal sealed partial class FileLockService : Singleton
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(125);

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public async Task<FileLockHandle?> AcquireAsync(string lockFilePath, string category, string resourceDescription)
    {
        try
        {
            string? parentDirectory = Path.GetDirectoryName(lockFilePath);
            if(!String.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(category, $"Failed to prepare lock file '{lockFilePath}': {ex.Message}");
            return null;
        }

        bool waitingLogged = false;
        while(true)
        {
            try
            {
                var stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if(waitingLogged)
                {
                    _deferredSessionLogService.Write(category, $"acquired {resourceDescription} lock", LogLevel.Information);
                }

                return new FileLockHandle(stream);
            }
            catch(IOException)
            {
            }
            catch(UnauthorizedAccessException)
            {
            }
            catch(Exception ex)
            {
                _deferredSessionLogService.WriteErrorOrConsole(category, $"Failed to acquire lock '{lockFilePath}': {ex.Message}");
                return null;
            }

            if(!waitingLogged)
            {
                _deferredSessionLogService.Write(category, $"waiting for {resourceDescription} lock", LogLevel.Information);
                waitingLogged = true;
            }

            await Task.Delay(_retryDelay);
        }
    }

    internal sealed class FileLockHandle(FileStream stream) : IDisposable, IAsyncDisposable
    {
        private readonly FileStream _stream = stream;
        private bool _disposed;

        public void Dispose()
        {
            if(_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
