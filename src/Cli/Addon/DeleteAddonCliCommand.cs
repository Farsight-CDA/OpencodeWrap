using Spectre.Console;
using System.CommandLine;

namespace OpencodeWrap.Cli.Addon;

internal sealed class DeleteAddonCliCommand : Command
{
    private readonly Option<bool> _yesOption;
    private readonly SessionAddonService _sessionAddonService;

    public DeleteAddonCliCommand(SessionAddonService sessionAddonService)
        : base("delete", "Interactively delete a session addon directory.")
    {
        _sessionAddonService = sessionAddonService;
        _yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Skip confirmation prompt."
        };

        Add(_yesOption);

        SetAction(async parseResult =>
        {
            bool skipConfirmation = parseResult.GetValue(_yesOption);
            return await ExecuteAsync(skipConfirmation);
        });
    }

    private async Task<int> ExecuteAsync(bool skipConfirmation)
    {
        if(!_sessionAddonService.TryLoadCatalog(out var catalog))
        {
            return 1;
        }

        string? selectedAddonName = ResolveAddonName(catalog);
        if(String.IsNullOrWhiteSpace(selectedAddonName) || !catalog.Addons.TryGetValue(selectedAddonName, out var addonEntry))
        {
            return 1;
        }

        string normalizedName = selectedAddonName.Trim();
        if(_sessionAddonService.TryGetAddonNameValidationError(normalizedName) is { } validationError)
        {
            AppIO.WriteError(validationError);
            return 1;
        }

        bool hasOverrideDirectory = !String.IsNullOrWhiteSpace(addonEntry.DirectoryPath);
        bool isBuiltIn = addonEntry.BuiltInAddon is not null;

        if(!_sessionAddonService.TryResolveAddonDirectoryPath(catalog.AddonsRoot, normalizedName, out string addonDirectoryPath))
        {
            AppIO.WriteError($"Session addon '{normalizedName}' directory resolves outside '{catalog.AddonsRoot}'.");
            return 1;
        }

        string confirmMessage = isBuiltIn
            ? $"Delete override addon '{normalizedName}' and remove '{addonDirectoryPath}'? This falls back to the built-in addon."
            : $"Delete session addon '{normalizedName}' and remove '{addonDirectoryPath}'?";

        if(!skipConfirmation && !AppIO.Confirm(confirmMessage))
        {
            AppIO.WriteWarning("addon delete cancelled.");
            return 0;
        }

        try
        {
            bool deleted = AppIO.RunWithLoadingState($"Deleting session addon '{normalizedName}'...", () =>
            {
                if(Directory.Exists(addonDirectoryPath))
                {
                    Directory.Delete(addonDirectoryPath, recursive: true);
                }

                return true;
            });

            if(!deleted)
            {
                return 1;
            }
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to delete session addon '{normalizedName}': {ex.Message}");
            return 1;
        }

        if(isBuiltIn)
        {
            AppIO.WriteSuccess($"Deleted override addon '{normalizedName}'. Now using built-in addon content.");
            return 0;
        }

        AppIO.WriteSuccess($"Deleted session addon '{normalizedName}'.");
        return 0;
    }

    private string? ResolveAddonName(SessionAddonCatalog catalog)
    {
        if(!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AppIO.WriteError("Interactive addon selection is unavailable. Run this command in an interactive terminal.");
            return null;
        }

        List<AddonChoice> addonChoices = [.. catalog.Addons.Values
            .Where(addon => !String.IsNullOrWhiteSpace(addon.DirectoryPath))
            .OrderBy(addon => addon.Name, StringComparer.OrdinalIgnoreCase)
            .Select(addon => new AddonChoice(addon.Name, addon.BuiltInAddon is not null))];

        if(addonChoices.Count == 0)
        {
            AppIO.WriteError("No deletable session addons found. Use 'ocw addon add <name>' first.");
            return null;
        }

        var selectedAddon = AnsiConsole.Prompt(
            new SelectionPrompt<AddonChoice>()
                .Title("[bold red]🗑 Select a session addon to delete[/]")
                .PageSize(Math.Min(addonChoices.Count, 10))
                .UseConverter(choice => choice.IsBuiltIn ? $"{choice.Name} (built-in override)" : choice.Name)
                .AddChoices(addonChoices));

        return selectedAddon.Name;
    }

    private sealed record AddonChoice(string Name, bool IsBuiltIn);
}
