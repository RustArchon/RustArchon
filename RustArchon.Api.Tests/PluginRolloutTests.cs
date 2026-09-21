// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The staged roll-out of automatic updates against a real Postgres: a version's start is recorded once, the eligible share grows in a straight
/// line over the configured hours, the same servers come first on every pass, zero hours means everyone at once, and the automatic updater
/// only starts the servers whose turn it is.
/// </summary>
public class PluginRolloutTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IPlatformSettingsCache> _settings = new();

    private ApiDbContext Platform() => new(postgres.Options);

    private void GivenHours(string? hours) =>
        _settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.PluginRolloutHours)).ReturnsAsync(hours);

    private async Task<ApiDbContext> FreshAsync()
    {
        var context = Platform();
        await context.PluginRollouts.ExecuteDeleteAsync();
        return context;
    }

    // ---- the service ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ARolloutStartsTheFirstTimeItIsAskedAboutAndKeepsThatStart()
    {
        await using var context = await FreshAsync();
        GivenHours("10");
        var service = new PluginRolloutService(context, _settings.Object);
        Assert.Null(await service.PeekAsync(PluginReleaseKind.Main, "1.0.0", Start));

        var first = await service.BeginAsync(PluginReleaseKind.Main, "1.0.0", Start);
        var later = await service.BeginAsync(PluginReleaseKind.Main, "1.0.0", Start.AddHours(3));

        Assert.Equal(Start, first.StartedAtUtc);
        Assert.Equal(Start, later.StartedAtUtc);
        Assert.Equal(1, await context.PluginRollouts.CountAsync());
        Assert.Equal(Start, (await service.PeekAsync(PluginReleaseKind.Main, "1.0.0", Start.AddHours(9)))!.StartedAtUtc);
    }

    [Fact]
    public async Task TheSameVersionOfTheOtherFileIsARolloutOfItsOwn()
    {
        await using var context = await FreshAsync();
        GivenHours("10");
        var service = new PluginRolloutService(context, _settings.Object);

        await service.BeginAsync(PluginReleaseKind.Main, "1.0.0", Start);
        var updater = await service.BeginAsync(PluginReleaseKind.Updater, "1.0.0", Start.AddHours(2));

        Assert.Equal(Start.AddHours(2), updater.StartedAtUtc);
        Assert.Equal(2, await context.PluginRollouts.CountAsync());
    }

    [Fact]
    public async Task TwoInstancesBeginningTheSameRolloutTogetherLeaveOneRowAndAgreeOnTheStart()
    {
        await using (await FreshAsync()) { }
        GivenHours("10");
        await using var one = Platform();
        await using var two = Platform();

        var results = await Task.WhenAll(
            new PluginRolloutService(one, _settings.Object).BeginAsync(PluginReleaseKind.Main, "1.0.0", Start),
            new PluginRolloutService(two, _settings.Object).BeginAsync(PluginReleaseKind.Main, "1.0.0", Start.AddSeconds(1)));

        await using var check = Platform();
        Assert.Equal(1, await check.PluginRollouts.CountAsync());
        Assert.Equal(results[0].StartedAtUtc, results[1].StartedAtUtc);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(5, 0.5)]
    [InlineData(10, 1.0)]
    [InlineData(25, 1.0)]
    public async Task TheEligibleShareGrowsInAStraightLineOverTheConfiguredHours(int hoursAfterStart, double expected)
    {
        await using var context = await FreshAsync();
        GivenHours("10");
        var service = new PluginRolloutService(context, _settings.Object);
        await service.BeginAsync(PluginReleaseKind.Main, "1.0.0", Start);

        var status = await service.PeekAsync(PluginReleaseKind.Main, "1.0.0", Start.AddHours(hoursAfterStart));

        Assert.Equal(expected, status!.Fraction, 6);
        Assert.Equal(expected >= 1.0, status.Complete);
        Assert.Equal(Start.AddHours(10), status.CompleteAtUtc);
    }

    [Theory]
    [InlineData("0")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-4")]
    public async Task ZeroOrNothingSensibleMeansNoRampAndEveryoneAtOnce(string? hours)
    {
        await using var context = await FreshAsync();
        GivenHours(hours);
        var service = new PluginRolloutService(context, _settings.Object);

        var status = await service.BeginAsync(PluginReleaseKind.Main, "1.0.0", Start);

        Assert.Equal(1.0, status.Fraction);
        Assert.True(status.Complete);
        Assert.Null(status.CompleteAtUtc);
    }

    // ---- who comes first -----------------------------------------------------------------------------------

    [Fact]
    public void EveryoneIsIncludedAtFullShareAndNobodyAtNone()
    {
        var servers = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();

        Assert.All(servers, s => Assert.True(PluginRolloutService.Includes(s, PluginReleaseKind.Main, "1.0.0", 1.0)));
        Assert.All(servers, s => Assert.False(PluginRolloutService.Includes(s, PluginReleaseKind.Main, "1.0.0", 0.0)));
    }

    [Fact]
    public void ServersAreSpreadEvenlyAcrossTheOrderSoTheShareIsAboutTheFractionAsked()
    {
        var servers = Enumerable.Range(0, 4000).Select(_ => Guid.NewGuid()).ToList();

        var share = servers.Count(s => PluginRolloutService.Includes(s, PluginReleaseKind.Main, "1.0.0", 0.3)) / 4000.0;

        Assert.InRange(share, 0.27, 0.33);
        Assert.All(servers, s => Assert.InRange(PluginRolloutService.Position(s, PluginReleaseKind.Main, "1.0.0"), 0.0, 0.999999999));
    }

    [Fact]
    public void AServersPlaceIsTheSameEveryTimeAndOnlyGrowsIncludedAsTheShareGrows()
    {
        var server = Guid.NewGuid();
        var place = PluginRolloutService.Position(server, PluginReleaseKind.Main, "1.0.0");

        Assert.Equal(place, PluginRolloutService.Position(server, PluginReleaseKind.Main, "1.0.0"));
        Assert.False(PluginRolloutService.Includes(server, PluginReleaseKind.Main, "1.0.0", place));                 // exactly at its place: not yet
        Assert.True(PluginRolloutService.Includes(server, PluginReleaseKind.Main, "1.0.0", Math.Min(1.0, place + 0.0001)));
    }

    [Fact]
    public void EachVersionAndEachFileHasItsOwnOrderSoTheSameServersAreNotAlwaysFirst()
    {
        var servers = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToList();

        var firstForOne = servers.Where(s => PluginRolloutService.Includes(s, PluginReleaseKind.Main, "1.0.0", 0.2)).ToHashSet();
        var firstForNext = servers.Where(s => PluginRolloutService.Includes(s, PluginReleaseKind.Main, "1.0.1", 0.2)).ToHashSet();
        var firstForUpdater = servers.Where(s => PluginRolloutService.Includes(s, PluginReleaseKind.Updater, "1.0.0", 0.2)).ToHashSet();

        Assert.NotEqual(firstForOne, firstForNext);
        Assert.NotEqual(firstForOne, firstForUpdater);
        Assert.True(firstForOne.Intersect(firstForNext).Count() < firstForOne.Count * 0.6);
    }

    // ---- the automatic updater ------------------------------------------------------------------------------

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private const string ActiveKey = "0123456789abcdef";

    private async Task<(PluginAutoUpdater Updater, List<Guid> Servers, List<Guid> Started)> UpdaterWithServersAsync(int serverCount, string hours)
    {
        await using (var clean = Platform())
        {
            await clean.PluginUpdateAttempts.AcrossAllTenants().ExecuteDeleteAsync();
            await clean.PluginRollouts.ExecuteDeleteAsync();
            await clean.Set<ServerPlugin>().AcrossAllTenants().ExecuteDeleteAsync();
            await clean.Set<ServerPluginStatus>().AcrossAllTenants().ExecuteDeleteAsync();
            await clean.Set<PlayerSession>().AcrossAllTenants().ExecuteDeleteAsync();
            await clean.RustServers.AcrossAllTenants().ExecuteDeleteAsync();
        }

        var tenantId = Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Rollout tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();

        var servers = new List<Guid>();
        for (var i = 0; i < serverCount; i++)
        {
            var id = Guid.NewGuid();
            servers.Add(id);
            context.Set<RustServer>().Add(new RustServer
            {
                Id = id, TenantId = tenantId, Name = "Roll " + id.ToString("N")[..6], Host = "192.0.2.71", Port = 28016, RconPassword = "x",
                IsEnabled = true, PluginUpdatesEnabled = true, PluginAutoUpdateEnabled = true
            });
            context.Set<ServerPluginStatus>().Add(new ServerPluginStatus
            {
                TenantId = tenantId, RustServerId = id, PluginVersion = "0.8.0", SigningState = "valid", SigningKeyFingerprint = ActiveKey,
                Capabilities = [RustArchonPlugin.UpdaterUpdateCapability], CapturedAtUtc = Start.AddDays(30)      // fresh whatever hour the test runs the pass at
            });
            // The Updater is already current, so only the plugin is on offer and the test sees one ramp.
            context.Set<ServerPlugin>().Add(new ServerPlugin { TenantId = tenantId, RustServerId = id, Name = RustArchonPlugin.UpdaterName, Version = "v0.3.0", Author = "RustArchon", CapturedAtUtc = Start });
        }

        await context.SaveChangesAsync();

        var started = new List<Guid>();
        var script = new Mock<IPluginScriptService>();
        script.Setup(s => s.GetLatestVersionAsync()).ReturnsAsync("0.9.0");
        script.Setup(s => s.GetLatestUpdaterVersionAsync()).ReturnsAsync("0.3.0");
        script.Setup(s => s.GetKeyStateAsync(ActiveKey)).ReturnsAsync(PluginKeyState.Active);
        var updates = new Mock<IPluginUpdateService>();
        updates.Setup(u => u.StartAsync(It.IsAny<RustServer>(), PluginUpdateTriggers.Auto))
            .ReturnsAsync((RustServer s, string _) => { started.Add(s.Id); return new PluginUpdateResultDto { Started = true, Code = "started" }; });
        _settings.Setup(s => s.GetBooleanAsync(PlatformSettingsRegistry.PluginAutoUpdatesEnabled, true)).ReturnsAsync(true);
        GivenHours(hours);

        var updater = new PluginAutoUpdater(
            context, new ServerPluginStatusRepository(context), new ServerPluginRepository(context), script.Object,
            new PluginUpdateAttemptRepository(context), updates.Object, _settings.Object, new PluginRolloutService(context, _settings.Object),
            NullLogger<PluginAutoUpdater>.Instance);
        return (updater, servers, started);
    }

    [Fact]
    public async Task WithNoRampEveryServerIsStartedInTheFirstPass()
    {
        var (updater, servers, started) = await UpdaterWithServersAsync(4, "0");

        await updater.RunPassAsync(Start);

        Assert.Equal(servers.OrderBy(s => s), started.OrderBy(s => s));
    }

    [Fact]
    public async Task WithARampNobodyIsStartedAtTheStartAndTheServersWhoseTurnItIsAreStartedAsTheHoursPass()
    {
        var (updater, servers, started) = await UpdaterWithServersAsync(4, "10");
        bool Due(Guid s, double fraction) => PluginRolloutService.Includes(s, PluginReleaseKind.Main, "0.9.0", fraction);

        await updater.RunPassAsync(Start);
        Assert.Empty(started);                                             // the first pass only begins the roll-out

        await updater.RunPassAsync(Start.AddHours(5));
        Assert.Equal(servers.Where(s => Due(s, 0.5)).OrderBy(s => s), started.OrderBy(s => s));

        await updater.RunPassAsync(Start.AddHours(10));
        Assert.Equal(servers.OrderBy(s => s), started.Distinct().OrderBy(s => s));    // everyone by the end (the stand-in update service records no attempts, so a server may be offered twice)
    }

    [Fact]
    public async Task AVersionAlreadyThroughItsRampIsNotHeldBackWhenAServerAppearsLater()
    {
        var (updater, servers, started) = await UpdaterWithServersAsync(2, "10");
        await updater.RunPassAsync(Start);                                  // begins the roll-out at Start

        await updater.RunPassAsync(Start.AddDays(3));

        Assert.Equal(servers.OrderBy(s => s), started.Distinct().OrderBy(s => s));
    }
}
