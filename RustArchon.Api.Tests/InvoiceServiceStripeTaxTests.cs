// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="InvoiceService.IssueForPeriodAsync"/>'s Stripe Tax integration - see
/// <see cref="IStripeTaxService"/>'s own remarks. <see cref="IStripeTaxService"/> is mocked throughout;
/// the guard clauses that decide whether it's even called (no billing address on file) are
/// <see cref="StripeTaxService"/>'s own concern, covered separately in <see cref="StripeTaxServiceTests"/>.
/// </summary>
/// <remarks>
/// Runs against a real (Testcontainers-hosted) Postgres, not EF Core's InMemory provider -
/// <see cref="InvoiceService.IssueForPeriodAsync"/> finalises through a raw-SQL <c>SELECT ... FOR UPDATE</c>
/// against <see cref="InvoiceNumberSequence"/> (see that class's own remarks on why), which InMemory
/// can't execute at all. See <see cref="PostgresFixture"/>'s own remarks for the same reasoning applied
/// elsewhere in this test suite.
/// </remarks>
public class InvoiceServiceStripeTaxTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private static async Task EnsureInvoiceNumberSequenceAsync(ApiDbContext context)
    {
        // Normally seeded once by the BillingTables migration - EnsureCreatedAsync (see
        // PostgresFixture) builds the schema straight from the model and skips migration-only
        // InsertData, so this fixture's database never gets that seed row on its own. Checked first
        // since every test in this class shares one Postgres database/schema.
        var exists = await context.Set<InvoiceNumberSequence>()
            .AnyAsync(s => s.Scope == InvoiceNumberSequence.DefaultScope);

        if (!exists)
        {
            context.Set<InvoiceNumberSequence>().Add(new InvoiceNumberSequence());
            await context.SaveChangesAsync();
        }
    }

    private async Task<SubscriptionPeriod> SeedAsync(ApiDbContext context)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });

        var plan = new Plan { Name = $"Test plan {Guid.NewGuid()}", Active = true };
        plan.Prices.Add(new PlanPrice { TermMonths = 1, UnitAmount = 0m, IncludedUnits = 1, Currency = "USD" });
        context.Set<Plan>().Add(plan);

        var subscription = new Subscription
        {
            TenantId = tenantId, PlanId = plan.Id, Plan = plan, StartDate = DateTimeOffset.UtcNow
        };
        context.Set<Subscription>().Add(subscription);

        var period = new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id,
            Subscription = subscription,
            TermMonths = 1,
            Quantity = 1,
            PeriodStart = DateTimeOffset.UtcNow,
            PeriodEnd = DateTimeOffset.UtcNow.AddMonths(1),
            StartDate = DateTimeOffset.UtcNow,
            EarnedAmount = 15m
        };
        context.Set<SubscriptionPeriod>().Add(period);

        await context.SaveChangesAsync();
        return period;
    }

    private static InvoiceService CreateService(ApiDbContext context, IStripeTaxService stripeTax) => new(
        context, Mock.Of<ICommunicationPublisher>(), stripeTax, TimeProvider.System,
        NullLogger<InvoiceService>.Instance);

    [Fact]
    public async Task NoTaxResultLeavesTheInvoiceAtItsPreTaxAmount()
    {
        await using var context = CreateContext();
        await EnsureInvoiceNumberSequenceAsync(context);
        var period = await SeedAsync(context);
        var tenantId = period.Subscription.TenantId;

        var stripeTax = new Mock<IStripeTaxService>();
        stripeTax
            .Setup(t => t.CalculateAsync(
                tenantId, 15m, "USD", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StripeTaxResult?)null);

        var invoice = await CreateService(context, stripeTax.Object)
            .IssueForPeriodAsync(period, 15m, "Wood - Monthly");

        Assert.NotNull(invoice);
        Assert.Equal(0m, invoice!.TaxTotal);
        Assert.Equal(15m, invoice.Total);
        Assert.Null(invoice.TaxTransactionId);
        Assert.Equal(0m, Assert.Single(invoice.Lines).TaxAmount);
    }

    [Fact]
    public async Task ATaxResultAddsToTheInvoiceTotalAndRecordsTheTransactionId()
    {
        await using var context = CreateContext();
        await EnsureInvoiceNumberSequenceAsync(context);
        var period = await SeedAsync(context);
        var tenantId = period.Subscription.TenantId;

        var stripeTax = new Mock<IStripeTaxService>();
        stripeTax
            .Setup(t => t.CalculateAsync(
                tenantId, 15m, "USD", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeTaxResult(1.24m, "tax_1Abc"));

        var invoice = await CreateService(context, stripeTax.Object)
            .IssueForPeriodAsync(period, 15m, "Wood - Monthly");

        Assert.NotNull(invoice);
        Assert.Equal(1.24m, invoice!.TaxTotal);
        Assert.Equal(16.24m, invoice.Total);
        Assert.Equal("tax_1Abc", invoice.TaxTransactionId);
        Assert.Equal(1.24m, Assert.Single(invoice.Lines).TaxAmount);
    }

    [Fact]
    public async Task ATaxCalculationFailurePreventsTheInvoiceFromBeingIssuedAtAll()
    {
        await using var context = CreateContext();
        await EnsureInvoiceNumberSequenceAsync(context);
        var period = await SeedAsync(context);
        var tenantId = period.Subscription.TenantId;

        var stripeTax = new Mock<IStripeTaxService>();
        stripeTax
            .Setup(t => t.CalculateAsync(
                tenantId, 15m, "USD", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stripe unreachable"));

        var service = CreateService(context, stripeTax.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IssueForPeriodAsync(period, 15m, "Wood - Monthly"));

        // Nothing was persisted - HasBeenBilledAsync should still say "no", so the next
        // SubscriptionScheduleService pass retries this period rather than treating it as already
        // handled (with $0 tax) or permanently skipped.
        Assert.False(await service.HasBeenBilledAsync(period.Id));
    }
}
