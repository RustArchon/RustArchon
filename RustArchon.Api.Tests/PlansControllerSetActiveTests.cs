// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using JumpStart.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Mapping;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Switching a plan on or off on its own: it is not one of the terms a subscriber signed up under, so it must not need superseding (which would leave
/// an identical copy of the plan behind) - and it must change nothing but the flag, on a plan with subscribers or without.
/// </summary>
public class PlansControllerSetActiveTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static IMapper Mapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<PlanMappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private PlansController Controller() => new(new PlanRepository(new ApiDbContext(postgres.Options, null)), Mapper());

    private static string UniqueName() => "Toggle " + Guid.NewGuid().ToString("N")[..10];

    private static PlanPriceDto Monthly(decimal amount = 5m) => new() { TermMonths = BillingTerms.Monthly, BaseAmount = amount, IncludedUnits = 3, UnitAmount = 0m };

    private async Task<PlanDto> CreateAsync(string name, bool active = true, bool offers = true)
    {
        var created = await Controller().Create(new CreatePlanDto
        {
            Name = name, ColorCode = "#123456", PricingModel = PricingModel.Flat, RetentionHistory = 45, HasRoles = true, OnePerOwner = false,
            OffersThirdPartyPluginUpdates = offers, MaximumServers = 3, MaximumUsers = 7, Active = active, Prices = [Monthly()]
        });
        return Assert.IsType<PlanDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
    }

    /// <summary>Puts an organization on the plan, so it has a subscriber and could not be edited in place.</summary>
    private async Task SubscribeAsync(Guid planId)
    {
        await using var context = new ApiDbContext(postgres.Options);
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Toggle tenant {tenantId}", IsActive = true });
        context.Set<Subscription>().Add(new Subscription { TenantId = tenantId, PlanId = planId, StartDate = DateTimeOffset.UtcNow.AddMonths(-1) });
        await context.SaveChangesAsync();
    }

    private async Task<int> RowsNamedAsync(string name)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.Set<Plan>().CountAsync(p => p.Name == name);
    }

    private static PlanDto Ok(ActionResult<PlanDto> result) => Assert.IsType<PlanDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task DeactivatingAPlanWithSubscribersChangesTheFlagAndCreatesNoCopy()
    {
        var name = UniqueName();
        var plan = await CreateAsync(name);
        await SubscribeAsync(plan.Id);

        var after = Ok(await Controller().SetActive(plan.Id, new SetPlanActiveDto { Active = false }));

        Assert.False(after.Active);
        Assert.Equal(plan.Id, after.Id);                        // the same plan, not a new one
        Assert.Equal(1, await RowsNamedAsync(name));            // and nothing left behind
        Assert.Equal(1, after.SubscriberCount);                 // its subscriber is still on it
    }

    [Fact]
    public async Task OnlyTheFlagChangesEverythingElseAboutThePlanIsAsItWas()
    {
        var plan = await CreateAsync(UniqueName());
        await SubscribeAsync(plan.Id);

        var after = Ok(await Controller().SetActive(plan.Id, new SetPlanActiveDto { Active = false }));

        Assert.Equal(plan.ColorCode, after.ColorCode);
        Assert.Equal(plan.PricingModel, after.PricingModel);
        Assert.Equal(plan.RetentionHistory, after.RetentionHistory);
        Assert.Equal(plan.HasRoles, after.HasRoles);
        Assert.Equal(plan.OffersThirdPartyPluginUpdates, after.OffersThirdPartyPluginUpdates);
        Assert.Equal(plan.OnePerOwner, after.OnePerOwner);
        Assert.Equal(plan.MaximumServers, after.MaximumServers);
        Assert.Equal(plan.MaximumUsers, after.MaximumUsers);
        var price = Assert.Single(after.Prices);
        Assert.Equal(Monthly().BaseAmount, price.BaseAmount);
        Assert.Equal(3, price.IncludedUnits);
    }

    [Fact]
    public async Task ThePlanASubscriberIsOnIsTheSameRowBeforeAndAfter()
    {
        var plan = await CreateAsync(UniqueName());
        await SubscribeAsync(plan.Id);

        await Controller().SetActive(plan.Id, new SetPlanActiveDto { Active = false });

        await using var context = new ApiDbContext(postgres.Options);
        Assert.Equal(1, await context.Set<Subscription>().IgnoreQueryFilters().CountAsync(s => s.PlanId == plan.Id && s.EndDate == null));
    }

    [Fact]
    public async Task ADeactivatedPlanCanBeSwitchedBackOn()
    {
        var plan = await CreateAsync(UniqueName());
        await Controller().SetActive(plan.Id, new SetPlanActiveDto { Active = false });

        var after = Ok(await Controller().SetActive(plan.Id, new SetPlanActiveDto { Active = true }));

        Assert.True(after.Active);
    }

    [Fact]
    public async Task SwitchingOnDeactivatesTheOtherActivePlanOfTheSameNameSoOnlyOneIsActive()
    {
        var name = UniqueName();
        var older = await CreateAsync(name, active: true);
        await Controller().SetActive(older.Id, new SetPlanActiveDto { Active = false });
        var newer = await CreateAsync(name, active: true);

        Ok(await Controller().SetActive(older.Id, new SetPlanActiveDto { Active = true }));

        await using var context = new ApiDbContext(postgres.Options);
        var rows = await context.Set<Plan>().Where(p => p.Name == name).ToDictionaryAsync(p => p.Id, p => p.Active);
        Assert.True(rows[older.Id]);
        Assert.False(rows[newer.Id]);
    }

    [Fact]
    public async Task SwitchingOnePlanOffLeavesOtherPlansAlone()
    {
        var mine = await CreateAsync(UniqueName());
        var other = await CreateAsync(UniqueName());

        await Controller().SetActive(mine.Id, new SetPlanActiveDto { Active = false });

        await using var context = new ApiDbContext(postgres.Options);
        Assert.True((await context.Set<Plan>().SingleAsync(p => p.Id == other.Id)).Active);
    }

    [Fact]
    public async Task SettingItToWhatItAlreadyIsChangesNothing()
    {
        var plan = await CreateAsync(UniqueName());

        var after = Ok(await Controller().SetActive(plan.Id, new SetPlanActiveDto { Active = true }));

        Assert.True(after.Active);
        Assert.Equal(plan.Prices.Count, after.Prices.Count);
    }

    [Fact]
    public async Task APlanThatDoesNotExistIsNotFound()
    {
        var result = await Controller().SetActive(Guid.NewGuid(), new SetPlanActiveDto { Active = false });

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public void TheEndpointIsGatedLikeEveryOtherPlanAction()
    {
        var policy = typeof(PlansController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Policy;
        var method = typeof(PlansController).GetMethod(nameof(PlansController.SetActive))!;

        Assert.Equal("ManagePlans", policy);
        Assert.Empty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true));
    }
}
