using System.CommandLine;

internal sealed class BuildProfileCliCommand : Command
{
    private readonly Argument<string> _nameArgument;

    public BuildProfileCliCommand()
        : base("build", "Rebuild a profile Docker image without using Docker cache.")
    {
        _nameArgument = new Argument<string>("name")
        {
            Description = "Profile name to rebuild."
        };

        Add(_nameArgument);

        SetAction(async parseResult =>
        {
            string name = parseResult.GetRequiredValue(_nameArgument);
            var (success, profile) = await ProfileService.TryResolveProfileAsync(name);
            if(!success)
            {
                return 1;
            }

            try
            {
                AppIO.WriteInfo($"Rebuilding Docker image for profile '{profile.Name}' without cache...");

                var (buildSuccess, imageTag) = await DockerImageService.TryBuildImageAsync(profile.DockerfilePath, noCache: true);
                if(!buildSuccess)
                {
                    return 1;
                }

                AppIO.WriteSuccess($"Rebuilt Docker image '{imageTag}' for profile '{profile.Name}'.");
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
