// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JumpStart.Repositories;
using JumpStart.Services.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using RustArchon.Messaging.Contracts;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Servers;
using RustArchon.Panel.Infrastructure;
using RustArchon.Panel.Localization;
using RustArchon.Panel.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The Plugins tab on <c>ServerDetail</c>: it lists what the Api has stored for the server - name,
/// author, version - and says so plainly when there is nothing to list or the fetch fails.
/// </summary>
public class ServerDetailPluginsTabTests : BunitContext
{
    private readonly Mock<IRustServerApiClient> _rustServerClient = new();
    private readonly Guid _serverId = Guid.NewGuid();

    public ServerDetailPluginsTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var server = new RustServerDto
        {
            Id = _serverId,
            Name = "Test Server",
            Host = "127.0.0.1",
            Port = 28015,
            ConnectionStatus = RconConnectionStatus.Connected
        };

        _rustServerClient.Setup(c => c.GetByIdAsync(_serverId)).ReturnsAsync(server);
        _rustServerClient
            .Setup(c => c.GetEventsAsync(
                _serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<bool>()))
            .ReturnsAsync(new PagedResult<RconEventDto> { Items = [] });
        _rustServerClient.Setup(c => c.GetCurrentPlayersAsync(_serverId)).ReturnsAsync([]);
        _rustServerClient
            .Setup(c => c.GetKillsAsync(
                _serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync(new PagedResult<PlayerKillEventDto> { Items = [] });
        _rustServerClient
            .Setup(c => c.GetInactivePlayersAsync(_serverId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new PagedResult<InactivePlayerDto> { Items = [] });
        _rustServerClient
            .Setup(c => c.GetServerInfoHistoryAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync([]);
        _rustServerClient
            .Setup(c => c.GetConnectionLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync([]);
        _rustServerClient
            .Setup(c => c.SendCommandAsync(_serverId, It.IsAny<SendCommandRequest>()))
            .ReturnsAsync(new RconCommandResult(false, null, null, null, null));

        var hubClient = new Mock<IRconHubClient>();
        hubClient.Setup(h => h.ConnectAsync(_serverId)).Returns(Task.CompletedTask);

        var tokenStore = new Mock<ITokenStore>();
        tokenStore.Setup(t => t.GetToken()).Returns((string?)null);

        var valkeyCache = new Mock<IValkeyCache>();
        valkeyCache.Setup(c => c.GetStringAsync(It.IsAny<string>())).ReturnsAsync("RustArchon");

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()])
            .Returns((string key) => new LocalizedString(key, key));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns((string key, object[] args) => new LocalizedString(key, string.Format(key, args)));

        Services.AddSingleton(_rustServerClient.Object);
        Services.AddSingleton(hubClient.Object);
        Services.AddSingleton(tokenStore.Object);
        Services.AddSingleton(new SiteBrandingService(valkeyCache.Object, new Mock<ISiteBrandingApiClient>().Object));
        Services.AddSingleton(localizer.Object);

        AddAuthorization().SetAuthorized("test-admin");
    }

    private IRenderedComponent<ServerDetail> RenderPluginsTab()
    {
        // TabQuery is [SupplyParameterFromQuery] - see ServerDetailLogOrderingTests for why the tab is
        // chosen by navigating the fake NavigationManager rather than through the parameter builder.
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/servers/{_serverId}?tab=plugins");
        return Render<ServerDetail>(parameters => parameters.Add(p => p.Id, _serverId));
    }

    private static ServerPluginDto Plugin(string name, string author, string version, ServerModFramework framework) =>
        new() { Name = name, Author = author, Version = version, Framework = framework, CapturedAtUtc = DateTimeOffset.UtcNow };

    [Fact]
    public void ListsEachPluginsNameAuthorAndVersion()
    {
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync(
        [
            Plugin("Better Chat", "LaserHydra", "5.2.14", ServerModFramework.Carbon),
            Plugin("Kits", "Gachl", "4.0.0", ServerModFramework.Carbon)
        ]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".plugins-pane tbody tr")
                .Select(tr => tr.QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToArray())
                .ToList();

            Assert.Equal(2, rows.Count);
            Assert.Equal(["Better Chat", "LaserHydra", "5.2.14"], rows[0]);
            Assert.Equal(["Kits", "Gachl", "4.0.0"], rows[1]);
        });
        Assert.Contains("Carbon", cut.Find(".plugins-pane .badge").TextContent);
    }

