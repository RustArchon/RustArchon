// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
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
/// Tests for <see cref="PlansController.ToPrices"/>'s flat-tier normalization: a flat-tier plan's price
/// rows must carry the plan's own <see cref="Plan.MaximumServers"/> as their
/// <see cref="RustArchon.Api.Data.PlanPrice.IncludedUnits"/>, since that's the only place
/// <see cref="Billing.PlanChangeCalculator.ResolveQuantity"/> and
/// <see cref="Billing.SubscriptionService.GetAsync"/> ever look to find out how many servers a flat-tier
/// subscription actually grants (<see cref="RustArchon.Api.Data.PlanPrice.UnitAmount"/> is always zero on
/// a flat tier, which is exactly what makes <c>ResolveQuantity</c> stop looking any further).
/// </summary>
/// <remarks>
/// Runs against a real (throwaway, Testcontainers-hosted) Postgres - Plan/PlanPrice carry real unique
/// indexes (one active row per Name, one price per term) that InMemory doesn't enforce. See
/// <see cref="PostgresFixture"/>.
/// </remarks>
public class PlansControllerFlatTierIncludedUnitsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<PlanMappingProfile>(), NullLoggerFactory.Instance)
            .CreateMapper();

    private PlansController CreateController()
    {
        var context = new ApiDbContext(postgres.Options, null);
        var repository = new PlanRepository(context);
        return new PlansController(repository, CreateMapper());
    }

    private static CreatePlanDto FlatCreateDto(string name, int maximumServers, int submittedIncludedUnits) => new()
    {
        Name = name,
        ColorCode = "#888888",
        PricingModel = PricingModel.Flat,
        MaximumServers = maximumServers,
        MaximumUsers = 20,
        Active = true,
        // Mirrors exactly what the (buggy) admin form sent: a flat-tier price row with IncludedUnits
        // left at whatever the caller happened to submit, not synced to MaximumServers.
        Prices =
        [
            new PlanPriceDto
            {
                TermMonths = BillingTerms.Monthly, BaseAmount = 0m,
                IncludedUnits = submittedIncludedUnits, UnitAmount = 0m
            }
        ]
    };

    [Fact]
    public async Task Create_FlatTierPlan_ForcesIncludedUnitsToMaximumServers()
    {
        var controller = CreateController();

        var result = await controller.Create(FlatCreateDto("Gold (Comped) - create", maximumServers: 10, submittedIncludedUnits: 0));

        var created = Assert.IsType<PlanDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.All(created.Prices, p => Assert.Equal(10, p.IncludedUnits));
    }

    [Fact]
    public async Task Update_FlatTierPlan_ForcesIncludedUnitsToMaximumServers()
    {
        var controller = CreateController();

        var createResult = await controller.Create(
            FlatCreateDto("Gold (Comped) - update", maximumServers: 10, submittedIncludedUnits: 10));
        var created = (PlanDto)Assert.IsType<CreatedAtActionResult>(createResult.Result).Value!;

        var updateResult = await controller.Update(created.Id, new UpdatePlanDto
        {
            Id = created.Id,
            ColorCode = "#888888",
            PricingModel = PricingModel.Flat,
            MaximumServers = 5,
            MaximumUsers = 20,
            Active = true,
            Prices =
            [
                new PlanPriceDto
                {
                    TermMonths = BillingTerms.Monthly, BaseAmount = 0m, IncludedUnits = 0, UnitAmount = 0m
                }
            ]
        });

        var updated = Assert.IsType<PlanDto>(Assert.IsType<OkObjectResult>(updateResult.Result).Value);
        Assert.All(updated.Prices, p => Assert.Equal(5, p.IncludedUnits));
    }

    [Fact]
    public async Task Supersede_FlatTierPlan_ForcesIncludedUnitsToMaximumServers()
    {
        var controller = CreateController();

        var createResult = await controller.Create(
            FlatCreateDto("Gold (Comped) - supersede", maximumServers: 10, submittedIncludedUnits: 10));
        var created = (PlanDto)Assert.IsType<CreatedAtActionResult>(createResult.Result).Value!;

        var supersedeResult = await controller.Supersede(created.Id, new SupersedePlanDto
        {
            ColorCode = "#888888",
            PricingModel = PricingModel.Flat,
            MaximumServers = 15,
            MaximumUsers = 20,
            Prices =
            [
                new PlanPriceDto
                {
                    TermMonths = BillingTerms.Monthly, BaseAmount = 0m, IncludedUnits = 0, UnitAmount = 0m
                }
            ]
        });

        var superseded = Assert.IsType<PlanDto>(Assert.IsType<OkObjectResult>(supersedeResult.Result).Value);
        Assert.All(superseded.Prices, p => Assert.Equal(15, p.IncludedUnits));
    }

    [Fact]
    public async Task Create_PerUnitPlan_KeepsTheSubmittedIncludedUnits()
    {
        var controller = CreateController();

        var result = await controller.Create(new CreatePlanDto
        {
            Name = "Per-server plan",
            ColorCode = "#888888",
            PricingModel = PricingModel.PerUnit,
            MaximumServers = null,
            MaximumUsers = 20,
            Active = true,
            Prices =
            [
                new PlanPriceDto
                {
                    TermMonths = BillingTerms.Monthly, BaseAmount = 2m, IncludedUnits = 3, UnitAmount = 2m
                }
            ]
        });

        var created = Assert.IsType<PlanDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.All(created.Prices, p => Assert.Equal(3, p.IncludedUnits));
    }
}
