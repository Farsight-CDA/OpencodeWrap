using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpencodeWrap.Services.Runtime.Networking;

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

    public bool TrySelectUnusedTcpPort(out ReservedLocalPort reservation)
    {
        const int MIN_PORT = 49152;
        const int MAX_PORT = 65535;
        const int ATTEMPTS = 256;

        try
        {
            HashSet<int> activePorts =
            [
                .. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port)
            ];

            for(int attempt = 0; attempt < ATTEMPTS; attempt++)
            {
                int candidatePort = Random.Shared.Next(MIN_PORT, MAX_PORT + 1);
                if(activePorts.Add(candidatePort))
                {
                    reservation = new ReservedLocalPort(candidatePort, null);
                    return true;
                }
            }

            for(int candidatePort = MIN_PORT; candidatePort <= MAX_PORT; candidatePort++)
            {
                if(activePorts.Add(candidatePort))
                {
                    reservation = new ReservedLocalPort(candidatePort, null);
                    return true;
                }
            }
        }
        catch(Exception ex)
        {
            _deferredSessionLogService.WriteErrorOrConsole("startup", $"Failed to select an attach port for Windows host networking: {ex.Message}");
            reservation = new ReservedLocalPort(0, null);
            return false;
        }

        _deferredSessionLogService.WriteErrorOrConsole("startup", "Failed to find an unused attach port for Windows host networking.");
        reservation = new ReservedLocalPort(0, null);
        return false;
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
