using System.CommandLine;

internal sealed class AddProfileCliCommand : Command
{
    private readonly ProfileService _profileService;
    private readonly Argument<string> _nameArgument;

    public AddProfileCliCommand(ProfileService profileService)
        : base("add", "Add a new profile with a starter Dockerfile.")
    {
        _profileService = profileService;
        _nameArgument = new Argument<string>("name")
        {
            Description = "New profile name."
        };

        Add(_nameArgument);

        SetAction(async parseResult =>
        {
            string name = parseResult.GetRequiredValue(_nameArgument);
            return await _profileService.TryAddProfileAsync(name) ? 0 : 1;
        });
    }
}
