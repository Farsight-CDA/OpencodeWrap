using OpencodeWrap.Services.Runtime.Infrastructure;
using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class RuntimeAgentInstructionsService : Singleton
{
    [Inject]
    private readonly DockerHostService _dockerHostService;

    public string Build(
        string containerWorkDir,
        WorkspaceMountMode workspaceMountMode,
        List<(string HostPath, string ContainerPath)> additionalReadonlyMounts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# OCW Runtime Environment");
        builder.AppendLine();
        builder.AppendLine("- You're running in a disposable Docker container started by OpencodeWrap.");
        builder.AppendLine("- You may clone temporary reference repositories and create scratch files under `/tmp`.");
        builder.AppendLine("- You may freely install tools into writable locations such as `/tmp`");
        builder.AppendLine(_dockerHostService.IsWindows
            ? "- The container runs as root."
            : "- Do not assume root access or that system-wide package installation is available.");

        if(workspaceMountMode == WorkspaceMountMode.None)
        {
            builder.AppendLine("- No workspace directory is mounted for this session.");
        }
        else
        {
            builder.AppendLine($"- The current workspace for this session is `{containerWorkDir}`.");
        }

        if(additionalReadonlyMounts.Count == 0)
        {
            builder.AppendLine("- No additional read-only reference directories are mounted for this session.");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"- Additional reference directories are mounted read-only under `{OpencodeWrapConstants.CONTAINER_RESOURCE_ROOT}`.");
        builder.AppendLine("- Treat those resource directories as reference only if instructed by the user to do so.");
        builder.AppendLine();
        builder.AppendLine("## Current Read-Only Resource Directories");
        builder.AppendLine();
        foreach(var (_, containerPath) in additionalReadonlyMounts)
        {
            builder.AppendLine($"- `{containerPath}`");
        }

        return builder.ToString().TrimEnd();
    }
}
