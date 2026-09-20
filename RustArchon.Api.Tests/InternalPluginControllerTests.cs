// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// The Api end of the public plugin download door. Every refusal has to look identical (a bare 404) so a guesser
/// learns nothing, and the script is only built after a token has actually been redeemed.
/// </summary>
public class InternalPluginControllerTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly PluginTokenRedemption Good = new("0123456789abcdef");
    private readonly Mock<IPluginUpdateTokenRepository> _tokens = new();
    private readonly Mock<IPluginScriptService> _script = new();

    private InternalPluginController Create()
    {
        _script.Setup(s => s.BuildBridgeAsync(It.IsAny<string>())).ReturnsAsync(new PluginScript([9, 8, 7], "0123456789abcdef", "0.2.1"));
        return new InternalPluginController(_tokens.Object, _script.Object, NullLogger<InternalPluginController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task AGoodTokenGetsTheSignedScript()
    {
        _tokens.Setup(t => t.RedeemAsync(ServerId, "good")).ReturnsAsync(Good);
        var controller = Create();

        var result = await controller.Download(ServerId, "good");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal([9, 8, 7], file.FileContents);
        Assert.Equal("RustArchon.cs", file.FileDownloadName);
        Assert.Equal("0.2.1", controller.Response.Headers["X-RustArchon-Plugin-Version"]);
    }

    [Fact]
    public async Task TheScriptIsNeverCachedByAnythingBetweenTheApiAndTheGameServer()
    {
        _tokens.Setup(t => t.RedeemAsync(ServerId, "good")).ReturnsAsync(Good);
        var controller = Create();

        await controller.Download(ServerId, "good");

        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task ARefusedTokenIsABareNotFoundAndNothingIsBuilt()
    {
        _tokens.Setup(t => t.RedeemAsync(ServerId, "bad")).ReturnsAsync((PluginTokenRedemption?)null);
        var controller = Create();

        var result = await controller.Download(ServerId, "bad");

        Assert.IsType<NotFoundResult>(result);
        _script.Verify(s => s.BuildBridgeAsync(It.IsAny<string>()), Times.Never);
        Assert.False(controller.Response.Headers.ContainsKey("X-RustArchon-Plugin-Version"));
    }

    [Fact]
    public async Task AWrongServerIsIndistinguishableFromAWrongToken()
    {
        _tokens.Setup(t => t.RedeemAsync(It.IsAny<Guid>(), It.IsAny<string>())).ReturnsAsync((PluginTokenRedemption?)null);
        _tokens.Setup(t => t.RedeemAsync(ServerId, "good")).ReturnsAsync(Good);
        var controller = Create();

        var wrongServer = await controller.Download(Guid.NewGuid(), "good");
        var wrongToken = await controller.Download(ServerId, "nope");

        Assert.Equal(wrongServer.GetType(), wrongToken.GetType());
        Assert.IsType<NotFoundResult>(wrongServer);
    }

    [Fact]
    public async Task ATokenUsedTwiceWorksOnlyTheFirstTime()
    {
        _tokens.SetupSequence(t => t.RedeemAsync(ServerId, "once")).ReturnsAsync(Good).ReturnsAsync((PluginTokenRedemption?)null);
        var controller = Create();

        Assert.IsType<FileContentResult>(await controller.Download(ServerId, "once"));
        Assert.IsType<NotFoundResult>(await controller.Download(ServerId, "once"));
    }

    [Theory]
    [InlineData("")]
    public async Task AnEmptyTokenNeverEvenReachesTheDatabase(string token)
    {
        var controller = Create();

        var result = await controller.Download(ServerId, token);

        Assert.IsType<NotFoundResult>(result);
        _tokens.Verify(t => t.RedeemAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AnOversizedTokenNeverEvenReachesTheDatabase()
    {
        var controller = Create();

        var result = await controller.Download(ServerId, new string('a', 129));

        Assert.IsType<NotFoundResult>(result);
        _tokens.Verify(t => t.RedeemAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ATokenAtTheLimitIsStillLookedUp()
    {
        var token = new string('a', 128);
        _tokens.Setup(t => t.RedeemAsync(ServerId, token)).ReturnsAsync((PluginTokenRedemption?)null);
        var controller = Create();

        await controller.Download(ServerId, token);

        _tokens.Verify(t => t.RedeemAsync(ServerId, token), Times.Once);
    }

    [Fact]
    public async Task AnUnusableSigningKeyIsA503NotACrashAndNotAnUnsignedScript()
    {
        _tokens.Setup(t => t.RedeemAsync(ServerId, "good")).ReturnsAsync(Good);
        var controller = Create();
        _script.Setup(s => s.BuildBridgeAsync(It.IsAny<string>())).ThrowsAsync(new PluginSigningKeyException("unreadable"));

        var result = await controller.Download(ServerId, "good");

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public void TheEndpointRequiresTheInternalServiceKey()
    {
        var authorize = Assert.Single(typeof(InternalPluginController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());

        Assert.Equal("InternalApiKey", authorize.AuthenticationSchemes);
    }
    [Fact]
    public async Task TheFileIsBuiltForTheKeyTheTokenWasMintedFor()
    {
        _tokens.Setup(t => t.RedeemAsync(ServerId, "good")).ReturnsAsync(new PluginTokenRedemption("aaaaaaaaaaaaaaaa"));
        var controller = Create();

        await controller.Download(ServerId, "good");

        _script.Verify(s => s.BuildBridgeAsync("aaaaaaaaaaaaaaaa"), Times.Once);
    }

    [Fact]
    public async Task AKeyRevokedSinceTheUpdateStartedIsABareNotFoundNotAnUnsignedOrWronglySignedFile()
    {
        _tokens.Setup(t => t.RedeemAsync(ServerId, "good")).ReturnsAsync(Good);
        var controller = Create();
        _script.Setup(s => s.BuildBridgeAsync(It.IsAny<string>()))
            .ThrowsAsync(new PluginKeyUnavailableException("0123456789abcdef", RustArchon.Api.Data.PluginKeyState.Revoked));

        var result = await controller.Download(ServerId, "good");

        Assert.IsType<NotFoundResult>(result);
        Assert.False(controller.Response.Headers.ContainsKey("X-RustArchon-Plugin-Version"));
    }

    // ---- the header form (Updater 0.3.0 and later) ---------------------------------------------------------

    [Fact]
    public async Task AGoodTokenInTheHeaderGetsTheSignedScriptForTheServerTheTokenNames()
    {
        _tokens.Setup(t => t.RedeemAsync("good")).ReturnsAsync(new PluginTokenRedemption("aaaaaaaaaaaaaaaa", ServerId));
        var controller = Create();

        var result = await controller.DownloadWithHeaderToken("good");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal([9, 8, 7], file.FileContents);
        Assert.Equal("0.2.1", controller.Response.Headers["X-RustArchon-Plugin-Version"]);
        _script.Verify(s => s.BuildBridgeAsync("aaaaaaaaaaaaaaaa"), Times.Once);
    }

    [Fact]
    public async Task TheHeaderFormIsNeverCachedEitherWayItGoes()
    {
        _tokens.Setup(t => t.RedeemAsync("good")).ReturnsAsync(Good);
        _tokens.Setup(t => t.RedeemAsync("bad")).ReturnsAsync((PluginTokenRedemption?)null);
        var accepted = Create();
        var refused = Create();

        await accepted.DownloadWithHeaderToken("good");
        await refused.DownloadWithHeaderToken("bad");

        Assert.Equal("no-store", accepted.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-store", refused.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ANullOrEmptyHeaderIsABareNotFoundWithoutTouchingTheTokens(string? token)
    {
        var result = await Create().DownloadWithHeaderToken(token);

        Assert.IsType<NotFoundResult>(result);
        _tokens.Verify(t => t.RedeemAsync(It.IsAny<string>()), Times.Never);
        _script.Verify(s => s.BuildBridgeAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AnOversizedHeaderIsABareNotFoundWithoutTouchingTheTokens()
    {
        var result = await Create().DownloadWithHeaderToken(new string('a', 129));

        Assert.IsType<NotFoundResult>(result);
        _tokens.Verify(t => t.RedeemAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ATokenThatIsNotGoodInTheHeaderIsABareNotFoundAndNothingIsBuilt()
    {
        _tokens.Setup(t => t.RedeemAsync("nope")).ReturnsAsync((PluginTokenRedemption?)null);

        var result = await Create().DownloadWithHeaderToken("nope");

        Assert.IsType<NotFoundResult>(result);
        _script.Verify(s => s.BuildBridgeAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AKeyRevokedSinceTheUpdateStartedIsABareNotFoundInTheHeaderFormToo()
    {
        _tokens.Setup(t => t.RedeemAsync("good")).ReturnsAsync(Good);
        var controller = Create();
        _script.Setup(s => s.BuildBridgeAsync(It.IsAny<string>()))
            .ThrowsAsync(new PluginKeyUnavailableException("0123456789abcdef", RustArchon.Api.Data.PluginKeyState.Revoked));

        var result = await controller.DownloadWithHeaderToken("good");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void TheHeaderNameTheApiReadsIsTheOneTheUpdaterSends()
    {
        var parameter = typeof(InternalPluginController).GetMethod(nameof(InternalPluginController.DownloadWithHeaderToken))!.GetParameters().Single();

        var fromHeader = (Microsoft.AspNetCore.Mvc.FromHeaderAttribute)parameter.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.FromHeaderAttribute), false).Single();

        Assert.Equal("X-RustArchon-Update-Token", fromHeader.Name);
    }
}
