using System.CommandLine;

internal sealed class DataCliCommand : Command
{
    public DataCliCommand(VolumeStateService volumeService)
        : base("data", "Import, export, and manage persisted Opencode state.")
    {
        Add(new ImportArchiveCliCommand(volumeService));
        Add(new ExportCliCommand(volumeService));
        Add(new ImportHostCliCommand(volumeService));
        Add(new ResetVolumeCliCommand(volumeService));
    }
}
