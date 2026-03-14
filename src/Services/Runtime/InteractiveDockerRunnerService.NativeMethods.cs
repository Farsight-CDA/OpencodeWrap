using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class InteractiveDockerRunnerService
{
    private static partial class WindowsConsoleInput
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr GetStdHandle(int nStdHandle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool FlushConsoleInputBuffer(IntPtr hConsoleInput);
    }

    private sealed partial class UnixTerminalModeScope
    {
        [LibraryImport("libc", SetLastError = true)]
        private static partial int tcflush(int fd, int queueSelector);
    }
}
