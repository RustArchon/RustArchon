// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="DunningService"/>'s three stages - see its own remarks for why this is a
/// separate sweep from <see cref="SubscriptionScheduleService"/>. Runs a real
/// <see cref="OrganizationLifecycleService"/> (not a mock) against a real (InMemory) <see cref="ApiDbContext"/>,
/// so a test asserting "past due" or "suspended" is asserting the actual <see cref="Subscription.Status"/>
/// that landed in the database, not just that some method was called.
/// </summary>
public class DunningServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    /// <summary>
    /// Wires a real <see cref="DunningService"/> and a real <see cref="OrganizationLifecycleService"/>
    /// sharing one InMemory database (by name) across scopes - the same database every
    /// <see cref="CreateContext"/> call in this test also opens, so seeding and asserting through a
    /// separately-opened context sees exactly what the service did.
    /// </summary>
    private DunningService CreateService(
        TimeProvider clock, Mock<ICommunicationPublisher> communicationPublisher,
        out ServiceProvider provider)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new ApiDbContext(
            new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options));
        services.AddSingleton(communicationPublisher.Object);
        services.AddSingleton(Mock.Of<IPublishEndpoint>());
        services.AddSingleton(clock);
        services.AddSingleton<ILogger<OrganizationLifecycleService>>(NullLogger<OrganizationLifecycleService>.Instance);
        services.AddScoped<IOrganizationLifecycleService, OrganizationLifecycleService>();

        provider = services.BuildServiceProvider();
        return new DunningService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<DunningService>.Instance);
    }

    private static Mock<ICommunicationPublisher> CreatePublisher()
    {
        var publisher = new Mock<ICommunicationPublisher>();
        publisher
            .Setup(p => p.QueueTemplatedAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        return publisher;
    }

    private async Task<Guid> SeedTenantAsync(
        ApiDbContext context, SubscriptionStatus status = SubscriptionStatus.Active,
        DateTimeOffset? statusChangedOn = null, string? contactEmail = "owner@acme.example")
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant
        {
            Id = tenantId, Name = "Acme", IsActive = true, ContactEmail = contactEmail
        });
        context.Set<Subscription>().Add(new Subscription
        {
            TenantId = tenantId,
            PlanId = Guid.NewGuid(),
            StartDate = DateTimeOffset.UtcNow.AddYears(-1),
            Status = status,
            StatusChangedOn = statusChangedOn
        });
        await context.SaveChangesAsync();
        return tenantId;
    }

    private static Invoice OpenInvoice(Guid tenantId, DateTimeOffset dueOn, decimal total = 15m) => new()
    {
        TenantId = tenantId,
        Number = "INV-0001",
        Status = InvoiceStatus.Open,
        IssuedOn = dueOn.AddDays(-14),
        DueOn = dueOn,
        Subtotal = total,
        Total = total
    };

    [Fact]
    public async Task AnInvoiceDueInsideTheReminderWindowGetsTheDueSoonEmailExactlyOnce()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var publisher = CreatePublisher();

        await using (var seed = CreateContext())
        {
            var tenantId = await SeedTenantAsync(seed);
            // 10 days out - inside the default 14-day reminder window, not yet due.
            seed.Set<Invoice>().Add(OpenInvoice(tenantId, now.AddDays(10)));
            await seed.SaveChangesAsync();
        }

        var service = CreateService(clock, publisher, out var provider);
        await using (provider)
        {
            await service.RunPassAsync(CancellationToken.None);
            // A second pass, same instant - the reminder must not go out twice.
            await service.RunPassAsync(CancellationToken.None);
        }

        publisher.Verify(p => p.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.PaymentDueSoon, It.IsAny<IReadOnlyDictionary<string, string>>(),
            "owner@acme.example", null, It.IsAny<Guid?>(), null, It.IsAny<CancellationToken>()),
            Times.Once);

        await using var check = CreateContext();
        var invoice = await check.Set<Invoice>().SingleAsync();
        Assert.NotNull(invoice.DueSoonReminderSentOn);
    }

    [Fact]
    public async Task AnInvoiceDueTooFarOutGetsNoReminderYet()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var publisher = CreatePublisher();

        await using (var seed = CreateContext())
        {
            var tenantId = await SeedTenantAsync(seed);
            // 30 days out - outside the default 14-day window.
            seed.Set<Invoice>().Add(OpenInvoice(tenantId, now.AddDays(30)));
            await seed.SaveChangesAsync();
        }

        var service = CreateService(clock, publisher, out var provider);
        await using (provider)
        {
            await service.RunPassAsync(CancellationToken.None);
        }

        publisher.Verify(p => p.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.PaymentDueSoon, It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnActiveTenantWithAnOverdueInvoiceIsMarkedPastDueAndNotified()
    {
        var now = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var publisher = CreatePublisher();

        Guid tenantId;
        await using (var seed = CreateContext())
        {
            tenantId = await SeedTenantAsync(seed);
            seed.Set<Invoice>().Add(OpenInvoice(tenantId, now.AddDays(-1)));
            await seed.SaveChangesAsync();
        }

        var service = CreateService(clock, publisher, out var provider);
        await using (provider)
        {
            await service.RunPassAsync(CancellationToken.None);
        }

        await using var check = CreateContext();
        var subscription = await check.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);

        publisher.Verify(p => p.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.SubscriptionPastDue, It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string>(), It.IsAny<Guid?>(), tenantId, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task APastDueTenantStillUnpaidPastTheGracePeriodIsSuspended()
    {
        var now = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var publisher = CreatePublisher();

        Guid tenantId;
        await using (var seed = CreateContext())
        {
            // Marked past due 8 days ago - past the default 7-day grace period.
            tenantId = await SeedTenantAsync(seed, SubscriptionStatus.PastDue, now.AddDays(-8));
            seed.Set<Invoice>().Add(OpenInvoice(tenantId, now.AddDays(-8)));
            await seed.SaveChangesAsync();
        }

        var service = CreateService(clock, publisher, out var provider);
        await using (provider)
        {
            await service.RunPassAsync(CancellationToken.None);
        }

        await using var check = CreateContext();
        var subscription = await check.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(SubscriptionStatus.Suspended, subscription.Status);
    }

    [Fact]
    public async Task APastDueTenantStillInsideTheGracePeriodIsNotYetSuspended()
    {
        var now = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var publisher = CreatePublisher();

        Guid tenantId;
        await using (var seed = CreateContext())
        {
            // Marked past due 2 days ago - well inside the default 7-day grace period.
            tenantId = await SeedTenantAsync(seed, SubscriptionStatus.PastDue, now.AddDays(-2));
            seed.Set<Invoice>().Add(OpenInvoice(tenantId, now.AddDays(-2)));
            await seed.SaveChangesAsync();
        }

        var service = CreateService(clock, publisher, out var provider);
        await using (provider)
        {
            await service.RunPassAsync(CancellationToken.None);
        }

        await using var check = CreateContext();
        var subscription = await check.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
    }

    [Fact]
    public async Task PlatformSettingsOverrideTheDefaultDayCounts()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var publisher = CreatePublisher();

        await using (var seed = CreateContext())
        {
            var tenantId = await SeedTenantAsync(seed);
            // 25 days out - outside the default 14-day window, but inside a configured 30-day one.
            seed.Set<Invoice>().Add(OpenInvoice(tenantId, now.AddDays(25)));
            seed.Set<PlatformSetting>().Add(new PlatformSetting
            {
                Key = PlatformSettingsRegistry.PaymentDueSoonReminderDays,
                Category = PlatformSettingsRegistry.Categories.Billing,
                DisplayName = "Payment due soon reminder (days before due)",
                ValueType = RustArchon.Api.Data.PlatformSettingValueType.Integer,
                Value = "30",
                CreatedById = Guid.Empty,
                CreatedOn = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var service = CreateService(clock, publisher, out var provider);
        await using (provider)
        {
            await service.RunPassAsync(CancellationToken.None);
        }

        publisher.Verify(p => p.QueueTemplatedAsync(
            EmailTemplateRegistry.Codes.PaymentDueSoon, It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
