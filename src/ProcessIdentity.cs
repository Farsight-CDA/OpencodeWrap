using System.Diagnostics;

namespace OpencodeWrap;

/// <summary>
/// Utility methods for working with process identity information.
/// </summary>
internal static class ProcessIdentity
{
    /// <summary>
    /// Attempts to get the current process identity including process ID and start time.
    /// </summary>
    /// <param name="processId">The current process ID.</param>
    /// <param name="processStartTicks">The current process start time in UTC ticks.</param>
    /// <returns>True if the identity was successfully resolved; otherwise, false.</returns>
    public static bool TryGetCurrentProcessIdentity(out int processId, out long processStartTicks)
    {
        processId = Environment.ProcessId;

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            processStartTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
            return processStartTicks > 0;
        }
        catch
        {
            processStartTicks = 0;
            return false;
        }
    }

    /// <summary>
    /// Attempts to get the parent process identity including process path, ID, and start time.
    /// </summary>
    /// <param name="executablePath">The path to the current executable.</param>
    /// <param name="parentPid">The parent process ID (same as current process in this context).</param>
    /// <param name="parentStartTicks">The parent process start time in UTC ticks.</param>
    /// <returns>True if the identity was successfully resolved; otherwise, false.</returns>
    public static bool TryGetParentProcessIdentity(out string? executablePath, out int parentPid, out long parentStartTicks)
    {
        executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            parentPid = 0;
            parentStartTicks = 0;
            return false;
        }

        parentPid = Environment.ProcessId;

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            parentStartTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
            return parentStartTicks > 0;
        }
        catch
        {
            parentStartTicks = 0;
            return false;
        }
    }

    /// <summary>
    /// Checks if a process with the specified identity is still alive.
    /// </summary>
    /// <param name="processId">The process ID to check.</param>
    /// <param name="processStartTicks">The expected process start time in UTC ticks.</param>
    /// <returns>True if the process is alive and matches the expected start time; otherwise, false.</returns>
    public static bool IsProcessAlive(int processId, long processStartTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == processStartTicks;
        }
        catch
        {
            return false;
        }
    }
}
