using Spectre.Console;
using Microsoft.Extensions.Logging;

namespace OpencodeWrap;

internal static class AppIO
{
    private const string BRAND_LABEL = "[bold deepskyblue1]ocw[/]";

    public static void WriteError(string message)
        => WriteMessage("[red]✖[/]", message);
    public static void WriteInfo(string message)
        => WriteMessage("[deepskyblue1]ℹ[/]", message);
    public static void WriteSuccess(string message)
        => WriteMessage("[green]✔[/]", message);
    public static void WriteWarning(string message)
        => WriteMessage("[yellow]⚠[/]", message);
    public static void WriteLog(LogLevel level, string message)
    {
        switch(level)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
                WriteMessage("[grey]·[/]", message);
                break;
            case LogLevel.Information:
                WriteInfo(message);
                break;
            case LogLevel.Warning:
                WriteWarning(message);
                break;
            case LogLevel.Error:
                WriteError(message);
                break;
            case LogLevel.Critical:
                WriteMessage("[bold red]‼[/]", message);
                break;
            default:
                WriteInfo(message);
                break;
        }
    }

    public static void WriteHeader(string title, string? subtitle = null)
    {
        AnsiConsole.MarkupLine($"{BRAND_LABEL} [grey]-[/] [bold]{Markup.Escape(title)}[/]");
        if(!String.IsNullOrWhiteSpace(subtitle))
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(subtitle)}[/]");
        }

        AnsiConsole.WriteLine();
    }

    public static bool Confirm(string message)
        => AnsiConsole.Confirm($"[yellow]⚠[/] {BRAND_LABEL} {Markup.Escape(message)}", defaultValue: false);

    public static T RunWithLoadingState<T>(string statusMessage, Func<T> action)
        => !AnsiConsole.Profile.Capabilities.Interactive
            ? RunNonInteractiveStatus(statusMessage, action)
            : AnsiConsole.Status()
            .Spinner(Spinner.Known.Star)
            .SpinnerStyle(Style.Parse("deepskyblue1"))
            .Start($"[deepskyblue1]>>[/] {Markup.Escape(statusMessage)}", _ => action());

    public static Task<T> RunWithLoadingStateAsync<T>(string statusMessage, Func<Task<T>> action)
        => !AnsiConsole.Profile.Capabilities.Interactive
            ? RunNonInteractiveStatusAsync(statusMessage, action)
            : AnsiConsole.Status()
            .Spinner(Spinner.Known.Star)
            .SpinnerStyle(Style.Parse("deepskyblue1"))
            .StartAsync($"[deepskyblue1]>>[/] {Markup.Escape(statusMessage)}", _ => action());

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

    private static void WriteMessage(string iconMarkup, string message)
        => AnsiConsole.MarkupLine($"{iconMarkup} {BRAND_LABEL} {Markup.Escape(message)}");

    private static T RunNonInteractiveStatus<T>(string statusMessage, Func<T> action)
    {
        WriteInfo(statusMessage);
        return action();
    }

    private static async Task<T> RunNonInteractiveStatusAsync<T>(string statusMessage, Func<Task<T>> action)
    {
        WriteInfo(statusMessage);
        return await action();
    }
}
