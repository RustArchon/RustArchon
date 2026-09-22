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
/// Which plan replaced which. Superseding records the link; a replaced plan cannot be reactivated, edited into a fork, or superseded again, while one
/// that was only deactivated can be reactivated; deleting the newer version frees the older one; the chain can be followed to its newest version; and the
/// migration's reconstruction of the history that existed before the link did follows its stated rule.
/// </summary>
public class PlanVersionLinkTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static IMapper Mapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<PlanMappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private PlansController Controller() => new(new PlanRepository(new ApiDbContext(postgres.Options, null)), Mapper());

    private PlanRepository Repository() => new(new ApiDbContext(postgres.Options, null));

    private static string UniqueName() => "Version " + Guid.NewGuid().ToString("N")[..10];

    private static PlanPriceDto Monthly(decimal amount = 5m) => new() { TermMonths = BillingTerms.Monthly, BaseAmount = amount, IncludedUnits = 3, UnitAmount = 0m };

    private async Task<PlanDto> CreateAsync(string name, bool active = true)
    {
        var created = await Controller().Create(new CreatePlanDto
        {
            Name = name, ColorCode = "#123456", PricingModel = PricingModel.Flat, RetentionHistory = 45, MaximumServers = 3, MaximumUsers = 7, Active = active,
            Prices = [Monthly()]
        });
        return Assert.IsType<PlanDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value);
    }

    private static SupersedePlanDto Terms(decimal amount = 9m) => new()
    {
        ColorCode = "#123456", PricingModel = PricingModel.Flat, RetentionHistory = 90, MaximumServers = 3, MaximumUsers = 7, Prices = [Monthly(amount)]
    };

    private async Task<PlanDto> SupersedeAsync(Guid id, decimal amount = 9m) =>
        Assert.IsType<PlanDto>(Assert.IsType<OkObjectResult>((await Controller().Supersede(id, Terms(amount))).Result).Value);

    private async Task<Plan> RowAsync(Guid id)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.Set<Plan>().AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private async Task<int> RowsNamedAsync(string name)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.Set<Plan>().CountAsync(p => p.Name == name);
    }

    private async Task SubscribeAsync(Guid planId)
    {
        await using var context = new ApiDbContext(postgres.Options);
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Version tenant {tenantId}", IsActive = true });
        context.Set<Subscription>().Add(new Subscription { TenantId = tenantId, PlanId = planId, StartDate = DateTimeOffset.UtcNow.AddMonths(-1) });
        await context.SaveChangesAsync();
    }

    // ---- superseding records the link ------------------------------------------------------------------------------------

    [Fact]
    public async Task SupersedingRecordsWhichPlanReplacedWhich()
    {
        var old = await CreateAsync(UniqueName());
        await SubscribeAsync(old.Id);

        var newer = await SupersedeAsync(old.Id);

        var oldRow = await RowAsync(old.Id);
        Assert.Equal(newer.Id, oldRow.SupersededByPlanId);
        Assert.False(oldRow.Active);
        Assert.Null(newer.SupersededByPlanId);            // the newest version has been replaced by nothing
        Assert.True(newer.Active);
    }

    [Fact]
    public async Task TheLinkShowsOnThePlanTheAdminPageReads()
    {
        var old = await CreateAsync(UniqueName());
        var newer = await SupersedeAsync(old.Id);

        var listed = Assert.IsType<System.Collections.Generic.List<PlanDto>>(Assert.IsType<OkObjectResult>((await Controller().GetAll()).Result).Value);

        Assert.Equal(newer.Id, listed.Single(p => p.Id == old.Id).SupersededByPlanId);
        Assert.Null(listed.Single(p => p.Id == newer.Id).SupersededByPlanId);
    }

    [Fact]
    public async Task AVersionCanBeReplacedInTurnMakingAChain()
    {
        var name = UniqueName();
        var v1 = await CreateAsync(name);
        var v2 = await SupersedeAsync(v1.Id, 7m);
        var v3 = await SupersedeAsync(v2.Id, 8m);

        Assert.Equal(v2.Id, (await RowAsync(v1.Id)).SupersededByPlanId);
        Assert.Equal(v3.Id, (await RowAsync(v2.Id)).SupersededByPlanId);
        Assert.Null((await RowAsync(v3.Id)).SupersededByPlanId);
        Assert.Equal(3, await RowsNamedAsync(name));
    }

    // ---- what a replaced plan may not do -----------------------------------------------------------------------------------

    [Fact]
    public async Task AReplacedPlanCannotBeSwitchedBackOn()
    {
        var old = await CreateAsync(UniqueName());
        var newer = await SupersedeAsync(old.Id);

        var result = await Controller().SetActive(old.Id, new SetPlanActiveDto { Active = true });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.False((await RowAsync(old.Id)).Active);
        Assert.True((await RowAsync(newer.Id)).Active);                // and the plan in use was not displaced
    }

    [Fact]
    public async Task ASavedEditCannotSwitchAReplacedPlanBackOnEither()
    {
        var old = await CreateAsync(UniqueName());
        await SupersedeAsync(old.Id);

        var result = await Controller().Update(old.Id, new UpdatePlanDto
        {
            Id = old.Id, ColorCode = "#123456", PricingModel = PricingModel.Flat, MaximumServers = 3, MaximumUsers = 7, Active = true, Prices = [Monthly()]
        });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.False((await RowAsync(old.Id)).Active);
    }

    [Fact]
    public async Task AReplacedPlanIsNotEditedAtAllNotItsTermsEither()
    {
        var old = await CreateAsync(UniqueName());
        await SupersedeAsync(old.Id);

        var result = await Controller().Update(old.Id, new UpdatePlanDto
        {
            Id = old.Id, ColorCode = "#654321", PricingModel = PricingModel.Flat, RetentionHistory = 999, MaximumServers = 3, MaximumUsers = 7, Active = false, Prices = [Monthly(99m)]
        });

        Assert.IsType<ConflictObjectResult>(result.Result);
        var row = await RowAsync(old.Id);
        Assert.Equal("#123456", row.ColorCode);
        Assert.Equal(45, row.RetentionHistory);
    }

    [Fact]
    public async Task DeletingAnActivePlanFreesItsNameSoAnotherCanBeCreatedOrActivated()
    {
        // Deleting is a soft delete; the unique index that allows one active plan per name would otherwise still count the deleted, active row.
        var name = UniqueName();
        var first = await CreateAsync(name);
        Assert.IsType<NoContentResult>(await Controller().Delete(first.Id));

        var second = await CreateAsync(name);

        Assert.True(second.Active);
    }

    [Fact]
    public async Task AReplacedPlanCanStillBeSwitchedOffSoItIsAlwaysSafeToDeactivate()
    {
        var old = await CreateAsync(UniqueName());
        await SupersedeAsync(old.Id);

        var result = await Controller().SetActive(old.Id, new SetPlanActiveDto { Active = false });

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task ReplacingAPlanThatWasAlreadyReplacedIsRefusedSoTheChainNeverForks()
    {
        var name = UniqueName();
        var v1 = await CreateAsync(name);
        var v2 = await SupersedeAsync(v1.Id);

        var result = await Controller().Supersede(v1.Id, Terms(12m));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(2, await RowsNamedAsync(name));                       // nothing new was made
        Assert.Equal(v2.Id, (await RowAsync(v1.Id)).SupersededByPlanId);   // and the link is as it was
        Assert.True((await RowAsync(v2.Id)).Active);
    }

    // ---- a plan that was only deactivated ------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeactivatedPlanNothingReplacedCanBeReactivated()
    {
        var plan = await CreateAsync(UniqueName());
        await SubscribeAsync(plan.Id);
        await Controller().SetActive(plan.Id, new SetPlanActiveDto { Active = false });

        var result = await Controller().SetActive(plan.Id, new SetPlanActiveDto { Active = true });

        var back = Assert.IsType<PlanDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(back.Active);
        Assert.Null(back.SupersededByPlanId);
    }

    // ---- deleting the newer version -------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeletingTheNewerVersionFreesTheOlderOneToBeReactivated()
    {
        var old = await CreateAsync(UniqueName());
        var newer = await SupersedeAsync(old.Id);
        Assert.Equal(newer.Id, (await RowAsync(old.Id)).SupersededByPlanId);

        Assert.IsType<NoContentResult>(await Controller().Delete(newer.Id));

        Assert.Null((await RowAsync(old.Id)).SupersededByPlanId);
        var reactivated = await Controller().SetActive(old.Id, new SetPlanActiveDto { Active = true });
        Assert.True(Assert.IsType<PlanDto>(Assert.IsType<OkObjectResult>(reactivated.Result).Value).Active);
    }

    [Fact]
    public async Task DeletingSomethingNothingReplacedLeavesEveryLinkAlone()
    {
        var name = UniqueName();
        var v1 = await CreateAsync(name);
        var v2 = await SupersedeAsync(v1.Id);
        var unrelated = await CreateAsync(UniqueName());

        Assert.IsType<NoContentResult>(await Controller().Delete(unrelated.Id));

        Assert.Equal(v2.Id, (await RowAsync(v1.Id)).SupersededByPlanId);
    }

    [Fact]
    public async Task ThePlanASubscriberIsOnCannotBeDeletedSoTheLinkToItIsKept()
    {
        var old = await CreateAsync(UniqueName());
        var newer = await SupersedeAsync(old.Id);
        await SubscribeAsync(newer.Id);

        Assert.IsType<ConflictObjectResult>(await Controller().Delete(newer.Id));

        Assert.Equal(newer.Id, (await RowAsync(old.Id)).SupersededByPlanId);
    }

    // ---- following the chain -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLatestVersionIsTheEndOfTheChainWithItsPrices()
    {
        var name = UniqueName();
        var v1 = await CreateAsync(name);
        var v2 = await SupersedeAsync(v1.Id, 7m);
        var v3 = await SupersedeAsync(v2.Id, 8m);

        var latest = await Repository().GetLatestVersionAsync(v1.Id);

        Assert.Equal(v3.Id, latest!.Id);
        Assert.Equal(8m, Assert.Single(latest.Prices).BaseAmount);
    }

    [Fact]
    public async Task APlanNothingReplacedIsItsOwnLatestVersionAndAMissingOneHasNone()
    {
        var plan = await CreateAsync(UniqueName());

        Assert.Equal(plan.Id, (await Repository().GetLatestVersionAsync(plan.Id))!.Id);
        Assert.Null(await Repository().GetLatestVersionAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ACycleThatShouldNeverExistDoesNotLoopForever()
    {
        var a = await CreateAsync(UniqueName(), active: false);
        var b = await CreateAsync(UniqueName(), active: false);
        await using (var context = new ApiDbContext(postgres.Options))
        {
            await context.Set<Plan>().Where(p => p.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.SupersededByPlanId, b.Id));
            await context.Set<Plan>().Where(p => p.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.SupersededByPlanId, a.Id));
        }

        var latest = await Repository().GetLatestVersionAsync(a.Id);

        Assert.Equal(b.Id, latest!.Id);          // stops at the first row it has already seen
    }

    [Fact]
    public async Task AChainThatEndsAtADeletedPlanEndsAtTheLastOneThatIsThere()
    {
        var v1 = await CreateAsync(UniqueName());
        var v2 = await SupersedeAsync(v1.Id);
        await using (var context = new ApiDbContext(postgres.Options))
        {
            await context.Set<Plan>().IgnoreQueryFilters().Where(p => p.Id == v2.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedOn, DateTimeOffset.UtcNow));
        }

        Assert.Equal(v1.Id, (await Repository().GetLatestVersionAsync(v1.Id))!.Id);
    }

    // ---- nothing but a supersede can write it -----------------------------------------------------------------------------------

    [Fact]
    public void NoCreateOrEditBodyCanSetOrClearTheLink()
    {
        Assert.DoesNotContain(typeof(CreatePlanDto).GetProperties(), p => p.Name == nameof(Plan.SupersededByPlanId));
        Assert.DoesNotContain(typeof(UpdatePlanDto).GetProperties(), p => p.Name == nameof(Plan.SupersededByPlanId));
        Assert.DoesNotContain(typeof(SupersedePlanDto).GetProperties(), p => p.Name == nameof(Plan.SupersededByPlanId));
        Assert.DoesNotContain(typeof(SetPlanActiveDto).GetProperties(), p => p.Name == nameof(Plan.SupersededByPlanId));
    }

    [Fact]
    public void TheMappingsAreStillValidAndAnEditNeverTouchesTheLink()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<PlanMappingProfile>(), NullLoggerFactory.Instance);
        config.AssertConfigurationIsValid();
        var replacedBy = Guid.NewGuid();
        var entity = new Plan { Name = "X", SupersededByPlanId = replacedBy };

        config.CreateMapper().Map(new UpdatePlanDto { ColorCode = "#000000", PricingModel = PricingModel.Flat, Active = false }, entity);

        Assert.Equal(replacedBy, entity.SupersededByPlanId);
    }

    // ---- rebuilding the history that existed before the link did -----------------------------------------------------------------

    /// <summary>Seeds plans of one name with the given creation times, active flags and soft-deletion, then runs the migration's SQL and reads the links back.</summary>
    private async Task<string?[]> BackfillAsync(params (string Order, bool Active, int MinutesAgo, bool Deleted)[] plans)
    {
        var name = UniqueName();
        var ids = new Guid[plans.Length];
        var now = DateTimeOffset.UtcNow;
        await using (var setup = new ApiDbContext(postgres.Options))
        {
            for (var i = 0; i < plans.Length; i++)
            {
                var plan = new Plan { Name = name, Active = false };
                plan.Prices.Add(new PlanPrice { TermMonths = 1, UnitAmount = 0m, IncludedUnits = 1, Currency = "USD" });
                setup.Set<Plan>().Add(plan);
                await setup.SaveChangesAsync();
                ids[i] = plan.Id;
            }

            for (var i = 0; i < plans.Length; i++)
            {
                var (_, active, minutesAgo, deleted) = plans[i];
                var id = ids[i];
                await setup.Set<Plan>().IgnoreQueryFilters().Where(p => p.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.CreatedOn, now.AddMinutes(-minutesAgo))
                    .SetProperty(p => p.Active, active)
                    .SetProperty(p => p.DeletedOn, deleted ? now : (DateTimeOffset?)null));
            }
        }

        // In a transaction that is rolled back: the SQL is the migration's and reaches every plan in the shared database, and this must not alter what
        // any other test sees. What is read back is read inside it.
        await using var context = new ApiDbContext(postgres.Options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync(PlanVersionBackfill.Sql);
        var byId = await context.Set<Plan>().IgnoreQueryFilters().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.SupersededByPlanId);
        await transaction.RollbackAsync();

        return ids.Select(id => byId[id] is { } to ? plans[Array.IndexOf(ids, to)].Order : null).ToArray();
    }

    [Fact]
    public async Task AnInactivePlanIsReplacedByTheNextOneOfItsNameCreatedAfterIt()
    {
        var links = await BackfillAsync(("old", false, 60, false), ("current", true, 30, false));

        Assert.Equal(["current", null], links);
    }

    [Fact]
    public async Task AChainOfVersionsLinksEachToTheNext()
    {
        var links = await BackfillAsync(("v1", false, 90, false), ("v2", false, 60, false), ("v3", true, 30, false));

        Assert.Equal(["v2", "v3", null], links);
    }

    [Fact]
    public async Task AnInactivePlanWithNoLaterPlanOfItsNameWasOnlyDeactivatedAndIsNotMarked()
    {
        var links = await BackfillAsync(("only", false, 60, false));

        Assert.Equal([null], links);
    }

    [Fact]
    public async Task AnActivePlanIsNeverMarkedEvenWithALaterOne()
    {
        // Two active plans of one name cannot exist (a unique index), so the later one here is inactive - the earlier, active one is still left alone.
        var links = await BackfillAsync(("active", true, 60, false), ("later", false, 30, false));

        Assert.Null(links[0]);
    }

    [Fact]
    public async Task ADeletedLaterPlanDoesNotCountAsAReplacement()
    {
        var links = await BackfillAsync(("old", false, 60, false), ("gone", false, 30, true));

        Assert.Equal([null, null], links);
    }

    [Fact]
    public async Task ADeletedPlanIsNotMarkedItself()
    {
        var links = await BackfillAsync(("gone", false, 60, true), ("current", true, 30, false));

        Assert.Equal([null, null], links);
    }

    [Fact]
    public async Task APlanCreatedAtTheSameMomentIsOrderedByItsId()
    {
        var links = await BackfillAsync(("a", false, 60, false), ("b", false, 60, false));

        // Same creation time: the one with the smaller id is taken to have come first. Whichever that is, exactly one points at the other, and never both
        // (which would be a cycle).
        Assert.Equal(1, links.Count(l => l is not null));
    }

    [Fact]
    public async Task PlansOfOtherNamesNeverInfluenceEachOther()
    {
        var other = await CreateAsync(UniqueName(), active: false);
        var links = await BackfillAsync(("only", false, 60, false));

        Assert.Equal([null], links);
        Assert.Null((await RowAsync(other.Id)).SupersededByPlanId);
    }

    [Fact]
    public async Task RunningItAgainChangesNothingThatIsAlreadyLinked()
    {
        var old = await CreateAsync(UniqueName());
        var newer = await SupersedeAsync(old.Id);

        await using var context = new ApiDbContext(postgres.Options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync(PlanVersionBackfill.Sql);
        await context.Database.ExecuteSqlRawAsync(PlanVersionBackfill.Sql);
        var after = await context.Set<Plan>().AsNoTracking().SingleAsync(p => p.Id == old.Id);
        await transaction.RollbackAsync();

        Assert.Equal(newer.Id, after.SupersededByPlanId);
    }
}
