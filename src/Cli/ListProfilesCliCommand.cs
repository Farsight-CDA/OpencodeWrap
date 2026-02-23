using System.CommandLine;

internal sealed class ListProfilesCliCommand : Command
{
    private readonly ProfileService _profileService;

    public ListProfilesCliCommand(ProfileService profileService)
        : base("list", "List all configured profiles.")
    {
        _profileService = profileService;

        SetAction(async _ => await _profileService.TryListProfilesAsync() ? 0 : 1);
    }
}
