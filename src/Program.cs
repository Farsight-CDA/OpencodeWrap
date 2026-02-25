using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSingleton<DockerHostService>();
    builder.Services.AddSingleton<VolumeStateService>();
    builder.Services.AddSingleton<OpencodeLauncherService>();

    builder.Services.AddTransient<OpencodeWrapRootCommand>();
    builder.Services.AddTransient<RunCliCommand>();
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

    using IHost host = builder.Build();
    Environment.ExitCode = await OpencodeWrapCli.RunAsync(args, host.Services);
}
catch(Exception ex)
{
    AppIO.WriteError($"unexpected error: {ex.Message}");
    Environment.ExitCode = 1;
}
