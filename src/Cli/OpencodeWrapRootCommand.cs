using OpencodeWrap.Services.Runtime.Infrastructure;
using System.CommandLine;

namespace OpencodeWrap.Cli;

internal sealed class OpencodeWrapRootCommand : RootCommand
{
    private readonly OpencodeLauncherService _launcherService;

    public OpencodeWrapRootCommand(OpencodeLauncherService launcherService, RunCliCommand runCliCommand, UpdateCliCommand updateCliCommand, DataCliCommand dataCliCommand, ProfileCliCommand profileCliCommand)
        : base("Run OpenCode in Docker with OCW-managed host and runtime OpenCode installs. `ocw run` launches `opencode serve` in Docker and then opens the selected TUI, web, or desktop client; other top-level commands still run directly in the container.")
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
        => _launcherService.ExecuteAsync(opencodeArgs, requestedProfileName, includeProfileConfig, OpencodeRuntimeMode.AttachedContainer, workspaceMountMode: workspaceMountMode, extraReadonlyMountDirs: extraReadonlyMountDirs);
}
