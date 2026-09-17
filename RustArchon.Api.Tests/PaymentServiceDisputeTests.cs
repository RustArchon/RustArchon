// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
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
/// Tests for <see cref="PaymentService.RecordDisputeAsync"/> - see that method's own remarks for why it
/// never calls Stripe's API (the money already moved by the time this fires) but still reuses the same
/// allocation-reversal bookkeeping a refund uses. Postgres-backed - reversal opens a real transaction,
/// same reasoning as <see cref="PaymentServiceRefundTests"/>.
/// </summary>
public class PaymentServiceDisputeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private static PaymentService CreateService(ApiDbContext context) => new(
        context,
        Mock.Of<ICommunicationPublisher>(),
        new OrganizationLifecycleService(
            context, Mock.Of<IPublishEndpoint>(), Mock.Of<ICommunicationPublisher>(), TimeProvider.System,
            NullLogger<OrganizationLifecycleService>.Instance),
        Mock.Of<IStripeRefundService>(),
        Mock.Of<IStripeTaxService>(),
        TimeProvider.System,
        NullLogger<PaymentService>.Instance);

    private async Task<(Guid InvoiceId, Guid PaymentId, string ProviderPaymentId)> SeedPaidInvoiceAsync(
        ApiDbContext context, decimal total = 20m)
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
            Subtotal = total,
            Total = total
        };
        context.Set<Invoice>().Add(invoice);
        await context.SaveChangesAsync();

        var providerPaymentId = $"pi_test_{Guid.NewGuid():N}";
        var payment = await CreateService(context).RecordPaymentAsync(
            invoice.Id, total, PaymentMethod.Card, DateTimeOffset.UtcNow, "test", providerPaymentId);

        return (invoice.Id, payment.Id, providerPaymentId);
    }

    [Fact]
    public async Task ADisputeReopensTheInvoiceAndMarksThePaymentDisputed()
    {
        await using var context = CreateContext();
        var (invoiceId, paymentId, providerPaymentId) = await SeedPaidInvoiceAsync(context);
        var dueBy = DateTimeOffset.UtcNow.AddDays(7);

        var result = await CreateService(context).RecordDisputeAsync(
            providerPaymentId, "dp_test_1", "fraudulent", dueBy);

        Assert.NotNull(result);
        Assert.Equal(PaymentStatus.Disputed, result!.Status);
        Assert.Equal("dp_test_1", result.DisputeId);
        Assert.Equal("fraudulent", result.DisputeReason);
        Assert.Equal(dueBy, result.DisputeDueBy);

        var invoice = await context.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(0m, invoice.AmountPaid);

        var payment = await context.Set<Payment>().Include(p => p.Allocations).SingleAsync(p => p.Id == paymentId);
        var allocation = Assert.Single(payment.Allocations);
        Assert.Equal(allocation.Amount, allocation.ReversedAmount);
    }

    [Fact]
    public async Task NoStripeRefundIsEverCalledForADispute()
    {
        await using var context = CreateContext();
        var (_, _, providerPaymentId) = await SeedPaidInvoiceAsync(context);

        var stripeRefund = new Mock<IStripeRefundService>();
        var service = new PaymentService(
            context, Mock.Of<ICommunicationPublisher>(),
            new OrganizationLifecycleService(
                context, Mock.Of<IPublishEndpoint>(), Mock.Of<ICommunicationPublisher>(), TimeProvider.System,
                NullLogger<OrganizationLifecycleService>.Instance),
            stripeRefund.Object, Mock.Of<IStripeTaxService>(), TimeProvider.System, NullLogger<PaymentService>.Instance);

        await service.RecordDisputeAsync(providerPaymentId, "dp_test_1", "fraudulent", null);

        stripeRefund.Verify(
            s => s.RefundAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheSameDisputeRedeliveredDoesNotReverseTwice()
    {
        await using var context = CreateContext();
        var (invoiceId, _, providerPaymentId) = await SeedPaidInvoiceAsync(context);
        var service = CreateService(context);

        await service.RecordDisputeAsync(providerPaymentId, "dp_test_1", "fraudulent", null);
        var second = await service.RecordDisputeAsync(providerPaymentId, "dp_test_1", "fraudulent", null);

        Assert.NotNull(second);

        var invoice = await context.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        // Still just fully reopened once, not double-credited back past zero.
        Assert.Equal(0m, invoice.AmountPaid);
    }

    [Fact]
    public async Task ADisputeForAnUnknownPaymentIntentIsNotRecorded()
    {
        await using var context = CreateContext();

        var result = await CreateService(context).RecordDisputeAsync(
            "pi_does_not_exist", "dp_test_1", "fraudulent", null);

        Assert.Null(result);
    }

    [Fact]
    public async Task ADisputeClosedLostRecordsTheOutcomeWithoutTouchingTheInvoice()
    {
        await using var context = CreateContext();
        var (invoiceId, paymentId, providerPaymentId) = await SeedPaidInvoiceAsync(context);
        var service = CreateService(context);
        var disputeId = $"dp_test_{Guid.NewGuid():N}";
        await service.RecordDisputeAsync(providerPaymentId, disputeId, "fraudulent", null);

        var result = await service.RecordDisputeClosedAsync(disputeId, "lost");

        Assert.NotNull(result);
        Assert.Equal("lost", result!.DisputeStatus);
        Assert.NotNull(result.DisputeClosedOn);

        // Still fully reopened - a lost dispute has no more money to move, so nothing here changes.
        var invoice = await context.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        var payment = await context.Set<Payment>().SingleAsync(p => p.Id == paymentId);
        Assert.Equal(PaymentStatus.Disputed, payment.Status);
    }

    [Fact]
    public async Task TheSameDisputeClosedEventRedeliveredDoesNotReRecordIt()
    {
        await using var context = CreateContext();
        var (_, _, providerPaymentId) = await SeedPaidInvoiceAsync(context);
        var service = CreateService(context);
        var disputeId = $"dp_test_{Guid.NewGuid():N}";
        await service.RecordDisputeAsync(providerPaymentId, disputeId, "fraudulent", null);

        var first = await service.RecordDisputeClosedAsync(disputeId, "lost");
        var firstClosedOn = first!.DisputeClosedOn;
        var second = await service.RecordDisputeClosedAsync(disputeId, "lost");

        Assert.Equal(firstClosedOn, second!.DisputeClosedOn);
    }

    [Fact]
    public async Task ADisputeClosedEventForAnUnknownDisputeIsNotRecorded()
    {
        await using var context = CreateContext();

        var result = await CreateService(context).RecordDisputeClosedAsync(
            $"dp_does_not_exist_{Guid.NewGuid():N}", "lost");

        Assert.Null(result);
    }

    [Fact]
    public async Task FundsReinstatedAfterAWonDisputeReopensTheInvoiceAsPaidAndTheStatusAsSucceeded()
    {
        await using var context = CreateContext();
        var (invoiceId, paymentId, providerPaymentId) = await SeedPaidInvoiceAsync(context, total: 20m);
        var service = CreateService(context);
        var disputeId = $"dp_test_{Guid.NewGuid():N}";
        await service.RecordDisputeAsync(providerPaymentId, disputeId, "fraudulent", null);
        await service.RecordDisputeClosedAsync(disputeId, "won");

        var result = await service.RecordDisputeFundsReinstatedAsync(disputeId);

        Assert.NotNull(result);
        Assert.NotNull(result!.DisputeFundsReinstatedOn);
        Assert.Equal(PaymentStatus.Succeeded, result.Status);

        var invoice = await context.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(20m, invoice.AmountPaid);

        var payment = await context.Set<Payment>().Include(p => p.Allocations).SingleAsync(p => p.Id == paymentId);
        var allocation = Assert.Single(payment.Allocations);
        Assert.Equal(0m, allocation.ReversedAmount);
        Assert.Null(allocation.ReversedOn);
    }

    [Fact]
    public async Task TheSameFundsReinstatedEventRedeliveredDoesNotDoubleCreditTheInvoice()
    {
        await using var context = CreateContext();
        var (invoiceId, _, providerPaymentId) = await SeedPaidInvoiceAsync(context, total: 20m);
        var service = CreateService(context);
        var disputeId = $"dp_test_{Guid.NewGuid():N}";
        await service.RecordDisputeAsync(providerPaymentId, disputeId, "fraudulent", null);

        await service.RecordDisputeFundsReinstatedAsync(disputeId);
        await service.RecordDisputeFundsReinstatedAsync(disputeId);

        var invoice = await context.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        // Still just fully reinstated once, not double-credited past the invoice's own total.
        Assert.Equal(20m, invoice.AmountPaid);
    }

    [Fact]
    public async Task FundsReinstatedForAnUnknownDisputeIsNotRecorded()
    {
        await using var context = CreateContext();

        var result = await CreateService(context).RecordDisputeFundsReinstatedAsync(
            $"dp_does_not_exist_{Guid.NewGuid():N}");

        Assert.Null(result);
    }

    [Fact]
    public async Task FundsReinstatedWhenNothingWasEverReversedStillRecordsTheEventWithNoBookkeeping()
    {
        await using var context = CreateContext();
        // A payment whose dispute was recorded with nothing live left to reverse (see
        // RecordDisputeAsync's own remarks for how that happens - already fully refunded some other
        // way) - DisputeAmountReversed stays zero, so reinstatement has nothing to give back either.
        var (_, _, providerPaymentId) = await SeedPaidInvoiceAsync(context);
        var service = CreateService(context);
        await service.ReversePaymentAsync(
            (await context.Set<Payment>().SingleAsync(p => p.ProviderPaymentId == providerPaymentId)).Id,
            PaymentStatus.Refunded);
        var disputeId = $"dp_test_{Guid.NewGuid():N}";
        await service.RecordDisputeAsync(providerPaymentId, disputeId, "fraudulent", null);

        var result = await service.RecordDisputeFundsReinstatedAsync(disputeId);

        Assert.NotNull(result);
        Assert.NotNull(result!.DisputeFundsReinstatedOn);
        // Already Refunded before the dispute ever recorded - reinstating a dispute that had nothing
        // live to reverse doesn't resurrect an unrelated refund.
        Assert.Equal(PaymentStatus.Refunded, result.Status);
    }
}
