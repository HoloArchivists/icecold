using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Icecold.Api.Content;
using Icecold.Api.Options;
using Icecold.Api.Torrents;
using Microsoft.Extensions.Options;

namespace Icecold.Api.PeerWire;

public sealed class PeerWireConnectionHandler(
    PeerWireTorrentResolver torrents,
    PeerWirePeerIdentity identity,
    IOptions<IcecoldOptions> options,
    ILogger<PeerWireConnectionHandler> logger)
{
    static readonly byte[] ProtocolName = Encoding.ASCII.GetBytes("BitTorrent protocol");
    const int ExtensionProtocolReservedByteIndex = 25;
    const byte ExtensionProtocolReservedBit = 0x10;
    const byte LocalMetadataExtensionId = 1;
    const int MetadataPieceLength = 16 * 1024;

    public async Task HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        PeerWireTorrentContext? torrent = null;
        var infoHashHex = "";
        var state = new PeerWireSessionState();
        try
        {
            var handshake = await ReadHandshakeAsync(stream, cancellationToken);
            if (handshake is null)
            {
                logger.LogDebug("Peer-wire connection closed before a valid handshake was received.");
                return;
            }

            infoHashHex = InfoHashUtil.ToHex(handshake.InfoHash);
            logger.LogDebug(
                "Peer-wire handshake received for {InfoHash} from peer {PeerId}; extensions supported: {SupportsExtensions}.",
                infoHashHex,
                InfoHashUtil.ToHex(handshake.PeerId),
                handshake.SupportsExtensions);

            torrent = await torrents.ResolveAsync(infoHashHex, cancellationToken);
            if (torrent is null)
            {
                logger.LogDebug("Peer-wire connection requested unknown or unavailable torrent {InfoHash}.", infoHashHex);
                return;
            }

            await WriteHandshakeAsync(stream, handshake.InfoHash, cancellationToken);
            if (handshake.SupportsExtensions)
                await WriteExtensionHandshakeAsync(stream, torrent, cancellationToken);

            await WriteBitfieldAsync(stream, torrent.PieceCount, cancellationToken);
            await WriteMessageAsync(stream, PeerWireMessageId.Unchoke, ReadOnlyMemory<byte>.Empty, cancellationToken);
            logger.LogDebug(
                "Peer-wire announced {PieceCount} pieces and unchoked {InfoHash}.",
                torrent.PieceCount,
                torrent.InfoHashHex);

            await RunMessageLoopAsync(stream, torrent, state, cancellationToken);
            logger.LogDebug("Peer-wire message loop ended for {InfoHash}.", torrent.InfoHashHex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or ContentSourceException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Peer-wire connection closed for torrent {InfoHash}.", torrent?.InfoHashHex ?? infoHashHex);
        }
        finally
        {
            await state.DisposeAsync();
        }
    }

    async Task<PeerWireHandshake?> ReadHandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var pstrlen = await ReadByteAsync(stream, cancellationToken);
        if (pstrlen != ProtocolName.Length)
            return null;

        var protocol = new byte[pstrlen];
        if (!await ReadExactlyOrFalseAsync(stream, protocol, cancellationToken))
            return null;

        if (!protocol.AsSpan().SequenceEqual(ProtocolName))
            return null;

        var reservedAndHashes = new byte[48];
        if (!await ReadExactlyOrFalseAsync(stream, reservedAndHashes, cancellationToken))
            return null;

        return new PeerWireHandshake(
            reservedAndHashes.AsSpan(8, 20).ToArray(),
            reservedAndHashes.AsSpan(28, 20).ToArray(),
            (reservedAndHashes[5] & ExtensionProtocolReservedBit) != 0);
    }

    async Task RunMessageLoopAsync(
        Stream stream,
        PeerWireTorrentContext torrent,
        PeerWireSessionState state,
        CancellationToken cancellationToken)
    {
        var lengthPrefix = new byte[4];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await ReadExactlyOrFalseAsync(stream, lengthPrefix, cancellationToken))
            {
                logger.LogDebug("Peer-wire client closed connection for {InfoHash}.", torrent.InfoHashHex);
                return;
            }

            var messageLength = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
            if (messageLength == 0)
            {
                logger.LogTrace("Peer-wire keepalive received for {InfoHash}.", torrent.InfoHashHex);
                continue;
            }
            if (messageLength < 0 || messageLength > options.Value.PeerWire.MaxBlockLength + 1024)
            {
                logger.LogDebug(
                    "Peer-wire closing {InfoHash}: invalid message length {MessageLength}.",
                    torrent.InfoHashHex,
                    messageLength);
                return;
            }

            var messageId = await ReadByteAsync(stream, cancellationToken);
            if (messageId < 0)
            {
                logger.LogDebug("Peer-wire client closed after length prefix for {InfoHash}.", torrent.InfoHashHex);
                return;
            }

            var payloadLength = messageLength - 1;
            switch ((PeerWireMessageId)messageId)
            {
                case PeerWireMessageId.Interested:
                    logger.LogDebug("Peer-wire interested received for {InfoHash}.", torrent.InfoHashHex);
                    if (payloadLength > 0 && !await DrainAsync(stream, payloadLength, cancellationToken))
                        return;
                    break;
                case PeerWireMessageId.NotInterested:
                    logger.LogDebug("Peer-wire not interested received for {InfoHash}.", torrent.InfoHashHex);
                    if (payloadLength > 0 && !await DrainAsync(stream, payloadLength, cancellationToken))
                        return;
                    break;
                case PeerWireMessageId.Request when payloadLength == 12:
                    var payload = new byte[12];
                    if (!await ReadExactlyOrFalseAsync(stream, payload, cancellationToken))
                        return;

                    var pieceIndex = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
                    var begin = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4, 4));
                    var length = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(8, 4));
                    logger.LogDebug(
                        "Peer-wire request received for {InfoHash}: piece {PieceIndex}, begin {Begin}, length {Length}.",
                        torrent.InfoHashHex,
                        pieceIndex,
                        begin,
                        length);

                    if (!PeerWirePieceRequest.TryCreate(
                            torrent,
                            pieceIndex,
                            begin,
                            length,
                            options.Value.PeerWire.MaxBlockLength,
                            out var request))
                    {
                        logger.LogDebug(
                            "Peer-wire closing {InfoHash}: invalid request piece {PieceIndex}, begin {Begin}, length {Length}.",
                            torrent.InfoHashHex,
                            pieceIndex,
                            begin,
                            length);
                        return;
                    }

                    await WritePieceAsync(stream, torrent, state, request, cancellationToken);
                    break;
                case PeerWireMessageId.Request:
                    logger.LogDebug(
                        "Peer-wire closing {InfoHash}: request payload length was {PayloadLength}, expected 12.",
                        torrent.InfoHashHex,
                        payloadLength);
                    return;
                case PeerWireMessageId.Extended when payloadLength > 0:
                    var extendedPayload = new byte[payloadLength];
                    if (!await ReadExactlyOrFalseAsync(stream, extendedPayload, cancellationToken))
                        return;

                    await HandleExtendedMessageAsync(stream, torrent, state, extendedPayload, cancellationToken);
                    break;
                case PeerWireMessageId.Extended:
                    logger.LogDebug("Peer-wire closing {InfoHash}: empty extended message.", torrent.InfoHashHex);
                    return;
                default:
                    logger.LogTrace(
                        "Peer-wire message {MessageId} received for {InfoHash} with {PayloadLength} payload bytes.",
                        messageId,
                        torrent.InfoHashHex,
                        payloadLength);
                    if (payloadLength > 0 && !await DrainAsync(stream, payloadLength, cancellationToken))
                    {
                        logger.LogDebug(
                            "Peer-wire client closed while draining message {MessageId} for {InfoHash}.",
                            messageId,
                            torrent.InfoHashHex);
                        return;
                    }
                    break;
            }
        }
    }

    async Task WriteHandshakeAsync(Stream stream, byte[] infoHash, CancellationToken cancellationToken)
    {
        var handshake = new byte[68];
        handshake[0] = (byte)ProtocolName.Length;
        ProtocolName.CopyTo(handshake.AsSpan(1));
        handshake[ExtensionProtocolReservedByteIndex] = ExtensionProtocolReservedBit;
        infoHash.CopyTo(handshake.AsSpan(28));
        identity.PeerId.CopyTo(handshake.AsSpan(48));
        await stream.WriteAsync(handshake, cancellationToken);
    }

    static async Task WriteExtensionHandshakeAsync(
        Stream stream,
        PeerWireTorrentContext torrent,
        CancellationToken cancellationToken)
    {
        var payload = Bencode.Encode(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["m"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ut_metadata"] = (long)LocalMetadataExtensionId
            },
            ["metadata_size"] = (long)torrent.InfoBytes.Length,
            ["reqq"] = 128L,
            ["v"] = "Icecold"
        });

        await WriteExtendedMessageAsync(stream, 0, payload, cancellationToken);
    }

    async Task HandleExtendedMessageAsync(
        Stream stream,
        PeerWireTorrentContext torrent,
        PeerWireSessionState state,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var extensionId = payload[0];
        var extensionPayload = payload.AsSpan(1);
        if (extensionId == 0)
        {
            HandleRemoteExtensionHandshake(torrent, state, extensionPayload);
            return;
        }

        if (extensionId != LocalMetadataExtensionId)
        {
            logger.LogTrace(
                "Peer-wire extended message {ExtensionId} received for {InfoHash} with {PayloadLength} payload bytes.",
                extensionId,
                torrent.InfoHashHex,
                extensionPayload.Length);
            return;
        }

        if (!PeerWireBencode.TryDecodeDictionary(extensionPayload, out var request)
            || !PeerWireBencode.TryGetInteger(request, "msg_type", out var messageType)
            || !PeerWireBencode.TryGetInteger(request, "piece", out var piece))
        {
            logger.LogDebug("Peer-wire invalid metadata extension payload received for {InfoHash}.", torrent.InfoHashHex);
            return;
        }

        if (messageType != 0)
        {
            logger.LogTrace(
                "Peer-wire ignored metadata extension message type {MessageType} for {InfoHash}.",
                messageType,
                torrent.InfoHashHex);
            return;
        }

        await WriteMetadataPieceAsync(stream, torrent, state, piece, cancellationToken);
    }

    void HandleRemoteExtensionHandshake(
        PeerWireTorrentContext torrent,
        PeerWireSessionState state,
        ReadOnlySpan<byte> payload)
    {
        if (!PeerWireBencode.TryDecodeDictionary(payload, out var handshake))
        {
            logger.LogDebug("Peer-wire invalid extension handshake received for {InfoHash}.", torrent.InfoHashHex);
            return;
        }

        if (!PeerWireBencode.TryGetDictionary(handshake, "m", out var mappings)
            || !PeerWireBencode.TryGetInteger(mappings, "ut_metadata", out var metadataExtensionId)
            || metadataExtensionId is < 1 or > byte.MaxValue)
        {
            logger.LogTrace("Peer-wire extension handshake for {InfoHash} did not advertise ut_metadata.", torrent.InfoHashHex);
            return;
        }

        state.RemoteMetadataExtensionId = (byte)metadataExtensionId;
        logger.LogDebug(
            "Peer-wire remote ut_metadata extension id for {InfoHash} is {ExtensionId}.",
            torrent.InfoHashHex,
            state.RemoteMetadataExtensionId);
    }

    async Task WriteMetadataPieceAsync(
        Stream stream,
        PeerWireTorrentContext torrent,
        PeerWireSessionState state,
        long requestedPiece,
        CancellationToken cancellationToken)
    {
        var pieceCount = (torrent.InfoBytes.Length + MetadataPieceLength - 1) / MetadataPieceLength;
        if (requestedPiece < 0 || requestedPiece >= pieceCount)
        {
            await WriteMetadataRejectAsync(stream, state, requestedPiece, cancellationToken);
            logger.LogDebug(
                "Peer-wire rejected metadata request for {InfoHash}: piece {Piece}.",
                torrent.InfoHashHex,
                requestedPiece);
            return;
        }

        var piece = (int)requestedPiece;
        var offset = piece * MetadataPieceLength;
        var length = Math.Min(MetadataPieceLength, torrent.InfoBytes.Length - offset);
        var header = Bencode.Encode(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["msg_type"] = 1L,
            ["piece"] = requestedPiece,
            ["total_size"] = (long)torrent.InfoBytes.Length
        });

        var payload = new byte[header.Length + length];
        header.CopyTo(payload.AsSpan(0, header.Length));
        torrent.InfoBytes.AsSpan(offset, length).CopyTo(payload.AsSpan(header.Length));

        await WriteExtendedMessageAsync(
            stream,
            state.RemoteMetadataExtensionId ?? LocalMetadataExtensionId,
            payload,
            cancellationToken);

        logger.LogDebug(
            "Peer-wire metadata piece sent for {InfoHash}: piece {Piece}, length {Length}.",
            torrent.InfoHashHex,
            requestedPiece,
            length);
    }

    static async Task WriteMetadataRejectAsync(
        Stream stream,
        PeerWireSessionState state,
        long requestedPiece,
        CancellationToken cancellationToken)
    {
        var payload = Bencode.Encode(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["msg_type"] = 2L,
            ["piece"] = requestedPiece
        });

        await WriteExtendedMessageAsync(stream, state.RemoteMetadataExtensionId ?? LocalMetadataExtensionId, payload, cancellationToken);
    }

    static async Task WriteBitfieldAsync(Stream stream, int pieceCount, CancellationToken cancellationToken)
    {
        var bitfield = new byte[(pieceCount + 7) / 8];
        for (var i = 0; i < pieceCount; i++)
            bitfield[i / 8] |= (byte)(1 << (7 - (i % 8)));

        await WriteMessageAsync(stream, PeerWireMessageId.Bitfield, bitfield, cancellationToken);
    }

    async Task WritePieceAsync(
        Stream stream,
        PeerWireTorrentContext torrent,
        PeerWireSessionState state,
        PeerWirePieceRequest request,
        CancellationToken cancellationToken)
    {
        var buffer = state.GetBlockBuffer(request.Length);
        await ReadPieceBlockAsync(torrent, state, request, buffer.AsMemory(0, request.Length), cancellationToken);

        var prefix = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(0, 4), 9 + request.Length);
        prefix[4] = (byte)PeerWireMessageId.Piece;
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(5, 4), request.PieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(9, 4), request.Begin);

        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(buffer.AsMemory(0, request.Length), cancellationToken);
        logger.LogDebug(
            "Peer-wire piece sent for {InfoHash}: piece {PieceIndex}, begin {Begin}, length {Length}.",
            torrent.InfoHashHex,
            request.PieceIndex,
            request.Begin,
            request.Length);
    }

    static async Task ReadPieceBlockAsync(
        PeerWireTorrentContext torrent,
        PeerWireSessionState state,
        PeerWirePieceRequest request,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var seekable = await state.GetSeekableContentAsync(torrent, cancellationToken);
        if (seekable is not null)
        {
            seekable.Seek(request.Offset, SeekOrigin.Begin);
            await ReadContentExactlyAsync(seekable, buffer, torrent.SourcePath, cancellationToken);
            return;
        }

        await using var content = await torrent.Source.OpenRangeAsync(torrent.SourcePath, request.Offset, request.Length, cancellationToken);
        await ReadContentExactlyAsync(content, buffer, torrent.SourcePath, cancellationToken);
    }

    static async Task ReadContentExactlyAsync(
        Stream content,
        Memory<byte> buffer,
        string path,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await content.ReadAsync(buffer[read..], cancellationToken);
            if (current == 0)
                throw new ContentSourceException($"Could not read requested peer-wire block from '{path}'.");

            read += current;
        }
    }

    static async Task WriteMessageAsync(
        Stream stream,
        PeerWireMessageId messageId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(0, 4), payload.Length + 1);
        prefix[4] = (byte)messageId;
        await stream.WriteAsync(prefix, cancellationToken);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, cancellationToken);
    }

    static async Task WriteExtendedMessageAsync(
        Stream stream,
        byte extensionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[6];
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(0, 4), payload.Length + 2);
        prefix[4] = (byte)PeerWireMessageId.Extended;
        prefix[5] = extensionId;
        await stream.WriteAsync(prefix, cancellationToken);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, cancellationToken);
    }

    static async Task<bool> DrainAsync(Stream stream, int byteCount, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(byteCount, 16 * 1024));
        try
        {
            var remaining = byteCount;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                    return false;

                remaining -= read;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        return await ReadExactlyOrFalseAsync(stream, one, cancellationToken) ? one[0] : -1;
    }

    static async Task<bool> ReadExactlyOrFalseAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (current == 0)
                return false;

            read += current;
        }

        return true;
    }
}

