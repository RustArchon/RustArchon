// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="PaymentService.RecordPaymentAsync"/>'s reactivation wiring - the counterpart to
/// <see cref="DunningService"/>'s escalation: a tenant PastDue or Suspended for non-payment moves back
/// to <see cref="SubscriptionStatus.Active"/> the moment their overdue invoices are actually cleared,
/// rather than waiting out that sweep's next hourly pass. Runs a real
/// <see cref="OrganizationLifecycleService"/> against a real (Testcontainers-hosted) Postgres, not EF
/// Core's InMemory provider - <see cref="PaymentService.RecordPaymentAsync"/> opens a real database
/// transaction, which InMemory doesn't support (see <see cref="PostgresFixture"/>'s own remarks for the
/// same reasoning applied to <c>ExecuteUpdateAsync</c>).
/// </summary>
public class PaymentServiceReactivationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private static PaymentService CreatePaymentService(ApiDbContext context, TimeProvider clock) => new(
        context,
        Mock.Of<ICommunicationPublisher>(),
        new OrganizationLifecycleService(
            context, Mock.Of<IPublishEndpoint>(), Mock.Of<ICommunicationPublisher>(), clock,
            NullLogger<OrganizationLifecycleService>.Instance),
        // Never actually called in these tests - every payment here is Manual, and
        // PaymentService.ReversePaymentAsync only ever reaches Stripe for a Card payment with a
        // ProviderPaymentId. See PaymentServiceRefundTests for the Stripe-calling path itself.
        Mock.Of<IStripeRefundService>(),
        Mock.Of<IStripeTaxService>(),
        clock,
        NullLogger<PaymentService>.Instance);

    private async Task<(Guid TenantId, Guid InvoiceId)> SeedAsync(
        ApiDbContext context, DateTimeOffset now, SubscriptionStatus status, decimal invoiceTotal = 15m)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });

        // A real Plan row - Subscription.PlanId is a real foreign key, unlike EF Core's InMemory
        // provider (which never enforces it), this Postgres-backed fixture does.
        var plan = new Plan { Name = $"Test plan {Guid.NewGuid()}", Active = true };
        context.Set<Plan>().Add(plan);
        await context.SaveChangesAsync();

        context.Set<Subscription>().Add(new Subscription
        {
            TenantId = tenantId,
            PlanId = plan.Id,
            StartDate = now.AddYears(-1),
            Status = status,
            StatusChangedOn = status == SubscriptionStatus.Active ? null : now.AddDays(-3)
        });

        var invoice = new Invoice
        {
            TenantId = tenantId,
            Number = $"INV-{Guid.NewGuid():N}",
            Status = InvoiceStatus.Open,
            IssuedOn = now.AddDays(-17),
            DueOn = now.AddDays(-3),
            Subtotal = invoiceTotal,
            Total = invoiceTotal
        };
        context.Set<Invoice>().Add(invoice);

        await context.SaveChangesAsync();
        return (tenantId, invoice.Id);
    }

    [Fact]
    public async Task PayingOffTheOnlyOverdueInvoiceReactivatesAPastDueTenant()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);

        Guid tenantId, invoiceId;
        await using (var context = CreateContext())
        {
            (tenantId, invoiceId) = await SeedAsync(context, now, SubscriptionStatus.PastDue);
            await CreatePaymentService(context, clock)
                .RecordPaymentAsync(invoiceId, 15m, PaymentMethod.Manual, now, "test", cancellationToken: CancellationToken.None);
        }

        await using var check = CreateContext();
        var subscription = await check.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public async Task PayingOffTheOnlyOverdueInvoiceReactivatesASuspendedTenant()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);

        Guid tenantId, invoiceId;
        await using (var context = CreateContext())
        {
            (tenantId, invoiceId) = await SeedAsync(context, now, SubscriptionStatus.Suspended);
            await CreatePaymentService(context, clock)
                .RecordPaymentAsync(invoiceId, 15m, PaymentMethod.Manual, now, "test", cancellationToken: CancellationToken.None);
        }

        await using var check = CreateContext();
        var subscription = await check.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public async Task PartiallyPayingOneOfTwoOverdueInvoicesDoesNotReactivate()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);

        Guid tenantId, firstInvoiceId;
        await using (var context = CreateContext())
        {
            (tenantId, firstInvoiceId) = await SeedAsync(context, now, SubscriptionStatus.PastDue);

            // A second overdue invoice the payment below never touches.
            context.Set<Invoice>().Add(new Invoice
            {
                TenantId = tenantId,
                Number = $"INV-{Guid.NewGuid():N}",
                Status = InvoiceStatus.Open,
                IssuedOn = now.AddDays(-45),
                DueOn = now.AddDays(-31),
                Subtotal = 15m,
                Total = 15m
            });
            await context.SaveChangesAsync();

            await CreatePaymentService(context, clock)
                .RecordPaymentAsync(firstInvoiceId, 15m, PaymentMethod.Manual, now, "test", cancellationToken: CancellationToken.None);
        }

        await using var check = CreateContext();
        var subscription = await check.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
    }

    [Fact]
    public async Task PayingAnInvoiceForAnAlreadyActiveTenantDoesNothingUnexpected()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new FixedClock(now);

        Guid tenantId, invoiceId;
        await using (var context = CreateContext())
        {
            (tenantId, invoiceId) = await SeedAsync(context, now, SubscriptionStatus.Active);
            await CreatePaymentService(context, clock)
                .RecordPaymentAsync(invoiceId, 15m, PaymentMethod.Manual, now, "test", cancellationToken: CancellationToken.None);
        }

        await using var check = CreateContext();
        var subscription = await check.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
