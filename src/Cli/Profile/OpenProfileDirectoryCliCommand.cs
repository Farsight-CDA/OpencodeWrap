using System.CommandLine;
using System.Diagnostics;

namespace OpencodeWrap.Cli.Profile;

internal sealed class OpenProfileDirectoryCliCommand : Command
{
    private readonly ProfileService _profileService;

    public OpenProfileDirectoryCliCommand(ProfileService profileService)
        : base("open", "Open %USERPROFILE%\\.opencode-wrap\\profiles in File Explorer.")
    {
        _profileService = profileService;

        SetAction(_ => Execute());
    }

    private int Execute()
    {
        var (success, catalog) = _profileService.TryLoadProfileCatalog();
        if(!success)
        {
            return 1;
        }

        bool opened = TryOpenDirectory(catalog.ProfilesRoot);

        if(!opened)
        {
            return 1;
        }

        AppIO.WriteInfo($"Opened profile directory: '{catalog.ProfilesRoot}'.");
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
            AppIO.WriteError($"Failed to open profile directory '{directoryPath}'.");
            AppIO.WriteError(ex.Message);
            return false;
        }
    }
}
