namespace OpencodeWrap;

internal static class OpencodeWrapConstants
{
    public const string CONTAINER_WORKSPACE = "/workspace";
    public const string CONTAINER_RESOURCE_ROOT = $"{CONTAINER_WORKSPACE}/.ocw-resources";
    public const string CONTAINER_PASTE_ROOT = $"{CONTAINER_WORKSPACE}/.ocw-pastes";
    public const string CONTAINER_OCW_ROOT = "/ocw";
    public const string CONTAINER_PERSISTENT_ROOT = $"{CONTAINER_OCW_ROOT}/state";
    public const string CONTAINER_SESSION_ROOT = $"{CONTAINER_OCW_ROOT}/session";
    public const string CONTAINER_TOOL_BIN_ROOT = $"{CONTAINER_SESSION_ROOT}/bin";
    public const string HOST_GLOBAL_CONFIG_DIRECTORY_NAME = ".opencode-wrap";
    public const string HOST_PROFILE_ROOT_DIRECTORY_NAME = "profiles";
    public const string HOST_SESSION_ROOT_DIRECTORY_NAME = "sessions";
    public const string HOST_SESSION_PASTE_DIRECTORY_NAME = "pastes";
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
