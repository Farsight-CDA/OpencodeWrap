using System.CommandLine;

namespace OpencodeWrap.Cli.Addon;

internal sealed class AddAddonCliCommand : Command
{
    private readonly Argument<string> _nameArgument;
    private readonly SessionAddonService _sessionAddonService;
    private readonly BuiltInSessionAddonService _builtInSessionAddonService;

    public AddAddonCliCommand(SessionAddonService sessionAddonService, BuiltInSessionAddonService builtInSessionAddonService)
        : base("add", "Add a new session addon directory under the OCW config directory.")
    {
        _sessionAddonService = sessionAddonService;
        _builtInSessionAddonService = builtInSessionAddonService;
        _nameArgument = new Argument<string>("name")
        {
            Description = "New addon name."
        };

        Add(_nameArgument);

        SetAction(async parseResult =>
        {
            string name = parseResult.GetRequiredValue(_nameArgument);
            return await ExecuteAsync(name);
        });
    }

    private async Task<int> ExecuteAsync(string addonName)
    {
        string normalizedName = addonName.Trim();
        if(_sessionAddonService.TryGetAddonNameValidationError(normalizedName) is { } validationError)
        {
            AppIO.WriteError(validationError);
            return 1;
        }

        if(!_sessionAddonService.TryLoadCatalog(out var catalog))
        {
            return 1;
        }

        bool hasOverrideDirectory = catalog.Addons.TryGetValue(normalizedName, out var addonEntry)
            && !String.IsNullOrWhiteSpace(addonEntry.DirectoryPath);
        if(hasOverrideDirectory)
        {
            AppIO.WriteError($"Session addon '{normalizedName}' already exists.");
            return 1;
        }

        if(!_sessionAddonService.TryResolveAddonDirectoryPath(catalog.AddonsRoot, normalizedName, out string addonDirectoryPath))
        {
            AppIO.WriteError($"Session addon directory '{addonDirectoryPath}' resolves outside '{catalog.AddonsRoot}'.");
            return 1;
        }

        var builtInAddon = addonEntry?.BuiltInAddon;

        try
        {
            bool created = await AppIO.RunWithLoadingStateAsync($"Creating session addon '{normalizedName}'...", () =>
            {
                if(builtInAddon is not null)
                {
                    return Task.FromResult(_builtInSessionAddonService.TryMaterializeBuiltInAddon(builtInAddon, catalog.ConfigRoot, out _));
                }

                Directory.CreateDirectory(addonDirectoryPath);
                return Task.FromResult(true);
            });

            if(!created)
            {
                return 1;
            }
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to add session addon '{normalizedName}': {ex.Message}");
            return 1;
        }

        string mode = builtInAddon is not null ? "override" : "session addon";
        AppIO.WriteSuccess($"Added {mode} '{normalizedName}' at '{addonDirectoryPath}'.");
        AppIO.WriteInfo("Place files here using the same relative paths you want copied into the session profile.");
        return 0;
    }
}
