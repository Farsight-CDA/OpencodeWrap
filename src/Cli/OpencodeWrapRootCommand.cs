using OpencodeWrap.Services.Docker;
using OpencodeWrap.Services.Runtime.Core;
using System.CommandLine;

namespace OpencodeWrap.Cli;

internal sealed class OpencodeWrapRootCommand : RootCommand
{
    private readonly OpencodeLauncherService _launcherService;

    public OpencodeWrapRootCommand(OpencodeLauncherService launcherService, RunCliCommand runCliCommand, UpdateCliCommand updateCliCommand, DataCliCommand dataCliCommand, ProfileCliCommand profileCliCommand, AddonCliCommand addonCliCommand)
        : base("Run OpenCode in Docker with OCW-managed host and runtime OpenCode installs. `ocw run` launches `opencode serve` in Docker and then opens the selected TUI, web, or desktop client; other top-level commands still run directly in the container.")
    {
        _launcherService = launcherService;
        Add(runCliCommand);
        Add(updateCliCommand);
        Add(dataCliCommand);
        Add(profileCliCommand);
        Add(addonCliCommand);
    }

    public Task<int> ExecuteOpencodeAsync(
        IReadOnlyList<string> opencodeArgs,
        string? requestedProfileName,
        bool includeProfileConfig,
        WorkspaceMountMode workspaceMountMode = WorkspaceMountMode.ReadWrite)
        => _launcherService.ExecuteAsync(opencodeArgs, requestedProfileName, includeProfileConfig, OpencodeRuntimeMode.AttachedContainer, workspaceMountMode: workspaceMountMode);
}