sealed record PeerWireHandshake(byte[] InfoHash, byte[] PeerId, bool SupportsExtensions);

sealed class PeerWireSessionState : IAsyncDisposable
{
    byte[]? blockBuffer;
    Stream? seekableContent;

    public byte? RemoteMetadataExtensionId { get; set; }

    public byte[] GetBlockBuffer(int length)
    {
        if (blockBuffer is null || blockBuffer.Length < length)
        {
            if (blockBuffer is not null)
                ArrayPool<byte>.Shared.Return(blockBuffer);

            blockBuffer = ArrayPool<byte>.Shared.Rent(length);
        }

        return blockBuffer;
    }

    public async Task<Stream?> GetSeekableContentAsync(PeerWireTorrentContext torrent, CancellationToken cancellationToken)
    {
        if (seekableContent is not null)
            return seekableContent;

        if (torrent.Source is not ISeekableContentSource seekableSource)
            return null;

        var opened = await seekableSource.OpenSeekableReadAsync(torrent.SourcePath, cancellationToken);
        if (!opened.CanSeek)
        {
            await opened.DisposeAsync();
            return null;
        }

        seekableContent = opened;
        return seekableContent;
    }

    public async ValueTask DisposeAsync()
    {
        if (seekableContent is not null)
            await seekableContent.DisposeAsync();

        if (blockBuffer is not null)
            ArrayPool<byte>.Shared.Return(blockBuffer);
    }
}

enum PeerWireMessageId : byte
{
    Choke = 0,
    Unchoke = 1,
    Interested = 2,
    NotInterested = 3,
    Have = 4,
    Bitfield = 5,
    Request = 6,
    Piece = 7,
    Cancel = 8,
    Port = 9,
    Extended = 20
}
