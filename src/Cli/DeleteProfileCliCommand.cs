using System.CommandLine;

internal sealed class DeleteProfileCliCommand : Command
{
    private readonly ProfileService _profileService;
    private readonly Argument<string> _nameArgument;

    public DeleteProfileCliCommand(ProfileService profileService)
        : base("delete", "Delete a profile and its directory.")
    {
        _profileService = profileService;
        _nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to delete."
        };

        Add(_nameArgument);

        SetAction(async parseResult =>
        {
            string name = parseResult.GetRequiredValue(_nameArgument);
            return await _profileService.TryDeleteProfileAsync(name) ? 0 : 1;
        });
    }
}
