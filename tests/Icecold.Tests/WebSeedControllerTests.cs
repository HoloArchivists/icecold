using Icecold.Api.Controllers;
using Icecold.Api.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Icecold.Tests;

public sealed class WebSeedControllerTests
{
    [Fact]
    public async Task Get_Returns_NotFound_When_WebSeed_Is_Disabled()
    {
        var controller = new WebSeedController(
            null!,
            Options.Create(new IcecoldOptions
            {
                WebSeed = new WebSeedOptions { Enabled = false },
                PeerWire = new PeerWireOptions { Enabled = true }
            }));

        var result = await controller.Get(new string('a', 40), "payload.bin", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }
}
