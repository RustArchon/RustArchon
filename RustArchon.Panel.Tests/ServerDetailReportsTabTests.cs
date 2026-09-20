// Copyright ©2026 Scott Blomfield

using System;
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
/// The Reports tab on <c>ServerDetail</c>: a tab of its own with a badge for what is still new, live (the server only says "something
/// changed" and the page re-reads through the endpoint that checks the permission), and reachable by address.
/// </summary>
public class ServerDetailReportsTabTests : BunitContext
{
    private readonly Mock<IRustServerApiClient> _client = new();
    private readonly Mock<IRconHubClient> _hub = new();
    private readonly Guid _serverId = Guid.NewGuid();
    private int _newCount = 3;

    public ServerDetailReportsTabTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        _client.Setup(c => c.GetByIdAsync(_serverId)).ReturnsAsync(new RustServerDto
        {
            Id = _serverId, Name = "Test Server", Host = "127.0.0.1", Port = 28015, ConnectionStatus = RconConnectionStatus.Connected
        });
        _client.Setup(c => c.GetEventsAsync(_serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<bool>()))
            .ReturnsAsync(new PagedResult<RconEventDto> { Items = [] });
        _client.Setup(c => c.GetCurrentPlayersAsync(_serverId)).ReturnsAsync([]);
        _client.Setup(c => c.GetKillsAsync(_serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync(new PagedResult<PlayerKillEventDto> { Items = [] });
        _client.Setup(c => c.GetInactivePlayersAsync(_serverId, It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new PagedResult<InactivePlayerDto> { Items = [] });
        _client.Setup(c => c.GetServerInfoHistoryAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>())).ReturnsAsync([]);
        _client.Setup(c => c.GetConnectionLogAsync(_serverId, It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>())).ReturnsAsync([]);
        _client.Setup(c => c.SendCommandAsync(_serverId, It.IsAny<SendCommandRequest>())).ReturnsAsync(new RconCommandResult(false, null, null, null, null));
        _client.Setup(c => c.GetReportCountAsync(_serverId)).ReturnsAsync(() => new ServerReportCountDto { New = _newCount });
        _client.Setup(c => c.GetReportsAsync(_serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ServerReportType?>(), It.IsAny<ServerReportStatus?>(), It.IsAny<string?>()))
            .ReturnsAsync(new ServerReportListDto { Items = [], TotalCount = 0, PageNumber = 1, PageSize = 25 });

        _hub.Setup(h => h.ConnectAsync(_serverId)).Returns(Task.CompletedTask);

        var tokenStore = new Mock<ITokenStore>();
        tokenStore.Setup(t => t.GetToken()).Returns((string?)null);
        var valkeyCache = new Mock<IValkeyCache>();
        valkeyCache.Setup(c => c.GetStringAsync(It.IsAny<string>())).ReturnsAsync("RustArchon");

        Services.AddSingleton(_client.Object);
        Services.AddSingleton(_hub.Object);
        Services.AddSingleton(tokenStore.Object);
        Services.AddSingleton(new SiteBrandingService(valkeyCache.Object, new Mock<ISiteBrandingApiClient>().Object));
        Services.AddSingleton(ReportTestSupport.Localizer());

        AddAuthorization().SetAuthorized("test-admin");
    }

    private IRenderedComponent<ServerDetail> RenderDetail(string? tab = null)
    {
        // TabQuery is [SupplyParameterFromQuery]: the tab is chosen by navigating the fake NavigationManager (see ServerDetailLogOrderingTests).
        var query = tab is null ? string.Empty : $"?tab={tab}";
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/servers/{_serverId}{query}");
        return Render<ServerDetail>(p => p.Add(x => x.Id, _serverId));
    }

    [Fact]
    public void TheTabShowsHowManyReportsAreStillNew()
    {
        var cut = RenderDetail();

        cut.WaitForAssertion(() => Assert.Equal("3", cut.Find("[data-testid=reports-badge]").TextContent.Trim()));
    }

    [Fact]
    public void WithNothingNewThereIsNoBadge()
    {
        _newCount = 0;

        var cut = RenderDetail();

        cut.WaitForElement("[data-testid=tab-reports]");
        Assert.Empty(cut.FindAll("[data-testid=reports-badge]"));
    }

    [Fact]
    public void ACallerWhoMayNotReadReportsGetsNoBadgeAndNoError()
    {
        _client.Setup(c => c.GetReportCountAsync(_serverId)).ThrowsAsync(ReportTestSupport.Forbidden);

        var cut = RenderDetail();

        cut.WaitForElement("[data-testid=tab-reports]");
        Assert.Empty(cut.FindAll("[data-testid=reports-badge]"));
        Assert.Empty(cut.FindAll(".alert-danger"));
    }

    [Fact]
    public void TheReportsAddressOpensTheReportsTab()
    {
        var cut = RenderDetail("reports");

        cut.WaitForElement("[data-testid=reports-pane]");
        Assert.Contains("active", cut.Find("[data-testid=tab-reports]").ClassList);
    }

    [Fact]
    public void OtherTabsDoNotMountTheReportsPaneSoNothingIsFetchedForIt()
    {
        var cut = RenderDetail();

        cut.WaitForElement("[data-testid=tab-reports]");
        Assert.Empty(cut.FindAll("[data-testid=reports-pane]"));
        _client.Verify(c => c.GetReportsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ServerReportType?>(), It.IsAny<ServerReportStatus?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void ClickingTheTabShowsItAndPutsItInTheAddress()
    {
        var cut = RenderDetail();
        cut.WaitForElement("[data-testid=tab-reports]").Click();

        cut.WaitForElement("[data-testid=reports-pane]");
        Assert.EndsWith($"/servers/{_serverId}?tab=reports", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void ALiveReportUpdatesTheBadgeAndReloadsTheOpenTab()
    {
        var cut = RenderDetail("reports");
        cut.WaitForElement("[data-testid=reports-pane]");
        _newCount = 4;

        _hub.Raise(h => h.ServerReportsChanged += null);

        cut.WaitForAssertion(() => Assert.Equal("4", cut.Find("[data-testid=reports-badge]").TextContent.Trim()));
        cut.WaitForAssertion(() => _client.Verify(
            c => c.GetReportsAsync(_serverId, 1, 25, null, null, null), Times.AtLeast(2)));
    }

    [Fact]
    public void ALiveReportWhileOnAnotherTabStillUpdatesTheBadge()
    {
        var cut = RenderDetail();
        cut.WaitForElement("[data-testid=reports-badge]");
        _newCount = 9;

        _hub.Raise(h => h.ServerReportsChanged += null);

        cut.WaitForAssertion(() => Assert.Equal("9", cut.Find("[data-testid=reports-badge]").TextContent.Trim()));
        _client.Verify(c => c.GetReportsAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ServerReportType?>(), It.IsAny<ServerReportStatus?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task LeavingThePageStopsListeningForReports()
    {
        var cut = RenderDetail();
        cut.WaitForElement("[data-testid=tab-reports]");

        await DisposeComponentsAsync();

        _hub.VerifyRemove(h => h.ServerReportsChanged -= It.IsAny<Action>(), Times.Once);
    }
}
