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
/// Tests for <see cref="PaymentService.ReversePaymentAsync"/>'s partial-refund and Stripe-calling
/// behaviour - see that method's own remarks. Runs against a real (Testcontainers-hosted) Postgres,
/// not InMemory, since it opens a real transaction (see <see cref="PostgresFixture"/>'s own remarks).
/// </summary>
public class PaymentServiceRefundTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private static PaymentService CreateService(
        ApiDbContext context, IStripeRefundService? stripeRefund = null, IStripeTaxService? stripeTax = null) => new(
        context,
        Mock.Of<ICommunicationPublisher>(),
        new OrganizationLifecycleService(
            context, Mock.Of<IPublishEndpoint>(), Mock.Of<ICommunicationPublisher>(), TimeProvider.System,
            NullLogger<OrganizationLifecycleService>.Instance),
        stripeRefund ?? Mock.Of<IStripeRefundService>(),
        stripeTax ?? Mock.Of<IStripeTaxService>(),
        TimeProvider.System,
        NullLogger<PaymentService>.Instance);

    /// <summary>Seeds a tenant with one open invoice, then records one payment fully settling it -
    /// returning the payment id and invoice id to act on.</summary>
    private async Task<(Guid TenantId, Guid InvoiceId, Guid PaymentId)> SeedPaidInvoiceAsync(
        ApiDbContext context, PaymentMethod method = PaymentMethod.Manual, string? providerPaymentId = null,
        string? taxTransactionId = null, decimal total = 20m)
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
            Total = total,
            TaxTransactionId = taxTransactionId
        };
        context.Set<Invoice>().Add(invoice);
        await context.SaveChangesAsync();

        var payment = await CreateService(context).RecordPaymentAsync(
            invoice.Id, total, method, DateTimeOffset.UtcNow, "test", providerPaymentId);

        return (tenantId, invoice.Id, payment.Id);
    }

    [Fact]
    public async Task FullyReversingAManualPaymentReopensTheInvoiceAndMarksItRefunded()
    {
        await using var context = CreateContext();
        var (_, invoiceId, paymentId) = await SeedPaidInvoiceAsync(context);

        var result = await CreateService(context).ReversePaymentAsync(paymentId, PaymentStatus.Refunded);

        Assert.True(result);

        var invoice = await context.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(0m, invoice.AmountPaid);

        var payment = await context.Set<Payment>().Include(p => p.Allocations).SingleAsync(p => p.Id == paymentId);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        var allocation = Assert.Single(payment.Allocations);
        Assert.Equal(allocation.Amount, allocation.ReversedAmount);
        Assert.NotNull(allocation.ReversedOn);
    }

    [Fact]
    public async Task PartiallyReversingAManualPaymentReopensOnlyThatMuchAndStaysSucceeded()
    {
        await using var context = CreateContext();
        var (_, invoiceId, paymentId) = await SeedPaidInvoiceAsync(context, total: 20m);

        var result = await CreateService(context).ReversePaymentAsync(paymentId, PaymentStatus.Refunded, 5m);

        Assert.True(result);

        var invoice = await context.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        // Reopened, but only by the amount actually reversed - 15 of the original 20 is still paid.
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(15m, invoice.AmountPaid);

        var payment = await context.Set<Payment>().Include(p => p.Allocations).SingleAsync(p => p.Id == paymentId);
        // Still Succeeded - a partial refund doesn't retire the payment, same as Stripe's own
        // Charge.status.
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var allocation = Assert.Single(payment.Allocations);
        Assert.Equal(5m, allocation.ReversedAmount);
        Assert.Null(allocation.ReversedOn);
    }

    [Fact]
    public async Task TwoSuccessivePartialReversalsThatAddUpToTheFullAmountMarkItRefunded()
    {
        await using var context = CreateContext();
        var (_, _, paymentId) = await SeedPaidInvoiceAsync(context, total: 20m);

        var service = CreateService(context);
        await service.ReversePaymentAsync(paymentId, PaymentStatus.Refunded, 12m);
        var result = await service.ReversePaymentAsync(paymentId, PaymentStatus.Refunded, 8m);

        Assert.True(result);

        var payment = await context.Set<Payment>().Include(p => p.Allocations).SingleAsync(p => p.Id == paymentId);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        var allocation = Assert.Single(payment.Allocations);
        Assert.Equal(20m, allocation.ReversedAmount);
        Assert.NotNull(allocation.ReversedOn);
    }

    [Fact]
    public async Task RequestingMoreThanWhatsStillLiveIsRefusedAndChangesNothing()
    {
        await using var context = CreateContext();
        var (_, invoiceId, paymentId) = await SeedPaidInvoiceAsync(context, total: 20m);

        var result = await CreateService(context).ReversePaymentAsync(paymentId, PaymentStatus.Refunded, 25m);

        Assert.False(result);

        var invoice = await context.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(20m, invoice.AmountPaid);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }

    [Fact]
    public async Task ReversingACardPaymentCallsStripeForExactlyTheRequestedAmount()
    {
        await using var context = CreateContext();
        // Unique per test, not a fixed literal - this class shares one Postgres database
        // (IClassFixture), and RecordPaymentAsync's own idempotency check (see its remarks) treats a
        // repeated ProviderPaymentId as the same payment, which would silently reuse another test's
        // payment/invoice instead of this test's own.
        var providerPaymentId = $"pi_test_{Guid.NewGuid():N}";
        var (_, _, paymentId) = await SeedPaidInvoiceAsync(
            context, PaymentMethod.Card, providerPaymentId, total: 20m);

        var stripeRefund = new Mock<IStripeRefundService>();
        stripeRefund
            .Setup(s => s.RefundAsync(providerPaymentId, 7m, It.IsAny<CancellationToken>()))
            .ReturnsAsync("re_test_1");

        var result = await CreateService(context, stripeRefund.Object)
            .ReversePaymentAsync(paymentId, PaymentStatus.Refunded, 7m);

        Assert.True(result);
        stripeRefund.Verify(
            s => s.RefundAsync(providerPaymentId, 7m, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReversingACardPaymentAlsoReversesTheInvoicesTaxTransaction()
    {
        await using var context = CreateContext();
        var providerPaymentId = $"pi_test_{Guid.NewGuid():N}";
        var taxTransactionId = $"tax_test_{Guid.NewGuid():N}";
        var (_, _, paymentId) = await SeedPaidInvoiceAsync(
            context, PaymentMethod.Card, providerPaymentId, taxTransactionId, total: 20m);

        var stripeRefund = new Mock<IStripeRefundService>();
        stripeRefund
            .Setup(s => s.RefundAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("re_test_1");

        var stripeTax = new Mock<IStripeTaxService>();

        await CreateService(context, stripeRefund.Object, stripeTax.Object)
            .ReversePaymentAsync(paymentId, PaymentStatus.Refunded, 7m);

        stripeTax.Verify(
            s => s.ReverseAsync(taxTransactionId, 7m, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenStripeDeclinesTheRefundNothingInTheBooksChanges()
    {
        await using var context = CreateContext();
        var providerPaymentId = $"pi_test_{Guid.NewGuid():N}";
        var (_, invoiceId, paymentId) = await SeedPaidInvoiceAsync(
            context, PaymentMethod.Card, providerPaymentId, total: 20m);

        var stripeRefund = new Mock<IStripeRefundService>();
        stripeRefund
            .Setup(s => s.RefundAsync(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stripe refund failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService(context, stripeRefund.Object)
                .ReversePaymentAsync(paymentId, PaymentStatus.Refunded, 7m));

        var invoice = await context.Set<Invoice>().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(20m, invoice.AmountPaid);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);

        var payment = await context.Set<Payment>().Include(p => p.Allocations).SingleAsync(p => p.Id == paymentId);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(0m, Assert.Single(payment.Allocations).ReversedAmount);
    }
}
