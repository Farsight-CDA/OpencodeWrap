using Spectre.Console;
using System.CommandLine;

namespace OpencodeWrap.Cli.Profile;

internal sealed class DeleteProfileCliCommand : Command
{
    private readonly Option<string?> _profileOption;
    private readonly Option<bool> _yesOption;
    private readonly ProfileService _profileService;
    private readonly BuiltInProfileTemplateService _builtInProfileTemplateService;

    public DeleteProfileCliCommand(ProfileService profileService, BuiltInProfileTemplateService builtInProfileTemplateService)
        : base("delete", "Delete a profile and its directory.")
    {
        _profileService = profileService;
        _builtInProfileTemplateService = builtInProfileTemplateService;
        _profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Profile name from a directory under $HOME/.opencode-wrap/profiles."
        };
        _yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Skip confirmation prompt."
        };

        Add(_profileOption);
        Add(_yesOption);

        SetAction(async parseResult =>
        {
            string? profileName = parseResult.GetValue(_profileOption);
            bool skipConfirmation = parseResult.GetValue(_yesOption);
            return await ExecuteAsync(profileName, skipConfirmation);
        });
    }

    private async Task<int> ExecuteAsync(string? profileNameInput, bool skipConfirmation)
    {
        var (success, catalog) = _profileService.TryLoadProfileCatalog();
        if (!success)
        {
            return 1;
        }

        string? selectedProfileName = ResolveProfileName(profileNameInput, catalog);
        if (String.IsNullOrWhiteSpace(selectedProfileName))
        {
            return 1;
        }

        string normalizedName = selectedProfileName.Trim();
        if (_profileService.TryGetProfileNameValidationError(normalizedName) is { } validationError)
        {
            AppIO.WriteError(validationError);
            return 1;
        }

        bool hasOverrideDirectory = catalog.ProfileDirectories.TryGetValue(normalizedName, out string? relativeDirectoryPath);
        bool isBuiltIn = _builtInProfileTemplateService.BuiltInProfiles.Any(profile =>
            profile.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));

        if (!hasOverrideDirectory)
        {
            if (isBuiltIn)
            {
                AppIO.WriteInfo($"Profile '{normalizedName}' is using the built-in template. Nothing to delete.");
                return 0;
            }

            AppIO.WriteError($"Profile '{normalizedName}' does not exist.");
            return 1;
        }

        if (!_profileService.TryResolveProfileDirectoryPath(catalog.ProfilesRoot, relativeDirectoryPath!, out string profileDirectoryPath))
        {
            AppIO.WriteError($"Profile '{normalizedName}' directory resolves outside '{catalog.ProfilesRoot}'.");
            return 1;
        }

        string confirmMessage = isBuiltIn
            ? $"Delete override profile '{normalizedName}' and remove '{profileDirectoryPath}'? This falls back to the built-in template."
            : $"Delete profile '{normalizedName}' and remove '{profileDirectoryPath}'?";

        if (!skipConfirmation && !AppIO.Confirm(confirmMessage))
        {
            AppIO.WriteWarning("profile delete cancelled.");
            return 0;
        }

        try
        {
            bool deleted = AppIO.RunWithLoadingState($"Deleting profile '{normalizedName}'...", () =>
            {
                if (Directory.Exists(profileDirectoryPath))
                {
                    Directory.Delete(profileDirectoryPath, recursive: true);
                }

                return true;
            });

            if (!deleted)
            {
                return 1;
            }
        }
        catch (Exception ex)
        {
            AppIO.WriteError($"Failed to delete profile '{normalizedName}': {ex.Message}");
            return 1;
        }

        if(isBuiltIn)
        {
            AppIO.WriteSuccess($"Deleted override profile '{normalizedName}'. Now using built-in template.");
            return 0;
        }

        AppIO.WriteSuccess($"Deleted profile '{normalizedName}'.");
        return 0;
    }

    private string? ResolveProfileName(string? profileNameInput, ProfileCatalog catalog)
    {
        if (!String.IsNullOrWhiteSpace(profileNameInput))
        {
            return profileNameInput;
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AppIO.WriteError("No profile provided and interactive selection is unavailable. Pass --profile <name>.");
            return null;
        }

        var profileNames = new HashSet<string>(catalog.ProfileDirectories.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var builtInProfile in _builtInProfileTemplateService.BuiltInProfiles)
        {
            profileNames.Add(builtInProfile.Name);
        }

        if (profileNames.Count == 0)
        {
            AppIO.WriteError("No profiles found. Use 'ocw profile add <name>' first.");
            return null;
        }

        List<ProfileChoice> profileChoices = [.. profileNames
            .OrderByDescending(name => String.Equals(name, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ProfileChoice(name, String.Equals(name, catalog.DefaultProfileName, StringComparison.OrdinalIgnoreCase)))];

        var selectedProfile = AnsiConsole.Prompt(
            new SelectionPrompt<ProfileChoice>()
                .Title("[bold red]🗑 Select a profile to delete[/]")
                .PageSize(Math.Min(profileChoices.Count, 10))
                .UseConverter(choice => choice.IsDefault ? $"★ {choice.Name} (default)" : choice.Name)
                .AddChoices(profileChoices));

        return selectedProfile.Name;
    }

    private sealed record ProfileChoice(string Name, bool IsDefault);
}
