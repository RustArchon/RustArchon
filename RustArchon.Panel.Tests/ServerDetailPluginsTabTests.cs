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
    private readonly Mock<IRconHubClient> _hub = new();
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

        _hub.Setup(h => h.ConnectAsync(_serverId)).Returns(Task.CompletedTask);

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
        Services.AddSingleton(_hub.Object);
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
            Assert.Equal(["Better Chat", "LaserHydra", "5.2.14", "Yes"], rows[0]);
            Assert.Equal(["Kits", "Gachl", "4.0.0", "Yes"], rows[1]);
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
        string name, string installed = "3.1.0", string latest = "3.2.4", string url = "https://umod.org/plugins/backpacks", string marketplace = "uMod",
        string download = "") => new()
    {
        Name = name, InstalledVersion = installed, ReportedVersion = installed, LatestVersion = latest, Url = url, Marketplace = marketplace, DownloadUrl = download,
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

    // ---- the direct download link ----------------------------------------------------------------------------

    [Fact]
    public void AnUpdateWithADirectDownloadOffersItBesideTheMarketplacePageLink()
    {
        GivenPluginsAndUpdates([Notice("Backpacks", download: "https://umod.org/plugins/Backpacks.cs")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update-download]")));
        var row = cut.FindAll("[data-testid=plugin-row]")[0];
        var download = row.QuerySelector("[data-testid=plugin-update-download]")!;
        Assert.Equal("https://umod.org/plugins/Backpacks.cs", download.GetAttribute("href"));
        Assert.Contains("Download 3.2.4", download.TextContent);
        Assert.Equal("_blank", download.GetAttribute("target"));
        Assert.Contains("noopener", download.GetAttribute("rel"));
        Assert.Contains("noreferrer", download.GetAttribute("rel"));
        Assert.Contains("Check what you download", download.GetAttribute("title"));
        Assert.NotNull(row.QuerySelector("[data-testid=plugin-update-link]"));   // the page link is still there
    }

    [Fact]
    public void WithNoDirectDownloadThereIsNoDownloadLinkOnlyThePageLink()
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update-download]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update-link]"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative/path.cs")]
    [InlineData("not a url")]
    public void AnAddressThatIsNotAWebAddressIsNeverLinkedEvenIfTheApiSentIt(string download)
    {
        // The Api checks first; the page does not rely on it.
        GivenPluginsAndUpdates([Notice("Backpacks", download: download)]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-update-download]"));
    }

    [Fact]
    public void OnlyThePluginsThatHaveAFoundAddressGetADownloadLink()
    {
        GivenPluginsAndUpdates([
            Notice("Backpacks", download: "https://umod.org/plugins/Backpacks.cs"),
            Notice("Kits", "4.0.0", "4.1.0", "https://codefling.com/plugins/kits", "Codefling")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=plugin-update]").Count));
        Assert.Single(cut.FindAll("[data-testid=plugin-update-download]"));
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

    // ---- the chip on the Plugins tab button ---------------------------------------------------------------

    /// <summary>The page on a tab other than Plugins, which is where the chip has to be useful: the person is not looking at the list.</summary>
    private IRenderedComponent<ServerDetail> RenderConsoleTab()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/servers/{_serverId}");
        return Render<ServerDetail>(parameters => parameters.Add(p => p.Id, _serverId));
    }

    [Fact]
    public void TheTabShowsHowManyPluginsUpdateCheckerSaysHaveUpdatesFromAnyTab()
    {
        GivenPluginsAndUpdates([Notice("Backpacks"), Notice("Kits", "4.0.0", "4.1.0", "https://codefling.com/plugins/kits", "Codefling")]);

        var cut = RenderConsoleTab();

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("[data-testid=plugins-updates-badge]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid=plugin-updates-summary]"));   // the tab itself is not open, only its button says so
    }

    [Fact]
    public void TheChipSaysWhereTheCountComesFrom()
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]);

        var cut = RenderConsoleTab();

        cut.WaitForAssertion(() => Assert.Contains("UpdateChecker", cut.Find("[data-testid=plugins-updates-badge]").GetAttribute("title")));
    }

    // ---- the chip's color says how much of the gap RustArchon can already close --------------------------------------

    [Fact]
    public void WhenEveryUpdateHasADirectLinkTheChipIsGreen()
    {
        GivenPluginsAndUpdates([Notice("Backpacks", download: "https://umod.org/plugins/Backpacks.cs")]);

        var cut = RenderConsoleTab();

        cut.WaitForAssertion(() => Assert.Contains("text-bg-success", cut.Find("[data-testid=plugins-updates-badge]").GetAttribute("class")));
        Assert.Contains("A direct download link is available for all of these.", cut.Find("[data-testid=plugins-updates-badge]").GetAttribute("title"));
    }

    [Fact]
    public void WhenNoUpdateHasADirectLinkTheChipIsRed()
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]); // Notice()'s default download is "" - no direct link

        var cut = RenderConsoleTab();

        cut.WaitForAssertion(() => Assert.Contains("text-bg-danger", cut.Find("[data-testid=plugins-updates-badge]").GetAttribute("class")));
        Assert.Contains("each needs a trip to its own marketplace page", cut.Find("[data-testid=plugins-updates-badge]").GetAttribute("title"));
    }

    [Fact]
    public void AMixOfBothIsAmber()
    {
        GivenPluginsAndUpdates(
        [
            Notice("Backpacks", download: "https://umod.org/plugins/Backpacks.cs"),
            Notice("Kits", "4.0.0", "4.1.0", "https://codefling.com/plugins/kits", "Codefling")
        ]);

        var cut = RenderConsoleTab();

        cut.WaitForAssertion(() => Assert.Contains("text-bg-warning", cut.Find("[data-testid=plugins-updates-badge]").GetAttribute("class")));
        Assert.Contains("1", cut.Find("[data-testid=plugins-updates-badge]").GetAttribute("title"));
    }

    [Fact]
    public void TheInPaneSummaryBadgeMatchesTheTabsColorAndExplanation()
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]); // no direct link -> red

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-updates-summary] .badge")));
        var badge = cut.Find("[data-testid=plugin-updates-summary] .badge");
        Assert.Contains("text-bg-danger", badge.GetAttribute("class"));
        Assert.Contains("each needs a trip to its own marketplace page", badge.GetAttribute("title"));
    }

    [Fact]
    public void WithNoUpdatesTheTabHasNoChip()
    {
        GivenPluginsAndUpdates([]);

        var cut = RenderConsoleTab();

        cut.WaitForAssertion(() => _rustServerClient.Verify(c => c.GetPluginUpdatesAsync(_serverId), Times.AtLeastOnce));
        Assert.Empty(cut.FindAll("[data-testid=plugins-updates-badge]"));
    }

    [Fact]
    public void AFailedUpdatesFetchLeavesNoChipAndBreaksNothing()
    {
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync([]);
        _rustServerClient.Setup(c => c.GetPluginUpdatesAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderConsoleTab();

        cut.WaitForAssertion(() => _rustServerClient.Verify(c => c.GetPluginUpdatesAsync(_serverId), Times.AtLeastOnce));
        Assert.Empty(cut.FindAll("[data-testid=plugins-updates-badge]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=tab-map]"));   // the page itself is up
    }

    [Fact]
    public void TheChipAppearsAsSoonAsTheApiSaysAnUpdateArrivedWithoutReloadingThePage()
    {
        GivenPluginsAndUpdates([]);
        var cut = RenderConsoleTab();
        cut.WaitForAssertion(() => _rustServerClient.Verify(c => c.GetPluginUpdatesAsync(_serverId), Times.AtLeastOnce));
        Assert.Empty(cut.FindAll("[data-testid=plugins-updates-badge]"));

        _rustServerClient.Setup(c => c.GetPluginUpdatesAsync(_serverId)).ReturnsAsync([Notice("Backpacks")]);
        _hub.Raise(h => h.PluginUpdatesChanged += null);

        cut.WaitForAssertion(() => Assert.Equal("1", cut.Find("[data-testid=plugins-updates-badge]").TextContent.Trim()));
    }

    [Fact]
    public void TheChipCountGrowsWhenAnotherUpdateArrives()
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]);
        var cut = RenderConsoleTab();
        cut.WaitForAssertion(() => Assert.Equal("1", cut.Find("[data-testid=plugins-updates-badge]").TextContent.Trim()));

        _rustServerClient.Setup(c => c.GetPluginUpdatesAsync(_serverId)).ReturnsAsync(
            [Notice("Backpacks"), Notice("Kits", "4.0.0", "4.1.0", "https://codefling.com/plugins/kits", "Codefling")]);
        _hub.Raise(h => h.PluginUpdatesChanged += null);

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("[data-testid=plugins-updates-badge]").TextContent.Trim()));
    }

    [Fact]
    public void AFailedLiveRefreshShowsNoChipRatherThanAStaleCount()
    {
        // The chip is only ever what the Api last said. A failed re-read shows no chip - the same as a failed first load - because a
        // wrong number is worse than none.
        GivenPluginsAndUpdates([Notice("Backpacks")]);
        var cut = RenderConsoleTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugins-updates-badge]")));

        _rustServerClient.Setup(c => c.GetPluginUpdatesAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));
        _hub.Raise(h => h.PluginUpdatesChanged += null);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=plugins-updates-badge]")));
    }

    [Fact]
    public async Task LeavingThePageStopsListeningForUpdates()
    {
        GivenPluginsAndUpdates([]);
        var cut = RenderConsoleTab();
        cut.WaitForAssertion(() => _rustServerClient.Verify(c => c.GetPluginUpdatesAsync(_serverId), Times.AtLeastOnce));

        await DisposeComponentsAsync();

        _hub.VerifyRemove(h => h.PluginUpdatesChanged -= It.IsAny<Action>(), Times.Once);
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

    // ---- Refresh asks for a poll before it re-reads ----------------------------------------------------------------

    [Fact]
    public void ClickingRefreshPollsTheServerBeforeReReadingTheList()
    {
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync([Plugin("Backpacks", "Mevent", "3.1.0", ServerModFramework.Carbon)]);
        _rustServerClient.Setup(c => c.PollPluginsNowAsync(_serverId)).ReturnsAsync(new ServerPollResultDto { Outcome = "polled" });
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => _rustServerClient.Verify(c => c.GetPluginsAsync(_serverId), Times.AtLeastOnce));
        var readsBefore = _rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetPluginsAsync));

        cut.Find("[data-testid=plugins-refresh]").Click();

        cut.WaitForAssertion(() =>
        {
            _rustServerClient.Verify(c => c.PollPluginsNowAsync(_serverId), Times.Once);
            Assert.True(_rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetPluginsAsync)) > readsBefore);
        });
    }

    [Fact]
    public void APollThatCouldNotBeSentStillReReadsTheList()
    {
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync([Plugin("Backpacks", "Mevent", "3.1.0", ServerModFramework.Carbon)]);
        _rustServerClient.Setup(c => c.PollPluginsNowAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-row]")));
        var readsBefore = _rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetPluginsAsync));

        cut.Find("[data-testid=plugins-refresh]").Click();

        cut.WaitForAssertion(() => Assert.True(_rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetPluginsAsync)) > readsBefore));
        Assert.Single(cut.FindAll("[data-testid=plugin-row]"));
    }

    // ---- automatic updates for the other plugins -------------------------------------------------------------------

    private static readonly DateTimeOffset NextWipe = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    private static ThirdPartyPluginUpdateSettingsDto ThirdParty(
        bool planOffers = true, bool enabled = false, int holdDays = 7, DateTimeOffset? heldUntil = null) => new()
    {
        PlanOffers = planOffers, Enabled = enabled, HoldDays = holdDays, MaxHoldDays = 21, NextWipeUtc = NextWipe, HeldUntilUtc = heldUntil,
        State = !planOffers ? "plan_does_not_offer" : !enabled ? "not_opted_in" : heldUntil is not null ? "held_for_wipe" : "open"
    };

    private IRenderedComponent<ServerDetail> RenderWithThirdParty(ThirdPartyPluginUpdateSettingsDto? settings)
    {
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync([Plugin("Backpacks", "Mevent", "3.1.0", ServerModFramework.Carbon)]);
        if (settings is not null)
        {
            _rustServerClient.Setup(c => c.GetThirdPartyUpdateSettingsAsync(_serverId)).ReturnsAsync(settings);
        }

        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-row]")));
        return cut;
    }

    [Fact]
    public void TheThirdPartyUpdateCardIsNotOfferedWhenThePlanDoesNotIncludeIt()
    {
        var cut = RenderWithThirdParty(ThirdParty(planOffers: false));

        Assert.Empty(cut.FindAll("[data-testid=third-party-updates]"));
    }

    [Fact]
    public void TheCardIsNotShownWhenTheSettingsCannotBeReadAndTheListStillIs()
    {
        _rustServerClient.Setup(c => c.GetThirdPartyUpdateSettingsAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderWithThirdParty(null);

        Assert.Empty(cut.FindAll("[data-testid=third-party-updates]"));
        Assert.Single(cut.FindAll("[data-testid=plugin-row]"));
    }

    [Fact]
    public void TheCardShowsTheOptInTheDaysAndTheNextWipeInUtc()
    {
        var cut = RenderWithThirdParty(ThirdParty(enabled: true, holdDays: 14));

        Assert.True(cut.Find("#third-party-updates-enabled").HasAttribute("checked"));
        Assert.Equal("14", cut.Find("#third-party-hold-days").GetAttribute("value"));
        Assert.Equal("21", cut.Find("#third-party-hold-days").GetAttribute("max"));
        Assert.Contains("Thu 1 Oct 2026 18:00 UTC", cut.Find("[data-testid=third-party-wipe]").TextContent);
    }

    [Fact]
    public void AServerThatHasNotOptedInShowsTheSwitchOff()
    {
        var cut = RenderWithThirdParty(ThirdParty(enabled: false));

        Assert.False(cut.Find("#third-party-updates-enabled").HasAttribute("checked"));
    }

    [Fact]
    public void TheNoteThatUpdatesArePausedShowsOnlyForAnOptedInServerInsideItsWindow()
    {
        var inWindow = RenderWithThirdParty(ThirdParty(enabled: true, heldUntil: NextWipe));
        Assert.Single(inWindow.FindAll("[data-testid=third-party-held]"));
    }

    [Fact]
    public void NoPausedNoteOutsideTheWindowOrWhenNotOptedIn()
    {
        var outside = RenderWithThirdParty(ThirdParty(enabled: true, heldUntil: null));
        Assert.Empty(outside.FindAll("[data-testid=third-party-held]"));
    }

    [Fact]
    public void NoPausedNoteForAServerThatHasNotOptedInEvenInsideTheWindow()
    {
        var cut = RenderWithThirdParty(ThirdParty(enabled: false, heldUntil: NextWipe));

        Assert.Empty(cut.FindAll("[data-testid=third-party-held]"));
    }

    [Fact]
    public void TurningTheSwitchOnSavesItWithTheDaysAlreadyShownAndShowsWhatTheApiSaid()
    {
        UpdateThirdPartyPluginUpdateSettingsDto? sent = null;
        _rustServerClient.Setup(c => c.UpdateThirdPartyUpdateSettingsAsync(_serverId, It.IsAny<UpdateThirdPartyPluginUpdateSettingsDto>()))
            .Callback((Guid _, UpdateThirdPartyPluginUpdateSettingsDto dto) => sent = dto)
            .ReturnsAsync(ThirdParty(enabled: true, holdDays: 10));
        var cut = RenderWithThirdParty(ThirdParty(enabled: false, holdDays: 10));

        cut.Find("#third-party-updates-enabled").Change(true);

        cut.WaitForAssertion(() => Assert.True(cut.Find("#third-party-updates-enabled").HasAttribute("checked")));
        Assert.True(sent!.Enabled);
        Assert.Equal(10, sent.HoldDays);
    }

    [Fact]
    public void ChangingTheDaysSavesThemWithTheOptInAsItIs()
    {
        UpdateThirdPartyPluginUpdateSettingsDto? sent = null;
        _rustServerClient.Setup(c => c.UpdateThirdPartyUpdateSettingsAsync(_serverId, It.IsAny<UpdateThirdPartyPluginUpdateSettingsDto>()))
            .Callback((Guid _, UpdateThirdPartyPluginUpdateSettingsDto dto) => sent = dto)
            .ReturnsAsync(ThirdParty(enabled: true, holdDays: 3));
        var cut = RenderWithThirdParty(ThirdParty(enabled: true, holdDays: 7));

        cut.Find("#third-party-hold-days").Change("3");

        cut.WaitForAssertion(() => Assert.Equal("3", cut.Find("#third-party-hold-days").GetAttribute("value")));
        Assert.True(sent!.Enabled);
        Assert.Equal(3, sent.HoldDays);
    }

    [Fact]
    public void ZeroDaysIsAllowedAndSentAsZero()
    {
        UpdateThirdPartyPluginUpdateSettingsDto? sent = null;
        _rustServerClient.Setup(c => c.UpdateThirdPartyUpdateSettingsAsync(_serverId, It.IsAny<UpdateThirdPartyPluginUpdateSettingsDto>()))
            .Callback((Guid _, UpdateThirdPartyPluginUpdateSettingsDto dto) => sent = dto)
            .ReturnsAsync(ThirdParty(enabled: true, holdDays: 0));
        var cut = RenderWithThirdParty(ThirdParty(enabled: true, holdDays: 7));

        cut.Find("#third-party-hold-days").Change("0");

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal(0, sent!.HoldDays);
    }

    [Theory]
    [InlineData("22")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public void ADaysValueThatIsNotAWholeNumberInRangeIsNotSentAndSaysSo(string typed)
    {
        var cut = RenderWithThirdParty(ThirdParty(enabled: true, holdDays: 7));
        var before = cut.Find("#third-party-hold-days");

        before.Change(typed);

        cut.WaitForAssertion(() => Assert.Contains("0 to 21", cut.Find("[data-testid=third-party-error]").TextContent));
        // The box was re-created, so what was typed does not stay on screen next to a saved value that is different.
        Assert.NotSame(before, cut.Find("#third-party-hold-days"));
        Assert.Equal("7", cut.Find("#third-party-hold-days").GetAttribute("value"));
        _rustServerClient.Verify(c => c.UpdateThirdPartyUpdateSettingsAsync(It.IsAny<Guid>(), It.IsAny<UpdateThirdPartyPluginUpdateSettingsDto>()), Times.Never());
    }

    [Fact]
    public void AFailedSaveSaysSoAndKeepsTheCard()
    {
        _rustServerClient.Setup(c => c.UpdateThirdPartyUpdateSettingsAsync(_serverId, It.IsAny<UpdateThirdPartyPluginUpdateSettingsDto>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderWithThirdParty(ThirdParty(enabled: false));

        cut.Find("#third-party-updates-enabled").Change(true);

        cut.WaitForAssertion(() => Assert.Contains("Failed to save", cut.Find("[data-testid=third-party-error]").TextContent));
        Assert.Single(cut.FindAll("[data-testid=third-party-updates]"));
    }

    // ---- applying an update -----------------------------------------------------------------------------------------

    private const string OfferSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static ThirdPartyPluginUpdateOfferDto Offer(
        string state = "ready", bool canApply = true, string detail = "", string name = "Backpacks", DateTimeOffset? pausedUntil = null) => new()
    {
        PluginName = name, State = state, Detail = detail, FileSha256 = OfferSha, CanApply = canApply, AutomaticUpdatesPausedUntilUtc = pausedUntil
    };

    private IRenderedComponent<ServerDetail> RenderWithOffer(ThirdPartyPluginUpdateOfferDto? offer)
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]);
        _rustServerClient.Setup(c => c.GetThirdPartyUpdatesAsync(_serverId)).ReturnsAsync(offer is null ? [] : [offer]);
        var cut = RenderPluginsTab();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        return cut;
    }

    [Fact]
    public void APluginWhoseUpdateCheckerNameHasNoSpacesStillMatchesItsOfferByNormalizedName()
    {
        // Real shape: the installed plugin list ("Monument Addons") and the UpdateChecker-reported notice/offer name ("MonumentAddons") legitimately
        // differ by spacing alone. A plugin that came up matched (and Ready) on the Api side must not be split apart from its own row here.
        _rustServerClient.Setup(c => c.GetPluginsAsync(_serverId)).ReturnsAsync([Plugin("Monument Addons", "misticos", "0.21.3", ServerModFramework.Carbon)]);
        _rustServerClient.Setup(c => c.GetPluginUpdatesAsync(_serverId)).ReturnsAsync([Notice("Monument Addons", installed: "0.21.3", latest: "0.21.4")]);
        _rustServerClient.Setup(c => c.GetThirdPartyUpdatesAsync(_serverId)).ReturnsAsync([Offer(name: "MonumentAddons")]);

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.Equal("Update now", cut.Find("[data-testid=plugin-apply-button]").TextContent.Trim()));
    }

    [Fact]
    public void AReadyFileOffersUpdateNow()
    {
        var cut = RenderWithOffer(Offer());

        cut.WaitForAssertion(() => Assert.Equal("Update now", cut.Find("[data-testid=plugin-apply-button]").TextContent.Trim()));
    }

    [Fact]
    public void NothingIsOfferedWhenTheApiHasNothingToSay()
    {
        var cut = RenderWithOffer(null);

        Assert.Empty(cut.FindAll("[data-testid=plugin-apply]"));
    }

    [Fact]
    public void AFailedOffersReadStillShowsTheNotices()
    {
        GivenPluginsAndUpdates([Notice("Backpacks")]);
        _rustServerClient.Setup(c => c.GetThirdPartyUpdatesAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderPluginsTab();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-update]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-apply]"));
    }

    [Fact]
    public void OfferedButNotApplicableShowsTheStateWithoutAButton()
    {
        var cut = RenderWithOffer(Offer(canApply: false));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-apply]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-apply-button]"));
    }

    [Fact]
    public void ClickingUpdateNowSendsTheHashThePersonWasShownAndRereadsTheOffers()
    {
        _rustServerClient.Setup(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.IsAny<ApplyThirdPartyPluginUpdateDto>()))
            .ReturnsAsync(new PluginUpdateResultDto { Started = true, Code = "started" });
        var cut = RenderWithOffer(Offer());
        var readsBefore = _rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync));

        cut.WaitForElement("[data-testid=plugin-apply-button]").Click();

        cut.WaitForAssertion(() =>
        {
            _rustServerClient.Verify(c => c.ApplyThirdPartyUpdateAsync(
                _serverId, It.Is<ApplyThirdPartyPluginUpdateDto>(r => r.PluginName == "Backpacks" && r.FileSha256 == OfferSha)), Times.Once);
            Assert.True(_rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync)) > readsBefore);
        });
    }

    [Fact]
    public void ARefusalIsShownBesideThePluginWithTheApisMessage()
    {
        _rustServerClient.Setup(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.IsAny<ApplyThirdPartyPluginUpdateDto>()))
            .ReturnsAsync(new PluginUpdateResultDto { Started = false, Code = "in_progress", Message = "Another plugin update is still in progress on this server." });
        var cut = RenderWithOffer(Offer());

        cut.WaitForElement("[data-testid=plugin-apply-button]").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Another plugin update is still in progress on this server.", cut.Find("[data-testid=plugin-apply-error]").TextContent.Trim()));
    }

    [Fact]
    public void AFailedRequestSaysTheUpdateCouldNotBeStarted()
    {
        _rustServerClient.Setup(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.IsAny<ApplyThirdPartyPluginUpdateDto>())).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderWithOffer(Offer());

        cut.WaitForElement("[data-testid=plugin-apply-button]").Click();

        cut.WaitForAssertion(() => Assert.Contains("could not be started", cut.Find("[data-testid=plugin-apply-error]").TextContent));
    }

    [Theory]
    [InlineData("applying", "Applying the update")]
    [InlineData("applied", "Updated. The new version loaded.")]
    [InlineData("rolled-back", "the old one was put back")]
    [InlineData("failed", "The update did not complete.")]
    [InlineData("refused", "The server turned the update down.")]
    [InlineData("needs-instructions", "zip archive")]
    [InlineData("not-applicable", "cannot be applied automatically")]
    public void EachStateIsExplainedInWords(string state, string expected)
    {
        var cut = RenderWithOffer(Offer(state, canApply: false));

        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Find("[data-testid=plugin-apply-status]").TextContent));
        Assert.Equal(state, cut.Find("[data-testid=plugin-apply]").GetAttribute("data-state"));
    }

    [Fact]
    public void AReadyOfferWithNothingBlockingItShowsNoExplanation()
    {
        var cut = RenderWithOffer(Offer("ready", canApply: true, detail: ""));

        cut.WaitForAssertion(() => Assert.Equal("Update now", cut.Find("[data-testid=plugin-apply-button]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid=plugin-apply-status]"));
    }

    [Fact]
    public void AReadyOfferThatIsBlockedExplainsWhyInsteadOfJustHidingTheButton()
    {
        var cut = RenderWithOffer(Offer("ready", canApply: false, detail: "Another update is in progress on this server; this one waits for it to finish."));

        cut.WaitForAssertion(() => Assert.Contains("Another update is in progress", cut.Find("[data-testid=plugin-apply-status]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=plugin-apply-button]"));
    }

    [Fact]
    public void ANeedsInstructionsOfferThatIsBlockedExplainsWhyBesideTheFixedCopy()
    {
        var offer = Offer("needs-instructions", canApply: false, detail: "The RustArchon plugin on this server cannot unpack archives.");
        offer.Kind = "zip";
        var cut = RenderWithOffer(offer);

        cut.WaitForAssertion(() => Assert.Contains("cannot unpack archives", cut.Find("[data-testid=plugin-apply-status]").TextContent));
        Assert.Contains("only applied once you say which of its files go where", cut.Find("[data-testid=plugin-apply-status]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=zip-instructions]"));
    }

    [Theory]
    [InlineData("rolled-back")]
    [InlineData("failed")]
    [InlineData("refused")]
    public void WhatTheServerSaidWentWrongIsShownAsTextAndTheUpdateCanBeTriedAgain(string state)
    {
        var cut = RenderWithOffer(Offer(state, detail: "the plugin did not appear <b>bold</b>"));

        cut.WaitForAssertion(() => Assert.Contains("the plugin did not appear <b>bold</b>", cut.Find("[data-testid=plugin-apply-status]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=plugin-apply-status] b"));          // text, never markup
        Assert.Equal("Try again", cut.Find("[data-testid=plugin-apply-button]").TextContent.Trim());
    }

    [Fact]
    public void AChangedFileIsNotAppliedByASingleClick()
    {
        var cut = RenderWithOffer(Offer("changed"));

        cut.WaitForAssertion(() => Assert.Contains("does not match the one on record here", cut.Find("[data-testid=plugin-apply-status]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=plugin-apply-button]"));           // only "Review and apply" is there
        cut.Find("[data-testid=plugin-apply-review]").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-apply-confirm]")));
        _rustServerClient.Verify(c => c.ApplyThirdPartyUpdateAsync(It.IsAny<Guid>(), It.IsAny<ApplyThirdPartyPluginUpdateDto>()), Times.Never);
    }

    [Fact]
    public void ConfirmingAChangedFileAppliesItAndCancellingDoesNot()
    {
        _rustServerClient.Setup(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.IsAny<ApplyThirdPartyPluginUpdateDto>()))
            .ReturnsAsync(new PluginUpdateResultDto { Started = true, Code = "started" });
        var cut = RenderWithOffer(Offer("changed"));
        cut.WaitForElement("[data-testid=plugin-apply-review]").Click();

        cut.WaitForElement("[data-testid=plugin-apply-cancel]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=plugin-apply-confirm]")));
        _rustServerClient.Verify(c => c.ApplyThirdPartyUpdateAsync(It.IsAny<Guid>(), It.IsAny<ApplyThirdPartyPluginUpdateDto>()), Times.Never);

        cut.Find("[data-testid=plugin-apply-review]").Click();
        cut.WaitForElement("[data-testid=plugin-apply-button]").Click();

        cut.WaitForAssertion(() => _rustServerClient.Verify(
            c => c.ApplyThirdPartyUpdateAsync(_serverId, It.Is<ApplyThirdPartyPluginUpdateDto>(r => r.FileSha256 == OfferSha)), Times.Once));
    }

    // ---- checking the file again, on request -----------------------------------------------------------------------------

    [Fact]
    public void CheckAgainIsOfferedEvenWhenTheOfferCannotBeAppliedRightNow()
    {
        // A recheck never talks to this server's plugin, so it is never blocked by whatever is holding CanApply back.
        var cut = RenderWithOffer(Offer("ready", canApply: false, detail: "Another update is in progress on this server; this one waits for it to finish."));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-recheck-button]")));
    }

    [Theory]
    [InlineData("applying")]
    [InlineData("applied")]
    public void CheckAgainIsNotOfferedWhileApplyingOrOnceApplied(string state)
    {
        var cut = RenderWithOffer(Offer(state, canApply: false));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-apply-status]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-recheck-button]"));
    }

    [Fact]
    public void ClickingCheckAgainAsksTheApiAndReReadsTheOffers()
    {
        _rustServerClient.Setup(c => c.RecheckThirdPartyFileAsync(_serverId, It.IsAny<RecheckThirdPartyPluginFileDto>())).ReturnsAsync(true);
        var cut = RenderWithOffer(Offer("changed"));
        var readsBefore = _rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync));

        cut.Find("[data-testid=plugin-recheck-button]").Click();

        cut.WaitForAssertion(() =>
        {
            _rustServerClient.Verify(c => c.RecheckThirdPartyFileAsync(_serverId, It.Is<RecheckThirdPartyPluginFileDto>(r => r.PluginName == "Backpacks")), Times.Once);
            Assert.True(_rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync)) > readsBefore);
        });
    }

    [Fact]
    public void WhenThereIsNothingToRecheckItSaysSo()
    {
        _rustServerClient.Setup(c => c.RecheckThirdPartyFileAsync(_serverId, It.IsAny<RecheckThirdPartyPluginFileDto>())).ReturnsAsync(false);
        var cut = RenderWithOffer(Offer("changed"));

        cut.Find("[data-testid=plugin-recheck-button]").Click();

        cut.WaitForAssertion(() => Assert.Contains("nothing to check again", cut.Find("[data-testid=plugin-recheck-error]").TextContent));
    }

    [Fact]
    public void AFailedRecheckRequestSaysSo()
    {
        _rustServerClient.Setup(c => c.RecheckThirdPartyFileAsync(_serverId, It.IsAny<RecheckThirdPartyPluginFileDto>())).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderWithOffer(Offer("changed"));

        cut.Find("[data-testid=plugin-recheck-button]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Could not check the file again", cut.Find("[data-testid=plugin-recheck-error]").TextContent));
    }

    [Fact]
    public void ACheckedFileThatTurnsOutStillTheSameCanStillBeAppliedNotJustLookedAt()
    {
        // The dead end Scott reported: "it says check it before applying, but there's no way to apply it after checking." CanApply now comes
        // straight from the Api regardless of whether the hash moved, so the Panel just has to honor it, not compute anything of its own.
        var cut = RenderWithOffer(Offer("changed", canApply: true, detail: "The file on record here still hashes the same as what was already sent; applying again asks the server to download it once more."));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-apply-review]")));
    }

    [Fact]
    public void WhenAutomaticUpdatesArePausedTheOfferSaysUntilWhenAndStillAllowsUpdatingNow()
    {
        var cut = RenderWithOffer(Offer(pausedUntil: new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero)));

        cut.WaitForAssertion(() => Assert.Contains("paused until Thu 1 Oct 18:00 UTC", cut.Find("[data-testid=plugin-apply-paused]").TextContent));
        Assert.Single(cut.FindAll("[data-testid=plugin-apply-button]"));
    }

    // ---- excluding a plugin: for a notice that does not describe what is actually installed ------------------------------

    [Fact]
    public void DontUpdateThisIsOfferedForAnOutdatedPluginThatIsNotYetExcluded()
    {
        var cut = RenderWithOffer(Offer());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-exclude-button]")));
    }

    [Theory]
    [InlineData("applying")]
    [InlineData("applied")]
    public void DontUpdateThisIsNotOfferedWhileApplyingOrOnceApplied(string state)
    {
        var cut = RenderWithOffer(Offer(state, canApply: false));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=plugin-apply-status]")));
        Assert.Empty(cut.FindAll("[data-testid=plugin-exclude-button]"));
    }

    [Fact]
    public void ClickingDontUpdateThisOpensAFormWithAnOptionalReason()
    {
        var cut = RenderWithOffer(Offer());

        cut.Find("[data-testid=plugin-exclude-button]").Click();

        Assert.Single(cut.FindAll("[data-testid=plugin-exclude-note]"));
        Assert.Single(cut.FindAll("[data-testid=plugin-exclude-confirm]"));
    }

    [Fact]
    public void ExcludingSendsThePluginNameAndTheTypedReasonAndReReadsTheOffers()
    {
        _rustServerClient.Setup(c => c.ExcludeThirdPartyPluginAsync(_serverId, It.IsAny<ExcludeThirdPartyPluginUpdateDto>())).Returns(Task.CompletedTask);
        var cut = RenderWithOffer(Offer());
        var readsBefore = _rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync));
        cut.Find("[data-testid=plugin-exclude-button]").Click();
        cut.Find("[data-testid=plugin-exclude-note]").Input("This is the demo build; I run the paid one from the author.");

        cut.Find("[data-testid=plugin-exclude-confirm]").Click();

        cut.WaitForAssertion(() =>
        {
            _rustServerClient.Verify(c => c.ExcludeThirdPartyPluginAsync(_serverId, It.Is<ExcludeThirdPartyPluginUpdateDto>(
                r => r.PluginName == "Backpacks" && r.Note == "This is the demo build; I run the paid one from the author.")), Times.Once);
            Assert.True(_rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync)) > readsBefore);
        });
    }

    [Fact]
    public void CancellingTheExcludeFormAsksNothingAndClosesIt()
    {
        var cut = RenderWithOffer(Offer());
        cut.Find("[data-testid=plugin-exclude-button]").Click();

        cut.Find("[data-testid=plugin-exclude-cancel]").Click();

        Assert.Empty(cut.FindAll("[data-testid=plugin-exclude-note]"));
        Assert.Single(cut.FindAll("[data-testid=plugin-exclude-button]"));
        _rustServerClient.Verify(c => c.ExcludeThirdPartyPluginAsync(It.IsAny<Guid>(), It.IsAny<ExcludeThirdPartyPluginUpdateDto>()), Times.Never);
    }

    [Fact]
    public void AFailedExcludeRequestSaysSo()
    {
        _rustServerClient.Setup(c => c.ExcludeThirdPartyPluginAsync(_serverId, It.IsAny<ExcludeThirdPartyPluginUpdateDto>())).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderWithOffer(Offer());
        cut.Find("[data-testid=plugin-exclude-button]").Click();

        cut.Find("[data-testid=plugin-exclude-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Could not exclude this plugin", cut.Find("[data-testid=plugin-exclude-error]").TextContent));
    }

    [Fact]
    public void AnExcludedOfferShowsWhyAndOffersToAllowUpdatesAgainInsteadOfApplyOrRecheck()
    {
        var cut = RenderWithOffer(Offer("excluded", canApply: false, detail: "You've excluded this plugin from updates on this server: the demo build."));

        cut.WaitForAssertion(() => Assert.Contains("the demo build", cut.Find("[data-testid=plugin-apply-status]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=plugin-apply-button]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-recheck-button]"));
        Assert.Empty(cut.FindAll("[data-testid=plugin-exclude-button]"));
        Assert.Single(cut.FindAll("[data-testid=plugin-include-button]"));
    }

    [Fact]
    public void AllowUpdatesAgainSendsThePluginNameAndReReadsTheOffers()
    {
        _rustServerClient.Setup(c => c.IncludeThirdPartyPluginAsync(_serverId, It.IsAny<RecheckThirdPartyPluginFileDto>())).Returns(Task.CompletedTask);
        var cut = RenderWithOffer(Offer("excluded", canApply: false));
        var readsBefore = _rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync));

        cut.Find("[data-testid=plugin-include-button]").Click();

        cut.WaitForAssertion(() =>
        {
            _rustServerClient.Verify(c => c.IncludeThirdPartyPluginAsync(_serverId, It.Is<RecheckThirdPartyPluginFileDto>(r => r.PluginName == "Backpacks")), Times.Once);
            Assert.True(_rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync)) > readsBefore);
        });
    }

    [Fact]
    public void AFailedIncludeRequestSaysSo()
    {
        _rustServerClient.Setup(c => c.IncludeThirdPartyPluginAsync(_serverId, It.IsAny<RecheckThirdPartyPluginFileDto>())).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderWithOffer(Offer("excluded", canApply: false));

        cut.Find("[data-testid=plugin-include-button]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Could not allow updates again", cut.Find("[data-testid=plugin-exclude-error]").TextContent));
    }

    // ---- a zip: instructions, then apply ---------------------------------------------------------------------------------------

    private static ThirdPartyPluginUpdateOfferDto ZipOffer(string state = "needs-instructions", bool trusted = false, bool saved = false) => new()
    {
        PluginName = "Backpacks", State = state, FileSha256 = OfferSha, CanApply = true, Kind = "zip", MappingTrusted = trusted,
        Files =
        [
            new("en/plugins/backpacks.cs", 100), new("en/configs/backpacks.json", 20), new("ru/plugins/backpacks.cs", 110), new("ru/configs/backpacks.json", 21)
        ],
        SavedRules = saved
            ?
            [
                new() { IsFolder = true, Source = "en/plugins/", Role = "plugins" }, new() { IsFolder = true, Source = "en/configs/", Role = "config" },
                new() { IsFolder = true, Source = "ru/", Role = "skip" }
            ]
            : []
    };

    [Fact]
    public void AZipThatNeedsInstructionsOffersToTakeThemAndNotAnUpdateNowButton()
    {
        var cut = RenderWithOffer(ZipOffer());

        cut.WaitForAssertion(() => Assert.Equal("Give instructions", cut.Find("[data-testid=zip-instructions]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid=plugin-apply-button]"));
        Assert.Empty(cut.FindAll("[data-testid=zip-editor]"));           // asked for, not thrust at the person
    }

    [Fact]
    public void GivingInstructionsOpensTheEditorForThatArchive()
    {
        var cut = RenderWithOffer(ZipOffer());

        cut.WaitForElement("[data-testid=zip-instructions]").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=zip-editor]")));
        Assert.Contains(cut.FindAll("[data-testid=zip-row]"), r => r.GetAttribute("data-path") == "en/plugins/");
    }

    [Fact]
    public void TheEditorSendsTheRulesAndTheChoiceToSaveThemWithTheHashThePersonWasShown()
    {
        _rustServerClient.Setup(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.IsAny<ApplyThirdPartyPluginUpdateDto>()))
            .ReturnsAsync(new PluginUpdateResultDto { Started = true, Code = "started" });
        var cut = RenderWithOffer(ZipOffer());
        cut.WaitForElement("[data-testid=zip-instructions]").Click();
        cut.WaitForElement("[data-testid=zip-editor]");
        void Set(string path, string role) => cut.Find($"[data-testid=zip-row][data-path='{path}'] [data-testid=zip-role]").Change(role);
        Set("en/plugins/", "plugins"); Set("en/configs/", "config"); Set("ru/", "skip");
        cut.Find("[data-testid=zip-save]").Change(true);

        cut.Find("[data-testid=zip-apply]").Click();

        cut.WaitForAssertion(() => _rustServerClient.Verify(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.Is<ApplyThirdPartyPluginUpdateDto>(r =>
            r.PluginName == "Backpacks" && r.FileSha256 == OfferSha && r.SaveMapping
            && r.Rules != null && r.Rules.Count == 3 && r.Rules.Any(x => x.Source == "en/plugins/" && x.Role == "plugins") && r.Rules.Any(x => x.Source == "ru/" && x.Role == "skip"))), Times.Once));
    }

    [Fact]
    public void TheEditorClosesOnceTheUpdateHasStarted()
    {
        _rustServerClient.Setup(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.IsAny<ApplyThirdPartyPluginUpdateDto>()))
            .ReturnsAsync(new PluginUpdateResultDto { Started = true, Code = "started" });
        var cut = RenderWithOffer(ZipOffer(saved: true));
        cut.WaitForElement("[data-testid=zip-instructions]").Click();
        cut.WaitForElement("[data-testid=zip-editor]");

        cut.Find("[data-testid=zip-apply]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=zip-editor]")));
    }

    [Fact]
    public void ARefusedZipKeepsTheEditorOpenAndSaysWhy()
    {
        _rustServerClient.Setup(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.IsAny<ApplyThirdPartyPluginUpdateDto>()))
            .ReturnsAsync(new PluginUpdateResultDto { Started = false, Code = "mapping_invalid", Message = "en/plugins/backpacks.cs: this file is a program." });
        var cut = RenderWithOffer(ZipOffer(saved: true));
        cut.WaitForElement("[data-testid=zip-instructions]").Click();
        cut.WaitForElement("[data-testid=zip-editor]");

        cut.Find("[data-testid=zip-apply]").Click();

        cut.WaitForAssertion(() => Assert.Contains("this file is a program", cut.Find("[data-testid=plugin-apply-error]").TextContent));
        Assert.Single(cut.FindAll("[data-testid=zip-editor]"));
    }

    [Fact]
    public void SavedInstructionsThatCanBeUsedOfferUpdateNowAndAWayToEditThem()
    {
        var cut = RenderWithOffer(ZipOffer("ready", trusted: true, saved: true));

        cut.WaitForAssertion(() => Assert.Equal("Update now", cut.Find("[data-testid=plugin-apply-button]").TextContent.Trim()));
        Assert.Equal("Edit instructions", cut.Find("[data-testid=zip-instructions]").TextContent.Trim());
        Assert.Contains("Using the instructions saved", cut.Find("[data-testid=zip-trusted]").TextContent);
    }

    [Fact]
    public void UpdateNowOnAZipSendsNoRulesSoTheSavedOnesAreUsed()
    {
        _rustServerClient.Setup(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.IsAny<ApplyThirdPartyPluginUpdateDto>()))
            .ReturnsAsync(new PluginUpdateResultDto { Started = true, Code = "started" });
        var cut = RenderWithOffer(ZipOffer("ready", trusted: true, saved: true));

        cut.WaitForElement("[data-testid=plugin-apply-button]").Click();

        cut.WaitForAssertion(() => _rustServerClient.Verify(c => c.ApplyThirdPartyUpdateAsync(_serverId, It.Is<ApplyThirdPartyPluginUpdateDto>(r => r.Rules == null && !r.SaveMapping)), Times.Once));
    }

    [Fact]
    public void AZipThePluginOnTheServerCannotUnpackOffersNoInstructions()
    {
        var offer = ZipOffer();
        offer.CanApply = false;

        var cut = RenderWithOffer(offer);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-apply]")));
        Assert.Empty(cut.FindAll("[data-testid=zip-instructions]"));
    }

    [Fact]
    public void ASingleFilesOfferShowsNoInstructionsControl()
    {
        var cut = RenderWithOffer(Offer());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=plugin-apply]")));
        Assert.Empty(cut.FindAll("[data-testid=zip-instructions]"));
    }

    [Fact]
    public void LeavingThePageWhileAnUpdateIsUnderWayStopsTheLookingAgain()
    {
        var cut = RenderWithOffer(Offer("applying", canApply: false));
        var reads = _rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync));

        cut.Dispose();

        Assert.Equal(reads, _rustServerClient.Invocations.Count(i => i.Method.Name == nameof(IRustServerApiClient.GetThirdPartyUpdatesAsync)));
    }
}
