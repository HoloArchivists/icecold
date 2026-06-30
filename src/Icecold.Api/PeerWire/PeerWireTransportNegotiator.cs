using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Microsoft.Extensions.Options;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireTransportNegotiator(
    PeerWireTorrentResolver torrents,
    IOptions<IcecoldOptions> options,
    ILogger<PeerWireTransportNegotiator> logger)
{
    const int MaxInitialPayloadLength = 4096;

    public async Task<Stream?> NegotiateAsync(Stream stream, CancellationToken cancellationToken)
    {
        var handshakeTimeout = TimeSpan.FromSeconds(options.Value.PeerWire.HandshakeTimeoutSeconds);
        var firstByte = await PeerWireStreamIo.ReadByteAsync(stream, handshakeTimeout, cancellationToken);
        if (firstByte < 0)
            return null;

        if (firstByte == PeerWireProtocol.ProtocolName.Length)
        {
            var protocol = new byte[PeerWireProtocol.ProtocolName.Length];
            if (!await PeerWireStreamIo.ReadExactlyOrFalseAsync(stream, protocol, handshakeTimeout, cancellationToken))
                return null;

            var prefix = new byte[protocol.Length + 1];
            prefix[0] = (byte)firstByte;
            protocol.CopyTo(prefix.AsSpan(1));
            if (protocol.AsSpan().SequenceEqual(PeerWireProtocol.ProtocolName))
                return new PeerWirePrefixedStream(stream, prefix);

            return await NegotiateMseAsync(stream, prefix, handshakeTimeout, cancellationToken);
        }

        return await NegotiateMseAsync(stream, [(byte)firstByte], handshakeTimeout, cancellationToken);
    }

    async Task<Stream?> NegotiateMseAsync(
        Stream stream,
        byte[] publicKeyPrefix,
        TimeSpan handshakeTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var peerPublicKey = new byte[PeerWireMse.KeySize];
            if (publicKeyPrefix.Length > peerPublicKey.Length)
                return null;

            publicKeyPrefix.CopyTo(peerPublicKey.AsSpan());
            if (!await PeerWireStreamIo.ReadExactlyOrFalseAsync(stream, peerPublicKey.AsMemory(publicKeyPrefix.Length), handshakeTimeout, cancellationToken))
                return null;

            var privateKey = PeerWireMse.GeneratePrivateKey();
            var publicKey = PeerWireMse.ComputePublicKey(privateKey);
            var secret = PeerWireMse.ComputeSecret(peerPublicKey, privateKey);
            await stream.WriteAsync(publicKey, cancellationToken);

            if (!await ReadUntilNeedleAsync(stream, PeerWireMse.HashReq1(secret), PeerWireMse.MaxPadLength, handshakeTimeout, cancellationToken))
                return null;

            var xor = new byte[20];
            if (!await PeerWireStreamIo.ReadExactlyOrFalseAsync(stream, xor, handshakeTimeout, cancellationToken))
                return null;

            var obfuscatedHash = PeerWireMse.Xor(xor, PeerWireMse.HashReq3(secret));
            var torrent = await torrents.ResolveByMseObfuscatedHashAsync(InfoHashUtil.ToHex(obfuscatedHash), cancellationToken);
            if (torrent is null)
            {
                logger.LogDebug("Peer-wire MSE connection requested an unknown or unavailable torrent.");
                return null;
            }

            var infoHash = Convert.FromHexString(torrent.InfoHashHex);
            var decrypt = CreateRc4(PeerWireMse.IncomingDecryptKey(secret, infoHash));

            var header = await ReadEncryptedAsync(stream, decrypt, 14, handshakeTimeout, cancellationToken);
            if (header is null || !header.AsSpan(0, 8).SequenceEqual(new byte[8]))
                return null;

            var cryptoProvide = PeerWireMse.ReadUInt32(header.AsSpan(8, 4));
            if ((cryptoProvide & PeerWireMse.CryptoRc4) == 0)
            {
                logger.LogDebug(
                    "Peer-wire MSE connection for {InfoHash} did not offer RC4; crypto_provide={CryptoProvide}.",
                    torrent.InfoHashHex,
                    cryptoProvide);
                return null;
            }

            var padCLength = PeerWireMse.ReadUInt16(header.AsSpan(12, 2));
            if (padCLength > PeerWireMse.MaxPadLength)
                return null;

            if (padCLength > 0 && await ReadEncryptedAsync(stream, decrypt, padCLength, handshakeTimeout, cancellationToken) is null)
                return null;

            var initialPayloadLengthBytes = await ReadEncryptedAsync(stream, decrypt, 2, handshakeTimeout, cancellationToken);
            if (initialPayloadLengthBytes is null)
                return null;

            var initialPayloadLength = PeerWireMse.ReadUInt16(initialPayloadLengthBytes);
            if (initialPayloadLength > MaxInitialPayloadLength)
                return null;

            var initialPayload = initialPayloadLength == 0
                ? Array.Empty<byte>()
                : await ReadEncryptedAsync(stream, decrypt, initialPayloadLength, handshakeTimeout, cancellationToken);
            if (initialPayload is null)
                return null;

            var encrypt = CreateRc4(PeerWireMse.IncomingEncryptKey(secret, infoHash));
            var response = new byte[14];
            PeerWireMse.WriteUInt32(response.AsSpan(8, 4), PeerWireMse.CryptoRc4);
            PeerWireMse.WriteUInt16(response.AsSpan(12, 2), 0);
            encrypt.Process(response);
            await stream.WriteAsync(response, cancellationToken);

            logger.LogDebug("Peer-wire MSE RC4 transport negotiated for {InfoHash}.", torrent.InfoHashHex);
            return new PeerWireMseStream(stream, decrypt, encrypt, initialPayload);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Peer-wire MSE negotiation failed.");
            return null;
        }
        catch (TimeoutException ex)
        {
            logger.LogDebug(ex, "Peer-wire MSE negotiation timed out.");
            return null;
        }
    }

    static PeerWireRc4 CreateRc4(byte[] key)
    {
        var rc4 = new PeerWireRc4(key);
        rc4.Discard(1024);
        return rc4;
    }

    static async Task<byte[]?> ReadEncryptedAsync(
        Stream stream,
        PeerWireRc4 rc4,
        int byteCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[byteCount];
        if (!await PeerWireStreamIo.ReadExactlyOrFalseAsync(stream, buffer, timeout, cancellationToken))
            return null;

        rc4.Process(buffer);
        return buffer;
    }

    async Task<bool> ReadUntilNeedleAsync(
        Stream stream,
        byte[] needle,
        int maxPaddingLength,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var window = new byte[needle.Length];
        var totalRead = 0;

        while (totalRead < maxPaddingLength + needle.Length)
        {
            var value = await PeerWireStreamIo.ReadByteAsync(stream, timeout, cancellationToken);
            if (value < 0)
                return false;

            if (totalRead < window.Length)
            {
                window[totalRead] = (byte)value;
            }
            else
            {
                window.AsSpan(1).CopyTo(window);
                window[^1] = (byte)value;
            }

            totalRead++;
            if (totalRead >= needle.Length && window.AsSpan().SequenceEqual(needle))
                return true;
        }

        logger.LogDebug(
            "Peer-wire MSE connection did not send req1 within {MaxPaddingLength} PadA bytes.",
            maxPaddingLength);
        return false;
    }
}
