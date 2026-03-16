using System.Runtime.InteropServices;
using System.Text;

namespace OpencodeWrap.Services.Runtime.Relay;

internal static partial class WindowsConsoleInput
{
    private const int STD_INPUT_HANDLE = -10;
    private static readonly IntPtr _inputHandle = GetStdHandle(STD_INPUT_HANDLE);

    public static bool TryRead(byte[] buffer, out int bytesRead)
    {
        bytesRead = 0;
        if(!OperatingSystem.IsWindows() || _inputHandle == IntPtr.Zero || _inputHandle == new IntPtr(-1))
        {
            return false;
        }

        if(!ReadFile(_inputHandle, buffer, buffer.Length, out int read, IntPtr.Zero))
        {
            return false;
        }

        bytesRead = read;
        return true;
    }

    public static void DiscardPendingInput()
    {
        if(!OperatingSystem.IsWindows() || _inputHandle == IntPtr.Zero || _inputHandle == new IntPtr(-1))
        {
            return;
        }

        _ = FlushConsoleInputBuffer(_inputHandle);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushConsoleInputBuffer(IntPtr hConsoleInput);
}

internal sealed partial class UnixTerminalModeScope : IDisposable
{
    private static readonly byte[] _bracketedPasteDisableSequence = "\u001b[?2004l"u8.ToArray();
    private const int STDIN_FILE_DESCRIPTOR = 0;
    private const int TCIFLUSH = 0;
    private static readonly Lock _sync = new();
    private static string? _activeState;
    private bool _disposed;

    public static bool TryEnter(out UnixTerminalModeScope? scope, out string? failureReason)
    {
        scope = null;
        failureReason = null;

        var readStateResult = ProcessRunner.RunAsync("stty", ["-g"]).GetAwaiter().GetResult();
        if(!readStateResult.Success || String.IsNullOrWhiteSpace(readStateResult.StdOut))
        {
            failureReason = InteractiveDockerRunnerService.DescribeProcessFailure("`stty -g`", readStateResult);
            return false;
        }

        string savedState = readStateResult.StdOut.Trim();
        var rawModeResult = ProcessRunner.RunAsync("stty", ["raw", "-echo"]).GetAwaiter().GetResult();
        if(!rawModeResult.Success)
        {
            failureReason = InteractiveDockerRunnerService.DescribeProcessFailure("`stty raw -echo`", rawModeResult);
            return false;
        }

        lock(_sync)
        {
            _activeState = savedState;
        }

        scope = new UnixTerminalModeScope();
        return true;
    }

    public static void RestoreActiveState()
    {
        string? savedState;

        lock(_sync)
        {
            savedState = _activeState;
            _activeState = null;
        }

        if(String.IsNullOrWhiteSpace(savedState))
        {
            return;
        }

        TryWriteControlSequence(_bracketedPasteDisableSequence);
        _ = ProcessRunner.CommandSucceedsBlocking("stty", [savedState]);
    }

    public static void DiscardPendingInput()
        => _ = tcflush(STDIN_FILE_DESCRIPTOR, TCIFLUSH);

    public void Dispose()
    {
        if(_disposed)
        {
            return;
        }

        _disposed = true;
        RestoreActiveState();
    }

    private static void TryWriteControlSequence(byte[] sequence)
    {
        try
        {
            using var standardOutput = Console.OpenStandardOutput();
            standardOutput.Write(sequence, 0, sequence.Length);
            standardOutput.Flush();
        }
        catch
        {
            // Best effort terminal restoration only.
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int tcflush(int fd, int queueSelector);
}
