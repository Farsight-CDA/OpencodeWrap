namespace OpencodeWrap.Services.Runtime.Core;

internal sealed record RuntimeSessionContext(
    string SessionId,
    string HostSessionDirectory,
    int? Port = null,
    string? ServerUrl = null
);
