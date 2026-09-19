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
/// Regression coverage for <c>ServerDetail.OnLogEntryReceived</c>'s insertion-ordering fix: a live
/// log entry delivered out of chronological order (an older <c>OccurredAtUtc</c> arriving after a
/// newer one - see that handler's own remarks on why two valid transitions can race) must still land
/// in the right spot in the displayed list, not wherever it happened to arrive.
/// </summary>
/// <remarks>
/// Drives this through a real render rather than calling the private handler directly, so this also
/// exercises the actual <c>IRconHubClient.LogEntryReceived</c> subscription wiring and the Logs tab's
/// markup - not just the list-mutation logic in isolation.
/// </remarks>
public class ServerDetailLogOrderingTests : BunitContext
{
    private readonly Mock<IRustServerApiClient> _rustServerClient = new();
    private readonly Mock<IRconHubClient> _hubClient = new();
    private readonly Guid _serverId = Guid.NewGuid();

    public ServerDetailLogOrderingTests()
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
        // Covers both LoadBanListAsync's global.banlistex and LoadLiveServerInfoAsync's serverinfo -
        // a clean "the command failed" response is enough for both to settle into their own error
        // state without throwing; neither is what this test is about.
        _rustServerClient
            .Setup(c => c.SendCommandAsync(_serverId, It.IsAny<SendCommandRequest>()))
            .ReturnsAsync(new RconCommandResult(false, null, null, null, null));

        _hubClient.Setup(h => h.ConnectAsync(_serverId)).Returns(Task.CompletedTask);

        var tokenStore = new Mock<ITokenStore>();
        tokenStore.Setup(t => t.GetToken()).Returns((string?)null);

        var valkeyCache = new Mock<IValkeyCache>();
        valkeyCache.Setup(c => c.GetStringAsync(It.IsAny<string>())).ReturnsAsync("RustArchon");

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()])
            .Returns((string key) => new LocalizedString(key, key));

        Services.AddSingleton(_rustServerClient.Object);
        Services.AddSingleton(_hubClient.Object);
        Services.AddSingleton(tokenStore.Object);
        Services.AddSingleton(new SiteBrandingService(valkeyCache.Object, new Mock<ISiteBrandingApiClient>().Object));
        Services.AddSingleton(localizer.Object);

        AddAuthorization().SetAuthorized("test-admin");
    }

    [Fact]
    public void LogEntriesArrivingOutOfOrder_AreDisplayedOldestFirst()
    {
        // TabQuery is [SupplyParameterFromQuery] - bUnit can only feed it through the current URI,
        // not through the parameter builder (that throws), so the Logs tab is selected by navigating
        // the fake NavigationManager before rendering, same as a real "?tab=logs" link would.
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo($"/servers/{_serverId}?tab=logs");

        var cut = Render<ServerDetail>(parameters => parameters
            .Add(p => p.Id, _serverId));

        var baseline = DateTimeOffset.UtcNow;

        // Delivered second-arrives-first-chronologically, then earliest, then latest - none of the
        // three arrive in the order their own timestamps would sort into.
        RaiseLogEntry("second", baseline.AddSeconds(10));
        RaiseLogEntry("first", baseline);
        RaiseLogEntry("third", baseline.AddSeconds(20));

        cut.WaitForAssertion(() =>
        {
            var messages = cut.FindAll(".connection-log-detail").Select(e => e.TextContent.Trim()).ToList();
            Assert.Equal(["first", "second", "third"], messages);
        });
    }

    private void RaiseLogEntry(string message, DateTimeOffset occurredAtUtc) =>
        _hubClient.Raise(
            h => h.LogEntryReceived += null,
            _serverId, ConnectionLogLevel.Info, message, (RconConnectionStatus?)null, occurredAtUtc);
}
