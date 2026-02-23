try
{
    Environment.ExitCode = await OpencodeWrapCli.RunAsync(args);
}
catch(Exception ex)
{
    AppIO.WriteError($"unexpected error: {ex.Message}");
    Environment.ExitCode = 1;
}
