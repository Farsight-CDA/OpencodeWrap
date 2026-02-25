using System.CommandLine;

internal sealed class RunCliCommand : Command
{
    private readonly OpencodeLauncherService _launcherService;
    private readonly Argument<string> _profileArgument;
    private readonly Option<bool> _noMountOption;

    public RunCliCommand(OpencodeLauncherService launcherService)
        : base("run", "Run opencode with a specific profile config.")
    {
        _launcherService = launcherService;
        _profileArgument = new Argument<string>("profile")
        {
            Description = "Profile name from a directory under $HOME/.opencode-wrap."
        };
        _noMountOption = new Option<bool>("--no-mount")
        {
            Description = "Do not mount the current workspace; run from the container home directory."
        };

        Add(_profileArgument);
        Add(_noMountOption);

        SetAction(async parseResult =>
        {
            string profile = parseResult.GetRequiredValue(_profileArgument);
            bool noMount = parseResult.GetValue(_noMountOption);
            return await _launcherService.ExecuteAsync([], requestedProfileName: profile, includeProfileConfig: true, disableWorkspaceMount: noMount);
        });
    }
}
