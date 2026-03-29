namespace OpencodeWrap.Services.Runtime.Core;

internal enum ContainerMountSourceType
{
    Directory,
    NamedVolume
}

internal enum ContainerMountAccessMode
{
    ReadOnly,
    ReadWrite
}

internal sealed record ContainerMount(ContainerMountSourceType SourceType, string Source, string ContainerPath, ContainerMountAccessMode AccessMode);
