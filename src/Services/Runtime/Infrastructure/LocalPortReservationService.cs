using System.Net;
using System.Net.Sockets;

namespace OpencodeWrap.Services.Runtime.Infrastructure;

internal sealed partial class LocalPortReservationService : Singleton
{
    [Inject]
    private readonly DeferredSessionLogService _deferredSessionLogService;

    public bool TryReserveLoopbackPort(out ReservedLocalPort reservation)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            int port = ((IPEndPoint) listener.LocalEndpoint).Port;
            reservation = new ReservedLocalPort(port, listener);
            return true;
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole("startup", $"Failed to reserve a localhost port for the OpenCode backend: {ex.Message}");
            reservation = new ReservedLocalPort(0, null);
            return false;
        }
    }
}

internal sealed class ReservedLocalPort : IDisposable
{
    private TcpListener? _listener;

    public int Port { get; }

    internal ReservedLocalPort(int port, TcpListener? listener)
    {
        Port = port;
        _listener = listener;
    }

    public void Dispose()
    {
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Best effort release only.
        }
        finally
        {
            _listener = null;
        }
    }
}
