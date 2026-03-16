using System.CommandLine;

namespace OpencodeWrap.Cli.Data;

internal sealed class ExportCliCommand : Command
{
    private const string DEFAULT_EXPORT_ARCHIVE_NAME = "export.ocw";
    private readonly VolumeStateService _volumeService;

    public ExportCliCommand(VolumeStateService volumeService)
        : base("export", "Export state into a ZIP archive.")
    {
        _volumeService = volumeService;

        var exportArchiveArgument = new Argument<string>("archive-path")
        {
            Description = "Path to the output ZIP archive."
        };

        Add(exportArchiveArgument);

        SetAction(async parseResult =>
        {
            string archivePath = parseResult.GetRequiredValue(exportArchiveArgument);
            return await ExecuteAsync(archivePath);
        });
    }

    private async Task<int> ExecuteAsync(string archivePath)
    {
        if (!await AppIO.RunWithLoadingStateAsync("Checking Docker volume...", _volumeService.EnsureVolumeReadyAsync))
        {
            return 1;
        }

        string destinationArchive = ResolveDestinationArchivePath(archivePath);
        string tempRoot = Path.Combine(Path.GetTempPath(), $"opencode-wrap-export-{Guid.NewGuid():N}");
        string temporaryArchive = Path.Combine(tempRoot, DEFAULT_EXPORT_ARCHIVE_NAME);

        try
        {
            Directory.CreateDirectory(tempRoot);

            if (!await AppIO.RunWithLoadingStateAsync("Creating archive from volume...", () => _volumeService.ExportVolumeToHostArchiveAsync(temporaryArchive)))
            {
                return 1;
            }

            if (!AppIO.RunWithLoadingState("Writing archive...", () => PersistArchive(temporaryArchive, destinationArchive)))
            {
                return 1;
            }

            AppIO.WriteSuccess($"export complete to '{destinationArchive}'.");
            return 0;
        }
        finally
        {
            AppIO.TryDeleteDirectory(tempRoot);
        }
    }

    private static string ResolveDestinationArchivePath(string archivePath)
    {
        string resolvedPath = Path.GetFullPath(archivePath);
        return Directory.Exists(resolvedPath) || Path.EndsInDirectorySeparator(archivePath)
            ? Path.Combine(resolvedPath, DEFAULT_EXPORT_ARCHIVE_NAME)
            : resolvedPath;
    }

    private static bool PersistArchive(string sourceArchive, string destinationArchive)
    {
        string destinationDirectory = Path.GetDirectoryName(destinationArchive) ?? "";
        if (String.IsNullOrWhiteSpace(destinationDirectory))
        {
            AppIO.WriteError($"Invalid export destination '{destinationArchive}'.");
            return false;
        }

        if (!File.Exists(sourceArchive))
        {
            AppIO.WriteError($"Export archive was not created at '{sourceArchive}'.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            File.Copy(sourceArchive, destinationArchive, overwrite: true);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppIO.WriteError($"Access denied while writing archive '{destinationArchive}': {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            AppIO.WriteError($"Failed to write archive '{destinationArchive}': {ex.Message}");
            return false;
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to finalize archive '{destinationArchive}': {ex.Message}");
            return false;
        }
    }
}
