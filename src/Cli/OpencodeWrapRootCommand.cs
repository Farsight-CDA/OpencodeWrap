using System.CommandLine;

namespace OpencodeWrap.Cli;

internal sealed class OpencodeWrapRootCommand : RootCommand
{
    private readonly OpencodeLauncherService _launcherService;

    public OpencodeWrapRootCommand(OpencodeLauncherService launcherService, RunCliCommand runCliCommand, UpdateCliCommand updateCliCommand, DataCliCommand dataCliCommand, ProfileCliCommand profileCliCommand)
        : base("Run opencode in Docker and manage persisted Opencode state. Use 'ocw run' for profile-backed sessions; any other top-level command is forwarded directly to opencode in the container.")
    {
        _launcherService = launcherService;
        Add(runCliCommand);
        Add(updateCliCommand);
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
