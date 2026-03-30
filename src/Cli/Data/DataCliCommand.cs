using System.CommandLine;

namespace OpencodeWrap.Cli.Data;

internal sealed class DataCliCommand : Command
{
    public DataCliCommand(ImportArchiveCliCommand importArchiveCliCommand, ExportCliCommand exportCliCommand, ImportHostCliCommand importHostCliCommand, ResetVolumeCliCommand resetVolumeCliCommand)
        : base("data", "Import, export, or reset OpenCode state.")
    {
        Add(importArchiveCliCommand);
        Add(exportCliCommand);
        Add(importHostCliCommand);
        Add(resetVolumeCliCommand);
    }
}
