using Farsight.Common.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text;

namespace OpencodeWrap;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            EnsureUnicodeConsoleEncoding();

            var builder = Host.CreateApplicationBuilder(args);
            builder.AddApplication<BasicFarsightStartup>();

            builder.Services.AddTransient<OpencodeWrapRootCommand>();
            builder.Services.AddTransient<RunCliCommand>();
            builder.Services.AddTransient<UpdateCliCommand>();
            builder.Services.AddTransient<DataCliCommand>();
            builder.Services.AddTransient<ImportArchiveCliCommand>();
            builder.Services.AddTransient<ExportCliCommand>();
            builder.Services.AddTransient<ImportHostCliCommand>();
            builder.Services.AddTransient<ResetVolumeCliCommand>();
            builder.Services.AddTransient<ProfileCliCommand>();
            builder.Services.AddTransient<ListProfilesCliCommand>();
            builder.Services.AddTransient<AddProfileCliCommand>();
            builder.Services.AddTransient<DeleteProfileCliCommand>();
            builder.Services.AddTransient<BuildProfileCliCommand>();
            builder.Services.AddTransient<OpenProfileDirectoryCliCommand>();

            using var host = builder.Build();
            Environment.ExitCode = await OpencodeWrapCli.RunAsync(args, host.Services);
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"unexpected error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static void EnsureUnicodeConsoleEncoding()
    {
        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = utf8;
            Console.OutputEncoding = utf8;
        }
        catch
        {
            // Best effort only. Keep defaults if console encoding cannot be changed.
        }
    }
}
