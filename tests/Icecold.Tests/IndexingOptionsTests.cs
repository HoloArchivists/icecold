using Icecold.Api.Indexing;
using Icecold.Api.Options;

namespace Icecold.Tests;

public sealed class IndexingOptionsTests
{
    [Fact]
    public void Validate_Fails_When_Indexing_Options_Are_Invalid()
    {
        var result = new IndexingOptionsValidator().Validate(null, new IcecoldOptions
        {
            Indexing = new IndexingOptions
            {
                MaxConcurrency = 0,
                QueueCapacity = 0
            }
        });

        Assert.True(result.Failed);
        Assert.Contains("Icecold:Indexing:MaxConcurrency must be at least 1.", result.Failures);
        Assert.Contains("Icecold:Indexing:QueueCapacity must be at least 1.", result.Failures);
    }
}
