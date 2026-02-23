using System.CommandLine;

internal sealed class RunCliCommand : Command
{
    private readonly OpencodeWrapServices _services;
    private readonly Argument<string> _profileArgument;

    public RunCliCommand(OpencodeWrapServices services)
        : base("run", "Run opencode with a specific profile config.")
    {
        _services = services;
        _profileArgument = new Argument<string>("profile")
        {
            Description = "Profile name from a directory under $HOME/.opencode-wrap."
        };

        Add(_profileArgument);

        SetAction(async parseResult =>
        {
            string profile = parseResult.GetRequiredValue(_profileArgument);
            return await OpencodeWrapRootCommand.ExecuteOpencodeAsync(_services, [], requestedProfileName: profile, includeProfileConfig: true);
        });
    }
}
