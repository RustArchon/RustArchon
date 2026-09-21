// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>The smaller pieces around F7 reports: the per-server throttle, cleanup on server delete, and who may do what.</summary>
public class ReportSupportTests
{
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---- throttle ----

    private const int PerWindow = PlatformSettingsRegistry.DefaultReportsPerServerPerMinute;

    [Fact]
    public void AServerGetsItsBudgetPerWindowAndThenIsToldToWait()
    {
        var throttle = new ReportIngestThrottle(new MutableClock(DateTimeOffset.UnixEpoch));
        var server = Guid.NewGuid();

        var accepted = Enumerable.Range(0, PerWindow).Count(_ => throttle.TryAcquire(server, PerWindow));

        Assert.Equal(PerWindow, accepted);
        Assert.False(throttle.TryAcquire(server, PerWindow));
    }

    [Fact]
    public void TheLimitIsWhateverTheCallerSaysAndAChangeAppliesAtOnce()
    {
        var throttle = new ReportIngestThrottle(new MutableClock(DateTimeOffset.UnixEpoch));
        var server = Guid.NewGuid();

        Assert.Equal(3, Enumerable.Range(0, 10).Count(_ => throttle.TryAcquire(server, 3)));
        Assert.False(throttle.TryAcquire(server, 3));

        // The administrator raises it to 5 in the middle of the window: the two more reports are let through, the next is not.
        Assert.True(throttle.TryAcquire(server, 5));
        Assert.True(throttle.TryAcquire(server, 5));
        Assert.False(throttle.TryAcquire(server, 5));
    }

    [Fact]
    public void OneServersBudgetIsNotAnothers()
    {
        var throttle = new ReportIngestThrottle(new MutableClock(DateTimeOffset.UnixEpoch));
        var busy = Guid.NewGuid();
        for (var i = 0; i < PerWindow; i++)
        {
            throttle.TryAcquire(busy, PerWindow);
        }

        Assert.False(throttle.TryAcquire(busy, PerWindow));
        Assert.True(throttle.TryAcquire(Guid.NewGuid(), PerWindow));
    }

    [Fact]
    public void TheBudgetComesBackWhenTheWindowPasses()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var throttle = new ReportIngestThrottle(clock);
        var server = Guid.NewGuid();
        for (var i = 0; i < PerWindow; i++)
        {
            throttle.TryAcquire(server, PerWindow);
        }

