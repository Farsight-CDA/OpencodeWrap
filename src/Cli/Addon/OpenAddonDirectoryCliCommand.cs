using System.CommandLine;
using System.Diagnostics;

namespace OpencodeWrap.Cli.Addon;

internal sealed class OpenAddonDirectoryCliCommand : Command
{
    private readonly DockerHostService _hostService;
    private readonly SessionAddonService _sessionAddonService;

    public OpenAddonDirectoryCliCommand(DockerHostService hostService, SessionAddonService sessionAddonService)
        : base("open", "Open $HOME/.opencode-wrap/addons in the file explorer.")
    {
        _hostService = hostService;
        _sessionAddonService = sessionAddonService;

        SetAction(async _ => await ExecuteAsync());
    }

    private async Task<int> ExecuteAsync()
    {
        if(!_sessionAddonService.TryLoadCatalog(out var catalog))
        {
            return 1;
        }

        bool opened = await TryOpenDirectoryAsync(catalog.AddonsRoot);
        if(!opened)
        {
            return 1;
        }

        AppIO.WriteInfo($"Opened addon directory: '{catalog.AddonsRoot}'.");
        return 0;
    }

    private async Task<bool> TryOpenDirectoryAsync(string directoryPath)
    {
        if(_hostService.IsWindows)
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

        string openCommand = _hostService.IsMacOS ? "open" : "xdg-open";
        var openResult = await ProcessRunner.RunAsync(openCommand, [directoryPath]);
        if(!openResult.Success)
        {
            AppIO.WriteError($"Failed to open addon directory '{directoryPath}'.");
            if(!String.IsNullOrWhiteSpace(openResult.StdErr))
            {
                AppIO.WriteError(openResult.StdErr.Trim());
            }
        }

        return openResult.Success;
    }
}
