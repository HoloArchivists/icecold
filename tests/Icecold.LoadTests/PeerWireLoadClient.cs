using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Icecold.Api.PeerWire;

namespace Icecold.LoadTests;

sealed class PeerWireLoadClient(string host, int port, bool encrypted, int blockLength, int maxOutstanding)
{
    static readonly byte[] ProtocolName = Encoding.ASCII.GetBytes("BitTorrent protocol");

    public async Task<TransferMeasurement> DownloadAsync(
        ReadyTorrent torrent,
        int clientIndex,
        CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, cancellationToken);
        await using var network = tcp.GetStream();

        var infoHash = Convert.FromHexString(torrent.InfoHash);
        var peerId = BuildPeerId(clientIndex);
        var handshake = BuildHandshake(infoHash, peerId);
        PeerWireTransport transport = encrypted
            ? await MseTransport.ConnectAsync(network, infoHash, handshake, cancellationToken)
            : new PlainTransport(network);
        await using (transport)
        {
            if (!encrypted)
                await transport.WriteAsync(handshake, cancellationToken);

            var serverHandshake = new byte[68];
            await transport.ReadExactlyAsync(serverHandshake, cancellationToken);
            if (!serverHandshake.AsSpan(28, 20).SequenceEqual(infoHash))
                throw new InvalidOperationException("Peer-wire server returned a handshake for a different infohash.");

            await WaitForUnchokeAsync(transport, cancellationToken);
            await transport.WriteAsync(BuildInterested(), cancellationToken);

            var stopwatch = Stopwatch.StartNew();
            var downloaded = await DownloadPiecesAsync(torrent, transport, cancellationToken);
            stopwatch.Stop();
            return new TransferMeasurement(downloaded, stopwatch.Elapsed);
        }
    }

    async Task<long> DownloadPiecesAsync(
        ReadyTorrent torrent,
        PeerWireTransport transport,
        CancellationToken cancellationToken)
    {
        long nextOffset = 0;
        long downloaded = 0;
        var outstanding = 0;

        while (downloaded < torrent.ContentLength)
        {
            while (outstanding < maxOutstanding && nextOffset < torrent.ContentLength)
            {
                var request = CreateRequest(torrent, nextOffset);
                await transport.WriteAsync(request.Message, cancellationToken);
                nextOffset += request.Length;
                outstanding++;
            }

            var message = await ReadMessageAsync(transport, cancellationToken);
            if (message.Id != 7)
                continue;

            if (message.PayloadLength < 8)
                throw new InvalidOperationException("Received a malformed piece message.");

            downloaded += message.PayloadLength - 8;
            outstanding--;
        }

        return downloaded;
    }

    PieceRequest CreateRequest(ReadyTorrent torrent, long absoluteOffset)
    {
        var pieceIndex = checked((int)(absoluteOffset / torrent.PieceLength));
        var begin = checked((int)(absoluteOffset % torrent.PieceLength));
        var remainingInPiece = torrent.PieceLength - begin;
        var remainingContent = torrent.ContentLength - absoluteOffset;
        var length = checked((int)Math.Min(Math.Min(blockLength, remainingInPiece), remainingContent));

        var message = new byte[17];
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(0, 4), 13);
        message[4] = 6;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(5, 4), pieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(9, 4), begin);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(13, 4), length);
        return new PieceRequest(message, length);
    }

    static async Task WaitForUnchokeAsync(PeerWireTransport transport, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 16; i++)
        {
            var message = await ReadMessageAsync(transport, cancellationToken);
            if (message.Id == 1)
                return;
        }

        throw new InvalidOperationException("Peer-wire server did not unchoke the load-test client.");
    }

    static async Task<PeerWireMessage> ReadMessageAsync(PeerWireTransport transport, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await transport.ReadExactlyAsync(prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length == 0)
            return new PeerWireMessage(255, 0);

        if (length < 0 || length > 128 * 1024)
            throw new InvalidOperationException($"Peer-wire message length {length} is outside the load-test safety limit.");

        var message = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await transport.ReadExactlyAsync(message.AsMemory(0, length), cancellationToken);
            return new PeerWireMessage(message[0], length - 1);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(message);
        }
    }

    static byte[] BuildHandshake(byte[] infoHash, byte[] peerId)
    {
        var result = new byte[68];
        result[0] = (byte)ProtocolName.Length;
        ProtocolName.CopyTo(result.AsSpan(1));
        infoHash.CopyTo(result.AsSpan(28));
        peerId.CopyTo(result.AsSpan(48));
        return result;
    }

    static byte[] BuildInterested()
    {
        var result = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), 1);
        result[4] = 2;
        return result;
    }

    static byte[] BuildPeerId(int clientIndex)
    {
        var peerId = Encoding.ASCII.GetBytes($"-ICLT00-{clientIndex:x12}");
        if (peerId.Length != 20)
            throw new InvalidOperationException("Generated peer id is not 20 bytes.");

        return peerId;
    }

    readonly record struct PieceRequest(byte[] Message, int Length);
    sealed record PeerWireMessage(byte Id, int PayloadLength);
}

