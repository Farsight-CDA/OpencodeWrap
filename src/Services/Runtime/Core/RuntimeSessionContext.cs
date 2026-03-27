namespace OpencodeWrap.Services.Runtime.Core;

internal sealed record RuntimeSessionContext(
    string SessionId,
    string HostSessionDirectory,
    int? HostPort = null,
    int? ContainerPort = null,
    string? AttachUrl = null,
    RunUiMode UiMode = RunUiMode.Tui
);
