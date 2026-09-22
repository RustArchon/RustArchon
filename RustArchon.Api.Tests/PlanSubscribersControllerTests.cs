// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Mapping;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The endpoints for moving a plan's subscribers to its newer version: what a supersede would do to them (asked before, with the proposed terms), what a
/// move would do, and the move itself - refused unless the plan was replaced and its latest version is on offer, and moving to the <em>latest</em> version
/// however many times the plan was replaced.
/// </summary>
public class PlanSubscribersControllerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _admin = Guid.NewGuid();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string UniqueName() => "Endpoint " + Guid.NewGuid().ToString("N")[..10];

    private PlanRepository Repository() => new(new ApiDbContext(postgres.Options, null));

    private PlansController Plans() =>
        new(Repository(), new MapperConfiguration(cfg => cfg.AddProfile<PlanMappingProfile>(), NullLoggerFactory.Instance).CreateMapper());

    private PlanSubscribersController Controller()
    {
        var user = new Mock<IUserContext>();
        user.Setup(u => u.GetCurrentUserIdAsync()).ReturnsAsync(_admin);
        return new PlanSubscribersController(
            Repository(), new PlanSubscriberMover(new ApiDbContext(postgres.Options), new FixedClock(Now), NullLogger<PlanSubscriberMover>.Instance), user.Object);
    }

    private static PlanPriceDto Monthly(decimal amount) => new() { TermMonths = BillingTerms.Monthly, BaseAmount = amount, IncludedUnits = 3, UnitAmount = 0m };

    private async Task<PlanDto> CreateAsync(string name, decimal amount = 10m)
    {
        var created = await Plans().Create(new CreatePlanDto
        {
            Name = name, ColorCode = "#123456", PricingModel = PricingModel.Flat, RetentionHistory = 30, MaximumServers = 3, MaximumUsers = 5, Active = true, Prices = [Monthly(amount)]
        });
        return Assert.IsType<PlanDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
    }

    private static SupersedePlanDto Terms(decimal amount, int retention = 30) => new()
    {
        ColorCode = "#123456", PricingModel = PricingModel.Flat, RetentionHistory = retention, MaximumServers = 3, MaximumUsers = 5, Prices = [Monthly(amount)]
    };

    private async Task<PlanDto> SupersedeAsync(Guid id, decimal amount, int retention = 30) =>
        Assert.IsType<PlanDto>(Assert.IsType<OkObjectResult>((await Plans().Supersede(id, Terms(amount, retention))).Result).Value);

    /// <summary>An organization on the plan, half way through a monthly period.</summary>
    private async Task<Guid> SubscribeAsync(Guid planId)
    {
        var tenantId = Guid.NewGuid();
        await using var context = new ApiDbContext(postgres.Options);
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Endpoint tenant {tenantId}", IsActive = true });
        var subscription = new Subscription { TenantId = tenantId, PlanId = planId, StartDate = Now.AddMonths(-2) };
        context.Set<Subscription>().Add(subscription);
        await context.SaveChangesAsync();
        context.Set<SubscriptionPeriod>().Add(new SubscriptionPeriod
        {
            SubscriptionId = subscription.Id, TermMonths = 1, Quantity = 3, PeriodStart = Now.AddDays(-15), PeriodEnd = Now.AddDays(15),
            StartDate = Now.AddDays(-15), EndDate = Now.AddDays(15), EarnedAmount = 10m
        });
        await context.SaveChangesAsync();
        return tenantId;
    }

    private async Task<Guid> OpenPlanOfAsync(Guid tenantId)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return (await context.Set<Subscription>().IgnoreQueryFilters().SingleAsync(s => s.TenantId == tenantId && s.EndDate == null)).PlanId;
    }

    private static T Value<T>(ActionResult<T> result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    // ---- gating -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryActionIsBehindThePlanManagementPolicy()
    {
        var policy = typeof(PlanSubscribersController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Policy;

        Assert.Equal("ManagePlans", policy);
        foreach (var method in typeof(PlanSubscribersController).GetMethods().Where(m => m.DeclaringType == typeof(PlanSubscribersController) && m.IsPublic))
        {
            Assert.Empty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true));
        }
    }

    // ---- previewing a supersede -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASupersedeCanBePreviewedWithTheProposedTermsBeforeAnythingIsCreated()
    {
        var name = UniqueName();
        var plan = await CreateAsync(name, 10m);
        await SubscribeAsync(plan.Id);
        await SubscribeAsync(plan.Id);

        var better = Value(await Controller().PreviewSupersede(plan.Id, Terms(8m), CancellationToken.None));
        var worse = Value(await Controller().PreviewSupersede(plan.Id, Terms(12m), CancellationToken.None));

        Assert.Equal(2, better.CurrentSubscribers);
        Assert.Equal(2, better.CanMove);
        Assert.Equal(2, worse.CurrentSubscribers);
        Assert.Equal(0, worse.CanMove);
        Assert.Equal(PlanMoveAssessment.PriceHigher, Assert.Single(worse.WouldStay).Code);
        await using var context = new ApiDbContext(postgres.Options);
        Assert.Equal(1, await context.Set<Plan>().CountAsync(p => p.Name == name));         // a preview creates nothing
    }

    [Fact]
    public async Task PreviewingASupersedeOfAPlanThatDoesNotExistIsNotFound()
    {
        Assert.IsType<NotFoundResult>((await Controller().PreviewSupersede(Guid.NewGuid(), Terms(5m), CancellationToken.None)).Result);
    }

    // ---- moving after the plan was replaced -------------------------------------------------------------------------------------

    [Fact]
    public async Task AReplacedPlansSubscribersAreMovedToItsNewerVersionAndTheAnswerSaysHowMany()
    {
        var plan = await CreateAsync(UniqueName(), 10m);
        var one = await SubscribeAsync(plan.Id);
        var two = await SubscribeAsync(plan.Id);
        var newer = await SupersedeAsync(plan.Id, 8m);

        var result = Value(await Controller().MoveSubscribers(plan.Id, CancellationToken.None));

        Assert.Equal(2, result.Moved);
        Assert.Equal(0, result.Stayed);
        Assert.Equal(newer.Id, await OpenPlanOfAsync(one));
        Assert.Equal(newer.Id, await OpenPlanOfAsync(two));
    }

    [Fact]
    public async Task TheMoveRecordsWhichAdministratorDidIt()
    {
        var plan = await CreateAsync(UniqueName());
        var tenant = await SubscribeAsync(plan.Id);
        await SupersedeAsync(plan.Id, 9m);

        await Controller().MoveSubscribers(plan.Id, CancellationToken.None);

        await using var context = new ApiDbContext(postgres.Options);
        var open = await context.Set<Subscription>().IgnoreQueryFilters().SingleAsync(s => s.TenantId == tenant && s.EndDate == null);
        Assert.Equal(_admin, open.PlanChangedById);
    }

    [Fact]
    public async Task ThePreviewOfAMoveSaysWhatItWouldDoAndDoesNothing()
    {
        var plan = await CreateAsync(UniqueName(), 10m);
        var tenant = await SubscribeAsync(plan.Id);
        await SupersedeAsync(plan.Id, 8m);

        var preview = Value(await Controller().PreviewMove(plan.Id, CancellationToken.None));

        Assert.Equal(1, preview.CanMove);
        Assert.Equal(plan.Id, await OpenPlanOfAsync(tenant));
    }

    [Fact]
    public async Task ASubscriberTheNewerVersionIsWorseForStaysAndTheAnswerSaysWhy()
    {
        var plan = await CreateAsync(UniqueName(), 10m);
        var tenant = await SubscribeAsync(plan.Id);
        await SupersedeAsync(plan.Id, 15m);          // dearer: a deliberate price rise is a different feature, not something a move does

        var result = Value(await Controller().MoveSubscribers(plan.Id, CancellationToken.None));

        Assert.Equal(0, result.Moved);
        Assert.Equal(PlanMoveAssessment.PriceHigher, Assert.Single(result.StayedBecause).Code);
        Assert.Equal(plan.Id, await OpenPlanOfAsync(tenant));
    }

    [Fact]
    public async Task ThoseOnAnOlderVersionAreMovedStraightToTheLatestNotJustTheNext()
    {
        var v1 = await CreateAsync(UniqueName(), 10m);
        var onV1 = await SubscribeAsync(v1.Id);
        var v2 = await SupersedeAsync(v1.Id, 9m);
        var v3 = await SupersedeAsync(v2.Id, 8m);

        var result = Value(await Controller().MoveSubscribers(v1.Id, CancellationToken.None));

        Assert.Equal(1, result.Moved);
        Assert.Equal(v3.Id, await OpenPlanOfAsync(onV1));
    }

    [Fact]
    public async Task ARepeatedMoveFindsNobodyLeftToMove()
    {
        var plan = await CreateAsync(UniqueName());
        await SubscribeAsync(plan.Id);
        await SupersedeAsync(plan.Id, 9m);
        await Controller().MoveSubscribers(plan.Id, CancellationToken.None);

        var again = Value(await Controller().MoveSubscribers(plan.Id, CancellationToken.None));

        Assert.Equal(0, again.Moved);
    }

    // ---- when it is refused -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APlanThatWasNeverReplacedHasNowhereToMoveItsSubscribers()
    {
        var plan = await CreateAsync(UniqueName());
        var tenant = await SubscribeAsync(plan.Id);

        Assert.IsType<ConflictObjectResult>((await Controller().MoveSubscribers(plan.Id, CancellationToken.None)).Result);
        Assert.IsType<ConflictObjectResult>((await Controller().PreviewMove(plan.Id, CancellationToken.None)).Result);
        Assert.Equal(plan.Id, await OpenPlanOfAsync(tenant));
    }

    [Fact]
    public async Task NobodyIsMovedOntoAVersionThatIsNotOnOffer()
    {
        var plan = await CreateAsync(UniqueName());
        var tenant = await SubscribeAsync(plan.Id);
        var newer = await SupersedeAsync(plan.Id, 8m);
        await Plans().SetActive(newer.Id, new SetPlanActiveDto { Active = false });

        Assert.IsType<ConflictObjectResult>((await Controller().MoveSubscribers(plan.Id, CancellationToken.None)).Result);
        Assert.Equal(plan.Id, await OpenPlanOfAsync(tenant));
    }

    [Fact]
    public async Task APlanThatDoesNotExistIsNotFound()
    {
        Assert.IsType<NotFoundResult>((await Controller().MoveSubscribers(Guid.NewGuid(), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await Controller().PreviewMove(Guid.NewGuid(), CancellationToken.None)).Result);
    }
}
