using System.Text;
using Icecold.Api.PeerWire;

namespace Icecold.Tests;

public sealed class PeerWireBencodeTests
{
    [Fact]
    public void TryDecodeDictionary_Rejects_Excessive_Nesting()
    {
        var allowed = BuildNestedListDictionary(PeerWireBencode.MaxNestingDepth - 1);
        var tooDeep = BuildNestedListDictionary(PeerWireBencode.MaxNestingDepth);

        Assert.True(PeerWireBencode.TryDecodeDictionary(allowed, out _));
        Assert.False(PeerWireBencode.TryDecodeDictionary(tooDeep, out _));
    }

    static byte[] BuildNestedListDictionary(int listDepth)
    {
        var builder = new StringBuilder("d1:a");
        for (var i = 0; i < listDepth; i++)
            builder.Append('l');

        builder.Append("i1e");
        for (var i = 0; i < listDepth; i++)
            builder.Append('e');

        builder.Append('e');
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
