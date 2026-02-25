using System.CommandLine;

namespace OpencodeWrap.Cli.Profile;

internal sealed class ProfileCliCommand : Command
{
    public ProfileCliCommand(ListProfilesCliCommand listProfilesCliCommand, AddProfileCliCommand addProfileCliCommand, DeleteProfileCliCommand deleteProfileCliCommand, BuildProfileCliCommand buildProfileCliCommand, OpenProfileDirectoryCliCommand openProfileDirectoryCliCommand)
        : base("profile", "Manage profile definitions.")
    {
        Add(listProfilesCliCommand);
        Add(addProfileCliCommand);
        Add(deleteProfileCliCommand);
        Add(buildProfileCliCommand);
        Add(openProfileDirectoryCliCommand);
    }
}
