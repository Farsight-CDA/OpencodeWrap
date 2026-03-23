using Microsoft.Extensions.Logging;

namespace OpencodeWrap.Services.Logging;

internal sealed partial class SessionOutputService : Singleton
{
    [Inject] private readonly DeferredSessionLogService _deferredSessionLogService;

    public void WriteInfo(string category, string message)
    {
        _deferredSessionLogService.Write(category, message, LogLevel.Information);
        AppIO.WriteInfo(message);
    }

    public void WriteSuccess(string category, string message)
    {
        _deferredSessionLogService.Write(category, message, LogLevel.Information);
        AppIO.WriteSuccess(message);
    }

    public void WriteWarning(string category, string message)
    {
        _deferredSessionLogService.Write(category, message, LogLevel.Warning);
        AppIO.WriteWarning(message);
    }

    public void WriteError(string category, string message)
    {
        _deferredSessionLogService.Write(category, message, LogLevel.Error);
        AppIO.WriteError(message);
    }

    public void WriteLog(string category, LogLevel level, string message)
    {
        _deferredSessionLogService.Write(category, message, level);
        AppIO.WriteLog(level, message);
    }

    public T RunWithLoadingState<T>(string category, string statusMessage, Func<T> action, LogLevel level = LogLevel.Information)
    {
        _deferredSessionLogService.Write(category, statusMessage, level);
        return AppIO.RunWithLoadingState(statusMessage, action);
    }

    public Task<T> RunWithLoadingStateAsync<T>(string category, string statusMessage, Func<Task<T>> action, LogLevel level = LogLevel.Information)
    {
        _deferredSessionLogService.Write(category, statusMessage, level);
        return AppIO.RunWithLoadingStateAsync(statusMessage, action);
    }
}