// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The settings read behind a server's Plugins tab, including whether the automatic updater would act on the server right now and, if not, the first
/// reason why (change 002): the answer comes from the same checks a start uses, so the page and the updater cannot disagree.
/// </summary>
public class ServerThirdPartyUpdateSettingsControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ServerId = Guid.NewGuid();

    private readonly Mock<IRustServerRepository> _servers = new();
    private readonly Mock<IThirdPartyPluginUpdateGate> _gate = new();
    private readonly Mock<IThirdPartyPluginUpdateService> _updates = new();
    private readonly Mock<IPlatformSettingsCache> _settings = new();
    private readonly ThirdPartyAutoUpdateStatus _status = new();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public ServerThirdPartyUpdateSettingsControllerTests()
    {
        _servers.Setup(s => s.GetByIdAsync(ServerId, null)).ReturnsAsync(new RustServer { Id = ServerId, TenantId = Guid.NewGuid(), IsEnabled = true });
        _gate.Setup(g => g.EvaluateAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new ThirdPartyUpdateGateResult(ThirdPartyUpdateGateState.Open, true, true, 7, Now.AddDays(10), null));
        _settings.Setup(s => s.GetBooleanAsync(PlatformSettingsRegistry.ThirdPartyPluginAutoUpdatesEnabled, true)).ReturnsAsync(true);
        _updates.Setup(u => u.GetServerRefusalAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>(), true)).ReturnsAsync((PluginUpdateRefusal?)null);
    }

    private async Task<ThirdPartyPluginUpdateSettingsDto> GetAsync()
    {
        var controller = new ServerThirdPartyUpdateSettingsController(_servers.Object, _gate.Object, _updates.Object, _settings.Object, _status, new FixedClock(Now));
        var result = await controller.Get(ServerId);
        return Assert.IsType<ThirdPartyPluginUpdateSettingsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task AServerThatNothingIsStoppingIsEligible()
    {
        var dto = await GetAsync();

        Assert.Equal("eligible", dto.AutoState);
        Assert.Equal(string.Empty, dto.AutoStateMessage);
        Assert.True(dto.PlatformSwitchOn);
    }

    [Fact]
    public async Task ThePlatformSwitchBeingOffComesFirst()
    {
        _settings.Setup(s => s.GetBooleanAsync(PlatformSettingsRegistry.ThirdPartyPluginAutoUpdatesEnabled, true)).ReturnsAsync(false);
        _updates.Setup(u => u.GetServerRefusalAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>(), true)).ReturnsAsync(new PluginUpdateRefusal("not_opted_in", "x"));

        var dto = await GetAsync();

        Assert.False(dto.PlatformSwitchOn);
        Assert.Equal("platform_switch_off", dto.AutoState);
        Assert.Contains("switched off", dto.AutoStateMessage);
    }

    [Fact]
    public async Task TheFirstRefusalAnAutomaticStartWouldGetIsTheState()
    {
        _updates.Setup(u => u.GetServerRefusalAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>(), true))
            .ReturnsAsync(new PluginUpdateRefusal("not_signed_by_this_panel", "Not signed by this Panel."));

        var dto = await GetAsync();

        Assert.Equal("not_signed_by_this_panel", dto.AutoState);
        Assert.Equal("Not signed by this Panel.", dto.AutoStateMessage);
    }

    [Fact]
    public async Task WhenThePassLastRanAndNextRunsIsShown()
    {
        _status.PassStarted(Now.AddMinutes(-3));
        _status.NextDue(Now.AddMinutes(2));

        var dto = await GetAsync();

        Assert.Equal(Now.AddMinutes(-3), dto.LastPassUtc);
        Assert.Equal(Now.AddMinutes(2), dto.NextPassUtc);
    }

    [Fact]
    public async Task BeforeAnyPassHasRunThereIsNoLastPassTime()
    {
        var dto = await GetAsync();

        Assert.Null(dto.LastPassUtc);
    }
}
