using System.CommandLine;

namespace OpencodeWrap.Cli.Profile;

internal sealed class BuildProfileCliCommand : Command
{
    private readonly Argument<string> _nameArgument;
    private readonly ProfileService _profileService;
    private readonly DockerImageService _dockerImageService;

    public BuildProfileCliCommand(ProfileService profileService, DockerImageService dockerImageService)
        : base("build", "Rebuild a profile base Docker image without using Docker cache.")
    {
        _profileService = profileService;
        _dockerImageService = dockerImageService;
        _nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to rebuild."
        };

        Add(_nameArgument);

        SetAction(async parseResult =>
        {
            string name = parseResult.GetRequiredValue(_nameArgument);
            var (success, profile) = await _profileService.TryResolveProfileAsync(name);
            if(!success)
            {
                return 1;
            }

            try
            {
                AppIO.WriteInfo($"Rebuilding base Docker image for profile '{profile.Name}' without cache...");

                var (buildSuccess, imageTag) = await _dockerImageService.TryBuildImageAsync(profile.DockerfilePath, noCache: true);
                if(!buildSuccess)
                {
                    return 1;
                }

                AppIO.WriteSuccess($"Rebuilt base Docker image '{imageTag}' for profile '{profile.Name}'.");
                return 0;
            }
            finally
            {
                if(profile.CleanupDirectoryPath is not null)
                {
                    AppIO.TryDeleteDirectory(profile.CleanupDirectoryPath);
                }
            }
        });
    }
}
