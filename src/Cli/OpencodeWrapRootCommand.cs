using System.CommandLine;

namespace OpencodeWrap.Cli;

internal sealed class OpencodeWrapRootCommand : RootCommand
{
    private readonly OpencodeLauncherService _launcherService;

    public OpencodeWrapRootCommand(OpencodeLauncherService launcherService, RunCliCommand runCliCommand, DataCliCommand dataCliCommand, ProfileCliCommand profileCliCommand)
        : base("Run opencode in Docker and manage persisted Opencode state. Any command not matched to an ocw subcommand is forwarded to opencode in the container.")
    {
        _launcherService = launcherService;
        Add(runCliCommand);
        Add(dataCliCommand);
        Add(profileCliCommand);
    }

    public Task<int> ExecuteOpencodeAsync(
        IReadOnlyList<string> opencodeArgs,
        string? requestedProfileName,
        bool includeProfileConfig,
        WorkspaceMountMode workspaceMountMode = WorkspaceMountMode.ReadWrite,
        IReadOnlyList<string>? extraReadonlyMountDirs = null)
        => _launcherService.ExecuteAsync(opencodeArgs, requestedProfileName, includeProfileConfig, workspaceMountMode, extraReadonlyMountDirs);
}
