using System.CommandLine;
using System.Diagnostics;

namespace OpencodeWrap.Cli.Profile;

internal sealed class OpenProfileDirectoryCliCommand : Command
{
    private readonly DockerHostService _hostService;

    public OpenProfileDirectoryCliCommand(DockerHostService hostService)
        : base("open", "Open $HOME/.opencode-wrap in the file explorer.")
    {
        _hostService = hostService;

        SetAction(async _ => await ExecuteAsync());
    }

    private async Task<int> ExecuteAsync()
    {
        if(!ProfileService.TryEnsureInitialized())
        {
            return 1;
        }

        if(!DockerHostService.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return 1;
        }

        bool opened = await TryOpenDirectoryAsync(configRoot);

        if(!opened)
        {
            return 1;
        }

        AppIO.WriteInfo($"Opened profile directory: '{configRoot}'.");
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
                AppIO.WriteError($"Failed to open profile directory '{directoryPath}'.");
                AppIO.WriteError(ex.Message);
                return false;
            }
        }

        var openResult = await ProcessRunner.RunAsync("xdg-open", [directoryPath]);
        if(!openResult.Success)
        {
            AppIO.WriteError($"Failed to open profile directory '{directoryPath}'.");
            if(!String.IsNullOrWhiteSpace(openResult.StdErr))
            {
                AppIO.WriteError(openResult.StdErr.Trim());
            }
        }

        return openResult.Success;
    }
}
