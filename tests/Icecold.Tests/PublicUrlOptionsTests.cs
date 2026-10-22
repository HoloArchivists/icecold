using Icecold.Api.Options;

namespace Icecold.Tests;

public sealed class PublicUrlOptionsTests
{
    [Theory]
    [InlineData("http://localhost:5038")]
    [InlineData("https://icecold.example.test")]
    [InlineData("https://icecold.example.test/base/")]
    public void Validate_Accepts_Absolute_Http_Urls(string url)
    {
        var result = new PublicUrlOptionsValidator().Validate(null, new IcecoldOptions
        {
            PublicBaseUrl = url,
            WebSeed = new WebSeedOptions { PublicBaseUrl = url }
        });

        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.test")]
    [InlineData("/relative")]
    [InlineData("ftp://example.test")]
    [InlineData("https://example.test/?a=b")]
    [InlineData("https://example.test/#frag")]
    public void Validate_Rejects_Invalid_Public_Base_Url(string url)
    {
        var result = new PublicUrlOptionsValidator().Validate(null, new IcecoldOptions { PublicBaseUrl = url });

        Assert.True(result.Failed);
        Assert.Contains(
            "Icecold:PublicBaseUrl must be an absolute http or https URL without a query string or fragment.",
            result.Failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_Allows_Empty_WebSeed_Public_Base_Url(string? url)
    {
        var result = new PublicUrlOptionsValidator().Validate(null, new IcecoldOptions
        {
            WebSeed = new WebSeedOptions { PublicBaseUrl = url }
        });

        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData("cdn.example.test")]
    [InlineData("https://cdn.example.test/?token=x")]
    public void Validate_Rejects_Invalid_WebSeed_Public_Base_Url(string url)
    {
        var result = new PublicUrlOptionsValidator().Validate(null, new IcecoldOptions
        {
            WebSeed = new WebSeedOptions { PublicBaseUrl = url }
        });

        Assert.True(result.Failed);
        Assert.Contains(
            "Icecold:WebSeed:PublicBaseUrl must be empty or an absolute http or https URL without a query string or fragment.",
            result.Failures);
    }
}
