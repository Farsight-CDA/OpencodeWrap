internal sealed class OpencodeWrapServices
{
    public DockerHostService Host { get; }
    public OpencodeLauncherService Launcher { get; }
    public VolumeStateService Volume { get; }

    public OpencodeWrapServices()
    {
        Host = new DockerHostService();
        Volume = new VolumeStateService(Host);
        Launcher = new OpencodeLauncherService(Host, Volume);
    }
}
