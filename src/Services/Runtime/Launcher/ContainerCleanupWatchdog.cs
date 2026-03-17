using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OpencodeWrap.Services.Runtime.Launcher;

internal static class ContainerCleanupWatchdog
{
    private const string WATCHDOG_MODE = "__watchdog";
    private const string WATCHDOG_ENVIRONMENT_VARIABLE = "OCW_INTERNAL_WATCHDOG";
    private const string WATCHDOG_READY_FILE_PREFIX = "ocw-watchdog-";
    private const string WATCHDOG_READY_FILE_SUFFIX = ".ready";
    private static readonly TimeSpan _parentPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _readyPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan _removalRetryDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _removalRetryInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Lock _signalRegistrationLock = new();
    private static List<PosixSignalRegistration>? _ignoreSignalRegistrations;
    private readonly record struct WatchdogRunConfig(
        int ParentPid,
        long ParentStartTicks,
        string ContainerName,
        string SessionDirectoryPath,
        string ReadyFilePath);

    public static bool IsWatchdogInvocation(IReadOnlyList<string> args) => args.Count > 0
        && String.Equals(args[0], WATCHDOG_MODE, StringComparison.Ordinal)
        && String.Equals(Environment.GetEnvironmentVariable(WATCHDOG_ENVIRONMENT_VARIABLE), "1", StringComparison.Ordinal);

    public static async Task<int> RunWatchdogAsync(IReadOnlyList<string> args)
        => !TryParseWatchdogRunConfig(args, out var config) ? 1 : await WaitForParentAndCleanupAsync(config);

    public static async Task<bool> TryStartDetachedAndWaitReadyAsync(string containerName, string sessionDirectoryPath, TimeSpan timeout)
    {
        if(String.IsNullOrWhiteSpace(containerName) || timeout <= TimeSpan.Zero)
        {
            return false;
        }

        if(!ProcessIdentity.TryGetParentProcessIdentity(out string? executablePath, out int parentPid, out long parentStartTicks))
        {
            return false;
        }

        string[] watchdogArgs = BuildWatchdogArgs(parentPid, parentStartTicks, containerName, sessionDirectoryPath, out string readyFilePath);

        if(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if(TryStartWatchdog("setsid", [executablePath!, .. watchdogArgs]))
            {
                return await WaitForReadyAsync(readyFilePath, timeout);
            }
        }

        if(TryStartWatchdog(executablePath!, watchdogArgs))
        {
            return await WaitForReadyAsync(readyFilePath, timeout);
        }

        TryDeleteFile(readyFilePath);
        return false;
    }

    private static bool TryStartWatchdog(string fileName, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };

        foreach(string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment[WATCHDOG_ENVIRONMENT_VARIABLE] = "1";

        try
        {
            using var process = Process.Start(psi);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> WaitForParentAndCleanupAsync(WatchdogRunConfig config)
    {
        RegisterIgnoreTerminationHandlers();
        WriteReadyFile(config.ReadyFilePath);

        while(ProcessIdentity.IsProcessAlive(config.ParentPid, config.ParentStartTicks))
        {
            await Task.Delay(_parentPollInterval);
        }

        await RetryCleanupContainerAsync(config.ContainerName);
        if(!String.IsNullOrWhiteSpace(config.SessionDirectoryPath))
        {
            AppIO.TryDeleteDirectory(config.SessionDirectoryPath);
        }

        return 0;
    }

    private static async Task RetryCleanupContainerAsync(string containerName)
    {
        var deadlineUtc = DateTime.UtcNow + _removalRetryDuration;
        bool sawContainer = false;

        while(DateTime.UtcNow < deadlineUtc)
        {
            if(ProcessRunner.CommandSucceedsBlocking("docker", ["rm", "-f", containerName]))
            {
                return;
            }

            bool containerExists = ContainerExists(containerName);
            if(containerExists)
            {
                sawContainer = true;
            }
            else if(sawContainer)
            {
                return;
            }

            await Task.Delay(_removalRetryInterval);
        }
    }

    private static async Task<bool> WaitForReadyAsync(string readyFilePath, TimeSpan timeout)
    {
        var deadlineUtc = DateTime.UtcNow + timeout;
        while(DateTime.UtcNow < deadlineUtc)
        {
            if(File.Exists(readyFilePath))
            {
                TryDeleteFile(readyFilePath);
                return true;
            }

            await Task.Delay(_readyPollInterval);
        }

        TryDeleteFile(readyFilePath);
        return false;
    }

    private static void WriteReadyFile(string readyFilePath)
    {
        try
        {
            string? directoryPath = Path.GetDirectoryName(readyFilePath);
            if(!String.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            File.WriteAllText(readyFilePath, "ready");
        }
        catch
        {
            // Best effort signaling only.
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if(File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string[] BuildWatchdogArgs(int parentPid, long parentStartTicks, string containerName, string sessionDirectoryPath, out string readyFilePath)
    {
        readyFilePath = Path.Combine(Path.GetTempPath(), WATCHDOG_READY_FILE_PREFIX + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + WATCHDOG_READY_FILE_SUFFIX);
        return
        [
            WATCHDOG_MODE,
            parentPid.ToString(CultureInfo.InvariantCulture),
            parentStartTicks.ToString(CultureInfo.InvariantCulture),
            containerName,
            sessionDirectoryPath,
            readyFilePath
        ];
    }

    private static bool TryParseWatchdogRunConfig(IReadOnlyList<string> args, out WatchdogRunConfig config)
    {
        config = default;

        if(args.Count != 6)
        {
            return false;
        }

        if(!Int32.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid) || parentPid <= 0)
        {
            return false;
        }

        if(!Int64.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out long parentStartTicks) || parentStartTicks <= 0)
        {
            return false;
        }

        string containerName = args[3];
        if(String.IsNullOrWhiteSpace(containerName))
        {
            return false;
        }

        string sessionDirectoryPath = args[4];
        string readyFilePath = args[5];
        if(String.IsNullOrWhiteSpace(readyFilePath))
        {
            return false;
        }

        config = new WatchdogRunConfig(parentPid, parentStartTicks, containerName, sessionDirectoryPath, readyFilePath);
        return true;
    }

    private static bool ContainerExists(string containerName) => ProcessRunner.CommandSucceedsBlocking("docker", ["container", "inspect", containerName]);

    private static void RegisterIgnoreTerminationHandlers()
    {
        if(!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            return;
        }

        lock(_signalRegistrationLock)
        {
            if(_ignoreSignalRegistrations is not null)
            {
                return;
            }

            _ignoreSignalRegistrations =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGHUP, static context => context.Cancel = true),
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, static context => context.Cancel = true),
                PosixSignalRegistration.Create(PosixSignal.SIGINT, static context => context.Cancel = true)
            ];
        }
    }
}
