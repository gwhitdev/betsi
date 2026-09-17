namespace Betsi.Tests.Infrastructure;

using System.Net.Sockets;

/// <summary>Whether SQL Server tests can start a container here.</summary>
public static class Docker
{
    public const string UnavailableReason = "No Docker daemon is reachable, so SQL Server cannot be started.";

    public static async Task<bool> IsAvailableAsync()
    {
        // Probing the socket directly rather than asking Testcontainers, whose discovery
        // types are internal. A daemon that is installed but not running is the common case
        // on a developer workstation.
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(
                        AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint("/var/run/docker.sock"), cancellationToken);

                    return new NetworkStream(socket, ownsSocket: true);
                }
            })
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            using var response = await client.GetAsync("http://localhost/_ping");
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
