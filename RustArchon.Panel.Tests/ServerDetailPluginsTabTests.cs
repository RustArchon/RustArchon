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
}
