using System.CommandLine;

internal sealed class OpenProfileDirectoryCliCommand : Command
{
    private readonly ProfileService _profileService;
    private readonly DockerHostService _hostService;

    public OpenProfileDirectoryCliCommand(ProfileService profileService, DockerHostService hostService)
        : base("open", "Open $HOME/.opencode-wrap in the file explorer.")
    {
        _profileService = profileService;
        _hostService = hostService;

        SetAction(async _ => await ExecuteAsync());
    }

    private async Task<int> ExecuteAsync()
    {
        if(!await _profileService.TryEnsureInitializedAsync())
        {
            return 1;
        }

        if(!_hostService.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return 1;
        }

        bool opened = await ProcessRunner.TryOpenDirectoryAsync(
            configRoot,
            _hostService.IsWindows,
            onFailurePrefix: $"Failed to open profile directory '{configRoot}'.");

        if(!opened)
        {
            return 1;
        }

        AppIO.WriteInfo($"Opened profile directory: '{configRoot}'.");
        return 0;
    }
}
