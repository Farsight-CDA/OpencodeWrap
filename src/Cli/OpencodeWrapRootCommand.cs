using System.CommandLine;

internal sealed class OpencodeWrapRootCommand : RootCommand
{
    private readonly OpencodeLauncherService _launcherService;

    public OpencodeWrapRootCommand(OpencodeWrapServices services)
        : base("Run opencode in Docker and manage persisted Opencode state. Any command not matched to an ocw subcommand is forwarded to opencode in the container.")
    {
        _launcherService = services.Launcher;
        Add(new RunCliCommand(_launcherService));
        Add(new DataCliCommand(services.Volume));
        Add(new ProfileCliCommand(services.Host));
    }

    public Task<int> ExecuteOpencodeAsync(IReadOnlyList<string> opencodeArgs, string? requestedProfileName, bool includeProfileConfig, bool disableWorkspaceMount = false)
        => _launcherService.ExecuteAsync(opencodeArgs, requestedProfileName, includeProfileConfig, disableWorkspaceMount);
}
