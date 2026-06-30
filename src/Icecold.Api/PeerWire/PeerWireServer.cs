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
        using var connectionSlots = new SemaphoreSlim(peerWire.MaxConnections);

        listener.Start();
        logger.LogInformation("Peer-wire seeding listening on {BindAddress}:{Port}", bindAddress, peerWire.ListenPort);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                client.NoDelay = true;
                client.ReceiveBufferSize = 256 * 1024;
                client.SendBufferSize = 1024 * 1024;
                if (!connectionSlots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    using var ownedClient = client;
                    try
                    {
                        await using var stream = client.GetStream();
                        await using var peerWireStream = await transportNegotiator.NegotiateAsync(stream, stoppingToken);
                        if (peerWireStream is not null)
                            await handler.HandleAsync(peerWireStream, stoppingToken);
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
                    finally
                    {
                        connectionSlots.Release();
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }
}
