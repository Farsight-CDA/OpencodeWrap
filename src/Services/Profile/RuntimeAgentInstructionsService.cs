using OpencodeWrap.Services.Runtime.Core;
using System.Text;

namespace OpencodeWrap.Services.Profile;

internal sealed partial class RuntimeAgentInstructionsService : Singleton
{
    [Inject]
    private readonly DockerHostService _dockerHostService;

    public string Build(
        string containerWorkDir,
        WorkspaceMountMode workspaceMountMode,
        IReadOnlyList<ContainerMount> containerMounts)
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

        if(containerMounts.Count == 0)
        {
            builder.AppendLine("- No additional directories or Docker named volumes are mounted for this session.");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine();
        builder.AppendLine("## Current Additional Mounts");
        builder.AppendLine();
        foreach(var mount in containerMounts)
        {
            string sourceTypeLabel = mount.SourceType is ContainerMountSourceType.Directory
                ? "directory"
                : "Docker named volume";
            string accessModeLabel = mount.AccessMode is ContainerMountAccessMode.ReadOnly
                ? "read-only"
                : "read-write";
            builder.AppendLine($"- {sourceTypeLabel} `{mount.Source}` -> `{mount.ContainerPath}` ({accessModeLabel})");
        }

        return builder.ToString().TrimEnd();
    }
}
