// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Servers;
using RustArchon.Panel.Localization;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The Map tab. The picture and the named places are for anyone who may see the server; players and bases are the sensitive
/// layers, each asked for on its own and each simply left out (with a note) when the Api answers 403. Nothing is drawn
/// until the picture has really arrived, and a failure to load anything shows a message instead of breaking the page.
/// </summary>
public class ServerMapPaneTests : BunitContext
{
    private readonly Mock<IRustServerApiClient> _client = new();
    private readonly Guid _serverId = Guid.NewGuid();

    public ServerMapPaneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(localizer.Object);

        GivenMap(available: true);
        GivenBases();
        GivenPlayers();
    }

    private static readonly Refit.ApiException Forbidden =
        Refit.ApiException.Create(new HttpRequestMessage(), HttpMethod.Get, new HttpResponseMessage(HttpStatusCode.Forbidden), new Refit.RefitSettings()).GetAwaiter().GetResult();

    private void GivenMap(bool available, string monument = "Launch Site") =>
        _client.Setup(c => c.GetMapAsync(_serverId)).ReturnsAsync(new MapDto
        {
            Available = available, WorldSize = 4500, WorldSeed = 317670913,
            UploadedAtUtc = new DateTimeOffset(2026, 9, 20, 5, 30, 0, TimeSpan.Zero),
            Monuments = [new MapMonumentDto { Name = monument, X = 100, Y = 0, Z = -200 }]
        });

    private void GivenImage(byte[]? bytes = null) =>
        _client.Setup(c => c.GetMapImageAsync(_serverId)).ReturnsAsync(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? [0x89, 0x50, 0x4E, 0x47]) });

    private void GivenBases(params BaseTcDto[] tcs) =>
        _client.Setup(c => c.GetBasesAsync(_serverId)).ReturnsAsync(new BasesDto { Ready = true, Tcs = [.. tcs] });

    private void GivenPlayers(params PositionSampleDto[] samples) =>
        _client.Setup(c => c.GetPositionsAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ReturnsAsync(new PositionsDto { Samples = [.. samples] });

    private IRenderedComponent<ServerMapPane> RenderPane(bool listed = true, bool supportsMap = true) =>
        Render<ServerMapPane>(p => p.Add(x => x.ServerId, _serverId).Add(x => x.PluginListed, listed).Add(x => x.PluginSupportsMap, supportsMap));

    private static PositionSampleDto Player(string id, string name, double x, double z, string marker = "", int minutesAgo = 1, long seq = 1) => new()
    {
        Sequence = seq, PlayerId = id, PlayerName = name, X = x, Z = z, Marker = marker, OccurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)
    };

    private static BaseTcDto Base(int id, string owner, double x, double z, string name = "") => new()
    {
        Id = id, OwnerId = owner, X = x, Z = z, Authorized = name.Length == 0 ? [] : [new BaseAuthorizedPlayerDto { PlayerId = owner, Name = name }]
    };

    private System.Text.Json.JsonElement DrawModel()
    {
        var call = JSInterop.Invocations["serverMap.draw"].Last();
        return System.Text.Json.JsonSerializer.SerializeToElement(call.Arguments[1]);
    }

    // ---- when there is no picture ----------------------------------------------------------------------------------

    [Fact]
    public void WithNoPictureAndNoPluginTheTabSaysWhatItNeedsAndLoadsNothingElse()
    {
        GivenMap(available: false);

        var cut = RenderPane(listed: false, supportsMap: false);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=map-needs-plugin]")));
        _client.Verify(c => c.GetMapImageAsync(It.IsAny<Guid>()), Times.Never);
        _client.Verify(c => c.GetBasesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void AListedPluginThatCannotDrawTheMapYetIsToldToUpdate()
    {
        GivenMap(available: false);

        var cut = RenderPane(listed: true, supportsMap: false);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=map-not-supported]")));
    }

    [Fact]
    public void ACapablePluginWhoseMapHasNotArrivedYetIsToldItIsOnItsWay()
    {
        GivenMap(available: false);

        var cut = RenderPane();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=map-pending]")));
        Assert.Empty(cut.FindAll("[data-testid=map-canvas]"));
        _client.Verify(c => c.GetMapImageAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void AStoredMapIsShownEvenIfThePluginIsNotReportingRightNow()
    {
        GivenImage();

        var cut = RenderPane(listed: false, supportsMap: false);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=map-canvas]")));
        Assert.Empty(cut.FindAll("[data-testid=map-needs-plugin]"));
    }

    [Fact]
    public void AFailureToAskForTheMapShowsAMessageNotACrash()
    {
        _client.Setup(c => c.GetMapAsync(_serverId)).ThrowsAsync(new HttpRequestException("boom"));

        var cut = RenderPane();

        cut.WaitForAssertion(() => Assert.Contains("Failed to load the map.", cut.Find("[data-testid=map-error]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=map-canvas]"));
    }

    // ---- when there is ----------------------------------------------------------------------------------------------

    [Fact]
    public void AnAvailableMapShowsTheCanvasWithItsWorldSizeSeedAndWhenItWasCollected()
    {
        GivenImage();

        var cut = RenderPane();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=map-canvas]")));
        var info = cut.Find("[data-testid=map-info]").TextContent;
        Assert.Contains("4500 m", info);
        Assert.Contains("317670913", info);
        Assert.Contains("2026-09-20 05:30 UTC", info);
    }

    [Fact]
    public void ThePictureIsStreamedIntoTheCanvasAndThenDrawnOnce()
    {
        GivenImage();

        var cut = RenderPane();

        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));
        var load = Assert.Single(JSInterop.Invocations["serverMap.load"]);
        Assert.Equal($"server-map-{_serverId:N}", load.Arguments[0]);
        Assert.Single(JSInterop.Invocations["serverMap.draw"]);
    }

    [Fact]
    public void TheDrawnModelCarriesTheWorldSizeThePlacesTheBasesAndThePlayers()
    {
        GivenImage();
        GivenBases(Base(1, "76561198000000001", 379.2, -610.9, "CyberKnet"));
        GivenPlayers(Player("76561198000000001", "CyberKnet", 381.4, -638.1), Player("76561198000000002", "", 5, 6, marker: "off", seq: 2));

        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));
        var model = DrawModel();

        Assert.Equal(4500, model.GetProperty("size").GetInt32());
        Assert.Equal("Launch Site", model.GetProperty("monuments")[0].GetProperty("name").GetString());
        Assert.Equal(-200, model.GetProperty("monuments")[0].GetProperty("z").GetDouble());
        Assert.Equal("CyberKnet", model.GetProperty("bases")[0].GetProperty("name").GetString());
        var players = model.GetProperty("players").EnumerateArray().OrderBy(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(2, players.Count);
        Assert.True(players.Single(p => p.GetProperty("name").GetString() == "CyberKnet").GetProperty("online").GetBoolean());
        Assert.False(players.Single(p => p.GetProperty("name").GetString() == "76561198000000002").GetProperty("online").GetBoolean());
        Assert.True(model.GetProperty("showMonuments").GetBoolean());
        Assert.True(model.GetProperty("showBases").GetBoolean());
        Assert.True(model.GetProperty("showPlayers").GetBoolean());
    }

    [Fact]
    public void APlayersNameComesFromTheNewestSampleThatHasOneAndTheirPositionFromTheNewest()
    {
        GivenImage();
        GivenPlayers(
            Player("76561198000000001", "Alice", 1, 1, marker: "on", minutesAgo: 5, seq: 1),
            Player("76561198000000001", "", 50, 60, minutesAgo: 1, seq: 2));

        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));
        var player = Assert.Single(DrawModel().GetProperty("players").EnumerateArray().ToList());

        Assert.Equal("Alice", player.GetProperty("name").GetString());
        Assert.Equal(50, player.GetProperty("x").GetDouble());
        Assert.Equal(60, player.GetProperty("z").GetDouble());
    }

    [Fact]
    public void APictureThatCannotBeLoadedShowsAMessageAndNothingIsDrawn()
    {
        _client.Setup(c => c.GetMapImageAsync(_serverId)).ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var cut = RenderPane();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=map-image-error]")));
        Assert.Empty(JSInterop.Invocations["serverMap.draw"]);
    }

    // ---- the sensitive layers ---------------------------------------------------------------------------------------

    [Fact]
    public void ABaseIsLabelledWithTheOwnersResolvedNameEvenWhenTheyAreNotOnTheirOwnCupboard()
    {
        GivenImage();
        var tc = Base(1, "76561198000000001", 10, 20);
        tc.OwnerName = "Resolved Owner";
        GivenBases(tc);

        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));

        Assert.Equal("Resolved Owner", DrawModel().GetProperty("bases")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void WithoutThePlayerPermissionThePlayersLayerIsLeftOutAndTheRestStillShows()
    {
        GivenImage();
        _client.Setup(c => c.GetPositionsAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>())).ThrowsAsync(Forbidden);
        GivenBases(Base(1, "76561198000000001", 1, 2));

        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));
        var model = DrawModel();

        Assert.False(model.GetProperty("showPlayers").GetBoolean());
        Assert.True(model.GetProperty("showBases").GetBoolean());
        Assert.Equal(0, model.GetProperty("players").GetArrayLength());
        Assert.NotEmpty(cut.FindAll("[data-testid=map-layers-forbidden]"));
        Assert.True(cut.Find("[data-testid=map-toggle-players]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=map-toggle-bases]").HasAttribute("disabled"));
    }

    [Fact]
    public void WithoutTheBasesPermissionTheBasesLayerIsLeftOut()
    {
        GivenImage();
        _client.Setup(c => c.GetBasesAsync(_serverId)).ThrowsAsync(Forbidden);

        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));
        var model = DrawModel();

        Assert.False(model.GetProperty("showBases").GetBoolean());
        Assert.NotEmpty(cut.FindAll("[data-testid=map-layers-forbidden]"));
        Assert.True(cut.Find("[data-testid=map-toggle-bases]").HasAttribute("disabled"));
    }

    [Fact]
    public void ForbiddenLayersNeverLeakTheirDataIntoWhatIsDrawn()
    {
        GivenImage();
        _client.Setup(c => c.GetBasesAsync(_serverId)).ThrowsAsync(Forbidden);
        _client.Setup(c => c.GetPositionsAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<int>())).ThrowsAsync(Forbidden);

        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));
        var model = DrawModel();

        Assert.Equal(0, model.GetProperty("bases").GetArrayLength());
        Assert.Equal(0, model.GetProperty("players").GetArrayLength());
        Assert.True(model.GetProperty("monuments").GetArrayLength() > 0);          // the public map itself is still there
    }

    [Fact]
    public void AnErrorOtherThanForbiddenOnALayerLeavesItEmptyWithoutClaimingAPermissionProblem()
    {
        GivenImage();
        _client.Setup(c => c.GetBasesAsync(_serverId)).ThrowsAsync(new HttpRequestException("boom"));

        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));

        Assert.Empty(cut.FindAll("[data-testid=map-layers-forbidden]"));
        Assert.Equal(0, DrawModel().GetProperty("bases").GetArrayLength());
    }

    // ---- interaction -----------------------------------------------------------------------------------------------

    [Fact]
    public void TurningALayerOffRedrawsWithoutFetchingAnythingAgain()
    {
        GivenImage();
        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));
        var draws = JSInterop.Invocations["serverMap.draw"].Count;

        cut.Find("[data-testid=map-toggle-monuments]").Change(false);

        cut.WaitForAssertion(() => Assert.Equal(draws + 1, JSInterop.Invocations["serverMap.draw"].Count));
        Assert.False(DrawModel().GetProperty("showMonuments").GetBoolean());
        _client.Verify(c => c.GetMapImageAsync(_serverId), Times.Once);
        _client.Verify(c => c.GetBasesAsync(_serverId), Times.Once);
    }

    [Fact]
    public void RefreshReloadsThePlayersAndBasesAndRedrawsButNotThePicture()
    {
        GivenImage();
        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));

        cut.Find("[data-testid=map-refresh]").Click();

        cut.WaitForAssertion(() => _client.Verify(c => c.GetBasesAsync(_serverId), Times.Exactly(2)));
        _client.Verify(c => c.GetMapImageAsync(_serverId), Times.Once);
        _client.Verify(c => c.GetMapAsync(_serverId), Times.Once);
    }

    [Fact]
    public void ThePlayersAreAskedForOnlyAsRecentlyAsTheLiveWindow()
    {
        GivenImage();

        RenderPane().WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));

        _client.Verify(c => c.GetPositionsAsync(
            _serverId, It.Is<DateTimeOffset?>(s => s != null && s > DateTimeOffset.UtcNow.AddMinutes(-11) && s < DateTimeOffset.UtcNow.AddMinutes(-9)),
            null, null, It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void DisposingTheTabReleasesThePictureFromThePage()
    {
        GivenImage();
        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));

        cut.Instance.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Assert.Equal($"server-map-{_serverId:N}", Assert.Single(JSInterop.Invocations["serverMap.dispose"]).Arguments[0]);
    }

    [Fact]
    public void MonumentAndPlayerNamesAreDataForTheCanvasNeverMarkup()
    {
        GivenImage();
        GivenMap(available: true, monument: "<img src=x onerror=alert(1)>");
        GivenPlayers(Player("76561198000000001", "<script>alert(1)</script>", 1, 2));

        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));

        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll("script"));
        Assert.DoesNotContain("<img", cut.Markup);
    }

    // ---- zoom and pan ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheZoomButtonsAreOfferedWithAHintOnceThePictureIsShown()
    {
        GivenImage();

        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));

        Assert.NotEmpty(cut.FindAll("[data-testid=map-zoom-in]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=map-zoom-out]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=map-reset-view]"));
        Assert.Contains("Scroll to zoom", cut.Find("[data-testid=map-hint]").TextContent);
    }

    [Fact]
    public void ZoomInAndOutAskThePageToZoomAboutTheMiddleByTheSameFactorEitherWay()
    {
        GivenImage();
        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));

        cut.Find("[data-testid=map-zoom-in]").Click();
        cut.Find("[data-testid=map-zoom-out]").Click();

        var zooms = JSInterop.Invocations["serverMap.zoomBy"].ToList();
        Assert.Equal(2, zooms.Count);
        Assert.Equal($"server-map-{_serverId:N}", zooms[0].Arguments[0]);
        Assert.Equal(1.6, (double)zooms[0].Arguments[1]!, 6);
        Assert.Equal(1 / 1.6, (double)zooms[1].Arguments[1]!, 6);
    }

    [Fact]
    public void ResetShowsTheWholeMapAgain()
    {
        GivenImage();
        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));

        cut.Find("[data-testid=map-reset-view]").Click();

        Assert.Equal($"server-map-{_serverId:N}", Assert.Single(JSInterop.Invocations["serverMap.resetView"]).Arguments[0]);
    }

    [Fact]
    public void ZoomingNeverCrossesTheBlazorConnectionMoreThanOncePerButtonPress()
    {
        GivenImage();
        var cut = RenderPane();
        cut.WaitForAssertion(() => Assert.NotEmpty(JSInterop.Invocations["serverMap.draw"]));
        var draws = JSInterop.Invocations["serverMap.draw"].Count;

        cut.Find("[data-testid=map-zoom-in]").Click();

        Assert.Equal(draws, JSInterop.Invocations["serverMap.draw"].Count);        // the page redraws itself; Blazor is not asked to
        Assert.Single(JSInterop.Invocations["serverMap.zoomBy"]);
    }

    [Fact]
    public void TheZoomButtonsAreNotThereWhenThereIsNoPicture()
    {
        GivenMap(available: false);

        var cut = RenderPane();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=map-pending]")));
        Assert.Empty(cut.FindAll("[data-testid=map-zoom-in]"));
    }

    [Fact]
    public void ThePageHandlesTheWheelAndTheDragItselfSoBlazorNeverHearsAboutThem()
    {
        var script = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "RustArchon.Panel", "wwwroot", "js", "serverMap.js"));

        Assert.Contains("addEventListener('wheel'", script);
        Assert.Contains("addEventListener('pointermove'", script);
        Assert.Contains("passive: false", script);              // so the page can stop the wheel scrolling the whole page
        Assert.Contains("MAX_ZOOM", script);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RustArchon.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The solution folder was not found.");
    }
}
