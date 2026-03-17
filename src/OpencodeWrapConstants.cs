namespace OpencodeWrap;

internal static class OpencodeWrapConstants
{
    public const string CONTAINER_WORKSPACE = "/workspace";
    public const string CONTAINER_RESOURCE_ROOT = $"{CONTAINER_WORKSPACE}/.ocw-resources";
    public const string CONTAINER_OCW_ROOT = "/ocw";
    public const string CONTAINER_PERSISTENT_ROOT = $"{CONTAINER_OCW_ROOT}/state";
    public const string CONTAINER_SESSION_ROOT = $"{CONTAINER_OCW_ROOT}/session";
    public const string CONTAINER_TOOL_BIN_ROOT = $"{CONTAINER_SESSION_ROOT}/bin";
    public const string HOST_GLOBAL_CONFIG_DIRECTORY_NAME = ".opencode-wrap";
    public const string HOST_GLOBAL_AGENTS_FILE_NAME = "AGENTS.md";
    public const string HOST_RUN_MENU_DEFAULTS_FILE_NAME = "run-defaults.json";
    public const string HOST_LOCK_ROOT_DIRECTORY_NAME = "locks";
    public const string HOST_PROFILE_ROOT_DIRECTORY_NAME = "profiles";
    public const string HOST_SESSION_ROOT_DIRECTORY_NAME = "sessions";
    public const string HOST_TOOL_ROOT_DIRECTORY_NAME = "tools";
    public const string HOST_OPENCODE_TOOL_DIRECTORY_NAME = "opencode";
    public const string HOST_OPENCODE_CURRENT_DIRECTORY_NAME = "current";
    public const string HOST_OPENCODE_LEASE_DIRECTORY_NAME = "leases";
    public const string HOST_OPENCODE_METADATA_FILE_NAME = "metadata.json";
    public const string HOST_OPENCODE_LATEST_CACHE_FILE_NAME = "latest-release.json";
    public const string HOST_OPENCODE_LATEST_LOCK_FILE_NAME = "opencode-latest.lock";
    public const string HOST_OPENCODE_HOST_LOCK_FILE_NAME = "opencode-host.lock";
    public const string PROFILE_DOCKERFILE_NAME = "Dockerfile";
    public const string PROFILE_BIN_DIRECTORY_NAME = "bin";
    public const string PROFILE_OPENCODE_DIRECTORY_NAME = "opencode";
    public const string PROFILE_ENTRYPOINT_FILE_NAME = "entrypoint.sh";
    public const string XDG_VOLUME_NAME = "opencode-wrap-xdg";
    public const string CONTAINER_XDG_ROOT = CONTAINER_PERSISTENT_ROOT;
    public const string CONTAINER_XDG_CONFIG_HOME = $"{CONTAINER_PERSISTENT_ROOT}/.config";
    public const string CONTAINER_XDG_DATA_HOME = $"{CONTAINER_PERSISTENT_ROOT}/.local/share";
    public const string CONTAINER_XDG_STATE_HOME = $"{CONTAINER_PERSISTENT_ROOT}/.local/state";
    public const string CONTAINER_PROFILE_ROOT = $"{CONTAINER_SESSION_ROOT}/profile";
    public const string VOLUME_SHARE_SUBDIRECTORY = ".local/share/opencode";
    public const string VOLUME_STATE_SUBDIRECTORY = ".local/state/opencode";
}
