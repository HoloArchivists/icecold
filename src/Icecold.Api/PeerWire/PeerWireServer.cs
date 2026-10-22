using System.Net;
using System.Net.Sockets;
using Icecold.Api.Options;
using Microsoft.Extensions.Options;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireServer(
    IOptions<IcecoldOptions> options,
    PeerWireTransportNegotiator transportNegotiator,
    PeerWireConnectionHandler handler,
    ILogger<PeerWireServer> logger) : BackgroundService
{
    static readonly TimeSpan AcceptFailureDelay = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var peerWire = options.Value.PeerWire;
        if (!peerWire.Enabled)
        {
            logger.LogInformation("Peer-wire seeding is disabled.");
            return;
        }

        var bindAddress = IPAddress.Parse(peerWire.BindAddress);
        var listener = new TcpListener(bindAddress, peerWire.ListenPort);
        var limiter = new PeerWireConnectionLimiter(peerWire.MaxConnections, peerWire.MaxConnectionsPerIp);
        var connections = new HashSet<Task>();

        listener.Start();
        logger.LogInformation("Peer-wire seeding listening on {BindAddress}:{Port}", bindAddress, peerWire.ListenPort);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A failed accept (e.g. EMFILE, or a connection reset while queued) must not
                    // fault ExecuteAsync and stop the host; back off briefly and keep listening.
                    logger.LogWarning(ex, "Peer-wire accept failed; retrying.");
                    try
                    {
                        await Task.Delay(AcceptFailureDelay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                IPAddress remoteAddress;
                try
                {
                    client.NoDelay = true;
                    client.ReceiveBufferSize = 256 * 1024;
                    client.SendBufferSize = 1024 * 1024;
                    if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndPoint)
                    {
                        client.Dispose();
                        continue;
                    }

                    remoteAddress = remoteEndPoint.Address;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    logger.LogDebug(ex, "Peer-wire could not configure accepted client.");
                    client.Dispose();
                    continue;
                }

                if (!limiter.TryAcquire(remoteAddress))
                {
                    logger.LogDebug("Peer-wire rejected connection from {RemoteAddress}: connection limit reached.", remoteAddress);
                    client.Dispose();
                    continue;
                }

                var connection = Task.Run(
                    () => HandleClientAsync(client, remoteAddress, limiter, peerWire, stoppingToken),
                    CancellationToken.None);
                lock (connections)
                    connections.Add(connection);

                _ = connection.ContinueWith(
                    completed =>
                    {
                        lock (connections)
                            connections.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();

            Task[] pending;
            lock (connections)
                pending = [.. connections];

            // Handlers observe stoppingToken; wait for them so nothing outlives the service.
            await Task.WhenAll(pending);
        }
    }

    async Task HandleClientAsync(
        TcpClient client,
        IPAddress remoteAddress,
        PeerWireConnectionLimiter limiter,
        PeerWireOptions peerWire,
        CancellationToken stoppingToken)
    {
        try
        {
            using var ownedClient = client;
            var handshakeDeadline = PeerWireDeadline.After(TimeSpan.FromSeconds(peerWire.HandshakeTimeoutSeconds));
            await using var stream = client.GetStream();
            await using var peerWireStream = await transportNegotiator.NegotiateAsync(stream, handshakeDeadline, stoppingToken);
            if (peerWireStream is not null)
                await handler.HandleAsync(peerWireStream, handshakeDeadline, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            logger.LogDebug(ex, "Peer-wire client connection closed.");
        }
        catch (TimeoutException ex)
        {
            logger.LogDebug(ex, "Peer-wire client connection timed out.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Peer-wire client connection from {RemoteAddress} failed unexpectedly.", remoteAddress);
        }
        finally
        {
            limiter.Release(remoteAddress);
        }
    }
}
