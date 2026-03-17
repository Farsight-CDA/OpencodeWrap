using OpencodeWrap.Services.Runtime.Infrastructure;
using System.Globalization;

namespace OpencodeWrap.Services.Runtime;

internal sealed partial class SessionStagingService : Singleton
{
    private const string SESSION_METADATA_FILE_NAME = ".owner";
    private static readonly TimeSpan _missingMetadataGracePeriod = TimeSpan.FromMinutes(10);

    [Inject]
    private readonly DockerHostService _dockerHostService;

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public bool TryCreateSession(string containerName, out RuntimeSessionContext session)
    {
        session = new RuntimeSessionContext("", "");

        CleanupStaleSessions();

        if(String.IsNullOrWhiteSpace(containerName))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.SESSION, "Container name is required to create a runtime session.");
            return false;
        }

        if(!_dockerHostService.TryEnsureGlobalConfigDirectory(out string configDirectory))
        {
            return false;
        }

        if(!ProcessIdentity.TryGetCurrentProcessIdentity(out int ownerProcessId, out long ownerProcessStartTicks))
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.SESSION, "Failed to resolve the current process identity for runtime session cleanup.");
            return false;
        }

        string sessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string sessionsRoot = Path.Combine(configDirectory, OpencodeWrapConstants.HOST_SESSION_ROOT_DIRECTORY_NAME);
        string sessionDirectory = Path.Combine(sessionsRoot, sessionId);

        try
        {
            Directory.CreateDirectory(sessionDirectory);
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
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.SESSION, $"Failed to prepare runtime session staging directory '{sessionDirectory}': {ex.Message}");
            AppIO.TryDeleteDirectory(sessionDirectory);
            return false;
        }

        session = new RuntimeSessionContext(
            sessionId,
            sessionDirectory);
        return true;
    }

    public void CleanupStaleSessions()
    {
        if(!_dockerHostService.TryEnsureGlobalConfigDirectory(out string configDirectory))
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
            : !ProcessIdentity.IsProcessAlive(ownerProcessId, ownerProcessStartTicks);
    }

    private static string BuildMetadataPath(string sessionDirectory)
        => Path.Combine(sessionDirectory, SESSION_METADATA_FILE_NAME);
}
