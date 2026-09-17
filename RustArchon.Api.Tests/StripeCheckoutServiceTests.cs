// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="StripeCheckoutService.CreateCheckoutSessionAsync"/>'s guard clauses - every
/// case here is refused before the real Stripe API is ever called, so these need no live key and never
/// touch the network. The "a real invoice actually gets a Checkout URL back" path is not covered here -
/// that requires either a live Stripe test-mode key or refactoring for a mockable Stripe client, neither
/// of which this pass adds; see this codebase's own precedent (<c>IPaymentService</c>'s remarks) of
/// shipping the guarded, testable shape first.
/// </summary>
public class StripeCheckoutServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static StripeCheckoutService CreateService(ApiDbContext context) => new(
        context, Mock.Of<IStripeCredentialProvider>(), NullLogger<StripeCheckoutService>.Instance);

    private static Invoice NewInvoice(Guid tenantId, InvoiceStatus status, decimal total, decimal paid = 0m) => new()
    {
        TenantId = tenantId,
        Number = "INV-0001",
        Status = status,
        IssuedOn = DateTimeOffset.UtcNow,
        DueOn = DateTimeOffset.UtcNow.AddDays(14),
        Subtotal = total,
        Total = total,
        AmountPaid = paid
    };

    [Fact]
    public async Task AMissingInvoiceReturnsNull()
    {
        await using var context = CreateContext();
        var result = await CreateService(context)
            .CreateCheckoutSessionAsync(Guid.NewGuid(), Guid.NewGuid(), "https://x/success", "https://x/cancel");

        Assert.Null(result);
    }

    [Fact]
    public async Task AnInvoiceBelongingToAnotherTenantReturnsNull()
    {
        await using var context = CreateContext();
        var invoice = NewInvoice(Guid.NewGuid(), InvoiceStatus.Open, 15m);
        context.Set<Invoice>().Add(invoice);
        await context.SaveChangesAsync();

        // A different tenant than the one on the invoice - this is the whole reason tenantId is its own
        // parameter rather than trusted implicitly.
        var result = await CreateService(context)
            .CreateCheckoutSessionAsync(Guid.NewGuid(), invoice.Id, "https://x/success", "https://x/cancel");

        Assert.Null(result);
    }

    [Fact]
    public async Task ANonOpenInvoiceReturnsNull()
    {
        await using var context = CreateContext();
        var invoice = NewInvoice(Guid.NewGuid(), InvoiceStatus.Paid, 15m, paid: 15m);
        context.Set<Invoice>().Add(invoice);
        await context.SaveChangesAsync();

        var result = await CreateService(context)
            .CreateCheckoutSessionAsync(invoice.TenantId, invoice.Id, "https://x/success", "https://x/cancel");

        Assert.Null(result);
    }

    [Fact]
    public async Task AnInvoiceWithNothingOutstandingReturnsNull()
    {
        await using var context = CreateContext();
        // Still nominally Open but fully credited - AmountOutstanding is zero either way.
        var invoice = NewInvoice(Guid.NewGuid(), InvoiceStatus.Open, 15m);
        invoice.AmountCredited = 15m;
        context.Set<Invoice>().Add(invoice);
        await context.SaveChangesAsync();

        var result = await CreateService(context)
            .CreateCheckoutSessionAsync(invoice.TenantId, invoice.Id, "https://x/success", "https://x/cancel");

        Assert.Null(result);
    }

    [Fact]
    public async Task APayableInvoiceWithNoStripeSecretKeyConfiguredReturnsNullRatherThanCallingStripe()
    {
        await using var context = CreateContext();
        var invoice = NewInvoice(Guid.NewGuid(), InvoiceStatus.Open, 15m);
        context.Set<Invoice>().Add(invoice);
        await context.SaveChangesAsync();

        // The default Mock.Of<IStripeCredentialProvider>() below already returns null/empty for
        // GetSecretKeyAsync() - this test exists to pin that an unconfigured key is refused here,
        // before ever reaching Stripe.net's own SessionService.CreateAsync call.
        var result = await CreateService(context)
            .CreateCheckoutSessionAsync(invoice.TenantId, invoice.Id, "https://x/success", "https://x/cancel");

        Assert.Null(result);
    }
}