        clock.Now += ReportIngestThrottle.Window - TimeSpan.FromSeconds(1);
        Assert.False(throttle.TryAcquire(server, PerWindow));

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(throttle.TryAcquire(server, PerWindow));
    }

    // ---- cleanup ----

    private static (ServerReportCleanupConsumer Consumer, Mock<IServerReportRepository> Reports, Mock<IObjectStorage> Storage) NewCleanup(int removed = 3)
    {
        var reports = new Mock<IServerReportRepository>();
        reports.Setup(r => r.DeleteForServerAcrossTenantsAsync(It.IsAny<Guid>())).ReturnsAsync(removed);
        var storage = new Mock<IObjectStorage>();
        return (new ServerReportCleanupConsumer(reports.Object, storage.Object, NullLogger<ServerReportCleanupConsumer>.Instance), reports, storage);
    }

    private static ConsumeContext<ServerLifecycleChanged> Context(ServerLifecycleChanged message)
    {
        var context = new Mock<ConsumeContext<ServerLifecycleChanged>>();
        context.Setup(c => c.Message).Returns(message);
        context.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
        return context.Object;
    }

    [Fact]
    public async Task DeletingAServerRemovesItsReportsAndTheirScreenshots()
    {
        var (consumer, reports, storage) = NewCleanup();
        var server = Guid.NewGuid();

        await consumer.Consume(Context(new ServerLifecycleChanged(server, Guid.NewGuid(), ServerLifecycleChangeType.Deleted)));

        reports.Verify(r => r.DeleteForServerAcrossTenantsAsync(server), Times.Once);
        storage.Verify(s => s.DeleteByPrefixAsync($"reports/{server}/", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Disabling or editing a server is not deleting it: its reports are the moderation history and must survive.</summary>
    [Theory]
    [InlineData(ServerLifecycleChangeType.Updated)]
    [InlineData(ServerLifecycleChangeType.Disabled)]
    public async Task OnlyDeletionRemovesReports(ServerLifecycleChangeType change)
    {
        var (consumer, reports, storage) = NewCleanup();

        await consumer.Consume(Context(new ServerLifecycleChanged(Guid.NewGuid(), Guid.NewGuid(), change)));

        reports.VerifyNoOtherCalls();
        storage.VerifyNoOtherCalls();
    }

    // ---- who may do what ----

    private static string PermissionOn(Type controller)
    {
        var attribute = controller.GetCustomAttribute<RequirePermissionAttribute>();
        Assert.NotNull(attribute);
        return attribute!.Permission;
    }

    [Fact]
    public void TheForwardingAddressNeedsItsOwnPermissionBecauseItHoldsTheSecret()
    {
        Assert.Equal(PermissionCatalog.ServerManageReportForwarding, PermissionOn(typeof(ServerReportForwardingController)));
    }

    [Fact]
    public void TheOwnerHoldsEveryReportPermissionSoSetupIsSelfServeForTheCustomer()
    {
        Assert.Contains(PermissionCatalog.ServerViewReports, PermissionCatalog.OwnerPermissions);
        Assert.Contains(PermissionCatalog.ServerManageReports, PermissionCatalog.OwnerPermissions);
        Assert.Contains(PermissionCatalog.ServerManageReportForwarding, PermissionCatalog.OwnerPermissions);
    }

    /// <summary>
    /// Site Admin is the platform-operator role, so none of these may be platform-scoped: that would lock customers out of their own
    /// setup. (A site admin acting as the tenant passes every tenant permission anyway.)
    /// </summary>
    [Fact]
    public void TheReportPermissionsAreTenantScopedAndDelegable()
    {
        var byName = PermissionCatalog.All.ToDictionary(p => p.Name);

        foreach (var name in new[]
        {
            PermissionCatalog.ServerViewReports, PermissionCatalog.ServerManageReports, PermissionCatalog.ServerManageReportForwarding
        })
        {
            Assert.Equal(PermissionScope.Tenant, byName[name].Scope);
            Assert.True(byName[name].DelegableByTenantAdmin);
        }
    }

    [Fact]
    public void ReadingAServerDoesNotByItselfAllowReadingWhatItsPlayersReported()
    {
        Assert.NotEqual(PermissionCatalog.ServerGet, PermissionCatalog.ServerViewReports);
        Assert.NotEqual(PermissionCatalog.ServerViewReports, PermissionCatalog.ServerManageReportForwarding);
    }

    // ---- the secret stays where it belongs ----

    [Fact]
    public void NoDtoAServerIsReadOrWrittenThroughCarriesTheReportSecretOrItsState()
    {
        foreach (var dto in new[] { typeof(RustServerDto), typeof(CreateRustServerDto), typeof(UpdateRustServerDto) })
        {
            Assert.DoesNotContain(dto.GetProperties(), p => p.Name.Contains("ReportsSecret") || p.Name.Contains("ReportForwarding"));
        }
    }

    /// <summary>
    /// An ordinary edit can be made by a role that may not manage report forwarding. It must not be able to read the secret (above) or
    /// overwrite it - which would let it swap in a value it knows.
    /// </summary>
    [Fact]
    public void AnOrdinaryServerEditCannotOverwriteTheReportSecret()
    {
        var mapper = new AutoMapper.MapperConfiguration(
            c => c.AddProfile<RustArchon.Api.Mapping.RustServerMappingProfile>(), NullLoggerFactory.Instance).CreateMapper();
        var verifiedAt = DateTimeOffset.UnixEpoch.AddDays(1);
        var entity = new RustArchon.Api.Data.RustServer
        {
            Name = "Before", Host = "1.2.3.4", RconPassword = "protected", ReportsSecret = "protected-secret", ReportForwardingVerifiedAtUtc = verifiedAt
        };

        mapper.Map(new UpdateRustServerDto { Name = "After", Host = "5.6.7.8", Port = 28016 }, entity);

        Assert.Equal("After", entity.Name);
        Assert.Equal("protected-secret", entity.ReportsSecret);
        Assert.Equal(verifiedAt, entity.ReportForwardingVerifiedAtUtc);
    }

    [Fact]
    public void ANewServerStartsWithNoSecretSoItAcceptsNoReportsUntilOneIsAskedFor()
    {
        var mapper = new AutoMapper.MapperConfiguration(
            c => c.AddProfile<RustArchon.Api.Mapping.RustServerMappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

        var entity = mapper.Map<RustArchon.Api.Data.RustServer>(new CreateRustServerDto { Name = "New", Host = "1.2.3.4", RconPassword = "pw" });

        Assert.Null(entity.ReportsSecret);
        Assert.Null(entity.ReportForwardingVerifiedAtUtc);
    }

    [Fact]
    public void TheIntegrationVerifyEndpointIsGatedAndRateLimited()
    {
        Assert.Equal(PermissionCatalog.ServerCreate, PermissionOn(typeof(IntegrationVerificationController)));

        var action = typeof(IntegrationVerificationController).GetMethod(nameof(IntegrationVerificationController.Verify))!;
        var limit = action.GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>();
        Assert.Equal(IntegrationVerificationController.RateLimitPolicy, limit?.PolicyName);
    }
}
