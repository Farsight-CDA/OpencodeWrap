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

    private static partial class WindowsClipboardReader
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [LibraryImport("user32.dll")]
        private static partial uint GetClipboardSequenceNumber();

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint RegisterClipboardFormat(string lpszFormat);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool OpenClipboard(IntPtr hWndNewOwner);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseClipboard();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsClipboardFormatAvailable(uint format);

        [LibraryImport("user32.dll")]
        private static partial uint EnumClipboardFormats(uint format);

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int GetClipboardFormatName(uint format, [Out] char[] lpszFormatName, int cchMaxCount);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetClipboardData(uint uFormat);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetDC(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
        private static partial int GetObject(IntPtr h, int c, out BITMAP pv);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        private static partial int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr GlobalLock(IntPtr hMem);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GlobalUnlock(IntPtr hMem);

        [LibraryImport("kernel32.dll")]
        private static partial nuint GlobalSize(IntPtr hMem);
    }

    private sealed partial class UnixTerminalModeScope
    {
        [LibraryImport("libc", SetLastError = true)]
        private static partial int tcflush(int fd, int queueSelector);
    }
}
