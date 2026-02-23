using Spectre.Console;

internal static class AppIO
{
    public static void WriteError(string message) => AnsiConsole.MarkupLine($"[red]ocw:[/] {Markup.Escape(message)}");

    public static void WriteInfo(string message) => AnsiConsole.MarkupLine($"[deepskyblue1]ocw:[/] {Markup.Escape(message)}");

    public static void WriteSuccess(string message) => AnsiConsole.MarkupLine($"[green]ocw:[/] {Markup.Escape(message)}");

    public static void WriteWarning(string message) => AnsiConsole.MarkupLine($"[yellow]ocw:[/] {Markup.Escape(message)}");

    public static bool Confirm(string message) => AnsiConsole.Confirm($"[yellow]{Markup.Escape(message)}[/]", defaultValue: false);

    public static T WithStatus<T>(string statusMessage, Func<T> action) => !AnsiConsole.Profile.Capabilities.Interactive
            ? action()
            : AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("deepskyblue1"))
            .Start($"[deepskyblue1]{Markup.Escape(statusMessage)}[/]", _ => action());

    public static Task<T> WithStatusAsync<T>(string statusMessage, Func<Task<T>> action) => !AnsiConsole.Profile.Capabilities.Interactive
            ? action()
            : AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("deepskyblue1"))
            .StartAsync($"[deepskyblue1]{Markup.Escape(statusMessage)}[/]", _ => action());

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if(Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    public static Task TryDeleteDirectoryAsync(string path) => Task.Run(() => TryDeleteDirectory(path));
}
