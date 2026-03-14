using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpencodeWrap.Services.Runtime;

internal sealed class WindowsPseudoConsoleSession : IDisposable
{
    private const int HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint INFINITE = 0xFFFFFFFF;

    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _pseudoConsoleHandle;
    private bool _inputClosed;
    private bool _pseudoConsoleClosed;
    private bool _disposed;

    private WindowsPseudoConsoleSession(FileStream inputStream, FileStream outputStream, IntPtr processHandle, IntPtr pseudoConsoleHandle)
    {
        _inputStream = inputStream;
        _outputStream = outputStream;
        _processHandle = processHandle;
        _pseudoConsoleHandle = pseudoConsoleHandle;
    }

    public Stream InputStream => _inputStream;
    public Stream OutputStream => _outputStream;
    public bool HasExited => WaitForSingleObject(_processHandle, 0) == WAIT_OBJECT_0;

    public static bool TryStart(string fileName, IReadOnlyList<string> args, out WindowsPseudoConsoleSession? session, out string? failureReason)
    {
        session = null;
        failureReason = null;

        IntPtr inputRead = IntPtr.Zero;
        IntPtr inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsoleHandle = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            CreateAnonymousPipe(out inputRead, out inputWrite);
            CreateAnonymousPipe(out outputRead, out outputWrite);
            SetParentPipeState(inputWrite);
            SetParentPipeState(outputRead);

            var initialSize = GetCurrentConsoleSize();
            int createPseudoConsoleResult = CreatePseudoConsole(
                new Coord(initialSize.Columns, initialSize.Rows),
                inputRead,
                outputWrite,
                0,
                out pseudoConsoleHandle);
            if(createPseudoConsoleResult != 0)
            {
                throw new Win32Exception(createPseudoConsoleResult);
            }

            CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;

            attributeList = BuildAttributeList(pseudoConsoleHandle);

            var startupInfo = new StartupInfoEx();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            startupInfo.lpAttributeList = attributeList;

            var processInformation = new ProcessInformation();
            var commandLine = new StringBuilder(BuildCommandLine(fileName, args));

            bool created = CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                null,
                ref startupInfo,
                out processInformation);
            if(!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            processHandle = processInformation.hProcess;
            threadHandle = processInformation.hThread;
            CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;

            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            attributeList = IntPtr.Zero;

            var inputHandle = new SafeFileHandle(inputWrite, ownsHandle: true);
            var outputHandle = new SafeFileHandle(outputRead, ownsHandle: true);
            inputWrite = IntPtr.Zero;
            outputRead = IntPtr.Zero;

            session = new WindowsPseudoConsoleSession(
                new FileStream(inputHandle, FileAccess.Write, 4096, isAsync: false),
                new FileStream(outputHandle, FileAccess.Read, 4096, isAsync: false),
                processHandle,
                pseudoConsoleHandle);

            processHandle = IntPtr.Zero;
            pseudoConsoleHandle = IntPtr.Zero;
            return true;
        }
        catch(Exception ex)
        {
            int lastError = OperatingSystem.IsWindows() ? Marshal.GetLastWin32Error() : 0;
            string lastErrorDetail = lastError != 0 ? $" (Win32 error {lastError}: {new Win32Exception(lastError).Message})" : "";
            failureReason = $"{ex.GetType().Name}: {ex.Message}{lastErrorDetail}";

            if(attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if(threadHandle != IntPtr.Zero)
            {
                CloseHandle(threadHandle);
            }

            if(processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }

            if(pseudoConsoleHandle != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsoleHandle);
            }

            if(inputRead != IntPtr.Zero)
            {
                CloseHandle(inputRead);
            }

            if(inputWrite != IntPtr.Zero)
            {
                CloseHandle(inputWrite);
            }

            if(outputRead != IntPtr.Zero)
            {
                CloseHandle(outputRead);
            }

            if(outputWrite != IntPtr.Zero)
            {
                CloseHandle(outputWrite);
            }

            return false;
        }
    }

    public async Task<int> WaitForExitAsync()
    {
        await Task.Run(() => WaitForSingleObject(_processHandle, INFINITE));
        return GetExitCodeProcess(_processHandle, out uint exitCode)
            ? unchecked((int) exitCode)
            : 1;
    }

    public void Resize(short columns, short rows)
    {
        if(_pseudoConsoleHandle == IntPtr.Zero || columns <= 0 || rows <= 0)
        {
            return;
        }

        try
        {
            _ = ResizePseudoConsole(_pseudoConsoleHandle, new Coord(columns, rows));
        }
        catch
        {
            // Best effort only.
        }
    }

    public void CloseInput()
    {
        if(_inputClosed)
        {
            return;
        }

        _inputClosed = true;
        _inputStream.Dispose();
    }

    public void ClosePseudoConsole()
    {
        if(_pseudoConsoleClosed || _pseudoConsoleHandle == IntPtr.Zero)
        {
            return;
        }

        _pseudoConsoleClosed = true;
        ClosePseudoConsole(_pseudoConsoleHandle);
    }

    public void Dispose()
    {
        if(_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            CloseInput();
        }
        catch
        {
        }

        try
        {
            _outputStream.Dispose();
        }
        catch
        {
        }

        if(_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
        }

        ClosePseudoConsole();
    }

    public static (short Columns, short Rows) GetCurrentConsoleSize()
    {
        try
        {
            short columns = (short) Math.Clamp(Console.WindowWidth, 20, Int16.MaxValue);
            short rows = (short) Math.Clamp(Console.WindowHeight, 10, Int16.MaxValue);
            return (columns, rows);
        }
        catch
        {
            return (120, 30);
        }
    }

    private static void CreateAnonymousPipe(out IntPtr readHandle, out IntPtr writeHandle)
    {
        if(!CreatePipe(out readHandle, out writeHandle, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void SetParentPipeState(IntPtr handle)
    {
        if(!SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static IntPtr BuildAttributeList(IntPtr pseudoConsoleHandle)
    {
        nuint attributeListSize = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        IntPtr attributeList = Marshal.AllocHGlobal(unchecked((int) attributeListSize));

        if(!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
        {
            int error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(attributeList);
            throw new Win32Exception(error);
        }

        if(!UpdateProcThreadAttribute(
            attributeList,
            0,
            (IntPtr) PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            pseudoConsoleHandle,
            (IntPtr) IntPtr.Size,
            IntPtr.Zero,
            IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            throw new Win32Exception(error);
        }

        return attributeList;
    }

    private static string BuildCommandLine(string fileName, IReadOnlyList<string> args)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteForWindowsCommandLine(fileName));

        foreach(string arg in args)
        {
            builder.Append(' ');
            builder.Append(QuoteForWindowsCommandLine(arg));
        }

        return builder.ToString();
    }

    private static string QuoteForWindowsCommandLine(string value)
    {
        if(value.Length == 0)
        {
            return "\"\"";
        }

        bool requiresQuotes = value.Any(ch => Char.IsWhiteSpace(ch) || ch == '"');
        if(!requiresQuotes)
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append('"');

        int backslashCount = 0;
        foreach(char ch in value)
        {
            if(ch == '\\')
            {
                backslashCount++;
                continue;
            }

            if(ch == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if(backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }

            builder.Append(ch);
        }

        if(backslashCount > 0)
        {
            builder.Append('\\', backslashCount * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref nuint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public short X { get; } = x;
        public short Y { get; } = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}

internal sealed class WindowsConsoleModeScope : IDisposable
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_WINDOW_INPUT = 0x0008;
    private const uint ENABLE_MOUSE_INPUT = 0x0010;
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
        => _state = state;

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
            using Stream standardOutput = Console.OpenStandardOutput();
            standardOutput.Write(sequence, 0, sequence.Length);
            standardOutput.Flush();
        }
        catch
        {
            // Best effort terminal restoration only.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private readonly record struct WindowsConsoleState(IntPtr InputHandle, uint InputMode, IntPtr OutputHandle, uint OutputMode);
}
