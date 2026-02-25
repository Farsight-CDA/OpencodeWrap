internal static class OpencodeWrapConstants
{
    public const string CONTAINER_WORKSPACE = "/workspace";
    public const string CONTAINER_HOME = "/home/opencode";
    public const string HOST_GLOBAL_CONFIG_DIRECTORY_NAME = ".opencode-wrap";
    public const string DEFAULT_PROFILE_NAME = "default";
    public const string DOTNET_PROFILE_NAME = "dotnet";
    public const string DATA_SCIENCE_PROFILE_NAME = "data-science";
    public const string PROFILE_DOCKERFILE_NAME = "Dockerfile";
    public const string PROFILE_OPENCODE_DIRECTORY_NAME = "opencode";
    public const string XDG_VOLUME_NAME = "opencode-wrap-xdg";
    public const string CONTAINER_XDG_ROOT = "/home/opencode/";
    public const string CONTAINER_XDG_CONFIG_HOME = "/home/opencode/.config";
    public const string CONTAINER_XDG_DATA_HOME = "/home/opencode/.local/share";
    public const string CONTAINER_XDG_STATE_HOME = "/home/opencode/.local/state";
    public const string CONTAINER_HOST_CONFIG_SOURCE = "/opt/opencode-wrap/host-config";
    public const string VOLUME_SHARE_SUBDIRECTORY = ".local/share/opencode";
    public const string VOLUME_STATE_SUBDIRECTORY = ".local/state/opencode";
    public const string CONTAINER_COMMAND = "set -e; mkdir -p \"$XDG_CONFIG_HOME\" \"$XDG_DATA_HOME/opencode\" \"$XDG_STATE_HOME/opencode\" \"$HOME/.local/bin\"; rm -rf \"$XDG_CONFIG_HOME/opencode\"; mkdir -p \"$XDG_CONFIG_HOME/opencode\"; if [ -d \"" + CONTAINER_HOST_CONFIG_SOURCE + "\" ]; then cp -a " + CONTAINER_HOST_CONFIG_SOURCE + "/. \"$XDG_CONFIG_HOME/opencode/\"; fi; export PATH=\"/opt/opencode/.opencode/bin:/opt/opencode/.local/share/opencode/bin:/opt/opencode/.local/bin:$HOME/.opencode/bin:$XDG_DATA_HOME/opencode/bin:$HOME/.local/bin:$PATH\"; exec opencode \"$@\"";
}
