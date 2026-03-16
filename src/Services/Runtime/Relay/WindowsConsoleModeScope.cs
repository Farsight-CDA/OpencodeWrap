using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class WindowsConsoleModeScope : IDisposable
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;
    private const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    private static readonly byte[] _bracketedPasteDisableSequence = "\u001b[?2004l"u8.ToArray();
    private static readonly Lock _sync = new();
    private static WindowsConsoleState? _activeState;

    private readonly WindowsConsoleState _state;
    private bool _disposed;

    private WindowsConsoleModeScope(WindowsConsoleState state)
    {
        _state = state;
    }

    public static bool TryEnter(out WindowsConsoleModeScope? scope, out string? failureReason)
    {
        scope = null;
        failureReason = null;

        IntPtr inputHandle = GetStdHandle(STD_INPUT_HANDLE);
        IntPtr outputHandle = GetStdHandle(STD_OUTPUT_HANDLE);
        if(inputHandle == IntPtr.Zero || outputHandle == IntPtr.Zero || inputHandle == new IntPtr(-1) || outputHandle == new IntPtr(-1))
        {
            failureReason = "failed to get standard console handles";
            return false;
        }

        if(!GetConsoleMode(inputHandle, out uint savedInputMode) || !GetConsoleMode(outputHandle, out uint savedOutputMode))
        {
            failureReason = $"GetConsoleMode failed with Win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        uint inputMode = (savedInputMode | ENABLE_EXTENDED_FLAGS | ENABLE_VIRTUAL_TERMINAL_INPUT)
            & ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT | ENABLE_QUICK_EDIT_MODE);
        uint outputMode = savedOutputMode | ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN;

        if(!SetConsoleMode(inputHandle, inputMode))
        {
            failureReason = $"SetConsoleMode(input) failed with Win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        if(!SetConsoleMode(outputHandle, outputMode))
        {
            _ = SetConsoleMode(inputHandle, savedInputMode);
            failureReason = $"SetConsoleMode(output) failed with Win32 error {Marshal.GetLastWin32Error()}";
            return false;
        }

        var state = new WindowsConsoleState(inputHandle, savedInputMode, outputHandle, savedOutputMode);
        lock(_sync)
        {
            _activeState = state;
        }

        scope = new WindowsConsoleModeScope(state);
        return true;
    }

    public static void RestoreActiveState()
    {
        WindowsConsoleState? state;

        lock(_sync)
        {
            state = _activeState;
            _activeState = null;
        }

        if(state is null)
        {
            return;
        }

        TryWriteControlSequence(_bracketedPasteDisableSequence);
        _ = SetConsoleMode(state.Value.OutputHandle, state.Value.OutputMode);
        _ = SetConsoleMode(state.Value.InputHandle, state.Value.InputMode);
    }

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private readonly record struct WindowsConsoleState(IntPtr InputHandle, uint InputMode, IntPtr OutputHandle, uint OutputMode);
}
