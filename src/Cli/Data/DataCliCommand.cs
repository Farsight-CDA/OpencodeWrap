using System.CommandLine;

internal sealed class DataCliCommand : Command
{
    public DataCliCommand(ImportArchiveCliCommand importArchiveCliCommand, ExportCliCommand exportCliCommand, ImportHostCliCommand importHostCliCommand, ResetVolumeCliCommand resetVolumeCliCommand)
        : base("data", "Import, export, and manage persisted Opencode state.")
    {
        Add(importArchiveCliCommand);
        Add(exportCliCommand);
        Add(importHostCliCommand);
        Add(resetVolumeCliCommand);
    }
}
