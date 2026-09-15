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
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="InvoiceService.IssueForPeriodAsync"/>'s discount integration - see
/// <see cref="DiscountRedemption"/>'s own remarks for why redemption and application are two separate
/// moments. Postgres-backed - see <see cref="InvoiceServiceStripeTaxTests"/>'s identical reasoning
/// (<c>TakeNumberAsync</c>'s <c>SELECT ... FOR UPDATE</c>).
/// </summary>
public class InvoiceServiceDiscountTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private static async Task EnsureInvoiceNumberSequenceAsync(ApiDbContext context)
    {
        var exists = await context.Set<InvoiceNumberSequence>()
            .AnyAsync(s => s.Scope == InvoiceNumberSequence.DefaultScope);

        if (!exists)
        {
            context.Set<InvoiceNumberSequence>().Add(new InvoiceNumberSequence());
            await context.SaveChangesAsync();
        }
    }

    private async Task<(SubscriptionPeriod Period, Guid TenantId)> SeedAsync(ApiDbContext context)
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
            SubscriptionId = subscription.Id, Subscription = subscription, TermMonths = 1, Quantity = 1,
            PeriodStart = DateTimeOffset.UtcNow, PeriodEnd = DateTimeOffset.UtcNow.AddMonths(1),
            StartDate = DateTimeOffset.UtcNow, EarnedAmount = 20m
        };
        context.Set<SubscriptionPeriod>().Add(period);

        await context.SaveChangesAsync();
        return (period, tenantId);
    }

    private static InvoiceService CreateService(ApiDbContext context, IStripeTaxService? stripeTax = null) => new(
        context, Mock.Of<ICommunicationPublisher>(), stripeTax ?? Mock.Of<IStripeTaxService>(),
        TimeProvider.System, NullLogger<InvoiceService>.Instance);

    private static async Task<DiscountRedemption> SeedPendingRedemptionAsync(
        ApiDbContext context, Guid tenantId, DiscountAmountType amountType, decimal amountValue)
    {
        // Unique per call, not a fixed literal - this class shares one Postgres database
        // (IClassFixture) across every [Fact], and Discount.Code has a real unique constraint.
        var discount = new Discount
        {
            Code = $"TESTCODE{Guid.NewGuid():N}", AmountType = amountType, AmountValue = amountValue,
            CreatedOn = DateTimeOffset.UtcNow
        };
        context.Set<Discount>().Add(discount);

        var redemption = new DiscountRedemption
        {
            DiscountId = discount.Id, TenantId = tenantId, Status = DiscountRedemptionStatus.Pending,
            RedeemedOn = DateTimeOffset.UtcNow
        };
        context.Set<DiscountRedemption>().Add(redemption);

        await context.SaveChangesAsync();
        return redemption;
    }

    [Fact]
    public async Task APercentDiscountReducesTheInvoiceAndMarksTheRedemptionApplied()
    {
        await using var context = CreateContext();
        await EnsureInvoiceNumberSequenceAsync(context);
        var (period, tenantId) = await SeedAsync(context);
        var redemption = await SeedPendingRedemptionAsync(context, tenantId, DiscountAmountType.PercentOff, 25m);
        var expectedCode = (await context.Set<Discount>().SingleAsync(d => d.Id == redemption.DiscountId)).Code;

        var invoice = await CreateService(context).IssueForPeriodAsync(period, 20m, "Wood - Monthly");

        Assert.NotNull(invoice);
        Assert.Equal(5m, invoice!.DiscountTotal);
        Assert.Equal(expectedCode, invoice.DiscountCode);
        Assert.Equal(15m, invoice.Total);

        await using var check = CreateContext();
        var reloaded = await check.Set<DiscountRedemption>().SingleAsync(r => r.Id == redemption.Id);
        Assert.Equal(DiscountRedemptionStatus.Applied, reloaded.Status);
        Assert.Equal(5m, reloaded.DiscountAmount);
        Assert.Equal(invoice.Id, reloaded.InvoiceId);
    }

    [Fact]
    public async Task AFlatDiscountLargerThanTheInvoiceIsCappedAtTheInvoiceAmount()
    {
        await using var context = CreateContext();
        await EnsureInvoiceNumberSequenceAsync(context);
        var (period, tenantId) = await SeedAsync(context);
        await SeedPendingRedemptionAsync(context, tenantId, DiscountAmountType.FlatAmountOff, 999m);

        var invoice = await CreateService(context).IssueForPeriodAsync(period, 20m, "Wood - Monthly");

        Assert.NotNull(invoice);
        Assert.Equal(20m, invoice!.DiscountTotal);
        Assert.Equal(0m, invoice.Total);
    }

    [Fact]
    public async Task TaxIsCalculatedOnTheDiscountedAmountNotTheGrossAmount()
    {
        await using var context = CreateContext();
        await EnsureInvoiceNumberSequenceAsync(context);
        var (period, tenantId) = await SeedAsync(context);
        await SeedPendingRedemptionAsync(context, tenantId, DiscountAmountType.PercentOff, 50m);

        var stripeTax = new Mock<IStripeTaxService>();
        stripeTax
            .Setup(t => t.CalculateAsync(tenantId, 10m, "USD", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StripeTaxResult(0.83m, "tax_test_1"));

        var invoice = await CreateService(context, stripeTax.Object).IssueForPeriodAsync(period, 20m, "Wood - Monthly");

        Assert.NotNull(invoice);
        // Gross 20, 50% off = 10 taxable, plus the mocked 0.83 tax on that discounted figure.
        Assert.Equal(10m, invoice!.DiscountTotal);
        Assert.Equal(0.83m, invoice.TaxTotal);
        Assert.Equal(10.83m, invoice.Total);
        stripeTax.Verify(
            t => t.CalculateAsync(tenantId, 20m, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NoDiscountLeavesTheInvoiceUnaffected()
    {
        await using var context = CreateContext();
        await EnsureInvoiceNumberSequenceAsync(context);
        var (period, _) = await SeedAsync(context);

        var invoice = await CreateService(context).IssueForPeriodAsync(period, 20m, "Wood - Monthly");

        Assert.NotNull(invoice);
        Assert.Equal(0m, invoice!.DiscountTotal);
        Assert.Null(invoice.DiscountCode);
        Assert.Equal(20m, invoice.Total);
    }
}
