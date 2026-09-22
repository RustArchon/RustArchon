// Copyright ©2026 Scott Blomfield

using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Mapping;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The three gates on applying third-party plugin updates automatically - the plan offers it, the server opted in, and the server is outside
/// its days-before-wipe window - against a real Postgres: which gate wins when several are closed, that a missing or ended subscription can
/// only close the gate, that another organization's plan never opens mine, and the settings endpoint on top of them.
/// </summary>
public class ThirdPartyPluginUpdateGateTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // A Monday, well clear of any wipe: 2026-10-01 18:00 UTC is 10 days away.
    private static readonly DateTimeOffset Calm = new(2026, 9, 21, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Wipe = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Org(Guid TenantId, ApiDbContext Context);

    /// <summary>An organization, optionally on a plan that does or does not offer the feature (<c>null</c> = no subscription at all).</summary>
    private async Task<Org> OrgAsync(bool? planOffers, bool endedSubscription = false)
    {
        var tenantId = Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Gate tenant {tenantId}", IsActive = true });
        if (planOffers is { } offers)
        {
            var plan = await PlanAsync(context, offers);
            context.Set<Subscription>().Add(new Subscription
            {
                TenantId = tenantId, PlanId = plan.Id, Plan = plan, StartDate = Calm.AddYears(-1), EndDate = endedSubscription ? Calm.AddDays(-1) : null
            });
        }

        await context.SaveChangesAsync();
        return new Org(tenantId, context);
    }

    private static Task<Plan> PlanAsync(ApiDbContext context, bool offers)
    {
        var plan = new Plan { Name = $"Gate plan {Guid.NewGuid()}", Active = true, OffersThirdPartyPluginUpdates = offers };
        plan.Prices.Add(new PlanPrice { TermMonths = 1, UnitAmount = 0m, IncludedUnits = 1, Currency = "USD" });
        context.Set<Plan>().Add(plan);
        return Task.FromResult(plan);
    }

    private static async Task<RustServer> ServerAsync(Org org, bool optedIn, int holdDays = 7)
    {
        var server = new RustServer
        {
            Id = Guid.NewGuid(), TenantId = org.TenantId, Name = "Gate " + Guid.NewGuid().ToString("N")[..6], Host = "192.0.2.80", Port = 28016,
            RconPassword = "x", ThirdPartyPluginUpdatesEnabled = optedIn, ThirdPartyPluginUpdateHoldDays = holdDays
        };
        org.Context.Set<RustServer>().Add(server);
        await org.Context.SaveChangesAsync();
        return server;
    }

    private static Task<ThirdPartyUpdateGateResult> Gate(Org org, RustServer server, DateTimeOffset now) =>
        new ThirdPartyPluginUpdateGate(org.Context).EvaluateAsync(server, now);

    // ---- the gates -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AllThreeGatesPassingOpensIt()
    {
        var org = await OrgAsync(planOffers: true);
        var server = await ServerAsync(org, optedIn: true);

        var result = await Gate(org, server, Calm);

        Assert.True(result.IsOpen);
        Assert.Equal(ThirdPartyUpdateGateState.Open, result.State);
        Assert.Null(result.HeldUntilUtc);
        Assert.Equal(Wipe, result.NextWipeUtc);
    }

    [Fact]
    public async Task ThePlanGateClosesItWhateverTheServerSays()
    {
        var org = await OrgAsync(planOffers: false);
        var server = await ServerAsync(org, optedIn: true);

        var result = await Gate(org, server, Calm);

        Assert.Equal(ThirdPartyUpdateGateState.PlanDoesNotOffer, result.State);
        Assert.False(result.PlanOffers);
        Assert.True(result.OptedIn);
    }

    [Fact]
    public async Task AnOrganizationWithNoSubscriptionCanOnlyBeClosed()
    {
        var org = await OrgAsync(planOffers: null);
        var server = await ServerAsync(org, optedIn: true);

        Assert.Equal(ThirdPartyUpdateGateState.PlanDoesNotOffer, (await Gate(org, server, Calm)).State);
    }

    [Fact]
    public async Task APlanTheOrganizationHasLeftDoesNotCount()
    {
        var org = await OrgAsync(planOffers: true, endedSubscription: true);
        var server = await ServerAsync(org, optedIn: true);

        Assert.Equal(ThirdPartyUpdateGateState.PlanDoesNotOffer, (await Gate(org, server, Calm)).State);
    }

    [Fact]
    public async Task AnotherOrganizationsPlanNeverOpensMine()
    {
        var mine = await OrgAsync(planOffers: false);
        await OrgAsync(planOffers: true);
        var server = await ServerAsync(mine, optedIn: true);

        Assert.Equal(ThirdPartyUpdateGateState.PlanDoesNotOffer, (await Gate(mine, server, Calm)).State);
    }

    [Fact]
    public async Task TheServerGateIsAnExplicitOptIn()
    {
        var org = await OrgAsync(planOffers: true);
        var server = await ServerAsync(org, optedIn: false);

        Assert.Equal(ThirdPartyUpdateGateState.NotOptedIn, (await Gate(org, server, Calm)).State);
        Assert.False(new RustServer().ThirdPartyPluginUpdatesEnabled);          // a new server takes an explicit choice
    }

    [Fact]
    public async Task InsideTheWindowBeforeTheWipeTheGateIsHeldUntilTheWipe()
    {
        var org = await OrgAsync(planOffers: true);
        var server = await ServerAsync(org, optedIn: true, holdDays: 7);

        var result = await Gate(org, server, Wipe.AddDays(-3));

        Assert.Equal(ThirdPartyUpdateGateState.HeldForWipe, result.State);
        Assert.Equal(Wipe, result.HeldUntilUtc);
    }

    [Fact]
    public async Task JustOutsideTheWindowItIsOpenAndAtTheWipeItOpensAgain()
    {
        var org = await OrgAsync(planOffers: true);
        var server = await ServerAsync(org, optedIn: true, holdDays: 7);

        Assert.True((await Gate(org, server, Wipe.AddDays(-7).AddSeconds(-1))).IsOpen);
        Assert.Equal(ThirdPartyUpdateGateState.HeldForWipe, (await Gate(org, server, Wipe.AddDays(-7))).State);
        Assert.True((await Gate(org, server, Wipe)).IsOpen);
    }

    [Fact]
    public async Task AHoldOfZeroTakesEveryUpdateRightUpToWipeDay()
    {
        var org = await OrgAsync(planOffers: true);
        var server = await ServerAsync(org, optedIn: true, holdDays: 0);

        Assert.True((await Gate(org, server, Wipe.AddSeconds(-1))).IsOpen);
        Assert.True((await Gate(org, server, Wipe.AddDays(-1))).IsOpen);
    }

    [Fact]
    public async Task WhenSeveralGatesAreClosedThePlanIsReportedThenTheOptInThenTheWindow()
    {
        var inWindow = Wipe.AddDays(-2);

        var noPlan = await OrgAsync(planOffers: false);
        Assert.Equal(ThirdPartyUpdateGateState.PlanDoesNotOffer, (await Gate(noPlan, await ServerAsync(noPlan, optedIn: false), inWindow)).State);

        var noOptIn = await OrgAsync(planOffers: true);
        var notOptedIn = await Gate(noOptIn, await ServerAsync(noOptIn, optedIn: false), inWindow);
        Assert.Equal(ThirdPartyUpdateGateState.NotOptedIn, notOptedIn.State);
        Assert.Equal(Wipe, notOptedIn.HeldUntilUtc);        // the window is still reported, so the Panel can say when it lifts
    }

    // ---- the settings endpoint -----------------------------------------------------------------------------------

    private static ServerThirdPartyUpdateSettingsController Controller(Org org, TimeProvider clock) =>
        new(new RustServerRepository(org.Context), new ThirdPartyPluginUpdateGate(org.Context), clock);

    private static T Value<T>(ActionResult<T> result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task TheEndpointReportsWhereTheServerStands()
    {
        var org = await OrgAsync(planOffers: true);
        var server = await ServerAsync(org, optedIn: true, holdDays: 14);

        var dto = Value(await Controller(org, new FixedClock(Wipe.AddDays(-10))).Get(server.Id));

        Assert.True(dto.PlanOffers);
        Assert.True(dto.Enabled);
        Assert.Equal(14, dto.HoldDays);
        Assert.Equal(MonthlyWipeSchedule.MaxHoldDays, dto.MaxHoldDays);
        Assert.Equal(Wipe, dto.NextWipeUtc);
        Assert.Equal(Wipe, dto.HeldUntilUtc);
        Assert.Equal("held_for_wipe", dto.State);
    }

    [Theory]
    [InlineData(false, false, "plan_does_not_offer")]
    [InlineData(true, false, "not_opted_in")]
    [InlineData(true, true, "open")]
    public async Task TheStateCodeNamesTheFirstClosedGate(bool planOffers, bool optedIn, string expected)
    {
        var org = await OrgAsync(planOffers);
        var server = await ServerAsync(org, optedIn);

        Assert.Equal(expected, Value(await Controller(org, new FixedClock(Calm)).Get(server.Id)).State);
    }

    [Fact]
    public async Task ASavedChoiceIsWhatTheNextReadReturns()
    {
        var org = await OrgAsync(planOffers: true);
        var server = await ServerAsync(org, optedIn: false);
        var controller = Controller(org, new FixedClock(Calm));

        var saved = Value(await controller.Put(server.Id, new UpdateThirdPartyPluginUpdateSettingsDto { Enabled = true, HoldDays = 3 }));
        var read = Value(await controller.Get(server.Id));

        Assert.True(saved.Enabled);
        Assert.Equal(3, saved.HoldDays);
        Assert.Equal("open", saved.State);
        Assert.True(read.Enabled);
        Assert.Equal(3, read.HoldDays);
    }

    [Fact]
    public async Task OptingInIsRefusedWhenThePlanDoesNotOfferItAndNothingIsSaved()
    {
        var org = await OrgAsync(planOffers: false);
        var server = await ServerAsync(org, optedIn: false, holdDays: 7);
        var controller = Controller(org, new FixedClock(Calm));

        var result = await controller.Put(server.Id, new UpdateThirdPartyPluginUpdateSettingsDto { Enabled = true, HoldDays = 2 });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await using var fresh = new ApiDbContext(postgres.Options, new FixedTenantContext(org.TenantId));
        var stored = await fresh.Set<RustServer>().SingleAsync(s => s.Id == server.Id);
        Assert.False(stored.ThirdPartyPluginUpdatesEnabled);
        Assert.Equal(7, stored.ThirdPartyPluginUpdateHoldDays);
    }

    [Fact]
    public async Task OptingOutIsAlwaysAllowedEvenAfterAPlanStoppedOfferingIt()
    {
        var org = await OrgAsync(planOffers: false);
        var server = await ServerAsync(org, optedIn: true);       // opted in back when the plan offered it

        var saved = Value(await Controller(org, new FixedClock(Calm)).Put(server.Id, new UpdateThirdPartyPluginUpdateSettingsDto { Enabled = false, HoldDays = 7 }));

        Assert.False(saved.Enabled);
    }

    [Fact]
    public async Task AnotherOrganizationsServerIsNotFound()
    {
        var mine = await OrgAsync(planOffers: true);
        var theirs = await OrgAsync(planOffers: true);
        var theirServer = await ServerAsync(theirs, optedIn: false);
        var controller = Controller(mine, new FixedClock(Calm));

        Assert.IsType<NotFoundResult>((await controller.Get(theirServer.Id)).Result);
        Assert.IsType<NotFoundResult>((await controller.Put(theirServer.Id, new UpdateThirdPartyPluginUpdateSettingsDto { Enabled = false, HoldDays = 7 })).Result);
    }

    [Fact]
    public async Task ReadingNeedsTheServerReadPermissionAndSavingNeedsTheServerUpdatePermission()
    {
        await Task.CompletedTask;
        string PermissionOn(string action) => typeof(ServerThirdPartyUpdateSettingsController).GetMethod(action)!
            .GetCustomAttributes(typeof(JumpStart.Authorization.RequirePermissionAttribute), inherit: true)
            .Cast<JumpStart.Authorization.RequirePermissionAttribute>().Single().Permission;

        Assert.Equal(PermissionCatalog.ServerGet, PermissionOn(nameof(ServerThirdPartyUpdateSettingsController.Get)));
        Assert.Equal(PermissionCatalog.ServerUpdate, PermissionOn(nameof(ServerThirdPartyUpdateSettingsController.Put)));
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(false, 7, true)]
    [InlineData(true, 21, true)]
    [InlineData(true, 22, false)]
    [InlineData(true, -1, false)]
    [InlineData(null, 7, false)]
    [InlineData(true, null, false)]
    public void ABodyMustSayBothThingsAndKeepTheHoldInRange(bool? enabled, int? holdDays, bool valid)
    {
        var dto = new UpdateThirdPartyPluginUpdateSettingsDto { Enabled = enabled, HoldDays = holdDays };

        Assert.Equal(valid, Validator.TryValidateObject(dto, new ValidationContext(dto), null, validateAllProperties: true));
    }

    [Fact]
    public async Task AnOrdinaryServerEditCanNeitherSetNorResetTheSettings()
    {
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile<RustServerMappingProfile>(), NullLoggerFactory.Instance).CreateMapper();
        var entity = new RustServer { Name = "Before", Host = "192.0.2.1", Port = 28016, RconPassword = "x", ThirdPartyPluginUpdatesEnabled = true, ThirdPartyPluginUpdateHoldDays = 3 };

        mapper.Map(new UpdateRustServerDto { Name = "After", Host = "192.0.2.2", Port = 28016 }, entity);

        Assert.Equal("After", entity.Name);
        Assert.True(entity.ThirdPartyPluginUpdatesEnabled);
        Assert.Equal(3, entity.ThirdPartyPluginUpdateHoldDays);

        var created = mapper.Map<RustServer>(new CreateRustServerDto { Name = "New", Host = "192.0.2.3", Port = 28016, RconPassword = "x" });
        Assert.False(created.ThirdPartyPluginUpdatesEnabled);
        Assert.Equal(MonthlyWipeSchedule.DefaultHoldDays, created.ThirdPartyPluginUpdateHoldDays);
        await Task.CompletedTask;
    }

    // ---- the plan flag, carried through the plan endpoints ---------------------------------------------------------

    private PlansController Plans() =>
        new(new PlanRepository(new ApiDbContext(postgres.Options, null)),
            new MapperConfiguration(cfg => cfg.AddProfile<PlanMappingProfile>(), NullLoggerFactory.Instance).CreateMapper());

    private static PlanPriceDto Monthly() => new() { TermMonths = BillingTerms.Monthly, BaseAmount = 0m, IncludedUnits = 1, UnitAmount = 0m };

    [Fact]
    public async Task APlanIsCreatedEditedAndSupersededWithTheFlagAsGiven()
    {
        var controller = Plans();
        var name = $"Third party {Guid.NewGuid():N}"[..24];

        var created = Assert.IsType<PlanDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(new CreatePlanDto
            {
                Name = name, ColorCode = "#888888", PricingModel = PricingModel.Flat, MaximumServers = 1, MaximumUsers = 1, Active = true,
                OffersThirdPartyPluginUpdates = true, Prices = [Monthly()]
            })).Result).Value);
        Assert.True(created.OffersThirdPartyPluginUpdates);

        var edited = Assert.IsType<PlanDto>(Assert.IsType<OkObjectResult>(
            (await controller.Update(created.Id, new UpdatePlanDto
            {
                Id = created.Id, ColorCode = "#888888", PricingModel = PricingModel.Flat, MaximumServers = 1, MaximumUsers = 1, Active = true,
                OffersThirdPartyPluginUpdates = false, Prices = [Monthly()]
            })).Result).Value);
        Assert.False(edited.OffersThirdPartyPluginUpdates);

        var superseded = Assert.IsType<PlanDto>(Assert.IsType<OkObjectResult>(
            (await controller.Supersede(created.Id, new SupersedePlanDto
            {
                ColorCode = "#888888", PricingModel = PricingModel.Flat, MaximumServers = 1, MaximumUsers = 1,
                OffersThirdPartyPluginUpdates = true, Prices = [Monthly()]
            })).Result).Value);
        Assert.True(superseded.OffersThirdPartyPluginUpdates);
        Assert.NotEqual(created.Id, superseded.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePublicPlanTheMarketingSiteReadsSaysWhetherThePlanOffersIt(bool offers)
    {
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile<PlanMappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

        var publicPlan = mapper.Map<PublicPlanDto>(new Plan { Name = "Public", OffersThirdPartyPluginUpdates = offers });

        Assert.Equal(offers, publicPlan.OffersThirdPartyPluginUpdates);
    }

    [Fact]
    public void APlanDoesNotOfferItUnlessAnAdminSaysSo()
    {
        Assert.False(new Plan().OffersThirdPartyPluginUpdates);
        Assert.False(new CreatePlanDto().OffersThirdPartyPluginUpdates);
    }
}
