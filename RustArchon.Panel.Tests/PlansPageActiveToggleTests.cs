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
/// Switching a plan on or off from the plan list. Whether a plan is offered to new sign-ups is not one of the terms a subscriber signed up under, so it
/// has its own Deactivate / Activate button that changes nothing else - rather than a checkbox on the edit form, which for a plan with subscribers meant
/// "create a new identical plan and deactivate this one". The edit form shows the flag but does not let it be changed.
/// </summary>
public class PlansPageActiveToggleTests : BunitContext
{
    private readonly Mock<IPlanApiClient> _plans = new();

    public PlansPageActiveToggleTests()
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

    private static PlanDto Plan(string name, bool active = true, int subscribers = 0, decimal price = 5m, Guid? replacedBy = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), Name = name, ColorCode = "#888888", Active = active, PricingModel = PricingModel.Flat, SupersededByPlanId = replacedBy,
        MaximumServers = 1, MaximumUsers = 1, SubscriberCount = subscribers, CreatedOn = DateTimeOffset.UtcNow,
        Prices = [new PlanPriceDto { TermMonths = BillingTerms.Monthly, BaseAmount = price, IncludedUnits = 1, UnitAmount = 0m }]
    };

    private IRenderedComponent<Plans> RenderPage(params PlanDto[] plans)
    {
        _plans.Setup(p => p.GetAllPlansAsync()).ReturnsAsync([.. plans]);
        var cut = Render<Plans>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        return cut;
    }

    private static AngleSharp.Dom.IElement RowOf(IRenderedComponent<Plans> cut, string name) =>
        cut.FindAll("tbody tr").First(r => r.QuerySelectorAll("td")[1].TextContent.Contains(name));

    private static string ButtonText(AngleSharp.Dom.IElement row) => row.QuerySelector("[data-testid=plan-toggle-active]")!.TextContent.Trim();

    // ---- the button --------------------------------------------------------------------------------------------------

    [Fact]
    public void AnActivePlanOffersDeactivateAndADeactivatedOneOffersReactivate()
    {
        var cut = RenderPage(Plan("Live"), Plan("Retired", active: false));
        cut.Find("#show-inactive-plans").Change(true);

        Assert.Equal("Deactivate", ButtonText(RowOf(cut, "Live")));
        Assert.Equal("Reactivate", ButtonText(RowOf(cut, "Retired")));
    }

    // ---- a plan that a newer version replaced ------------------------------------------------------------------------

    [Fact]
    public void AReplacedPlanIsMarkedAndOffersNoReactivateButKeepsItsEditButton()
    {
        var newer = Plan("Gold");
        var replaced = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        var cut = RenderPage(newer, replaced);
        cut.Find("#show-inactive-plans").Change(true);

        var row = cut.FindAll("tbody tr").Single(r => r.QuerySelector("[data-testid=plan-replaced]") is not null);

        Assert.Equal("Replaced", row.QuerySelector("[data-testid=plan-replaced]")!.TextContent.Trim());
        Assert.Null(row.QuerySelector("[data-testid=plan-toggle-active]"));         // no Reactivate: the newer version is the one to offer
        var buttons = row.QuerySelectorAll(".btn-group .btn");
        Assert.Equal(3, buttons.Length);                                            // Edit, Email subscribers, Move subscribers (no Delete: it has subscribers)
        Assert.Contains("Edit", buttons[0].TextContent);
        Assert.Contains("Email subscribers", buttons[1].TextContent);
        Assert.Contains("Move subscribers", buttons[2].TextContent);
    }

    // ---- editing a plan that was replaced ------------------------------------------------------------------------------

    private static AngleSharp.Dom.IElement EditButton(AngleSharp.Dom.IElement row) => row.QuerySelector(".btn-group .btn")!;

    [Fact]
    public void EditingAReplacedPlanShowsABannerInsteadOfTheFormAndLinksToTheLatestVersion()
    {
        var newer = Plan("Gold", subscribers: 2);
        var replaced = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        var cut = RenderPage(newer, replaced);
        cut.Find("#show-inactive-plans").Change(true);

        EditButton(cut.FindAll("tbody tr").Single(r => r.QuerySelector("[data-testid=plan-replaced]") is not null)).Click();

        var banner = cut.Find("[data-testid=plan-replaced-banner]");
        Assert.Contains("replaced by a newer version", banner.TextContent);
        Assert.Contains("can no longer be edited", banner.TextContent);
        Assert.NotNull(banner.QuerySelector("[data-testid=plan-edit-latest]"));
        Assert.Empty(cut.FindAll("form"));                                          // no form, so nothing could be saved
        Assert.Empty(cut.FindAll("#edit-active"));
    }

    [Fact]
    public void TheLinkOpensTheEditFormOfTheLatestVersion()
    {
        var latest = Plan("Gold", subscribers: 2, price: 9m);
        var replaced = Plan("Gold", active: false, subscribers: 5, replacedBy: latest.Id);
        var cut = RenderPage(latest, replaced);
        cut.Find("#show-inactive-plans").Change(true);
        EditButton(cut.FindAll("tbody tr").Single(r => r.QuerySelector("[data-testid=plan-replaced]") is not null)).Click();

        cut.Find("[data-testid=plan-edit-latest]").Click();

        Assert.Empty(cut.FindAll("[data-testid=plan-replaced-banner]"));
        Assert.Single(cut.FindAll("form"));                                          // the real edit form, for the plan that can be edited
        Assert.Contains("Edit Gold Plan", cut.Markup);
    }

    [Fact]
    public void AChainOfSeveralVersionsLinksStraightToTheNewestNotTheNextOne()
    {
        var v3 = Plan("Gold", subscribers: 1, price: 30m);
        var v2 = Plan("Gold", active: false, subscribers: 2, replacedBy: v3.Id, price: 20m);
        var v1 = Plan("Gold", active: false, subscribers: 3, replacedBy: v2.Id, price: 10m);
        var cut = RenderPage(v3, v2, v1);
        cut.Find("#show-inactive-plans").Change(true);
        var oldest = cut.FindAll("tbody tr").First(r => r.TextContent.Contains("$10.00"));
        EditButton(oldest).Click();

        cut.Find("[data-testid=plan-edit-latest]").Click();

        // The form that opens is the newest version's - it is the one that is not itself replaced, so no banner follows.
        Assert.Empty(cut.FindAll("[data-testid=plan-replaced-banner]"));
        Assert.Single(cut.FindAll("form"));
    }

    [Fact]
    public void TheBannerHasNoLinkWhenThereIsNoEditableVersionToPointAt()
    {
        // A chain that leaves the list (its newer version is not there) and one that loops: neither may hang or point at something that is not editable.
        var lost = Plan("Lost", active: false, replacedBy: Guid.NewGuid());
        var a = Plan("Loop", active: false);
        var b = Plan("Loop", active: false, replacedBy: a.Id);
        a.SupersededByPlanId = b.Id;
        var cut = RenderPage(Plan("Live"), lost, a, b);       // one active plan so the list has rows before inactive ones are shown
        cut.Find("#show-inactive-plans").Change(true);

        EditButton(cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Lost"))).Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=plan-replaced-banner]"));
        Assert.Empty(cut.FindAll("[data-testid=plan-edit-latest]"));

        cut.Find("[data-testid=plan-replaced-banner] .btn-secondary").Click();
        EditButton(cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Loop"))).Click();
        Assert.NotEmpty(cut.FindAll("[data-testid=plan-replaced-banner]"));
        Assert.Empty(cut.FindAll("[data-testid=plan-edit-latest]"));
    }

    [Fact]
    public void TheBannerCanBeClosedAndSavesNothing()
    {
        var newer = Plan("Gold");
        var replaced = Plan("Gold", active: false, subscribers: 1, replacedBy: newer.Id);
        var cut = RenderPage(newer, replaced);
        cut.Find("#show-inactive-plans").Change(true);
        EditButton(cut.FindAll("tbody tr").Single(r => r.QuerySelector("[data-testid=plan-replaced]") is not null)).Click();

        cut.Find("[data-testid=plan-replaced-banner] .btn-secondary").Click();

        Assert.Empty(cut.FindAll("[data-testid=plan-replaced-banner]"));
        _plans.Verify(p => p.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdatePlanDto>()), Times.Never());
        _plans.Verify(p => p.SupersedeAsync(It.IsAny<Guid>(), It.IsAny<SupersedePlanDto>()), Times.Never());
    }

    [Fact]
    public void APlanThatWasNotReplacedStillOpensItsFormNotABanner()
    {
        var cut = RenderPage(Plan("Live", subscribers: 1));

        EditButton(RowOf(cut, "Live")).Click();

        Assert.Empty(cut.FindAll("[data-testid=plan-replaced-banner]"));
        Assert.Single(cut.FindAll("form"));
    }

    [Fact]
    public void ADeactivatedPlanNothingReplacedIsNotMarkedAsReplacedAndCanBeReactivated()
    {
        var cut = RenderPage(Plan("Live"), Plan("Retired", active: false, subscribers: 1));
        cut.Find("#show-inactive-plans").Change(true);

        var row = RowOf(cut, "Retired");

        Assert.Null(row.QuerySelector("[data-testid=plan-replaced]"));
        Assert.Equal("Reactivate", ButtonText(row));
        Assert.NotNull(row.QuerySelector(".btn-group .btn"));                       // Edit is still there
    }

    [Fact]
    public void ThePlanThatReplacedAnotherSaysSoAndTheOlderOneDoesNot()
    {
        var newer = Plan("Gold", subscribers: 2);
        var older = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        var cut = RenderPage(newer, older);
        cut.Find("#show-inactive-plans").Change(true);

        var rows = cut.FindAll("tbody tr");

        Assert.Single(rows.Where(r => r.QuerySelector("[data-testid=plan-replaces-earlier]") is not null));
        var withNote = rows.Single(r => r.QuerySelector("[data-testid=plan-replaces-earlier]") is not null);
        Assert.Null(withNote.QuerySelector("[data-testid=plan-replaced]"));         // the newer one is not itself replaced
        Assert.Contains("Replaces an earlier version", withNote.TextContent);
    }

    [Fact]
    public void APlanWithNoHistoryCarriesNoVersionNotes()
    {
        var cut = RenderPage(Plan("Solo"));

        Assert.Empty(cut.FindAll("[data-testid=plan-replaced]"));
        Assert.Empty(cut.FindAll("[data-testid=plan-replaces-earlier]"));
    }

    [Fact]
    public void AReplacedPlanNobodyWasOnCanStillBeDeleted()
    {
        var newer = Plan("Gold");
        var replaced = Plan("Gold", active: false, subscribers: 0, replacedBy: newer.Id);
        var cut = RenderPage(newer, replaced);
        cut.Find("#show-inactive-plans").Change(true);

        var row = cut.FindAll("tbody tr").Single(r => r.QuerySelector("[data-testid=plan-replaced]") is not null);

        var buttons = row.QuerySelectorAll(".btn-group .btn");
        Assert.Equal(2, buttons.Length);                                            // Edit (which explains) and Delete
        Assert.Contains("Delete", buttons[1].TextContent);
    }

    [Fact]
    public void TheButtonIsThereWhetherOrNotThePlanHasSubscribers()
    {
        var cut = RenderPage(Plan("Used", subscribers: 4), Plan("Unused"));

        Assert.NotNull(RowOf(cut, "Used").QuerySelector("[data-testid=plan-toggle-active]"));
        Assert.NotNull(RowOf(cut, "Unused").QuerySelector("[data-testid=plan-toggle-active]"));
    }

    [Fact]
    public void DeactivatingAPlanWithSubscribersSwitchesItOffAndCreatesNoNewPlan()
    {
        var plan = Plan("Gold (Comped)", subscribers: 1);
        SetActiveDto? sent = null;
        _plans.Setup(p => p.SetActiveAsync(plan.Id, It.IsAny<SetPlanActiveDto>()))
            .Callback((Guid _, SetPlanActiveDto dto) => sent = new SetActiveDto(dto.Active))
            .ReturnsAsync(() => { plan.Active = false; return plan; });
        var cut = RenderPage(plan);

        RowOf(cut, "Gold (Comped)").QuerySelector("[data-testid=plan-toggle-active]")!.Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.False(sent!.Active);
        cut.WaitForAssertion(() => Assert.Contains("Gold (Comped) plan deactivated.", cut.Markup));
        _plans.Verify(p => p.SupersedeAsync(It.IsAny<Guid>(), It.IsAny<SupersedePlanDto>()), Times.Never());
        _plans.Verify(p => p.CreateAsync(It.IsAny<CreatePlanDto>()), Times.Never());
        _plans.Verify(p => p.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdatePlanDto>()), Times.Never());
        Assert.Empty(cut.FindAll("[data-testid=plan-toggle-active]"));            // it is off now, so it is out of the active-only list
    }

    [Fact]
    public void ReactivatingADeactivatedPlanSendsActiveTrue()
    {
        var plan = Plan("Retired", active: false, subscribers: 2);
        SetActiveDto? sent = null;
        _plans.Setup(p => p.SetActiveAsync(plan.Id, It.IsAny<SetPlanActiveDto>()))
            .Callback((Guid _, SetPlanActiveDto dto) => sent = new SetActiveDto(dto.Active))
            .ReturnsAsync(() => { plan.Active = true; return plan; });
        var cut = RenderPage(Plan("Live"), plan);
        cut.Find("#show-inactive-plans").Change(true);

        RowOf(cut, "Retired").QuerySelector("[data-testid=plan-toggle-active]")!.Click();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.True(sent!.Active);
        cut.WaitForAssertion(() => Assert.Contains("Retired plan reactivated.", cut.Markup));
        _plans.Verify(p => p.SupersedeAsync(It.IsAny<Guid>(), It.IsAny<SupersedePlanDto>()), Times.Never());
    }

    [Fact]
    public void ABrokenSwitchSaysSoAndLeavesThePlanAsItWas()
    {
        var plan = Plan("Live", subscribers: 1);
        _plans.Setup(p => p.SetActiveAsync(plan.Id, It.IsAny<SetPlanActiveDto>())).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderPage(plan);

        RowOf(cut, "Live").QuerySelector("[data-testid=plan-toggle-active]")!.Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".alert-danger")));
        Assert.Equal("Deactivate", ButtonText(RowOf(cut, "Live")));
    }

    // ---- the edit form ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheEditFormShowsTheActiveFlagButDoesNotLetItBeChanged()
    {
        var cut = RenderPage(Plan("Live", subscribers: 1));

        RowOf(cut, "Live").QuerySelector(".btn-group .btn")!.Click();

        var box = cut.Find("#edit-active");
        Assert.True(box.HasAttribute("disabled"));
        Assert.True(box.HasAttribute("checked"));
        Assert.Contains("Deactivate or Activate", cut.Find("[data-testid=edit-active-hint]").TextContent);
    }

    [Fact]
    public void SavingAnUnchangedPlanWithSubscribersJustClosesTheFormWithoutOfferingToCreateANewPlan()
    {
        var cut = RenderPage(Plan("Live", subscribers: 3));
        RowOf(cut, "Live").QuerySelector(".btn-group .btn")!.Click();

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#edit-active")));
        Assert.DoesNotContain("Saving will create a new", cut.Markup);
        _plans.Verify(p => p.SupersedeAsync(It.IsAny<Guid>(), It.IsAny<SupersedePlanDto>()), Times.Never());
        _plans.Verify(p => p.UpdateAsync(It.IsAny<Guid>(), It.IsAny<UpdatePlanDto>()), Times.Never());
        _plans.Verify(p => p.SetActiveAsync(It.IsAny<Guid>(), It.IsAny<SetPlanActiveDto>()), Times.Never());
    }

    [Fact]
    public void ARealChangeToAPlanWithSubscribersStillOffersToCreateANewOne()
    {
        var cut = RenderPage(Plan("Live", subscribers: 3));
        RowOf(cut, "Live").QuerySelector(".btn-group .btn")!.Click();
        cut.Find("#edit-retention").Change("99");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Saving will create a new", cut.Markup));
    }

    [Fact]
    public void ChangingAFeatureFlagOnAPlanWithSubscribersStillOffersToCreateANewOne()
    {
        var cut = RenderPage(Plan("Live", subscribers: 3));
        RowOf(cut, "Live").QuerySelector(".btn-group .btn")!.Click();
        cut.Find("#edit-third-party-updates").Change(true);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Saving will create a new", cut.Markup));
    }

    [Fact]
    public void APlanNobodyIsOnIsStillEditedInPlaceAsBefore()
    {
        var plan = Plan("Fresh");
        UpdatePlanDto? sent = null;
        _plans.Setup(p => p.UpdateAsync(plan.Id, It.IsAny<UpdatePlanDto>()))
            .Callback((Guid _, UpdatePlanDto dto) => sent = dto)
            .ReturnsAsync(plan);
        var cut = RenderPage(plan);
        RowOf(cut, "Fresh").QuerySelector(".btn-group .btn")!.Click();
        cut.Find("#edit-retention").Change("77");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal(77, sent!.RetentionHistory);
        Assert.True(sent.Active);                        // the flag rides along as it was; the form cannot change it
    }

    private sealed record SetActiveDto(bool Active);
}
