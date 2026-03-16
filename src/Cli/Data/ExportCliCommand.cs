using System.CommandLine;
using System.IO.Compression;

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
        string? destinationDirectory = Path.GetDirectoryName(destinationArchive);
        if (!String.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), $"opencode-wrap-export-{Guid.NewGuid():N}");

        try
        {
            string destinationShare = Path.Combine(tempRoot, ".local", "share", "opencode");
            string destinationState = Path.Combine(tempRoot, ".local", "state", "opencode");
            Directory.CreateDirectory(destinationShare);
            Directory.CreateDirectory(destinationState);

            if (!await AppIO.RunWithLoadingStateAsync("Exporting state from volume...", () => ExportStateToDirectoryAsync(destinationShare, destinationState)))
            {
                return 1;
            }

            if (File.Exists(destinationArchive))
            {
                File.Delete(destinationArchive);
            }

            if (!AppIO.RunWithLoadingState("Writing archive...", () => CreateArchive(tempRoot, destinationArchive)))
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

    private async Task<bool> ExportStateToDirectoryAsync(string destinationShare, string destinationState)
        => await _volumeService.ExportVolumeSubdirectoryToHostDirectoryAsync(OpencodeWrapConstants.VOLUME_SHARE_SUBDIRECTORY, destinationShare)
            && await _volumeService.ExportVolumeSubdirectoryToHostDirectoryAsync(OpencodeWrapConstants.VOLUME_STATE_SUBDIRECTORY, destinationState);

    private static bool CreateArchive(string sourceDirectory, string destinationArchive)
    {
        try
        {
            ZipFile.CreateFromDirectory(sourceDirectory, destinationArchive, CompressionLevel.Optimal, includeBaseDirectory: false);
            return true;
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to create archive '{destinationArchive}': {ex.Message}");
            return false;
        }
    }
}
