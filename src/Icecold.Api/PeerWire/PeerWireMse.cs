using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Icecold.Api.Torrents;

namespace Icecold.Api.PeerWire;

public static class PeerWireMse
{
    public const int KeySize = 96;
    public const int PrivateKeySize = 20;
    public const int MaxPadLength = 512;
    public const uint CryptoPlaintext = 0x01;
    public const uint CryptoRc4 = 0x02;

    static readonly byte[] Req1 = Encoding.ASCII.GetBytes("req1");
    static readonly byte[] Req2 = Encoding.ASCII.GetBytes("req2");
    static readonly byte[] Req3 = Encoding.ASCII.GetBytes("req3");
    static readonly byte[] KeyA = Encoding.ASCII.GetBytes("keyA");
    static readonly byte[] KeyB = Encoding.ASCII.GetBytes("keyB");

    static readonly BigInteger Prime = BigInteger.Parse(
        "1552518092300708935130918131258481755631334049434514313202351194902966239949102107258669453876591642442910007680288864229150803718918046342632727613031282983744380820890196288509170691316593175367469551763119843371637221007211169123");

    static readonly BigInteger Generator = new(2);

    public static byte[] GeneratePrivateKey()
    {
        var privateKey = new byte[PrivateKeySize];
        RandomNumberGenerator.Fill(privateKey);
        return privateKey;
    }

    public static byte[] ComputePublicKey(byte[] privateKey)
        => ToFixedBigEndian(BigInteger.ModPow(Generator, FromBigEndian(privateKey), Prime));

    public static byte[] ComputeSecret(byte[] peerPublicKey, byte[] privateKey)
    {
        var peer = FromBigEndian(peerPublicKey);
        if (peer <= BigInteger.One || peer >= Prime)
            throw new InvalidOperationException("MSE peer public key is outside the Diffie-Hellman group.");

        return ToFixedBigEndian(BigInteger.ModPow(peer, FromBigEndian(privateKey), Prime));
    }

    public static byte[] HashReq1(byte[] secret)
        => Sha1(Req1, secret);

    public static byte[] HashReq2(byte[] infoHash)
        => Sha1(Req2, infoHash);

    public static string HashReq2Hex(string infoHashHex)
        => InfoHashUtil.ToHex(HashReq2(Convert.FromHexString(infoHashHex)));

    public static byte[] HashReq3(byte[] secret)
        => Sha1(Req3, secret);

    public static byte[] IncomingDecryptKey(byte[] secret, byte[] infoHash)
        => Sha1(KeyA, secret, infoHash);

    public static byte[] IncomingEncryptKey(byte[] secret, byte[] infoHash)
        => Sha1(KeyB, secret, infoHash);

    public static byte[] Xor(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
            throw new ArgumentException("MSE XOR inputs must have equal length.");

        var output = new byte[left.Length];
        for (var i = 0; i < output.Length; i++)
            output[i] = (byte)(left[i] ^ right[i]);

        return output;
    }

    public static void WriteUInt32(Span<byte> destination, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(destination, value);

    public static uint ReadUInt32(ReadOnlySpan<byte> source)
        => BinaryPrimitives.ReadUInt32BigEndian(source);

    public static void WriteUInt16(Span<byte> destination, ushort value)
        => BinaryPrimitives.WriteUInt16BigEndian(destination, value);

    public static ushort ReadUInt16(ReadOnlySpan<byte> source)
        => BinaryPrimitives.ReadUInt16BigEndian(source);

    static byte[] Sha1(params byte[][] buffers)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var buffer in buffers)
            hash.AppendData(buffer);

        return hash.GetHashAndReset();
    }

    static BigInteger FromBigEndian(byte[] bytes)
        => new(bytes, isUnsigned: true, isBigEndian: true);

    static byte[] ToFixedBigEndian(BigInteger value)
    {
        var source = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (source.Length > KeySize)
            throw new InvalidOperationException("MSE Diffie-Hellman value exceeded the fixed key size.");

        var output = new byte[KeySize];
        source.CopyTo(output.AsSpan(KeySize - source.Length));
        return output;
    }
}
