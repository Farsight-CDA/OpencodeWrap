using System.CommandLine;

internal sealed class OpenProfileDirectoryCliCommand : Command
{
    private readonly ProfileService _profileService;

    public OpenProfileDirectoryCliCommand(ProfileService profileService)
        : base("open", "Open $HOME/.opencode-wrap in the file explorer.")
    {
        _profileService = profileService;

        SetAction(async _ => await _profileService.TryOpenProfilesDirectoryAsync() ? 0 : 1);
    }
}
