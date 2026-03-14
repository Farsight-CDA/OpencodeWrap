namespace OpencodeWrap;

internal static class OpencodeWrapConstants
{
    public const string CONTAINER_WORKSPACE = "/workspace";
    public const string CONTAINER_RESOURCE_ROOT = CONTAINER_WORKSPACE + "/.ocw-resources";
    public const string CONTAINER_PASTE_ROOT = CONTAINER_WORKSPACE + "/.ocw-pastes";
    public const string CONTAINER_HOME = "/home/opencode";
    public const string HOST_GLOBAL_CONFIG_DIRECTORY_NAME = ".opencode-wrap";
    public const string HOST_SESSION_ROOT_DIRECTORY_NAME = "sessions";
    public const string HOST_SESSION_PASTE_DIRECTORY_NAME = "pastes";
    public const string PROFILE_DOCKERFILE_NAME = "Dockerfile";
    public const string PROFILE_OPENCODE_DIRECTORY_NAME = "opencode";
    public const string PROFILE_ENTRYPOINT_FILE_NAME = "entrypoint.sh";
    public const string XDG_VOLUME_NAME = "opencode-wrap-xdg";
    public const string CONTAINER_XDG_ROOT = "/home/opencode/";
    public const string CONTAINER_XDG_CONFIG_HOME = "/home/opencode/.config";
    public const string CONTAINER_XDG_DATA_HOME = "/home/opencode/.local/share";
    public const string CONTAINER_XDG_STATE_HOME = "/home/opencode/.local/state";
    public const string CONTAINER_PROFILE_ROOT = "/opt/opencode-wrap/profile";
    public const string CONTAINER_HOST_CONFIG_SOURCE = CONTAINER_PROFILE_ROOT + "/" + PROFILE_OPENCODE_DIRECTORY_NAME;
    public const string VOLUME_SHARE_SUBDIRECTORY = ".local/share/opencode";
    public const string VOLUME_STATE_SUBDIRECTORY = ".local/state/opencode";
}
