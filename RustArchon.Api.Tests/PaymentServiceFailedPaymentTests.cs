// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
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
/// Tests for <see cref="PaymentService.RecordFailedPaymentAsync"/> - see that method's own remarks for
/// why it dedupes by <see cref="Payment.ProviderEventId"/> rather than <see cref="Payment.ProviderPaymentId"/>.
/// Runs against InMemory, unlike <see cref="PaymentServiceRefundTests"/> - this method opens no
/// transaction of its own (a single insert, nothing to allocate).
/// </summary>
public class PaymentServiceFailedPaymentTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static PaymentService CreateService(ApiDbContext context) => new(
        context,
        Mock.Of<ICommunicationPublisher>(),
        new OrganizationLifecycleService(
            context, Mock.Of<IPublishEndpoint>(), Mock.Of<ICommunicationPublisher>(),
            Mock.Of<ISubscriptionService>(), Mock.Of<IRoleCompressionService>(), Mock.Of<IUserContext>(),
            TimeProvider.System, NullLogger<OrganizationLifecycleService>.Instance),
        Mock.Of<IStripeRefundService>(),
        Mock.Of<IStripeTaxService>(),
        TimeProvider.System,
        NullLogger<PaymentService>.Instance);

    private async Task<Guid> SeedInvoiceAsync(ApiDbContext context)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });

        var invoice = new Invoice
        {
            TenantId = tenantId,
            Number = $"INV-{Guid.NewGuid():N}",
            Status = InvoiceStatus.Open,
            IssuedOn = DateTimeOffset.UtcNow,
            DueOn = DateTimeOffset.UtcNow.AddDays(14),
            Subtotal = 20m,
            Total = 20m
        };
        context.Set<Invoice>().Add(invoice);
        await context.SaveChangesAsync();

        return invoice.Id;
    }

    [Fact]
    public async Task ARecordedFailureCreatesAFailedPaymentWithNoAllocations()
    {
        await using var context = CreateContext();
        var invoiceId = await SeedInvoiceAsync(context);

        var payment = await CreateService(context).RecordFailedPaymentAsync(
            invoiceId, 20m, PaymentMethod.Card, "pi_test_1", "evt_test_1", "card_declined", "Your card was declined.");

        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Failed, payment!.Status);
        Assert.Equal("card_declined", payment.FailureCode);
        Assert.Equal("Your card was declined.", payment.FailureMessage);
        Assert.Equal("pi_test_1", payment.ProviderPaymentId);
        Assert.Equal("evt_test_1", payment.ProviderEventId);

        await using var check = CreateContext();
        Assert.Empty(await check.Set<PaymentAllocation>().Where(a => a.PaymentId == payment.Id).ToListAsync());

        var invoice = await check.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(0m, invoice.AmountPaid);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
    }

    [Fact]
    public async Task TheSamePaymentIntentFailingTwiceRecordsTwoDistinctFailures()
    {
        await using var context = CreateContext();
        var invoiceId = await SeedInvoiceAsync(context);
        var service = CreateService(context);

        // Same PaymentIntent (a customer retrying with a different card on one Checkout Session), two
        // distinct Stripe events - both must be recorded, since deduping by ProviderPaymentId here would
        // silently drop the second genuine decline.
        var first = await service.RecordFailedPaymentAsync(
            invoiceId, 20m, PaymentMethod.Card, "pi_test_shared", "evt_test_1", "card_declined", "Declined.");
        var second = await service.RecordFailedPaymentAsync(
            invoiceId, 20m, PaymentMethod.Card, "pi_test_shared", "evt_test_2", "insufficient_funds", "Declined.");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Id, second!.Id);

        await using var check = CreateContext();
        Assert.Equal(2, await check.Set<Payment>().CountAsync(p => p.ProviderPaymentId == "pi_test_shared"));
    }

    [Fact]
    public async Task TheSameEventRedeliveredIsNotRecordedTwice()
    {
        await using var context = CreateContext();
        var invoiceId = await SeedInvoiceAsync(context);
        var service = CreateService(context);

        var first = await service.RecordFailedPaymentAsync(
            invoiceId, 20m, PaymentMethod.Card, "pi_test_1", "evt_test_redelivered", "card_declined", "Declined.");
        var redelivered = await service.RecordFailedPaymentAsync(
            invoiceId, 20m, PaymentMethod.Card, "pi_test_1", "evt_test_redelivered", "card_declined", "Declined.");

        Assert.NotNull(first);
        Assert.Null(redelivered);

        await using var check = CreateContext();
        Assert.Equal(1, await check.Set<Payment>().CountAsync(p => p.ProviderEventId == "evt_test_redelivered"));
    }

    [Fact]
    public async Task AFailureForAnInvoiceThatDoesNotExistIsNotRecorded()
    {
        await using var context = CreateContext();

        var payment = await CreateService(context).RecordFailedPaymentAsync(
            Guid.NewGuid(), 20m, PaymentMethod.Card, "pi_test_1", "evt_test_1", "card_declined", "Declined.");

        Assert.Null(payment);
    }
}
