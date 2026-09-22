// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Moving a plan's current subscribers to a newer version of it: only those the newer version is no worse for (judged on each organization's own term and
/// capacity), keeping their billing period exactly - nothing charged, nothing refunded, the amount earned for the period shared between the two halves of
/// it - with the history left truthful, and everybody else left where they are with the reason.
/// </summary>
public class PlanSubscriberMoverTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string UniqueName() => "Mover " + Guid.NewGuid().ToString("N")[..10];

    // ---- plans and subscribers -------------------------------------------------------------------------------------------

    private sealed record Terms(
        decimal Monthly = 10m, decimal? Annual = 100m, int Servers = 3, int Users = 5, int Retention = 30, bool Roles = false, bool Updates = false,
        PricingModel Model = PricingModel.Flat, decimal UnitAmount = 0m, int? MaxServers = null);

    private static Plan BuildPlan(string name, Terms t)
    {
        var plan = new Plan
        {
            Name = name, Active = true, PricingModel = t.Model, RetentionHistory = t.Retention, HasRoles = t.Roles, OffersThirdPartyPluginUpdates = t.Updates,
            MaximumServers = t.MaxServers ?? (t.Model == PricingModel.Flat ? t.Servers : null), MaximumUsers = t.Users
        };
        plan.Prices.Add(new PlanPrice { TermMonths = 1, BaseAmount = t.Monthly, IncludedUnits = t.Servers, UnitAmount = t.UnitAmount, Currency = "USD" });
        if (t.Annual is { } annual)
        {
            plan.Prices.Add(new PlanPrice { TermMonths = 12, BaseAmount = annual, IncludedUnits = t.Servers, UnitAmount = t.UnitAmount, Currency = "USD" });
        }

        return plan;
    }

    /// <summary>Saves an old and a newer version of a plan (the older one already marked as replaced by the newer, and switched off).</summary>
    private async Task<(Plan Old, Plan Newer)> PlansAsync(Terms old, Terms newer)
    {
        var name = UniqueName();
        await using var context = new ApiDbContext(postgres.Options);
        var older = BuildPlan(name, old);
        older.Active = false;
        var newest = BuildPlan(name, newer);
        context.Set<Plan>().AddRange(older, newest);
        await context.SaveChangesAsync();
        older.SupersededByPlanId = newest.Id;
        await context.SaveChangesAsync();
        return (await LoadAsync(older.Id), await LoadAsync(newest.Id));
    }

    private async Task<Plan> LoadAsync(Guid id)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.Set<Plan>().Include(p => p.Prices).AsNoTracking().SingleAsync(p => p.Id == id);
    }

    /// <summary>An organization with an open subscription on the plan, half way through a billing period that earned <paramref name="earned"/>.</summary>
    private async Task<Guid> SubscribeAsync(
        Plan plan, int term = 1, int quantity = 3, decimal earned = 10m, SubscriptionStatus status = SubscriptionStatus.Active, bool tenantActive = true,
        int servers = 0, bool periodOver = false)
    {
        var tenantId = Guid.NewGuid();
        await using var context = new ApiDbContext(postgres.Options);
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Mover tenant {tenantId}", IsActive = tenantActive });
        var subscription = new Subscription { TenantId = tenantId, PlanId = plan.Id, StartDate = Now.AddMonths(-3), Status = status };
        context.Set<Subscription>().Add(subscription);
        await context.SaveChangesAsync();

        // Half way through a 30 day period, or (when asked) a period that ended a day ago and has not been renewed yet.
        var start = periodOver ? Now.AddDays(-31) : Now.AddDays(-15);
        var end = periodOver ? Now.AddDays(-1) : Now.AddDays(15);
        context.Set<SubscriptionPeriod>().Add(new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id, TermMonths = term, Quantity = quantity, PeriodStart = start, PeriodEnd = end, StartDate = start, EndDate = end, EarnedAmount = earned
        });
        for (var i = 0; i < servers; i++)
        {
            context.Set<RustServer>().Add(new RustServer { Id = Guid.NewGuid(), TenantId = tenantId, Name = "S" + i, Host = "192.0.2.90", Port = 28016, RconPassword = "x" });
        }

        await context.SaveChangesAsync();
        return tenantId;
    }

    private PlanSubscriberMover Mover() => new(new ApiDbContext(postgres.Options), new FixedClock(Now), NullLogger<PlanSubscriberMover>.Instance);

    private async Task<List<Subscription>> SubscriptionsOfAsync(Guid tenantId)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.Set<Subscription>().IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.StartDate).ThenBy(s => s.EndDate).ToListAsync();
    }

    private async Task<List<SubscriptionPeriod>> PeriodsOfAsync(Guid tenantId)
    {
        await using var context = new ApiDbContext(postgres.Options);
        var ids = await context.Set<Subscription>().IgnoreQueryFilters().Where(s => s.TenantId == tenantId).Select(s => s.Id).ToListAsync();
        return await context.Set<SubscriptionPeriod>().AsNoTracking().Where(p => ids.Contains(p.SubscriptionId)).OrderBy(p => p.StartDate).ToListAsync();
    }

    private async Task<Subscription> OpenAsync(Guid tenantId) => (await SubscriptionsOfAsync(tenantId)).Single(s => s.EndDate == null);

    // ---- the assessment on its own -----------------------------------------------------------------------------------------

    private static string? Worse(Terms old, Terms newer, int term = 1, int quantity = 3, int servers = 1) =>
        PlanMoveAssessment.WhyWorse(BuildPlan("A", old), BuildPlan("A", newer), term, quantity, servers);

    [Fact]
    public void ANewerVersionThatIsTheSameOrBetterIsNotWorse()
    {
        Assert.Null(Worse(new Terms(), new Terms()));
        Assert.Null(Worse(new Terms(), new Terms(Monthly: 8m, Annual: 90m, Servers: 5, Users: 9, Retention: 60, Roles: true, Updates: true)));
    }

    [Fact]
    public void ADearerPriceOnTheSubscribersOwnTermIsWorse()
    {
        Assert.Equal(PlanMoveAssessment.PriceHigher, Worse(new Terms(), new Terms(Monthly: 11m)));
    }

    [Fact]
    public void APriceRiseOnAnotherTermDoesNotHoldBackSomeoneOnAtermItDidNotRise()
    {
        var newer = new Terms(Monthly: 10m, Annual: 120m);

        Assert.Null(Worse(new Terms(), newer, term: 1));                                         // monthly: unchanged
        Assert.Equal(PlanMoveAssessment.PriceHigher, Worse(new Terms(), newer, term: 12));       // annual: dearer
    }

    [Fact]
    public void ATermTheNewerVersionDoesNotOfferIsWorseOnlyForThoseOnIt()
    {
        var newer = new Terms(Annual: null);

        Assert.Null(Worse(new Terms(), newer, term: 1));
        Assert.Equal(PlanMoveAssessment.TermNotOffered, Worse(new Terms(), newer, term: 12));
    }

    [Fact]
    public void ADifferentPricingModelIsWorseBecauseItCannotBeComparedLikeForLike()
    {
        Assert.Equal(PlanMoveAssessment.PricingModelChanged, Worse(new Terms(), new Terms(Model: PricingModel.PerUnit, UnitAmount: 1m)));
    }

    [Fact]
    public void LowerLimitsAreWorseInTheOrderTheyAreChecked()
    {
        Assert.Equal(PlanMoveAssessment.FewerServers, Worse(new Terms(Servers: 5), new Terms(Servers: 3), quantity: 5));
        Assert.Equal(PlanMoveAssessment.FewerUsers, Worse(new Terms(Users: 9), new Terms(Users: 5)));
        Assert.Equal(PlanMoveAssessment.ShorterRetention, Worse(new Terms(Retention: 90), new Terms(Retention: 30)));
    }

    [Fact]
    public void ALostFeatureIsWorse()
    {
        Assert.Equal(PlanMoveAssessment.RolesRemoved, Worse(new Terms(Roles: true), new Terms(Roles: false)));
        Assert.Equal(PlanMoveAssessment.AutomaticUpdatesRemoved, Worse(new Terms(Updates: true), new Terms(Updates: false)));
        Assert.Null(Worse(new Terms(Roles: false, Updates: false), new Terms(Roles: true, Updates: true)));          // gaining one is fine
    }

    [Fact]
    public void ANewerVersionWithNoCeilingIsNotFewerServersButAnAddedCeilingIs()
    {
        var perUnitOld = new Terms(Model: PricingModel.PerUnit, UnitAmount: 2m, Servers: 1, MaxServers: null);

        Assert.Null(PlanMoveAssessment.WhyWorse(BuildPlan("A", new Terms(Servers: 3)), BuildPlan("A", new Terms(Model: PricingModel.Flat, Servers: 3)), 1, 3, 1));
        Assert.Equal(PlanMoveAssessment.FewerServers, PlanMoveAssessment.WhyWorse(BuildPlan("A", perUnitOld), BuildPlan("A", perUnitOld with { MaxServers = 4 }), 1, 6, 1));
    }

    [Fact]
    public void ARunningServerCountAboveTheNewerCeilingIsRefusedEvenWhenNothingElseIsWorse()
    {
        // The old plan already allowed fewer than the organization runs (an anomaly); the newer one is not lower than that, but still under what they run.
        var result = PlanMoveAssessment.WhyWorse(BuildPlan("A", new Terms(Servers: 2)), BuildPlan("A", new Terms(Servers: 3)), 1, 2, serverCount: 5);

        Assert.Equal(PlanMoveAssessment.OverServerLimit, result);
    }

    [Fact]
    public void EveryCodeHasWording()
    {
        foreach (var code in new[]
        {
            PlanMoveAssessment.PriceHigher, PlanMoveAssessment.TermNotOffered, PlanMoveAssessment.PricingModelChanged, PlanMoveAssessment.FewerServers,
            PlanMoveAssessment.FewerUsers, PlanMoveAssessment.ShorterRetention, PlanMoveAssessment.RolesRemoved, PlanMoveAssessment.AutomaticUpdatesRemoved,
            PlanMoveAssessment.OverServerLimit, PlanMoveAssessment.NotInGoodStanding, PlanMoveAssessment.RenewalDue, PlanMoveAssessment.CouldNotMove
        })
        {
            Assert.NotEqual(code, PlanMoveAssessment.Describe(code));
        }
    }

    // ---- the move keeps the period ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMovedOrganizationIsOnTheNewerVersionNowAndItsOldIntervalIsClosedNotRewritten()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms(Monthly: 8m));
        var tenant = await SubscribeAsync(old);
        var admin = Guid.NewGuid();

        var result = await Mover().MoveAsync(old, newer, admin, CancellationToken.None);

        Assert.Equal(1, result.Moved);
        var history = await SubscriptionsOfAsync(tenant);
        Assert.Equal(2, history.Count);
        Assert.Equal(old.Id, history[0].PlanId);
        Assert.Equal(Now, history[0].EndDate);                    // closed when it moved
        Assert.Equal(newer.Id, history[1].PlanId);
        Assert.Equal(Now, history[1].StartDate);
        Assert.Null(history[1].EndDate);                          // the one open interval, on the newer version
        Assert.Equal(admin, history[1].PlanChangedById);
        Assert.Contains("Moved to the newer version", history[1].PlanChangeReason);
    }

    [Fact]
    public async Task TheBillingPeriodIsExactlyAsItWasSplitAtTheMomentOfTheMove()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());
        var tenant = await SubscribeAsync(old, term: 1, quantity: 3, earned: 10m);
        var before = (await PeriodsOfAsync(tenant)).Single();

        await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        var periods = await PeriodsOfAsync(tenant);
        Assert.Equal(2, periods.Count);
        Assert.All(periods, p => Assert.Equal(before.PeriodStart, p.PeriodStart));          // one billing period, in two parts
        Assert.All(periods, p => Assert.Equal(before.PeriodEnd, p.PeriodEnd));               // so the renewal date has not moved
        Assert.All(periods, p => Assert.Equal(1, p.TermMonths));
        Assert.Equal(before.StartDate, periods[0].StartDate);
        Assert.Equal(Now, periods[0].EndDate);
        Assert.Equal(Now, periods[1].StartDate);
        Assert.Equal(before.EndDate, periods[1].EndDate);
    }

    [Fact]
    public async Task WhatWasEarnedForThePeriodIsSharedByTimeSoTheTotalIsUnchanged()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());
        var tenant = await SubscribeAsync(old, earned: 10m);          // half way through the period

        await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        var periods = await PeriodsOfAsync(tenant);
        Assert.Equal(5.00m, periods[0].EarnedAmount);
        Assert.Equal(5.00m, periods[1].EarnedAmount);
        Assert.Equal(10m, periods.Sum(p => p.EarnedAmount));
    }

    [Fact]
    public async Task AnAwkwardAmountStillAddsUpToExactlyWhatWasEarned()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());
        var tenant = await SubscribeAsync(old, earned: 9.99m);

        await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(9.99m, (await PeriodsOfAsync(tenant)).Sum(p => p.EarnedAmount));
    }

    [Fact]
    public async Task NothingIsInvoicedOrCreditedByAMove()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms(Monthly: 5m));
        var tenant = await SubscribeAsync(old);
        await using (var context = new ApiDbContext(postgres.Options))
        {
            Assert.Equal(0, await context.Set<Invoice>().IgnoreQueryFilters().CountAsync(i => i.TenantId == tenant));
        }

        await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        await using var after = new ApiDbContext(postgres.Options);
        Assert.Equal(0, await after.Set<Invoice>().IgnoreQueryFilters().CountAsync(i => i.TenantId == tenant));
    }

    [Fact]
    public async Task AFlatPlanMovedToOneWithMoreIncludedGetsTheNewCapacityAtOnce()
    {
        var (old, newer) = await PlansAsync(new Terms(Servers: 3), new Terms(Servers: 5));
        var tenant = await SubscribeAsync(old, quantity: 3);

        await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        var periods = await PeriodsOfAsync(tenant);
        Assert.Equal(3, periods[0].Quantity);
        Assert.Equal(5, periods[1].Quantity);
    }

    [Fact]
    public async Task APerServerSubscriberKeepsTheCapacityTheyBought()
    {
        var perUnit = new Terms(Model: PricingModel.PerUnit, UnitAmount: 2m, Servers: 1, Monthly: 5m);
        var (old, newer) = await PlansAsync(perUnit, perUnit with { Monthly = 4m });
        var tenant = await SubscribeAsync(old, quantity: 7, servers: 2);

        await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(7, (await PeriodsOfAsync(tenant)).Last().Quantity);
    }

    [Fact]
    public async Task AnAnnualSubscriberKeepsTheirTermAndRenewalDate()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());
        var tenant = await SubscribeAsync(old, term: 12);

        await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.All(await PeriodsOfAsync(tenant), p => Assert.Equal(12, p.TermMonths));
    }

    [Fact]
    public async Task APastDueSubscriberIsMovedAndKeepsThatStatus()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());
        var tenant = await SubscribeAsync(old, status: SubscriptionStatus.PastDue);

        var result = await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(1, result.Moved);
        Assert.Equal(SubscriptionStatus.PastDue, (await OpenAsync(tenant)).Status);
    }

    // ---- who stays ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyThoseTheNewerVersionIsNoWorseForAreMovedAndTheOthersStayWithTheReason()
    {
        var (old, newer) = await PlansAsync(new Terms(Monthly: 10m, Annual: 100m), new Terms(Monthly: 10m, Annual: 120m));
        var monthly = await SubscribeAsync(old, term: 1);
        var annual = await SubscribeAsync(old, term: 12);

        var result = await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Stayed);
        var why = Assert.Single(result.StayedBecause);
        Assert.Equal(PlanMoveAssessment.PriceHigher, why.Code);
        Assert.Equal(newer.Id, (await OpenAsync(monthly)).PlanId);
        Assert.Equal(old.Id, (await OpenAsync(annual)).PlanId);                       // untouched: same subscription, no split
        Assert.Single(await SubscriptionsOfAsync(annual));
        Assert.Single(await PeriodsOfAsync(annual));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Suspended, true)]
    [InlineData(SubscriptionStatus.Cancelled, true)]
    [InlineData(SubscriptionStatus.Active, false)]         // an organization that is not active at all
    public async Task NobodyWhoIsNotInGoodStandingIsMoved(SubscriptionStatus status, bool tenantActive)
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());
        var tenant = await SubscribeAsync(old, status: status, tenantActive: tenantActive);

        var result = await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.Equal(PlanMoveAssessment.NotInGoodStanding, Assert.Single(result.StayedBecause).Code);
        Assert.Equal(old.Id, (await OpenAsync(tenant)).PlanId);
    }

    [Fact]
    public async Task AnOrganizationWhosePeriodHasEndedAndIsAboutToRenewStaysUntilTheRenewalHasHappened()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());
        var tenant = await SubscribeAsync(old, periodOver: true);

        var result = await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.Equal(PlanMoveAssessment.RenewalDue, Assert.Single(result.StayedBecause).Code);
        Assert.Single(await SubscriptionsOfAsync(tenant));
    }

    [Fact]
    public async Task AWorseNewerVersionMovesNobodyAndChangesNothing()
    {
        var (old, newer) = await PlansAsync(new Terms(Retention: 90), new Terms(Retention: 30));
        var tenants = new[] { await SubscribeAsync(old), await SubscribeAsync(old) };

        var result = await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.Equal(2, result.Stayed);
        Assert.Equal(PlanMoveAssessment.ShorterRetention, Assert.Single(result.StayedBecause).Code);
        foreach (var tenant in tenants)
        {
            Assert.Single(await SubscriptionsOfAsync(tenant));
            Assert.Single(await PeriodsOfAsync(tenant));
        }
    }

    [Fact]
    public async Task AnotherPlansSubscribersAreNeverTouched()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());
        var (otherOld, _) = await PlansAsync(new Terms(), new Terms());
        var bystander = await SubscribeAsync(otherOld);

        await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(otherOld.Id, (await OpenAsync(bystander)).PlanId);
        Assert.Single(await SubscriptionsOfAsync(bystander));
    }

    // ---- previewing and repeating ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePreviewCountsWhatAMoveWouldDoAndChangesNothing()
    {
        var (old, newer) = await PlansAsync(new Terms(Monthly: 10m, Annual: 100m), new Terms(Monthly: 9m, Annual: 130m));
        var monthly = await SubscribeAsync(old, term: 1);
        var annual = await SubscribeAsync(old, term: 12);
        var suspended = await SubscribeAsync(old, status: SubscriptionStatus.Suspended);

        var preview = await Mover().PreviewAsync(old, newer, CancellationToken.None);

        Assert.Equal(3, preview.CurrentSubscribers);
        Assert.Equal(1, preview.CanMove);
        Assert.Equal(2, preview.WouldStay.Sum(r => r.Count));
        Assert.Contains(preview.WouldStay, r => r.Code == PlanMoveAssessment.PriceHigher && r.Count == 1);
        Assert.Contains(preview.WouldStay, r => r.Code == PlanMoveAssessment.NotInGoodStanding && r.Count == 1);
        foreach (var tenant in new[] { monthly, annual, suspended })
        {
            Assert.Single(await SubscriptionsOfAsync(tenant));         // nothing was done
            Assert.Single(await PeriodsOfAsync(tenant));
        }
    }

    [Fact]
    public async Task ThePreviewAndTheMoveAgree()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms(Users: 2));
        await SubscribeAsync(old);
        await SubscribeAsync(old, status: SubscriptionStatus.Suspended);

        var preview = await Mover().PreviewAsync(old, newer, CancellationToken.None);
        var result = await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(preview.CanMove, result.Moved);
        Assert.Equal(preview.CurrentSubscribers - preview.CanMove, result.Stayed);
    }

    [Fact]
    public async Task RunningItAgainMovesNobodyBecauseNobodyIsLeftOnTheOldVersion()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());
        var tenant = await SubscribeAsync(old);
        await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        var again = await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(0, again.Moved);
        Assert.Equal(0, again.Stayed);
        Assert.Equal(2, (await SubscriptionsOfAsync(tenant)).Count);
    }

    [Fact]
    public async Task ThePreviewOfProposedTermsThatAreNotSavedYetWorksAndSavesNothing()
    {
        var name = UniqueName();
        var old = BuildPlan(name, new Terms());
        await using (var context = new ApiDbContext(postgres.Options))
        {
            context.Set<Plan>().Add(old);
            await context.SaveChangesAsync();
        }

        await SubscribeAsync(old);
        var proposed = BuildPlan(name, new Terms(Monthly: 8m));         // never saved

        var preview = await Mover().PreviewAsync(await LoadAsync(old.Id), proposed, CancellationToken.None);

        Assert.Equal(1, preview.CanMove);
    }

    [Fact]
    public async Task AnOrganizationWithNothingSubscribedToThePlanGivesAnEmptyAnswer()
    {
        var (old, newer) = await PlansAsync(new Terms(), new Terms());

        var preview = await Mover().PreviewAsync(old, newer, CancellationToken.None);
        var result = await Mover().MoveAsync(old, newer, null, CancellationToken.None);

        Assert.Equal(0, preview.CurrentSubscribers);
        Assert.Equal(0, result.Moved);
        Assert.Empty(result.StayedBecause);
    }
}
