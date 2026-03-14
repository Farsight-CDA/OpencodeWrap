namespace OpencodeWrap.Services.Runtime;

internal sealed record InteractiveSessionContext(
    string SessionId,
    string ContainerName,
    string HostSessionDirectory,
    string HostPasteDirectory,
    string ContainerPasteDirectory,
    int OwnerProcessId,
    long OwnerProcessStartTicks);
