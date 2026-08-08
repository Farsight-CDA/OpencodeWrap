namespace OpencodeWrap;

internal static class OpencodeWrapConstants
{
    public const string CONTAINER_WORKSPACE = "/workspace";
    public const string CONTAINER_RESOURCE_ROOT = $"{CONTAINER_WORKSPACE}/.ocw-resources";
    public const string CONTAINER_ADDITIONAL_MOUNT_ROOT = "/resources";
    public const string CONTAINER_OCW_ROOT = "/ocw";
    public const string CONTAINER_PERSISTENT_ROOT = $"{CONTAINER_OCW_ROOT}/state";
    public const string CONTAINER_SESSION_ROOT = $"{CONTAINER_OCW_ROOT}/session";
    public const string HOST_GLOBAL_CONFIG_DIRECTORY_NAME = ".opencode-wrap";
    public const string HOST_WORKSPACE_RUN_CONFIG_FILE_NAME = "ocw.json";
    public const string AGENTS_FILE_NAME = "AGENTS.md";
    public const string HOST_ADDON_ROOT_DIRECTORY_NAME = "addons";
    public const string HOST_RUN_MENU_DEFAULTS_FILE_NAME = "run-defaults.json";
    public const string HOST_LOCK_ROOT_DIRECTORY_NAME = "locks";
    public const string HOST_PROFILE_ROOT_DIRECTORY_NAME = "profiles";
    public const string HOST_SESSION_ROOT_DIRECTORY_NAME = "sessions";
    public const string HOST_TOOL_ROOT_DIRECTORY_NAME = "tools";
    public const string HOST_OPENCODE_TOOL_DIRECTORY_NAME = "opencode2";
    public const string HOST_OPENCODE_VERSION_DIRECTORY_NAME = "versions";
    public const string HOST_OPENCODE_LEASE_DIRECTORY_NAME = "leases";
    public const string HOST_OPENCODE_PACKAGE_CACHE_FILE_NAME = "package-release.json";
    public const string HOST_OPENCODE_PACKAGE_LOCK_FILE_NAME = "opencode2-package.lock";
    public const string HOST_OPENCODE_HOST_LOCK_FILE_NAME = "opencode2-host.lock";
    public const string OPENCODE_PASSWORD_ENVIRONMENT_VARIABLE = "OPENCODE_PASSWORD";
    public const string OPENCODE_DISABLE_AUTOUPDATE_ENVIRONMENT_VARIABLE = "OPENCODE_DISABLE_AUTOUPDATE";
    public const string OPENCODE_BASIC_AUTH_USERNAME = "opencode";
    public const string PROFILE_DOCKERFILE_NAME = "Dockerfile";
    public const string PROFILE_BIN_DIRECTORY_NAME = "bin";
    public const string PROFILE_ENV_FILE_NAME = ".env";
    public const string PROFILE_OPENCODE_DIRECTORY_NAME = "opencode";
    public const string PROFILE_OPENCODE_CONFIG_FILE_NAME = "opencode.json";
    public const string PROFILE_ENTRYPOINT_FILE_NAME = "entrypoint.sh";
    public const string XDG_VOLUME_NAME = "opencode-wrap-xdg-v2";
    public const string CONTAINER_XDG_ROOT = CONTAINER_PERSISTENT_ROOT;
    public const string CONTAINER_XDG_CONFIG_HOME = $"{CONTAINER_PERSISTENT_ROOT}/.config";
    public const string CONTAINER_XDG_DATA_HOME = $"{CONTAINER_PERSISTENT_ROOT}/.local/share";
    public const string CONTAINER_XDG_STATE_HOME = $"{CONTAINER_PERSISTENT_ROOT}/.local/state";
    public const string CONTAINER_XDG_CACHE_HOME = $"{CONTAINER_PERSISTENT_ROOT}/.cache";
    public const string CONTAINER_OPENCODE_WORKTREE_ROOT = $"{CONTAINER_XDG_DATA_HOME}/opencode/worktree";
    public const string CONTAINER_PROFILE_ROOT = $"{CONTAINER_SESSION_ROOT}/profile";
}
