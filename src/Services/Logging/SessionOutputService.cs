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

    public Task<T> RunWithLoadingStateAsync<T>(string category, string statusMessage, Func<Task<T>> action)
    {
        _deferredSessionLogService.Write(category, statusMessage, LogLevel.Information);
        return AppIO.RunWithLoadingStateAsync(statusMessage, action);
    }
}