    [Fact]
    public void NoPlugins_ShowsTheEmptyState()
    {
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync([]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".plugins-pane table"));
            Assert.Contains("No Oxide or Carbon plugins have been reported", cut.Find(".plugins-pane .console-empty").TextContent);
        });
    }

    [Fact]
    public void AFailedFetch_ShowsAnErrorInsteadOfAnEmptyList()
    {
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() =>
            Assert.Contains("Failed to load the plugin list.", cut.Find(".plugins-pane .alert").TextContent));
    }

    // ---- update notices from UpdateChecker -----------------------------------------------------------------

    private static PluginUpdateNoticeDto Notice(
        string name, string installed = "3.1.0", string latest = "3.2.4", string url = "https://umod.org/plugins/backpacks", string marketplace = "uMod") => new()
    {
        Name = name, InstalledVersion = installed, ReportedVersion = installed, LatestVersion = latest, Url = url, Marketplace = marketplace,
        FirstSeenUtc = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero), LastSeenUtc = new DateTimeOffset(2026, 9, 20, 14, 30, 0, TimeSpan.Zero), TimesSeen = 5
    };

    private void GivenPluginsAndUpdates(PluginUpdateNoticeDto[] notices)
    {
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync(
        [
            Plugin("Backpacks", "Mevent", "3.1.0", ServerModFramework.Carbon),
            Plugin("Kits", "Gachl", "4.0.0", ServerModFramework.Carbon)
        ]);
        _rustServerClient.Setup(c => c.GetPluginUpdatesAsync(_serverId)).ReturnsAsync([.. notices]);
    }

    [Fact]
    public void APluginWithAnUpdateShowsTheNewestVersionAndALinkToItsPage()
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        var rows = cut.FindAll("[data-testid=plugin-row]");
        Assert.Contains("Update available: 3.2.4", rows[0].QuerySelector("[data-testid=plugin-update-badge]")!.TextContent);
        var link = rows[0].QuerySelector("[data-testid=plugin-update-link]")!;
        Assert.Equal("https://umod.org/plugins/backpacks", link.GetAttribute("href"));
        Assert.Contains("View on uMod", link.TextContent);
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("noopener", link.GetAttribute("rel"));
        Assert.Contains("noreferrer", link.GetAttribute("rel"));
    }

    [Fact]
    public void APluginWithNoUpdateShowsNothingExtra()
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        Assert.Null(cut.FindAll("[data-testid=plugin-row]")[1].QuerySelector("[data-testid=plugin-update]"));
    }

    [Fact]
    public void EveryFieldTheNoticeCarriesIsAvailableInTheBadgesTooltip()
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update-badge]")));
        var title = cut.Find("[data-testid=plugin-update-badge]").GetAttribute("title")!;
        Assert.Contains("Installed 3.1.0", title);
        Assert.Contains("newest 3.2.4", title);
        Assert.Contains("first 2026-09-20 08:00 UTC", title);
        Assert.Contains("last 2026-09-20 14:30 UTC", title);
        Assert.Contains("5 time(s)", title);
    }

    [Fact]
    public void TheSummaryCountsHowManyPluginsHaveUpdates()
    {
        GivenPluginsAndUpdates([Notice("Backpacks"), Notice("Kits", "4.0.0", "4.1.0", "https://codefling.com/plugins/kits", "Codefling")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-updates-summary]")));
        var summary = cut.Find("[data-testid=plugin-updates-summary]").TextContent;
        Assert.Contains("2", summary);
        Assert.Contains("UpdateChecker", summary);
        Assert.Equal(2, cut.FindAll("[data-testid=plugin-update]").Count);
    }

    [Fact]
    public void WithNoUpdatesThereIsNoSummaryAndNoBadge()
    {
        GivenPluginsAndUpdates([]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=plugin-row]").Count));
        Assert.Empty(cut.FindAll("[data-testid=plugin-updates-summary]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update]"));
    }

    [Fact]
    public void ANoticeWithNoAddressStillShowsTheUpdateAndTheMarketplaceButNoLink()
    {
        GivenPluginsAndUpdates([Notice("Backpacks", url: "")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update-link]"));
        Assert.Equal("uMod", cut.Find("[data-testid=plugin-update-marketplace]").TextContent);
    }

    [Fact]
    public void ANoticeWithNoMarketplaceLinksToTheUpdatePage()
    {
        GivenPluginsAndUpdates([Notice("Backpacks", marketplace: "")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update-link]")));
        Assert.Contains("View update page", cut.Find("[data-testid=plugin-update-link]").TextContent);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative")]
    public void AnAddressThatIsNotAWebAddressIsNeverMadeALinkEvenIfTheApiPassedItOn(string url)
    {
        GivenPluginsAndUpdates([Notice("Backpacks", url: url)]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update-link]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-row] a[href]"));
    }

    [Fact]
    public void TextFromTheNoticeIsShownAsTextNeverAsMarkup()
    {
        GivenPluginsAndUpdates([Notice("Backpacks", latest: "<img src=x onerror=alert(1)>", marketplace: "<b>evil</b>")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update] img"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update] b"));
        Assert.Contains("<img src=x onerror=alert(1)>", cut.Find("[data-testid=plugin-update-badge]").TextContent);
    }

    [Fact]
    public void ANoticeIsMatchedToItsPluginWithoutRegardToCase()
    {
        GivenPluginsAndUpdates([Notice("BACKPACKS")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotNull(cut.FindAll("[data-testid=plugin-row]")[0].QuerySelector("[data-testid=plugin-update]")));
    }

    [Fact]
    public void AFailedUpdatesFetchStillShowsThePluginList()
    {
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync([Plugin("Backpacks", "Mevent", "3.1.0", ServerModFramework.Carbon)]);
        _rustServerClient.Setup(c => c.GetPluginUpdatesAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-row]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update]"));
        Assert.Empty(cut.FindAll(".plugins-pane .alert"));
    }
}