abstract class PeerWireTransport(Stream stream) : IAsyncDisposable
{
    protected Stream Stream { get; } = stream;

    public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    public abstract Task ReadExactlyAsync(Memory<byte> bytes, CancellationToken cancellationToken);

    public virtual ValueTask DisposeAsync()
        => Stream.DisposeAsync();
}

sealed class PlainTransport(Stream stream) : PeerWireTransport(stream)
{
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        => Stream.WriteAsync(bytes, cancellationToken);

    public override async Task ReadExactlyAsync(Memory<byte> bytes, CancellationToken cancellationToken)
        => await Stream.ReadExactlyAsync(bytes, cancellationToken);
}

sealed class MseTransport(Stream stream, PeerWireRc4 encrypt, PeerWireRc4 decrypt) : PeerWireTransport(stream)
{
    public static async Task<MseTransport> ConnectAsync(
        Stream stream,
        byte[] infoHash,
        byte[] initialPayload,
        CancellationToken cancellationToken)
    {
        var privateKey = PeerWireMse.GeneratePrivateKey();
        var publicKey = PeerWireMse.ComputePublicKey(privateKey);
        await stream.WriteAsync(publicKey, cancellationToken);

        var serverPublicKey = new byte[PeerWireMse.KeySize];
        await stream.ReadExactlyAsync(serverPublicKey, cancellationToken);
        var secret = PeerWireMse.ComputeSecret(serverPublicKey, privateKey);

        await stream.WriteAsync(PeerWireMse.HashReq1(secret), cancellationToken);
        await stream.WriteAsync(PeerWireMse.Xor(PeerWireMse.HashReq2(infoHash), PeerWireMse.HashReq3(secret)), cancellationToken);

        var encrypt = CreateRc4(PeerWireMse.IncomingDecryptKey(secret, infoHash));
        var payload = new byte[16 + initialPayload.Length];
        PeerWireMse.WriteUInt32(payload.AsSpan(8, 4), PeerWireMse.CryptoRc4);
        PeerWireMse.WriteUInt16(payload.AsSpan(12, 2), 0);
        PeerWireMse.WriteUInt16(payload.AsSpan(14, 2), (ushort)initialPayload.Length);
        initialPayload.CopyTo(payload.AsSpan(16));
        encrypt.Process(payload);
        await stream.WriteAsync(payload, cancellationToken);

        var decrypt = CreateRc4(PeerWireMse.IncomingEncryptKey(secret, infoHash));
        var response = new byte[14];
        await stream.ReadExactlyAsync(response, cancellationToken);
        decrypt.Process(response);
        if (!response.AsSpan(0, 8).SequenceEqual(new byte[8])
            || PeerWireMse.ReadUInt32(response.AsSpan(8, 4)) != PeerWireMse.CryptoRc4)
        {
            throw new InvalidOperationException("Peer-wire MSE negotiation did not select RC4.");
        }

        return new MseTransport(stream, encrypt, decrypt);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(bytes.Length);
        try
        {
            encrypt.Process(bytes.Span, rented.AsSpan(0, bytes.Length));
            await Stream.WriteAsync(rented.AsMemory(0, bytes.Length), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override async Task ReadExactlyAsync(Memory<byte> bytes, CancellationToken cancellationToken)
    {
        await Stream.ReadExactlyAsync(bytes, cancellationToken);
        decrypt.Process(bytes.Span);
    }

    static PeerWireRc4 CreateRc4(byte[] key)
    {
        var rc4 = new PeerWireRc4(key);
        rc4.Discard(1024);
        return rc4;
    }
}
