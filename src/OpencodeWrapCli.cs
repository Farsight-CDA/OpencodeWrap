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

        if(!await services.Profiles.TryEnsureInitializedAsync())
        {
            return 1;
        }

        if(args.Length == 0)
        {
            return await InvokeAsync(rootCommand, ["--help"]);
        }

        if(String.Equals(args[0], "data", StringComparison.OrdinalIgnoreCase)
            || String.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase)
            || String.Equals(args[0], "profile", StringComparison.OrdinalIgnoreCase))
        {
            return await InvokeAsync(rootCommand, args);
        }

        return await OpencodeWrapRootCommand.ExecuteOpencodeAsync(services, args, requestedProfileName: null, includeProfileConfig: false);
    }

    private static Task<int> InvokeAsync(Command command, IReadOnlyList<string> args)
        => command.Parse(args).InvokeAsync();
}
