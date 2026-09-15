// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="ChargebackEvidenceService"/> - assembling and submitting the chargeback packet.
/// Runs against InMemory; nothing here opens a transaction (a read, and a single-row update).
/// </summary>
public class ChargebackEvidenceServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static ChargebackEvidenceService CreateService(
        ApiDbContext context, IStripeDisputeService? stripeDispute = null, TimeProvider? clock = null) => new(
        context, stripeDispute ?? Mock.Of<IStripeDisputeService>(), clock ?? TimeProvider.System,
        NullLogger<ChargebackEvidenceService>.Instance);

    private async Task<Guid> SeedDisputedPaymentAsync(ApiDbContext context, DateTimeOffset now)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant
        {
            Id = tenantId, Name = "Acme Corp", ContactEmail = "billing@acme.test", IsActive = true,
            CreatedOn = now.AddYears(-1)
        });

        var invoice = new Invoice
        {
            TenantId = tenantId, Number = "INV-0001", Status = InvoiceStatus.Open,
            IssuedOn = now.AddDays(-40), DueOn = now.AddDays(-26), Subtotal = 20m, Total = 20m, AmountPaid = 20m
        };
        context.Set<Invoice>().Add(invoice);

        var line = new InvoiceLine
        {
            Description = "Stone - Monthly", Amount = 20m, UnitAmount = 20m, Quantity = 1,
            ServiceStart = now.AddDays(-30), ServiceEnd = now.AddDays(0), InvoiceId = invoice.Id
        };
        invoice.Lines.Add(line);

        var server = new RustServer
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "Main", Host = "127.0.0.1", Port = 28016,
            CreatedOn = now.AddDays(-100)
        };
        context.Set<RustServer>().Add(server);

        context.Set<ConnectionLogEntry>().Add(new ConnectionLogEntry
        {
            TenantId = tenantId, RustServerId = server.Id, Level = ConnectionLogLevel.Info,
            Message = "Connected", OccurredAtUtc = now.AddDays(-20)
        });

        context.Set<Communication>().Add(new Communication
        {
            TenantId = tenantId, ToAddress = "billing@acme.test", Subject = "New invoice INV-0001",
            HtmlBody = "<p>...</p>", Status = RustArchon.Api.Data.CommunicationStatus.Viewed,
            QueuedOn = now.AddDays(-40), SentOn = now.AddDays(-40), ViewedOn = now.AddDays(-39)
        });

        var payment = new Payment
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Amount = 20m, Currency = "USD",
            Status = PaymentStatus.Disputed, Method = PaymentMethod.Card, ReceivedOn = now.AddDays(-38),
            ProviderPaymentId = "pi_test_1", DisputeId = "dp_test_1", DisputeReason = "fraudulent",
            DisputeDueBy = now.AddDays(5)
        };
        context.Set<Payment>().Add(payment);
        context.Set<PaymentAllocation>().Add(new PaymentAllocation
        {
            PaymentId = payment.Id, InvoiceId = invoice.Id, Amount = 20m, AllocatedOn = now.AddDays(-38),
            ReversedAmount = 20m, ReversedOn = now.AddDays(-38)
        });

        await context.SaveChangesAsync();
        return payment.Id;
    }

    [Fact]
    public async Task ReturnsNullForAPaymentThatIsNotDisputed()
    {
        await using var context = CreateContext();
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Test", IsActive = true });
        var payment = new Payment
        {
            TenantId = tenantId, Amount = 10m, Currency = "USD", Status = PaymentStatus.Succeeded,
            Method = PaymentMethod.Card, ReceivedOn = DateTimeOffset.UtcNow
        };
        context.Set<Payment>().Add(payment);
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetEvidenceAsync(payment.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task AssemblesInvoiceServerAndCommunicationEvidenceForADisputedPayment()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        var paymentId = await SeedDisputedPaymentAsync(context, now);

        var result = await CreateService(context).GetEvidenceAsync(paymentId);

        Assert.NotNull(result);
        Assert.Equal("dp_test_1", result!.DisputeId);
        Assert.Equal("Acme Corp", result.OrganizationName);

        var invoice = Assert.Single(result.Invoices);
        Assert.Equal("INV-0001", invoice.Number);
        Assert.NotNull(invoice.ServiceStart);

        var server = Assert.Single(result.Servers);
        Assert.Equal("Main", server.Name);
        Assert.Equal(1, server.ConnectionEventCount);

        var comm = Assert.Single(result.Communications);
        Assert.Equal("New invoice INV-0001", comm.Subject);
        Assert.NotNull(comm.ViewedOn);

        Assert.Contains("Acme Corp", result.SummaryText);
        Assert.Contains("Main", result.SummaryText);
    }

    [Fact]
    public async Task SubmittingEvidenceCallsStripeAndStampsTheSubmittedDate()
    {
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        var paymentId = await SeedDisputedPaymentAsync(context, now);

        var stripeDispute = new Mock<IStripeDisputeService>();
        var submitted = await CreateService(context, stripeDispute.Object, new FixedClock(now))
            .SubmitEvidenceAsync(paymentId);

        Assert.True(submitted);
        stripeDispute.Verify(
            s => s.SubmitEvidenceAsync(
                "dp_test_1", It.Is<DisputeEvidenceInput>(e => e.CustomerName == "Acme Corp"),
                It.IsAny<System.Threading.CancellationToken>()),
            Times.Once);

        var payment = await context.Set<Payment>().SingleAsync(p => p.Id == paymentId);
        Assert.Equal(now, payment.DisputeEvidenceSubmittedOn);
    }
}
