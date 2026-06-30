using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Text;
using Icecold.Api.Content;
using Icecold.Api.Data;
using Icecold.Api.Options;
using Icecold.Api.PeerWire;
using Icecold.Api.Torrents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MonoTorrent.BEncoding;

namespace Icecold.Tests;

public sealed class PeerWireTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "icecold-tests", Guid.NewGuid().ToString("n"));

    public PeerWireTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public void PieceRequest_Rejects_Final_Piece_Overrun()
    {
        var torrent = new PeerWireTorrentContext(
            new string('a', 40),
            new LocalFileContentSource("local", root),
            "sample.bin",
            ContentLength: 10,
            PieceLength: 4,
            PieceCount: 3,
            InfoBytes: []);

        Assert.False(PeerWirePieceRequest.TryCreate(torrent, 2, 2, 4, 16 * 1024, out _));
        Assert.True(PeerWirePieceRequest.TryCreate(torrent, 2, 0, 2, 16 * 1024, out var request));
        Assert.Equal(8, request.Offset);
    }

    [Fact]
    public async Task TransportNegotiator_Preserves_Plaintext_Handshake_Byte()
    {
        var handshake = BuildHandshake(Enumerable.Repeat((byte)1, 20).ToArray(), Enumerable.Repeat((byte)2, 20).ToArray());
        await using var raw = new MemoryStream(handshake);
        await using var db = CreateEmptyDb();
        await using var provider = CreateProvider(db);
        var negotiator = CreateTransportNegotiator(provider);

        await using var negotiated = await negotiator.NegotiateAsync(raw, CancellationToken.None);

        Assert.NotNull(negotiated);
        var buffer = new byte[handshake.Length];
        var read = await negotiated.ReadAsync(buffer, CancellationToken.None);
        Assert.Equal(buffer.Length, read);
        Assert.Equal(handshake, buffer);
    }

    [Fact]
    public async Task TransportNegotiator_Rejects_Unsupported_First_Byte()
    {
        await using var raw = new MemoryStream(new byte[] { 1, 2, 3 });
        await using var db = CreateEmptyDb();
        await using var provider = CreateProvider(db);
        var negotiator = CreateTransportNegotiator(provider);

        var negotiated = await negotiator.NegotiateAsync(raw, CancellationToken.None);

        Assert.Null(negotiated);
    }

    [Fact]
    public async Task Handler_Serves_Requested_Block()
    {
        var content = Encoding.ASCII.GetBytes("hello peer wire");
        await File.WriteAllBytesAsync(Path.Combine(root, "payload.bin"), content);
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await new TorrentBuilder(new PublicUrlBuilder(Options.Create(new IcecoldOptions { PublicBaseUrl = "http://example.test" })))
            .BuildSingleFileAsync(metadata, source, null, CancellationToken.None);
        await using var db = CreateDb(torrentResult, metadata);
        await using var provider = CreateProvider(db);
        var handler = new PeerWireConnectionHandler(
            new PeerWireTorrentResolver(new TestScopeFactory(provider), CreateRegistry()),
            new PeerWirePeerIdentity(),
            Options.Create(new IcecoldOptions { PeerWire = new PeerWireOptions { Enabled = true, MaxBlockLength = 16 * 1024 } }),
            NullLogger<PeerWireConnectionHandler>.Instance);
        await using var stream = new DuplexTestStream();
        var peerId = Enumerable.Repeat((byte)'P', 20).ToArray();

        await stream.WriteClientAsync(BuildHandshake(Convert.FromHexString(torrentResult.InfoHashHex), peerId));
        var handling = handler.HandleAsync(stream.ServerStream, CancellationToken.None);

        var serverHandshake = await stream.ReadClientAsync(68);
        Assert.Equal(19, serverHandshake[0]);
        Assert.Equal("BitTorrent protocol", Encoding.ASCII.GetString(serverHandshake, 1, 19));
        Assert.Equal(torrentResult.InfoHashHex, Convert.ToHexString(serverHandshake.AsSpan(28, 20)).ToLowerInvariant());

        var bitfield = await ReadMessageAsync(stream);
        Assert.Equal(5, bitfield.Id);
        Assert.NotEmpty(bitfield.Payload);

        var unchoke = await ReadMessageAsync(stream);
        Assert.Equal(1, unchoke.Id);
        Assert.Empty(unchoke.Payload);

        await stream.WriteClientAsync(BuildInterested());
        await stream.WriteClientAsync(BuildRequest(pieceIndex: 0, begin: 6, length: 4));

        var piece = await ReadMessageAsync(stream);
        Assert.Equal(7, piece.Id);
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(piece.Payload.AsSpan(0, 4)));
        Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(piece.Payload.AsSpan(4, 4)));
        Assert.Equal("peer", Encoding.ASCII.GetString(piece.Payload.AsSpan(8)));

        stream.CompleteClientWrites();
        await handling.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Handler_Serves_Requested_Block_Over_Mse()
    {
        var content = Encoding.ASCII.GetBytes("hello encrypted peer wire");
        await File.WriteAllBytesAsync(Path.Combine(root, "payload.bin"), content);
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await new TorrentBuilder(new PublicUrlBuilder(Options.Create(new IcecoldOptions { PublicBaseUrl = "http://example.test" })))
            .BuildSingleFileAsync(metadata, source, null, CancellationToken.None);
        await using var db = CreateDb(torrentResult, metadata);
        await using var provider = CreateProvider(db);
        var handler = new PeerWireConnectionHandler(
            new PeerWireTorrentResolver(new TestScopeFactory(provider), CreateRegistry()),
            new PeerWirePeerIdentity(),
            Options.Create(new IcecoldOptions { PeerWire = new PeerWireOptions { Enabled = true, MaxBlockLength = 16 * 1024 } }),
            NullLogger<PeerWireConnectionHandler>.Instance);
        var negotiator = CreateTransportNegotiator(provider);
        await using var stream = new DuplexTestStream();
        var peerId = Enumerable.Repeat((byte)'E', 20).ToArray();

        var handling = Task.Run(async () =>
        {
            await using var peerWireStream = await negotiator.NegotiateAsync(stream.ServerStream, CancellationToken.None);
            Assert.NotNull(peerWireStream);
            await handler.HandleAsync(peerWireStream, CancellationToken.None);
        });

        var client = await CompleteMseHandshakeAsync(
            stream,
            torrentResult.InfoHashHex,
            BuildHandshake(Convert.FromHexString(torrentResult.InfoHashHex), peerId));

        var serverHandshake = await client.ReadAsync(68);
        Assert.Equal(19, serverHandshake[0]);
        Assert.Equal("BitTorrent protocol", Encoding.ASCII.GetString(serverHandshake, 1, 19));
        Assert.Equal(torrentResult.InfoHashHex, Convert.ToHexString(serverHandshake.AsSpan(28, 20)).ToLowerInvariant());

        var bitfield = await ReadMessageAsync(client);
        Assert.Equal(5, bitfield.Id);

        var unchoke = await ReadMessageAsync(client);
        Assert.Equal(1, unchoke.Id);

        await client.WriteAsync(BuildInterested());
        await client.WriteAsync(BuildRequest(pieceIndex: 0, begin: 6, length: 9));

        var piece = await ReadMessageAsync(client);
        Assert.Equal(7, piece.Id);
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(piece.Payload.AsSpan(0, 4)));
        Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(piece.Payload.AsSpan(4, 4)));
        Assert.Equal("encrypted", Encoding.ASCII.GetString(piece.Payload.AsSpan(8)));

        stream.CompleteClientWrites();
        await handling.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Handler_Serves_Metadata_Extension_Request()
    {
        var content = Encoding.ASCII.GetBytes("hello metadata");
        await File.WriteAllBytesAsync(Path.Combine(root, "payload.bin"), content);
        var source = new LocalFileContentSource("local", root);
        var metadata = await source.GetMetadataAsync("payload.bin", CancellationToken.None);
        var torrentResult = await new TorrentBuilder(new PublicUrlBuilder(Options.Create(new IcecoldOptions { PublicBaseUrl = "http://example.test" })))
            .BuildSingleFileAsync(metadata, source, null, CancellationToken.None);
        var infoBytes = GetInfoBytes(torrentResult.TorrentBytes);
        await using var db = CreateDb(torrentResult, metadata);
        await using var provider = CreateProvider(db);
        var handler = new PeerWireConnectionHandler(
            new PeerWireTorrentResolver(new TestScopeFactory(provider), CreateRegistry()),
            new PeerWirePeerIdentity(),
            Options.Create(new IcecoldOptions { PeerWire = new PeerWireOptions { Enabled = true, MaxBlockLength = 16 * 1024 } }),
            NullLogger<PeerWireConnectionHandler>.Instance);
        await using var stream = new DuplexTestStream();
        var peerId = Enumerable.Repeat((byte)'M', 20).ToArray();

        await stream.WriteClientAsync(BuildHandshake(Convert.FromHexString(torrentResult.InfoHashHex), peerId, supportsExtensions: true));
        var handling = handler.HandleAsync(stream.ServerStream, CancellationToken.None);

        var serverHandshake = await stream.ReadClientAsync(68);
        Assert.Equal(0x10, serverHandshake[25] & 0x10);

        var extensionHandshake = await ReadMessageAsync(stream);
        Assert.Equal(20, extensionHandshake.Id);
        Assert.Equal(0, extensionHandshake.Payload[0]);
        Assert.Contains("ut_metadata", Encoding.UTF8.GetString(extensionHandshake.Payload.AsSpan(1)));

        var bitfield = await ReadMessageAsync(stream);
        Assert.Equal(5, bitfield.Id);

        var unchoke = await ReadMessageAsync(stream);
        Assert.Equal(1, unchoke.Id);

        await stream.WriteClientAsync(BuildExtendedMessage(0, Bencode.Encode(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["complete_ago"] = -1L,
            ["m"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ut_metadata"] = 7L
            },
            ["reqq"] = 500L
        })));
        await stream.WriteClientAsync(BuildExtendedMessage(1, Bencode.Encode(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["msg_type"] = 0L,
            ["piece"] = 0L
        })));

        var metadataPiece = await ReadMessageAsync(stream);
        Assert.Equal(20, metadataPiece.Id);
        Assert.Equal(7, metadataPiece.Payload[0]);
        Assert.Contains("total_size", Encoding.UTF8.GetString(metadataPiece.Payload.AsSpan(1, metadataPiece.Payload.Length - infoBytes.Length - 1)));
        Assert.True(metadataPiece.Payload.AsSpan(metadataPiece.Payload.Length - infoBytes.Length).SequenceEqual(infoBytes));

        stream.CompleteClientWrites();
        await handling.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Handler_Closes_Unknown_InfoHash()
    {
        await File.WriteAllTextAsync(Path.Combine(root, "payload.bin"), "hello");
        await using var db = CreateEmptyDb();
        await using var provider = CreateProvider(db);
        var handler = new PeerWireConnectionHandler(
            new PeerWireTorrentResolver(new TestScopeFactory(provider), CreateRegistry()),
            new PeerWirePeerIdentity(),
            Options.Create(new IcecoldOptions { PeerWire = new PeerWireOptions { Enabled = true } }),
            NullLogger<PeerWireConnectionHandler>.Instance);
        await using var stream = new DuplexTestStream();

        await stream.WriteClientAsync(BuildHandshake(Enumerable.Repeat((byte)1, 20).ToArray(), Enumerable.Repeat((byte)2, 20).ToArray()));
        await handler.HandleAsync(stream.ServerStream, CancellationToken.None);
        await stream.ServerStream.DisposeAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Equal(0, await stream.ClientStream.ReadAsync(new byte[1], timeout.Token));
    }

    IcecoldDbContext CreateDb(TorrentBuildResult torrentResult, ContentMetadata metadata)
    {
        var db = CreateEmptyDb();
        db.Torrents.Add(new TorrentRecord
        {
            Id = Guid.NewGuid(),
            SourceName = "local",
            SourcePath = metadata.Path,
            DisplayName = metadata.DisplayName,
            ContentLength = metadata.Length,
            ContentVersion = metadata.Version,
            Status = TorrentStatus.Ready,
            InfoHashHex = torrentResult.InfoHashHex,
            MseObfuscatedHashHex = PeerWireMse.HashReq2Hex(torrentResult.InfoHashHex),
            PieceLength = torrentResult.PieceLength,
            PieceCount = torrentResult.PieceCount,
            TorrentBytes = torrentResult.TorrentBytes,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
        return db;
    }

    static IcecoldDbContext CreateEmptyDb()
        => new(new DbContextOptionsBuilder<IcecoldDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options);

    static ServiceProvider CreateProvider(IcecoldDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        return services.BuildServiceProvider();
    }

    ContentSourceRegistry CreateRegistry()
        => new(
            Options.Create(new IcecoldOptions
            {
                ContentSources =
                [
                    new ContentSourceOptions
                    {
                        Name = "local",
                        Type = "local",
                        RootPath = root
                    }
                ]
            }),
            new TestWebHostEnvironment(root));

    PeerWireTransportNegotiator CreateTransportNegotiator(ServiceProvider provider)
        => new(
            new PeerWireTorrentResolver(new TestScopeFactory(provider), CreateRegistry()),
            NullLogger<PeerWireTransportNegotiator>.Instance);

    static byte[] BuildHandshake(byte[] infoHash, byte[] peerId, bool supportsExtensions = false)
    {
        var result = new byte[68];
        result[0] = 19;
        Encoding.ASCII.GetBytes("BitTorrent protocol", result.AsSpan(1));
        if (supportsExtensions)
            result[25] = 0x10;
        infoHash.CopyTo(result.AsSpan(28));
        peerId.CopyTo(result.AsSpan(48));
        return result;
    }

    static byte[] BuildExtendedMessage(byte extensionId, byte[] payload)
    {
        var result = new byte[payload.Length + 6];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), payload.Length + 2);
        result[4] = 20;
        result[5] = extensionId;
        payload.CopyTo(result.AsSpan(6));
        return result;
    }

    static byte[] GetInfoBytes(byte[] torrentBytes)
    {
        var decoded = BEncodedDictionary.DecodeTorrent(torrentBytes).torrent;
        return ((BEncodedDictionary)decoded["info"]).Encode();
    }

    static byte[] BuildInterested()
    {
        var result = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), 1);
        result[4] = 2;
        return result;
    }

    static byte[] BuildRequest(int pieceIndex, int begin, int length)
    {
        var result = new byte[17];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), 13);
        result[4] = 6;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(5, 4), pieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(9, 4), begin);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(13, 4), length);
        return result;
    }

    static async Task<(byte Id, byte[] Payload)> ReadMessageAsync(DuplexTestStream stream)
    {
        var prefix = await stream.ReadClientAsync(4);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        var message = await stream.ReadClientAsync(length);
        return (message[0], message[1..]);
    }

    static async Task<(byte Id, byte[] Payload)> ReadMessageAsync(MseClient client)
    {
        var prefix = await client.ReadAsync(4);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        var message = await client.ReadAsync(length);
        return (message[0], message[1..]);
    }

    static async Task<MseClient> CompleteMseHandshakeAsync(
        DuplexTestStream stream,
        string infoHashHex,
        byte[] initialPayload)
    {
        var infoHash = Convert.FromHexString(infoHashHex);
        var privateKey = PeerWireMse.GeneratePrivateKey();
        var publicKey = PeerWireMse.ComputePublicKey(privateKey);
        await stream.WriteClientAsync(publicKey);

        var serverPublicKey = await stream.ReadClientAsync(PeerWireMse.KeySize);
        var secret = PeerWireMse.ComputeSecret(serverPublicKey, privateKey);

        await stream.WriteClientAsync(PeerWireMse.HashReq1(secret));
        await stream.WriteClientAsync(PeerWireMse.Xor(PeerWireMse.HashReq2(infoHash), PeerWireMse.HashReq3(secret)));

        var encrypt = CreateRc4(PeerWireMse.IncomingDecryptKey(secret, infoHash));
        var handshakePayload = new byte[16 + initialPayload.Length];
        PeerWireMse.WriteUInt32(handshakePayload.AsSpan(8, 4), PeerWireMse.CryptoRc4);
        PeerWireMse.WriteUInt16(handshakePayload.AsSpan(12, 2), 0);
        PeerWireMse.WriteUInt16(handshakePayload.AsSpan(14, 2), (ushort)initialPayload.Length);
        initialPayload.CopyTo(handshakePayload.AsSpan(16));
        encrypt.Process(handshakePayload);
        await stream.WriteClientAsync(handshakePayload);

        var decrypt = CreateRc4(PeerWireMse.IncomingEncryptKey(secret, infoHash));
        var response = await stream.ReadClientAsync(14);
        decrypt.Process(response);
        Assert.Equal(new byte[8], response.AsSpan(0, 8).ToArray());
        Assert.Equal(PeerWireMse.CryptoRc4, PeerWireMse.ReadUInt32(response.AsSpan(8, 4)));
        Assert.Equal(0, PeerWireMse.ReadUInt16(response.AsSpan(12, 2)));

        return new MseClient(stream, encrypt, decrypt);
    }

    static PeerWireRc4 CreateRc4(byte[] key)
    {
        var rc4 = new PeerWireRc4(key);
        rc4.Discard(1024);
        return rc4;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    sealed class TestScopeFactory(ServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => provider.CreateScope();
    }

    sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "Icecold.Tests";

        public string WebRootPath { get; set; } = contentRootPath;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    sealed class DuplexTestStream : IAsyncDisposable
    {
        readonly AnonymousPipeServerStream clientInputWriter = new(PipeDirection.Out);
        readonly AnonymousPipeClientStream serverInputReader;
        readonly AnonymousPipeServerStream serverOutputWriter = new(PipeDirection.Out);
        readonly AnonymousPipeClientStream clientOutputReader;

        public DuplexTestStream()
        {
            serverInputReader = new AnonymousPipeClientStream(PipeDirection.In, clientInputWriter.ClientSafePipeHandle);
            clientOutputReader = new AnonymousPipeClientStream(PipeDirection.In, serverOutputWriter.ClientSafePipeHandle);
            ServerStream = new CombinedStream(serverInputReader, serverOutputWriter);
            ClientStream = clientOutputReader;
        }

        public Stream ServerStream { get; }

        public Stream ClientStream { get; }

        public async Task WriteClientAsync(byte[] bytes)
        {
            await clientInputWriter.WriteAsync(bytes);
            await clientInputWriter.FlushAsync();
        }

        public void CompleteClientWrites()
            => clientInputWriter.Dispose();

        public async Task<byte[]> ReadClientAsync(int length)
        {
            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var current = await clientOutputReader.ReadAsync(buffer.AsMemory(read, length - read));
                if (current == 0)
                    throw new EndOfStreamException();

                read += current;
            }

            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            await ServerStream.DisposeAsync();
            await ClientStream.DisposeAsync();
            clientInputWriter.Dispose();
            serverOutputWriter.Dispose();
        }
    }

    sealed class MseClient(DuplexTestStream stream, PeerWireRc4 encrypt, PeerWireRc4 decrypt)
    {
        public async Task WriteAsync(byte[] plaintext)
        {
            var encrypted = plaintext.ToArray();
            encrypt.Process(encrypted);
            await stream.WriteClientAsync(encrypted);
        }

        public async Task<byte[]> ReadAsync(int length)
        {
            var encrypted = await stream.ReadClientAsync(length);
            decrypt.Process(encrypted);
            return encrypted;
        }
    }

    sealed class CombinedStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
            => output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => output.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
            => input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => input.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => output.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
                output.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await input.DisposeAsync();
            await output.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
