using System.CommandLine;

internal static class OpencodeWrapCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if(ContainerCleanupWatchdog.IsWatchdogInvocation(args))
        {
            return await ContainerCleanupWatchdog.RunWatchdogAsync(args);
        }

        var services = new OpencodeWrapServices();
        var rootCommand = new OpencodeWrapRootCommand(services);

        if(!await ProfileService.TryEnsureInitializedAsync())
        {
            return 1;
        }

        if(args.Length == 0)
        {
            return await InvokeAsync(rootCommand, ["--help"]);
        }

        bool isReservedTopLevelCommand = rootCommand.Subcommands.Any(subcommand =>
            String.Equals(subcommand.Name, args[0], StringComparison.OrdinalIgnoreCase));

        if(isReservedTopLevelCommand)
        {
            return await InvokeAsync(rootCommand, args);
        }
        //
        return await OpencodeWrapRootCommand.ExecuteOpencodeAsync(services, args, requestedProfileName: null, includeProfileConfig: false);
    }

    private static Task<int> InvokeAsync(Command command, IReadOnlyList<string> args)
        => command.Parse(args).InvokeAsync();
}
