using System.CommandLine;
using System.Diagnostics;

namespace OpencodeWrap.Cli.Addon;

internal sealed class OpenAddonDirectoryCliCommand : Command
{
    private readonly SessionAddonService _sessionAddonService;

    public OpenAddonDirectoryCliCommand(SessionAddonService sessionAddonService)
        : base("open", "Open %USERPROFILE%\\.opencode-wrap\\addons in File Explorer.")
    {
        _sessionAddonService = sessionAddonService;

        SetAction(_ => Execute());
    }

    private int Execute()
    {
        if(!_sessionAddonService.TryLoadCatalog(out var catalog))
        {
            return 1;
        }

        bool opened = TryOpenDirectory(catalog.AddonsRoot);
        if(!opened)
        {
            return 1;
        }

        AppIO.WriteInfo($"Opened addon directory: '{catalog.AddonsRoot}'.");
        return 0;
    }

    private static bool TryOpenDirectory(string directoryPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = directoryPath,
                UseShellExecute = true
            });

            return true;
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to open addon directory '{directoryPath}'.");
            AppIO.WriteError(ex.Message);
            return false;
        }
    }
}
