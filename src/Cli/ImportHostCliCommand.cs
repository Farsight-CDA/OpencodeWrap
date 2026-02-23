using System.CommandLine;

internal sealed class ImportHostCliCommand : Command
{
    private readonly VolumeStateService _volumeService;

    public ImportHostCliCommand(VolumeStateService volumeService)
        : base("import-host", "Import state directly from host home directory.")
    {
        _volumeService = volumeService;

        var importHostForceOption = new Option<bool>("--force", ["-f"])
        {
            Description = "Overwrite existing imported state in the Docker volume."
        };

        Add(importHostForceOption);

        SetAction(async parseResult =>
        {
            bool force = parseResult.GetValue(importHostForceOption);
            return await ExecuteAsync(force);
        });
    }

    private async Task<int> ExecuteAsync(bool force)
    {
        if(!await AppIO.WithStatusAsync("Checking Docker volume...", () => _volumeService.EnsureVolumeReadyAsync()))
        {
            return 1;
        }

        if(!await _volumeService.ValidateImportTargetStateAsync(force))
        {
            return 1;
        }

        string sourceRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(String.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            AppIO.WriteError("Unable to resolve host home directory for import-host.");
            return 1;
        }

        if(!await AppIO.WithStatusAsync("Importing host state into volume...", () => _volumeService.ImportStateFromRootDirectoryAsync(sourceRoot)))
        {
            return 1;
        }

        AppIO.WriteSuccess($"import-host complete from '{sourceRoot}'.");
        return 0;
    }
}
