internal sealed class OpencodeWrapServices
{
    public DockerHostService Host { get; }
    public DockerImageService Image { get; }
    public ProfileService Profiles { get; }
    public VolumeStateService Volume { get; }

    public OpencodeWrapServices()
    {
        Host = new DockerHostService();
        Image = new DockerImageService();
        Profiles = new ProfileService();
        Volume = new VolumeStateService(Host);
    }
}
