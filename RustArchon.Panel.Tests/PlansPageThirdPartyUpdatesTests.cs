// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Admin;
using RustArchon.Panel.Localization;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The platform-admin Plans page and the plan gate on automatic third-party plugin updates: the column shows it, the edit form carries it
/// and saves it, and a plan somebody is subscribed to takes it through the same supersede path as any other change.
/// </summary>
public class PlansPageThirdPartyUpdatesTests : BunitContext
{
    private readonly Mock<IPlanApiClient> _plans = new();

    public PlansPageThirdPartyUpdatesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns((string key, object[] args) => new LocalizedString(key, string.Format(key, args)));

        Services.AddSingleton(_plans.Object);
        Services.AddSingleton(localizer.Object);
        AddAuthorization().SetAuthorized("test-admin");
    }

    private static PlanDto Plan(string name, bool offers, int subscribers = 0) => new()
    {
        Id = Guid.NewGuid(), Name = name, ColorCode = "#888888", Active = true, PricingModel = PricingModel.Flat,
        MaximumServers = 1, MaximumUsers = 1, SubscriberCount = subscribers, OffersThirdPartyPluginUpdates = offers,
        Prices = [new PlanPriceDto { TermMonths = BillingTerms.Monthly, BaseAmount = 5m, IncludedUnits = 1, UnitAmount = 0m }]
    };

    private IRenderedComponent<Plans> RenderPage(params PlanDto[] plans)
    {
        _plans.Setup(p => p.GetAllPlansAsync()).ReturnsAsync([.. plans]);
        var cut = Render<Plans>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        return cut;
    }

    [Fact]
    public void TheListSaysWhichPlansOfferIt()
    {
        var cut = RenderPage(Plan("Stone", offers: false), Plan("HQM", offers: true));

        var cells = cut.FindAll("[data-testid=plan-third-party-updates]");

        // Ordered by name: HQM, then Stone.
        Assert.Equal(["Yes", "No"], cells.Select(c => c.TextContent.Trim()));
    }

    [Fact]
    public void EditingAPlanNobodyIsOnShowsTheFlagAndSavesTheChange()
    {
        var plan = Plan("Metal", offers: false);
        UpdatePlanDto? sent = null;
        _plans.Setup(p => p.UpdateAsync(plan.Id, It.IsAny<UpdatePlanDto>()))
            .Callback((Guid _, UpdatePlanDto dto) => sent = dto)
            .ReturnsAsync(plan);
        var cut = RenderPage(plan);

        cut.Find("tbody tr .btn-group .btn").Click();
        var box = cut.Find("#edit-third-party-updates");
        Assert.False(box.HasAttribute("checked"));
        box.Change(true);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.True(sent!.OffersThirdPartyPluginUpdates);
    }

    [Fact]
    public void EditingAPlanThatOffersItShowsTheBoxTickedAndKeepsItWhenSavedUntouched()
    {
        var plan = Plan("HQM", offers: true);
        UpdatePlanDto? sent = null;
        _plans.Setup(p => p.UpdateAsync(plan.Id, It.IsAny<UpdatePlanDto>()))
            .Callback((Guid _, UpdatePlanDto dto) => sent = dto)
            .ReturnsAsync(plan);
        var cut = RenderPage(plan);

        cut.Find("tbody tr .btn-group .btn").Click();
        Assert.True(cut.Find("#edit-third-party-updates").HasAttribute("checked"));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.True(sent!.OffersThirdPartyPluginUpdates);
    }

    [Fact]
    public void ANewPlanCarriesTheBoxAsTicked()
    {
        CreatePlanDto? sent = null;
        _plans.Setup(p => p.CreateAsync(It.IsAny<CreatePlanDto>()))
            .Callback((CreatePlanDto dto) => sent = dto)
            .ReturnsAsync((CreatePlanDto dto) => Plan(dto.Name, dto.OffersThirdPartyPluginUpdates));
        var cut = RenderPage(Plan("Existing", offers: false));

        cut.Find("button.btn-primary").Click();
        cut.Find("#create-name").Change("Gold");
        cut.Find("#create-third-party-updates").Change(true);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.True(sent!.OffersThirdPartyPluginUpdates);
        Assert.Equal("Gold", sent.Name);
    }
}
