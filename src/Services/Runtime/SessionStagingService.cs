using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace OpencodeWrap.Services.Runtime;

internal static class SessionStagingService
{
    private const string SESSION_METADATA_FILE_NAME = ".owner";
    private static readonly TimeSpan _missingMetadataGracePeriod = TimeSpan.FromMinutes(10);

    public static bool TryCreateSession(string containerName, out InteractiveSessionContext session)
    {
        session = new InteractiveSessionContext("", "", "", "", "", 0, 0, new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        CleanupStaleSessions();

        if(String.IsNullOrWhiteSpace(containerName))
        {
            AppIO.WriteError("Container name is required to create a runtime session.");
            return false;
        }

        if(!DockerHostService.TryEnsureGlobalConfigDirectory(out string configDirectory))
        {
            return false;
        }

        if(!TryGetCurrentProcessIdentity(out int ownerProcessId, out long ownerProcessStartTicks))
        {
            AppIO.WriteError("Failed to resolve the current process identity for runtime session cleanup.");
            return false;
        }

        string sessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string sessionsRoot = Path.Combine(configDirectory, OpencodeWrapConstants.HOST_SESSION_ROOT_DIRECTORY_NAME);
        string sessionDirectory = Path.Combine(sessionsRoot, sessionId);
        string pasteDirectory = Path.Combine(sessionDirectory, OpencodeWrapConstants.HOST_SESSION_PASTE_DIRECTORY_NAME);

        try
        {
            Directory.CreateDirectory(pasteDirectory);
            File.WriteAllLines(
                BuildMetadataPath(sessionDirectory),
                [
                    ownerProcessId.ToString(CultureInfo.InvariantCulture),
                    ownerProcessStartTicks.ToString(CultureInfo.InvariantCulture),
                    containerName
                ]);
        }
        catch(Exception ex)
        {
            AppIO.WriteError($"Failed to prepare runtime session staging directory '{sessionDirectory}': {ex.Message}");
            AppIO.TryDeleteDirectory(sessionDirectory);
            return false;
        }

        session = new InteractiveSessionContext(
            sessionId,
            containerName,
            sessionDirectory,
            pasteDirectory,
            OpencodeWrapConstants.CONTAINER_PASTE_ROOT,
            ownerProcessId,
            ownerProcessStartTicks,
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        return true;
    }

    public static void CleanupStaleSessions()
    {
        if(!DockerHostService.TryEnsureGlobalConfigDirectory(out string configDirectory))
        {
            return;
        }

        string sessionsRoot = Path.Combine(configDirectory, OpencodeWrapConstants.HOST_SESSION_ROOT_DIRECTORY_NAME);
        if(!Directory.Exists(sessionsRoot))
        {
            return;
        }

        var utcNow = DateTime.UtcNow;

        IEnumerable<string> sessionDirectories;
        try
        {
            sessionDirectories = Directory.EnumerateDirectories(sessionsRoot);
        }
        catch
        {
            return;
        }

        foreach(string sessionDirectory in sessionDirectories)
        {
            try
            {
                if(ShouldDeleteSessionDirectory(sessionDirectory, utcNow))
                {
                    AppIO.TryDeleteDirectory(sessionDirectory);
                }
            }
            catch
            {
                // Best effort stale cleanup only.
            }
        }
    }

    private static bool ShouldDeleteSessionDirectory(string sessionDirectory, DateTime utcNow)
    {
        string metadataPath = BuildMetadataPath(sessionDirectory);
        if(!File.Exists(metadataPath))
        {
            return Directory.GetLastWriteTimeUtc(sessionDirectory) <= utcNow - _missingMetadataGracePeriod;
        }

        string[] metadataLines;
        try
        {
            metadataLines = File.ReadAllLines(metadataPath);
        }
        catch
        {
            return Directory.GetLastWriteTimeUtc(sessionDirectory) <= utcNow - _missingMetadataGracePeriod;
        }

        return metadataLines.Length < 2
            || !Int32.TryParse(metadataLines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int ownerProcessId)
            || !Int64.TryParse(metadataLines[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ownerProcessStartTicks)
            ? Directory.GetLastWriteTimeUtc(sessionDirectory) <= utcNow - _missingMetadataGracePeriod
            : !IsProcessIdentityMatch(ownerProcessId, ownerProcessStartTicks);
    }

    private static string BuildMetadataPath(string sessionDirectory)
        => Path.Combine(sessionDirectory, SESSION_METADATA_FILE_NAME);

    private static bool TryGetCurrentProcessIdentity(out int processId, out long processStartTicks)
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

    private static bool IsProcessIdentityMatch(int processId, long processStartTicks)
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
