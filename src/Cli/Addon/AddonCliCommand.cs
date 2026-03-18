using System.CommandLine;

namespace OpencodeWrap.Cli.Addon;

internal sealed class AddonCliCommand : Command
{
    public AddonCliCommand(ListAddonsCliCommand listAddonsCliCommand, AddAddonCliCommand addAddonCliCommand, DeleteAddonCliCommand deleteAddonCliCommand, OpenAddonDirectoryCliCommand openAddonDirectoryCliCommand)
        : base("addon", "Manage session addons.")
    {
        Add(listAddonsCliCommand);
        Add(addAddonCliCommand);
        Add(deleteAddonCliCommand);
        Add(openAddonDirectoryCliCommand);
    }
}
