using System.CommandLine;

internal sealed class ResetVolumeCliCommand : Command
{
    private readonly VolumeStateService _volumeService;

    public ResetVolumeCliCommand(VolumeStateService volumeService)
        : base("reset-volume", "Delete the named Docker volume used for Opencode state.")
    {
        _volumeService = volumeService;

        SetAction(async _ => await ExecuteAsync());
    }

    private async Task<int> ExecuteAsync()
    {
        if(!AppIO.Confirm($"This deletes Docker volume '{OpencodeWrapConstants.XDG_VOLUME_NAME}' and all saved state. Continue?"))
        {
            AppIO.WriteWarning("reset-volume cancelled.");
            return 0;
        }

        var (success, removed) = await AppIO.RunWithLoadingStateAsync("Resetting named volume...", _volumeService.ResetNamedVolumeAsync);
        if(!success)
        {
            return 1;
        }

        if(removed)
        {
            AppIO.WriteSuccess($"removed Docker volume '{OpencodeWrapConstants.XDG_VOLUME_NAME}'.");
        }
        else
        {
            AppIO.WriteInfo($"Docker volume '{OpencodeWrapConstants.XDG_VOLUME_NAME}' was already absent.");
        }

        return 0;
    }
}
