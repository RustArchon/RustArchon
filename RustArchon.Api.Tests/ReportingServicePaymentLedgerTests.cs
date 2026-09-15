// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Reporting;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="ReportingService.GetPaymentLedgerAsync"/> - the processor reconciliation and
/// failed-charge report, one row per <see cref="Payment"/> regardless of status.
/// </summary>
public class ReportingServicePaymentLedgerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task<Guid> SeedTenantAsync(ApiDbContext context)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();
        return tenantId;
    }

    [Fact]
    public async Task IncludesBothSucceededAndFailedPaymentsByDefault()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);

        context.Set<Payment>().Add(new Payment
        {
            TenantId = tenantId, Amount = 15m, Currency = "USD", Status = PaymentStatus.Succeeded,
            Method = PaymentMethod.Card, ReceivedOn = now.AddDays(-1), ProviderPaymentId = "pi_ok"
        });
        context.Set<Payment>().Add(new Payment
        {
            TenantId = tenantId, Amount = 15m, Currency = "USD", Status = PaymentStatus.Failed,
            Method = PaymentMethod.Card, ReceivedOn = now.AddDays(-1), ProviderPaymentId = "pi_bad",
            ProviderEventId = "evt_bad", FailureCode = "card_declined", FailureMessage = "Declined."
        });
        await context.SaveChangesAsync();

        var service = new ReportingService(context, new FixedClock(now));
        var result = await service.GetPaymentLedgerAsync(
            DateOnly.FromDateTime(now.AddDays(-7).UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime));

        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, r => r.Status == PaymentStatus.Succeeded && r.ProviderPaymentId == "pi_ok");
        var failedRow = Assert.Single(result.Rows, r => r.Status == PaymentStatus.Failed);
        Assert.Equal("card_declined", failedRow.FailureCode);
        Assert.Equal("Declined.", failedRow.FailureMessage);
    }

    [Fact]
    public async Task StatusFilterNarrowsToJustThatStatus()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);

        context.Set<Payment>().Add(new Payment
        {
            TenantId = tenantId, Amount = 15m, Currency = "USD", Status = PaymentStatus.Succeeded,
            Method = PaymentMethod.Card, ReceivedOn = now
        });
        context.Set<Payment>().Add(new Payment
        {
            TenantId = tenantId, Amount = 15m, Currency = "USD", Status = PaymentStatus.Failed,
            Method = PaymentMethod.Card, ReceivedOn = now, ProviderEventId = "evt_1"
        });
        await context.SaveChangesAsync();

        var service = new ReportingService(context, new FixedClock(now));
        var result = await service.GetPaymentLedgerAsync(
            DateOnly.FromDateTime(now.UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime), PaymentStatus.Failed);

        var row = Assert.Single(result.Rows);
        Assert.Equal(PaymentStatus.Failed, row.Status);
    }

    [Fact]
    public async Task ASucceededPaymentsAllocationsBecomeItsInvoiceNumbers()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);

        var invoice = new Invoice
        {
            TenantId = tenantId, Number = "INV-0001", Status = InvoiceStatus.Paid,
            IssuedOn = now, Subtotal = 15m, Total = 15m, AmountPaid = 15m
        };
        context.Set<Invoice>().Add(invoice);
        await context.SaveChangesAsync();

        var payment = new Payment
        {
            TenantId = tenantId, Amount = 15m, Currency = "USD", Status = PaymentStatus.Succeeded,
            Method = PaymentMethod.Card, ReceivedOn = now
        };
        context.Set<Payment>().Add(payment);
        await context.SaveChangesAsync();

        context.Set<PaymentAllocation>().Add(new PaymentAllocation
        {
            PaymentId = payment.Id, InvoiceId = invoice.Id, Amount = 15m, AllocatedOn = now
        });
        await context.SaveChangesAsync();

        var service = new ReportingService(context, new FixedClock(now));
        var result = await service.GetPaymentLedgerAsync(
            DateOnly.FromDateTime(now.UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime));

        var row = Assert.Single(result.Rows);
        Assert.Equal("INV-0001", row.InvoiceNumbers);
    }

    [Fact]
    public async Task PaymentsOutsideTheWindowAreExcluded()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        var tenantId = await SeedTenantAsync(context);

        context.Set<Payment>().Add(new Payment
        {
            TenantId = tenantId, Amount = 15m, Currency = "USD", Status = PaymentStatus.Succeeded,
            Method = PaymentMethod.Card, ReceivedOn = now.AddDays(-60)
        });
        await context.SaveChangesAsync();

        var service = new ReportingService(context, new FixedClock(now));
        var result = await service.GetPaymentLedgerAsync(
            DateOnly.FromDateTime(now.AddDays(-7).UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime));

        Assert.Empty(result.Rows);
    }
}
