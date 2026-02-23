using System.CommandLine;

internal sealed class ProfileCliCommand : Command
{
    public ProfileCliCommand(ProfileService profileService, DockerImageService imageService)
        : base("profile", "Manage profile definitions.")
    {
        Add(new ListProfilesCliCommand(profileService));
        Add(new AddProfileCliCommand(profileService));
        Add(new DeleteProfileCliCommand(profileService));
        Add(new BuildProfileCliCommand(profileService, imageService));
        Add(new OpenProfileDirectoryCliCommand(profileService));
    }
}
