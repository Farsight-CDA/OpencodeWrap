using System.CommandLine;

internal sealed class ProfileCliCommand : Command
{
    public ProfileCliCommand(DockerHostService hostService)
        : base("profile", "Manage profile definitions.")
    {
        Add(new ListProfilesCliCommand());
        Add(new AddProfileCliCommand());
        Add(new DeleteProfileCliCommand());
        Add(new BuildProfileCliCommand());
        Add(new OpenProfileDirectoryCliCommand(hostService));
    }
}
