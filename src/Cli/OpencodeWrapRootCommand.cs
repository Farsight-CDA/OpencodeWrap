using OpencodeWrap.Services.Docker;
using OpencodeWrap.Services.Runtime.Core;
using System.CommandLine;

namespace OpencodeWrap.Cli;

internal sealed class OpencodeWrapRootCommand : RootCommand
{
    private readonly OpencodeLauncherService _launcherService;

    public OpencodeWrapRootCommand(OpencodeLauncherService launcherService, RunCliCommand runCliCommand, UpdateCliCommand updateCliCommand, DataCliCommand dataCliCommand, ProfileCliCommand profileCliCommand, AddonCliCommand addonCliCommand)
        : base("Docker runner for OpenCode. Use 'run' to launch the interactive UI, or manage profiles/addons/data with other commands.")
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
