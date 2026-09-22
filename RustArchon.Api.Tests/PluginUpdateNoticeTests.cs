// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// UpdateChecker's notices against a real Postgres: merged by plugin name, the newest version replacing an older one, repeats only
/// refreshing times, nothing ever removed by a report, one organization unable to touch another's, everything logged, and the
/// endpoint showing only notices that still hold with a link that is safe to click.
/// </summary>
public class PluginUpdateNoticeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed record Harness(ApiDbContext Context, PluginUpdateNoticeRepository Repository, Guid TenantId, Guid ServerId);

    private async Task<Harness> CreateAsync()
    {
        var tenantId = Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Notice tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();
        return new Harness(context, new PluginUpdateNoticeRepository(context), tenantId, Guid.NewGuid());
    }

    private static PluginUpdateNoticeInfo Info(
        string name = "Backpacks", string current = "3.1.0", string latest = "3.2.4", string url = "https://umod.org/plugins/backpacks",
        string marketplace = "uMod", int firstMin = 0, int lastMin = 0, int times = 1) =>
        new(name, current, latest, url, marketplace, T0.AddMinutes(firstMin), T0.AddMinutes(lastMin), times);

    private static Task<PluginUpdateNoticeChanges> Merge(Harness h, params PluginUpdateNoticeInfo[] infos) =>
        h.Repository.MergeAsync(h.TenantId, h.ServerId, infos, T0.AddMinutes(1000));

    private async Task<List<PluginUpdateNotice>> RowsAsync(Harness h)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.PluginUpdateNotices.AcrossAllTenants().AsNoTracking().Where(n => n.RustServerId == h.ServerId).OrderBy(n => n.NormalizedName).ToListAsync();
    }

    // ---- merging -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewNoticeIsStoredWithEveryFieldItCarries()
    {
        var h = await CreateAsync();

        var changes = await Merge(h, Info(firstMin: 5, lastMin: 9, times: 4));

        Assert.Single(changes.Added);
        Assert.Empty(changes.Advanced);
        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal(("Backpacks", "backpacks", "3.1.0", "3.2.4"), (row.Name, row.NormalizedName, row.CurrentVersion, row.LatestVersion));
        Assert.Equal(("https://umod.org/plugins/backpacks", "uMod"), (row.Url, row.Marketplace));
        Assert.Equal((T0.AddMinutes(5), T0.AddMinutes(9), 4, T0.AddMinutes(1000)), (row.FirstSeenUtc, row.LastSeenUtc, row.TimesSeen, row.ReportedAtUtc));
        Assert.Equal((h.TenantId, h.ServerId), (row.TenantId, row.RustServerId));
    }

    [Fact]
    public async Task TheSameVersionAgainOnlyRefreshesTimesAndKeepsTheEarliestFirstSeen()
    {
        var h = await CreateAsync();
        await Merge(h, Info(firstMin: 10, lastMin: 20, times: 3));

        // The plugin reloaded meanwhile: its own first-seen is later and its count starts again.
        var changes = await Merge(h, Info(firstMin: 60, lastMin: 90, times: 1));

        Assert.Empty(changes.Added);
        Assert.Empty(changes.Advanced);
        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal((T0.AddMinutes(10), T0.AddMinutes(90), 3), (row.FirstSeenUtc, row.LastSeenUtc, row.TimesSeen));
    }

    [Fact]
    public async Task ANewerVersionReplacesTheNoticeAndIsReportedAsAdvanced()
    {
        var h = await CreateAsync();
        await Merge(h, Info(firstMin: 0, lastMin: 5));

        var changes = await Merge(h, Info(latest: "3.3.0", url: "https://umod.org/plugins/backpacks?v=3.3", firstMin: 30, lastMin: 30));

        Assert.Empty(changes.Added);
        Assert.Equal("3.3.0", Assert.Single(changes.Advanced).LatestVersion);
        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal(("3.3.0", "https://umod.org/plugins/backpacks?v=3.3", T0.AddMinutes(30), 1), (row.LatestVersion, row.Url, row.FirstSeenUtc, row.TimesSeen));
    }

    [Fact]
    public async Task AReportOlderThanWhatIsHeldIsIgnored()
    {
        var h = await CreateAsync();
        await Merge(h, Info(latest: "3.3.0", firstMin: 30, lastMin: 60));

        var changes = await Merge(h, Info(latest: "3.2.4", firstMin: 0, lastMin: 10));

        Assert.Empty(changes.Advanced);
        Assert.Equal("3.3.0", Assert.Single(await RowsAsync(h)).LatestVersion);
    }

    [Fact]
    public async Task PluginNamesAreMatchedWithoutRegardToCaseAndTheLatestCapitalizationIsKept()
    {
        var h = await CreateAsync();
        await Merge(h, Info(name: "backpacks", lastMin: 1));

        await Merge(h, Info(name: "BACKPACKS", lastMin: 2));

        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal("BACKPACKS", row.Name);
    }

    [Fact]
    public async Task ANoticeIsNeverRemovedByAReportThatLeavesItOut()
    {
        var h = await CreateAsync();
        await Merge(h, Info(name: "A"), Info(name: "B"));

        await Merge(h, Info(name: "A", lastMin: 5));      // the plugin reloaded and has only heard about A since

        Assert.Equal(["a", "b"], (await RowsAsync(h)).Select(r => r.NormalizedName).ToArray());
    }

    [Fact]
    public async Task ANameGivenTwiceInOneReportKeepsTheOneHeardLast()
    {
        var h = await CreateAsync();

        await Merge(h, Info(latest: "1.0.0", lastMin: 1), Info(name: "BACKPACKS", latest: "2.0.0", lastMin: 9));

        Assert.Equal("2.0.0", Assert.Single(await RowsAsync(h)).LatestVersion);
    }

    [Fact]
    public async Task ANoticeWithNoNameIsSkipped()
    {
        var h = await CreateAsync();

        await Merge(h, Info(name: "  "), Info(name: "Real"));

        Assert.Equal(["real"], (await RowsAsync(h)).Select(r => r.NormalizedName).ToArray());
    }

    [Fact]
    public async Task TextLongerThanTheColumnsIsCutNotRefused()
    {
        var h = await CreateAsync();

        await Merge(h, Info(name: new string('n', 300), current: new string('c', 300), latest: new string('l', 300), url: "https://" + new string('u', 1000), marketplace: new string('m', 300)));

        var row = Assert.Single(await RowsAsync(h));
        Assert.Equal((100, 50, 50, 500, 50), (row.Name.Length, row.CurrentVersion.Length, row.LatestVersion.Length, row.Url.Length, row.Marketplace.Length));
    }

    [Fact]
    public async Task TwoServersKeepTheirOwnNoticesForTheSamePlugin()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();

        await Merge(a, Info(latest: "2.0.0"));
        await Merge(b, Info(latest: "9.0.0"));

        Assert.Equal("2.0.0", Assert.Single(await RowsAsync(a)).LatestVersion);
        Assert.Equal("9.0.0", Assert.Single(await RowsAsync(b)).LatestVersion);
    }

    [Fact]
    public async Task AReportNamingAnotherOrganizationsServerIsRefusedAndChangesNothing()
    {
        var owner = await CreateAsync();
        var other = await CreateAsync();
        await Merge(owner, Info(latest: "2.0.0"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            other.Repository.MergeAsync(other.TenantId, owner.ServerId, [Info(latest: "6.6.6", lastMin: 50)], T0));

        Assert.Equal("2.0.0", Assert.Single(await RowsAsync(owner)).LatestVersion);
    }

    [Fact]
    public async Task TheRepositoryOnlyShowsARequestersOwnOrganizationsNotices()
    {
        var mine = await CreateAsync();
        var theirs = await CreateAsync();
        await Merge(mine, Info());

        Assert.Single(await mine.Repository.GetForServerAsync(mine.ServerId));
        Assert.Empty(await theirs.Repository.GetForServerAsync(mine.ServerId));
    }

    // ---- the consumer --------------------------------------------------------------------------------------

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // What the watchers of a server hear: the hub, the group it addresses, and what is sent down it.
    private readonly Mock<IClientProxy> _group = new();
    private readonly Mock<IHubClients> _clients = new();

    private PluginUpdatesCapturedConsumer NewConsumer(
        IPluginUpdateNoticeRepository repository, Microsoft.Extensions.Logging.ILogger<PluginUpdatesCapturedConsumer>? logger = null)
    {
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_group.Object);
        var hub = new Mock<IHubContext<RconHub>>();
        hub.Setup(h => h.Clients).Returns(_clients.Object);
        return new PluginUpdatesCapturedConsumer(repository, hub.Object, new FixedClock(T0), logger ?? new CapturingLogger<PluginUpdatesCapturedConsumer>());
    }

    private void VerifyWatchersToldTimes(Guid server, Times times)
    {
        _clients.Verify(c => c.Group(RconHub.GroupName(server)), times);
        _group.Verify(
            g => g.SendCoreAsync("ReceivePluginUpdatesChanged", It.Is<object?[]>(args => args.Length == 0), It.IsAny<CancellationToken>()), times);
    }

    private static Task Consume(PluginUpdatesCapturedConsumer consumer, Guid tenant, Guid server, params PluginUpdateNoticeInfo[] infos)
    {
        var context = new Mock<ConsumeContext<PluginUpdatesCaptured>>();
        context.SetupGet(c => c.Message).Returns(new PluginUpdatesCaptured(server, tenant, infos, T0));
        return consumer.Consume(context.Object);
    }

    [Fact]
    public async Task ANewNoticeIsLoggedWithEverythingItSaidAndARepeatIsNot()
    {
        var h = await CreateAsync();
        var logger = new CapturingLogger<PluginUpdatesCapturedConsumer>();
        var consumer = NewConsumer(h.Repository, logger);

        await Consume(consumer, h.TenantId, h.ServerId, Info());
        await Consume(consumer, h.TenantId, h.ServerId, Info(lastMin: 5, times: 2));

        var line = Assert.Single(logger.Messages);
        Assert.Contains("New plugin update reported by UpdateChecker", line);
        Assert.Contains("Backpacks 3.1.0 -> 3.2.4", line);
        Assert.Contains("(uMod)", line);
        Assert.Contains("https://umod.org/plugins/backpacks", line);
    }

    [Fact]
    public async Task ANewerVersionIsLoggedAgain()
    {
        var h = await CreateAsync();
        var logger = new CapturingLogger<PluginUpdatesCapturedConsumer>();
        var consumer = NewConsumer(h.Repository, logger);

        await Consume(consumer, h.TenantId, h.ServerId, Info());
        await Consume(consumer, h.TenantId, h.ServerId, Info(latest: "4.0.0", lastMin: 60));

        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("Newer plugin update", logger.Messages[1]);
        Assert.Contains("-> 4.0.0", logger.Messages[1]);
    }

    [Fact]
    public async Task AnEmptyReportDoesNothing()
    {
        var h = await CreateAsync();
        var consumer = NewConsumer(h.Repository);

        await Consume(consumer, h.TenantId, h.ServerId);

        Assert.Empty(await RowsAsync(h));
        VerifyWatchersToldTimes(h.ServerId, Times.Never());
    }

    // ---- telling the watchers (the Plugins tab's chip) ------------------------------------------------------

    [Fact]
    public async Task ANewNoticeTellsTheServersWatchersSomethingChangedAndNothingMore()
    {
        var h = await CreateAsync();
        var consumer = NewConsumer(h.Repository);

        await Consume(consumer, h.TenantId, h.ServerId, Info());

        VerifyWatchersToldTimes(h.ServerId, Times.Once());
    }

    [Fact]
    public async Task ANoticeThatMovesToANewerVersionTellsThemAgain()
    {
        var h = await CreateAsync();
        var consumer = NewConsumer(h.Repository);
        await Consume(consumer, h.TenantId, h.ServerId, Info());

        await Consume(consumer, h.TenantId, h.ServerId, Info(latest: "4.0.0", lastMin: 60));

        VerifyWatchersToldTimes(h.ServerId, Times.Exactly(2));
    }

    [Fact]
    public async Task ARepeatOfWhatIsAlreadyHeldSaysNothingSoTheChipIsNotRefreshedForNoReason()
    {
        var h = await CreateAsync();
        var consumer = NewConsumer(h.Repository);
        await Consume(consumer, h.TenantId, h.ServerId, Info());

        await Consume(consumer, h.TenantId, h.ServerId, Info(lastMin: 5, times: 2));

        VerifyWatchersToldTimes(h.ServerId, Times.Once());   // only the first
    }

    [Fact]
    public async Task AFailureToTellWatchersDoesNotLoseTheNotice()
    {
        var h = await CreateAsync();
        var consumer = NewConsumer(h.Repository);
        _group.Setup(g => g.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        await Consume(consumer, h.TenantId, h.ServerId, Info());

        Assert.Single(await RowsAsync(h));
    }

    [Fact]
    public async Task AReportThatLiesAboutWhoOwnsTheServerIsDroppedWithAWarningNotAnException()
    {
        var owner = await CreateAsync();
        var other = await CreateAsync();
        await Merge(owner, Info());
        var logger = new CapturingLogger<PluginUpdatesCapturedConsumer>();
        var consumer = NewConsumer(other.Repository, logger);

        await Consume(consumer, other.TenantId, owner.ServerId, Info(latest: "6.6.6", lastMin: 50));

        Assert.Contains(logger.Messages, m => m.Contains("Dropped"));
        Assert.Equal("3.2.4", Assert.Single(await RowsAsync(owner)).LatestVersion);
    }

    // ---- the endpoint --------------------------------------------------------------------------------------

    private ServerPluginUpdatesController Controller(Harness h) =>
        new(new RustServerRepository(h.Context), h.Repository, new ServerPluginRepository(h.Context), new PluginDownloadLookupRepository(h.Context));

    private async Task AddServerAsync(Harness h, params (string Name, string Version)[] installed)
    {
        h.Context.Set<RustServer>().Add(new RustServer { Id = h.ServerId, TenantId = h.TenantId, Name = "Notices " + h.ServerId.ToString("N")[..6], Host = "192.0.2.61", Port = 28016, RconPassword = "x" });
        foreach (var (name, version) in installed)
        {
            h.Context.Set<ServerPlugin>().Add(new ServerPlugin { TenantId = h.TenantId, RustServerId = h.ServerId, Name = name, Version = version, Author = "someone", CapturedAtUtc = T0 });
        }

        await h.Context.SaveChangesAsync();
    }

    private static List<PluginUpdateNoticeDto> Body(ActionResult<List<PluginUpdateNoticeDto>> result) =>
        Assert.IsType<List<PluginUpdateNoticeDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task AnInstalledPluginWithANewerVersionReportedIsListedWithEverything()
    {
        var h = await CreateAsync();
        await AddServerAsync(h, ("Backpacks", "v3.1.0"));
        await Merge(h, Info(firstMin: 5, lastMin: 9, times: 4));

        var n = Assert.Single(Body(await Controller(h).Get(h.ServerId)));

        Assert.Equal(("Backpacks", "v3.1.0", "3.1.0", "3.2.4"), (n.Name, n.InstalledVersion, n.ReportedVersion, n.LatestVersion));
        Assert.Equal(("https://umod.org/plugins/backpacks", "uMod"), (n.Url, n.Marketplace));
        Assert.Equal((T0.AddMinutes(5), T0.AddMinutes(9), 4), (n.FirstSeenUtc, n.LastSeenUtc, n.TimesSeen));
    }

    [Fact]
    public async Task ANoticeDisappearsOnceThePluginIsUpdatedWithoutWaitingForUpdateChecker()
    {
        var h = await CreateAsync();
        await AddServerAsync(h, ("Backpacks", "3.2.4"));
        await Merge(h, Info());

        Assert.Empty(Body(await Controller(h).Get(h.ServerId)));
    }

    [Fact]
    public async Task ANoticeForAPluginThatIsNoLongerInstalledIsNotListed()
    {
        var h = await CreateAsync();
        await AddServerAsync(h, ("SomethingElse", "1.0.0"));
        await Merge(h, Info());

        Assert.Empty(Body(await Controller(h).Get(h.ServerId)));
    }

    [Fact]
    public async Task ThePluginNameIsMatchedToTheInstalledOneWithoutRegardToCase()
    {
        var h = await CreateAsync();
        await AddServerAsync(h, ("BACKPACKS", "3.1.0"));
        await Merge(h, Info(name: "backpacks"));

        Assert.Equal("BACKPACKS", Assert.Single(Body(await Controller(h).Get(h.ServerId))).Name);
    }

    [Fact]
    public async Task AnUnknownServerIsNotFound()
    {
        var h = await CreateAsync();

        Assert.IsType<NotFoundResult>((await Controller(h).Get(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task AServerWithNoNoticesGivesAnEmptyList()
    {
        var h = await CreateAsync();
        await AddServerAsync(h, ("Backpacks", "3.1.0"));

        Assert.Empty(Body(await Controller(h).Get(h.ServerId)));
    }

    [Fact]
    public async Task ANoticeIsNotShownToAnotherOrganization()
    {
        var mine = await CreateAsync();
        var theirs = await CreateAsync();
        await AddServerAsync(mine, ("Backpacks", "3.1.0"));
        await Merge(mine, Info());

        // Their harness cannot see my server at all.
        Assert.IsType<NotFoundResult>((await Controller(theirs).Get(mine.ServerId)).Result);
    }

    [Fact]
    public async Task AnUnsafeAddressIsNeverPassedOn()
    {
        var h = await CreateAsync();
        await AddServerAsync(h, ("Backpacks", "3.1.0"));
        await Merge(h, Info(url: "javascript:alert(document.cookie)"));

        var n = Assert.Single(Body(await Controller(h).Get(h.ServerId)));

        Assert.Equal(string.Empty, n.Url);
        Assert.Equal("3.2.4", n.LatestVersion);        // the notice itself still shows
    }

    // ---- what "still outdated" means -----------------------------------------------------------------------

    [Theory]
    [InlineData("3.1.0", "3.2.4", true)]
    [InlineData("v3.1.0", "3.2.4", true)]
    [InlineData("3.1.0", "v3.2.4", true)]
    [InlineData("3.2.4", "3.2.4", false)]
    [InlineData("v3.2.4", "3.2.4", false)]
    [InlineData("3.3.0", "3.2.4", false)]
    [InlineData("3.10.0", "3.9.0", false)]          // compared as numbers
    [InlineData("3.9.0", "3.10.0", true)]
    [InlineData("1.2.3.4", "1.2.3.5", true)]        // a scheme we cannot compare: shown unless identical
    [InlineData("1.2.3.4", "1.2.3.4", false)]
    [InlineData("1.2.3.4", "1.2.3.4 ", false)]
    [InlineData("beta", "release", true)]
    [InlineData("", "1.0.0", true)]
    [InlineData(null, "1.0.0", true)]
    [InlineData("1.0.0", "", false)]                // nothing newer was said
    public void ANoticeHoldsUnlessTheInstalledVersionIsProvablyCurrent(string? installed, string latest, bool expected) =>
        Assert.Equal(expected, ServerPluginUpdatesController.StillOutdated(installed, latest));

    [Theory]
    [InlineData("https://umod.org/plugins/backpacks", "https://umod.org/plugins/backpacks")]
    [InlineData("http://example.com/a?b=c", "http://example.com/a?b=c")]
    [InlineData("  https://codefling.com/plugins/x  ", "https://codefling.com/plugins/x")]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("JaVaScRiPt:alert(1)", "")]
    [InlineData("data:text/html,<script>alert(1)</script>", "")]
    [InlineData("file:///etc/passwd", "")]
    [InlineData("ftp://example.com/x", "")]
    [InlineData("//evil.example.com/x", "")]
    [InlineData("/relative/path", "")]
    [InlineData("https://user:pass@example.com/x", "")]
    [InlineData("https://", "")]
    [InlineData("not a url", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void OnlyAnAbsoluteHttpOrHttpsAddressIsPassedOn(string? url, string expected) =>
        Assert.Equal(expected, ServerPluginUpdatesController.SafeUrl(url));

    [Fact]
    public void AnAddressLongerThanTheLimitIsNotPassedOn() =>
        Assert.Equal(string.Empty, ServerPluginUpdatesController.SafeUrl("https://example.com/" + new string('a', 600)));

    [Fact]
    public void TheEndpointNeedsTheSamePermissionAsReadingTheServer()
    {
        var permission = Assert.Single(typeof(ServerPluginUpdatesController)
            .GetCustomAttributes(typeof(JumpStart.Authorization.RequirePermissionAttribute), inherit: true)
            .Cast<JumpStart.Authorization.RequirePermissionAttribute>());

        Assert.Equal(RustArchon.Api.Infrastructure.PermissionCatalog.ServerGet, permission.Permission);
    }

    [Theory]
    [InlineData("BlueprintShare", "Blueprint Share")]
    [InlineData("MonumentAddons", "Monument Addons")]
    [InlineData("RaidableBases", "Raidable Bases")]
    [InlineData("HarborEvent", "HarborEvent")]
    [InlineData("NpcSpawn", "NPC-Spawn")]
    [InlineData("Better Chat", "BetterChat")]
    public async Task TheClassNameUpdateCheckerUsesMatchesThePluginTitleTheServerListsAndOneRowIsKept(string reported, string listed)
    {
        var h = await CreateAsync();
        await AddServerAsync(h, (listed, "1.0.0"));

        await Merge(h, Info(name: reported, current: "1.0.0", latest: "2.0.0"));
        await Merge(h, Info(name: listed, current: "1.0.0", latest: "2.0.0", lastMin: 3));

        Assert.Single(await RowsAsync(h));
        var n = Assert.Single(Body(await Controller(h).Get(h.ServerId)));
        Assert.Equal(listed, n.Name);           // shown under the name the server's own plugin list uses
    }

    [Fact]
    public void ANameWithNoLettersOrDigitsStillHasAKey()
    {
        Assert.Equal("---", PluginUpdateNoticeRepository.Normalize(" --- "));
        Assert.Equal("abc123", PluginUpdateNoticeRepository.Normalize("A-b c_1.2 3"));
    }
}
