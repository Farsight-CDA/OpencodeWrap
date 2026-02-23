using System.CommandLine;
using System.IO.Compression;

internal sealed class ImportArchiveCliCommand : Command
{
    private readonly VolumeStateService _volumeService;

    public ImportArchiveCliCommand(VolumeStateService volumeService)
        : base("import", "Import state from a ZIP archive.")
    {
        _volumeService = volumeService;

        var importArchiveArgument = new Argument<string>("archive-path")
        {
            Description = "Path to the input ZIP archive."
        };
        var importForceOption = new Option<bool>("--force", ["-f"])
        {
            Description = "Overwrite existing imported state in the Docker volume."
        };

        Add(importArchiveArgument);
        Add(importForceOption);

        SetAction(async parseResult =>
        {
            string archivePath = parseResult.GetRequiredValue(importArchiveArgument);
            bool force = parseResult.GetValue(importForceOption);
            return await ExecuteAsync(archivePath, force);
        });
    }

    private async Task<int> ExecuteAsync(string archivePath, bool force)
    {
        if(!await AppIO.WithStatusAsync("Checking Docker volume...", _volumeService.EnsureVolumeReadyAsync))
        {
            return 1;
        }

        if(!await VolumeStateService.ValidateImportTargetStateAsync(force))
        {
            return 1;
        }

        string sourceArchive = Path.GetFullPath(archivePath);
        if(!File.Exists(sourceArchive))
        {
            AppIO.WriteError($"Import archive not found: '{sourceArchive}'.");
            return 1;
        }

        string extractRoot = Path.Combine(Path.GetTempPath(), $"opencode-wrap-import-{Guid.NewGuid():N}");

        try
        {
            if(!await AppIO.WithStatusAsync("Extracting archive...", () => ExtractArchiveAsync(sourceArchive, extractRoot)))
            {
                return 1;
            }

            if(!await AppIO.WithStatusAsync("Importing state into volume...", () => _volumeService.ImportStateFromRootDirectoryAsync(extractRoot)))
            {
                return 1;
            }

            AppIO.WriteSuccess($"import complete from '{sourceArchive}'.");
            return 0;
        }
        finally
        {
            await AppIO.TryDeleteDirectoryAsync(extractRoot);
        }
    }

    private static async Task<bool> ExtractArchiveAsync(string sourceArchive, string extractRoot)
    {
        Directory.CreateDirectory(extractRoot);

        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(sourceArchive, extractRoot));
            return true;
        }
        catch(InvalidDataException ex)
        {
            AppIO.WriteError($"Invalid ZIP archive '{sourceArchive}': {ex.Message}");
            return false;
        }
    }
}
