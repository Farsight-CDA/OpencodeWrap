using Spectre.Console;
using System.CommandLine;

namespace OpencodeWrap.Cli.Addon;

internal sealed class ListAddonsCliCommand : Command
{
    private readonly SessionAddonService _sessionAddonService;

    public ListAddonsCliCommand(SessionAddonService sessionAddonService)
        : base("list", "List all available session addons.")
    {
        _sessionAddonService = sessionAddonService;
        SetAction(async _ => await ExecuteAsync());
    }

    private async Task<int> ExecuteAsync()
    {
        if(!_sessionAddonService.TryLoadCatalog(out var catalog))
        {
            return 1;
        }

        if(catalog.Addons.Count == 0)
        {
            AppIO.WriteWarning("No session addons found.");
            return 0;
        }

        AppIO.WriteHeader("Session Addons", $"Config directory: {catalog.AddonsRoot}");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.DeepSkyBlue1)
            .AddColumn("[deepskyblue1]Addon[/]")
            .AddColumn("[deepskyblue1]Kind[/]")
            .AddColumn("[deepskyblue1]Location[/]");

        int invalidPathCount = 0;

        foreach(var addon in catalog.Addons.Values.OrderBy(addon => addon.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool isBuiltIn = addon.BuiltInAddon is not null;
            bool hasOverride = !String.IsNullOrWhiteSpace(addon.DirectoryPath);

            string typeLabel = isBuiltIn
                ? hasOverride ? "🛠 built-in override" : "📦 built-in"
                : "✨ custom";

            string displayPath;
            if(!hasOverride)
            {
                displayPath = "[grey](embedded)[/]";
            }
            else if(_sessionAddonService.TryResolveAddonDirectoryPath(catalog.AddonsRoot, addon.Name, out string addonDirectoryPath))
            {
                displayPath = Markup.Escape(addonDirectoryPath);
            }
            else
            {
                invalidPathCount++;
                displayPath = $"[red](invalid: {Markup.Escape(addon.DirectoryPath!)})[/]";
            }

            table.AddRow(Markup.Escape(addon.Name), Markup.Escape(typeLabel), displayPath);
        }

        AnsiConsole.Write(table);

        if(invalidPathCount > 0)
        {
            AppIO.WriteWarning($"Found {invalidPathCount} addon path issue(s). Consider fixing or re-adding those addons.");
        }

        AppIO.WriteInfo("Tip: run 'ocw addon add <name>' to create a custom addon or override a built-in one.");
        return 0;
    }
}
