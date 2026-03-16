namespace OpencodeWrap.Services.Opencode;

internal sealed record OcwHostPaths(
    string ConfigRoot,
    string ProfilesRoot,
    string SessionsRoot,
    string ToolsRoot,
    string LocksRoot,
    string OpencodeRoot,
    string OpencodeCurrentRoot,
    string OpencodeLeasesRoot,
    string OpencodeMetadataPath,
    string OpencodeLatestCachePath,
    string OpencodeLatestLockPath,
    string OpencodeHostLockPath
);

internal sealed partial class OcwHostPathService : Singleton
{
    [Inject]
    private readonly DockerHostService _dockerHostService;

    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public bool TryGetPaths(out OcwHostPaths paths)
    {
        paths = new OcwHostPaths("", "", "", "", "", "", "", "", "", "", "", "");
        if(!_dockerHostService.TryEnsureGlobalConfigDirectory(out string configRoot))
        {
            return false;
        }

        string profilesRoot = Path.Combine(configRoot, OpencodeWrapConstants.HOST_PROFILE_ROOT_DIRECTORY_NAME);
        string sessionsRoot = Path.Combine(configRoot, OpencodeWrapConstants.HOST_SESSION_ROOT_DIRECTORY_NAME);
        string toolsRoot = Path.Combine(configRoot, OpencodeWrapConstants.HOST_TOOL_ROOT_DIRECTORY_NAME);
        string locksRoot = Path.Combine(configRoot, OpencodeWrapConstants.HOST_LOCK_ROOT_DIRECTORY_NAME);
        string opencodeRoot = Path.Combine(toolsRoot, OpencodeWrapConstants.HOST_OPENCODE_TOOL_DIRECTORY_NAME);
        string opencodeCurrentRoot = Path.Combine(opencodeRoot, OpencodeWrapConstants.HOST_OPENCODE_CURRENT_DIRECTORY_NAME);
        string opencodeLeasesRoot = Path.Combine(opencodeRoot, OpencodeWrapConstants.HOST_OPENCODE_LEASE_DIRECTORY_NAME);

        try
        {
            Directory.CreateDirectory(profilesRoot);
            Directory.CreateDirectory(sessionsRoot);
            Directory.CreateDirectory(toolsRoot);
            Directory.CreateDirectory(locksRoot);
            Directory.CreateDirectory(opencodeRoot);
            Directory.CreateDirectory(opencodeLeasesRoot);
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole(LogCategories.OpencodeHost, $"Failed to prepare OCW directories under '{configRoot}': {ex.Message}");
            return false;
        }

        paths = new OcwHostPaths(
            configRoot,
            profilesRoot,
            sessionsRoot,
            toolsRoot,
            locksRoot,
            opencodeRoot,
            opencodeCurrentRoot,
            opencodeLeasesRoot,
            Path.Combine(opencodeRoot, OpencodeWrapConstants.HOST_OPENCODE_METADATA_FILE_NAME),
            Path.Combine(opencodeRoot, OpencodeWrapConstants.HOST_OPENCODE_LATEST_CACHE_FILE_NAME),
            Path.Combine(locksRoot, OpencodeWrapConstants.HOST_OPENCODE_LATEST_LOCK_FILE_NAME),
            Path.Combine(locksRoot, OpencodeWrapConstants.HOST_OPENCODE_HOST_LOCK_FILE_NAME));
        return true;
    }
}
