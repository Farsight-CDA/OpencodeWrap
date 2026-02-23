using System.CommandLine;

internal sealed class BuildProfileCliCommand : Command
{
    private readonly ProfileService _profileService;
    private readonly DockerImageService _imageService;
    private readonly Argument<string> _nameArgument;

    public BuildProfileCliCommand(ProfileService profileService, DockerImageService imageService)
        : base("build", "Rebuild a profile Docker image without using Docker cache.")
    {
        _profileService = profileService;
        _imageService = imageService;
        _nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to rebuild."
        };

        Add(_nameArgument);

        SetAction(async parseResult =>
        {
            string name = parseResult.GetRequiredValue(_nameArgument);
            var profileResolution = await _profileService.TryResolveProfileAsync(name);
            if(!profileResolution.Success)
            {
                return 1;
            }

            ResolvedProfile profile = profileResolution.Profile;
            AppIO.WriteInfo($"Rebuilding Docker image for profile '{profile.Name}' without cache...");

            var buildResult = await _imageService.TryBuildImageAsync(profile.DockerfilePath, noCache: true);
            if(!buildResult.Success)
            {
                return 1;
            }

            AppIO.WriteSuccess($"Rebuilt Docker image '{buildResult.ImageTag}' for profile '{profile.Name}'.");
            return 0;
        });
    }
}
