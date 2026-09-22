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
/// Moving a plan's current subscribers to a newer version: the choice offered when superseding (off unless ticked, only for the people the new version is
/// no worse for, with the rest listed and why) and the "Move subscribers" action on a plan that was replaced. Nothing is moved unless someone asks.
/// </summary>
public class PlansPageMoveSubscribersTests : BunitContext
{
    private readonly Mock<IPlanApiClient> _plans = new();

    public PlansPageMoveSubscribersTests()
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

    private static PlanDto Plan(string name, bool active = true, int subscribers = 0, Guid? replacedBy = null) => new()
    {
        Id = Guid.NewGuid(), Name = name, ColorCode = "#888888", Active = active, PricingModel = PricingModel.Flat, SupersededByPlanId = replacedBy,
        MaximumServers = 1, MaximumUsers = 1, SubscriberCount = subscribers, CreatedOn = DateTimeOffset.UtcNow, RetentionHistory = 30,
        Prices = [new PlanPriceDto { TermMonths = BillingTerms.Monthly, BaseAmount = 5m, IncludedUnits = 1, UnitAmount = 0m }]
    };

    private IRenderedComponent<Plans> RenderPage(params PlanDto[] plans)
    {
        _plans.Setup(p => p.GetAllPlansAsync()).ReturnsAsync([.. plans]);
        var cut = Render<Plans>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        return cut;
    }

    private static PlanMovePreviewDto Preview(int current, int canMove, params (string Message, int Count)[] stay) => new()
    {
        CurrentSubscribers = current, CanMove = canMove,
        WouldStay = stay.Select(s => new PlanMoveReasonDto { Code = "x", Message = s.Message, Count = s.Count }).ToList()
    };

    /// <summary>Opens the supersede confirmation for a plan with subscribers (by changing something that is a term).</summary>
    private IRenderedComponent<Plans> OpenSupersede(PlanDto plan, PlanMovePreviewDto? preview, bool previewFails = false)
    {
        if (previewFails)
        {
            _plans.Setup(p => p.PreviewSupersedeAsync(plan.Id, It.IsAny<SupersedePlanDto>())).ThrowsAsync(new InvalidOperationException("boom"));
        }
        else
        {
            _plans.Setup(p => p.PreviewSupersedeAsync(plan.Id, It.IsAny<SupersedePlanDto>())).ReturnsAsync(preview!);
        }

        var cut = RenderPage(plan);
        cut.Find("tbody tr .btn-group .btn").Click();
        cut.Find("#edit-retention").Change("99");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-footer .btn-primary")));
        return cut;
    }

    private void SupersedeReturns(PlanDto oldPlan, PlanDto newPlan) =>
        _plans.Setup(p => p.SupersedeAsync(oldPlan.Id, It.IsAny<SupersedePlanDto>())).ReturnsAsync(newPlan);

    // ---- the choice when superseding -------------------------------------------------------------------------------------------

    [Fact]
    public void TheConfirmationOffersToMoveThosePeopleCanBeMovedAndItIsOffByDefault()
    {
        var plan = Plan("Gold", subscribers: 5);

        var cut = OpenSupersede(plan, Preview(current: 5, canMove: 4, ("the newer version costs more on their billing term", 1)));

        var offer = cut.Find("[data-testid=move-offer]");
        Assert.Contains("Also move 4 current subscriber(s)", offer.TextContent);
        Assert.False(cut.Find("[data-testid=move-subscribers]").HasAttribute("checked"));
        Assert.Contains("Nothing is charged or refunded", offer.TextContent);
    }

    [Fact]
    public void ThoseWhoWouldStayAreListedWithWhy()
    {
        var plan = Plan("Gold", subscribers: 5);

        var cut = OpenSupersede(plan, Preview(5, 3, ("the newer version costs more on their billing term", 1), ("their subscription is suspended, cancelled or the organization is inactive", 1)));

        var stay = cut.Find("[data-testid=move-stay]");
        Assert.Contains("2 would stay on the current version", stay.TextContent);
        Assert.Contains("1 × the newer version costs more", stay.TextContent);
        Assert.Contains("1 × their subscription is suspended", stay.TextContent);
    }

    [Fact]
    public void WhenNobodyCanBeMovedThereIsNoCheckboxAndTheModalSaysSo()
    {
        var plan = Plan("Gold", subscribers: 2);

        var cut = OpenSupersede(plan, Preview(2, 0, ("the newer version keeps history for fewer days", 2)));

        Assert.Empty(cut.FindAll("[data-testid=move-subscribers]"));
        Assert.Contains("None of the current subscribers can be moved", cut.Find("[data-testid=move-none]").TextContent);
    }

    [Fact]
    public void WhenNobodyIsCurrentlyOnThePlanThereIsNothingToOffer()
    {
        var plan = Plan("Gold", subscribers: 3);         // three organizations have EVER been on it; none is on it now

        var cut = OpenSupersede(plan, Preview(0, 0));

        Assert.Empty(cut.FindAll("[data-testid=move-offer]"));
    }

    [Fact]
    public void AFailedPreviewOffersNothingAndTheSupersedeCanStillProceed()
    {
        var plan = Plan("Gold", subscribers: 3);
        var newer = Plan("Gold");
        SupersedeReturns(plan, newer);

        var cut = OpenSupersede(plan, null, previewFails: true);
        Assert.Empty(cut.FindAll("[data-testid=move-offer]"));
        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => _plans.Verify(p => p.SupersedeAsync(plan.Id, It.IsAny<SupersedePlanDto>()), Times.Once()));
        _plans.Verify(p => p.MoveSubscribersAsync(It.IsAny<Guid>()), Times.Never());
    }

    [Fact]
    public void ThePreviewIsAskedWithTheTermsThatWereEdited()
    {
        var plan = Plan("Gold", subscribers: 5);

        OpenSupersede(plan, Preview(5, 5));

        _plans.Verify(p => p.PreviewSupersedeAsync(plan.Id, It.Is<SupersedePlanDto>(d => d.RetentionHistory == 99)), Times.Once());
    }

    [Fact]
    public void ConfirmingWithoutTickingTheBoxSupersedesAndMovesNobody()
    {
        var plan = Plan("Gold", subscribers: 5);
        SupersedeReturns(plan, Plan("Gold"));
        var cut = OpenSupersede(plan, Preview(5, 5));

        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => _plans.Verify(p => p.SupersedeAsync(plan.Id, It.IsAny<SupersedePlanDto>()), Times.Once()));
        _plans.Verify(p => p.MoveSubscribersAsync(It.IsAny<Guid>()), Times.Never());
    }

    [Fact]
    public void TickingTheBoxSupersedesThenMovesTheSubscribersOfTheOldPlanAndSaysHowManyMoved()
    {
        var plan = Plan("Gold", subscribers: 5);
        SupersedeReturns(plan, Plan("Gold"));
        _plans.Setup(p => p.MoveSubscribersAsync(plan.Id)).ReturnsAsync(new PlanMoveResultDto { Moved = 4, Stayed = 1 });
        var cut = OpenSupersede(plan, Preview(5, 4, ("the newer version costs more on their billing term", 1)));

        cut.Find("[data-testid=move-subscribers]").Change(true);
        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Contains("4 subscriber(s) moved to the new version; 1 stayed on the old one.", cut.Markup));
        Assert.Contains("A new Gold plan was created", cut.Markup);
        _plans.Verify(p => p.MoveSubscribersAsync(plan.Id), Times.Once());
    }

    [Fact]
    public void AFailedMoveAfterTheSupersedeSaysThePlanWasCreatedAndPointsToTheListToRetry()
    {
        var plan = Plan("Gold", subscribers: 5);
        SupersedeReturns(plan, Plan("Gold"));
        _plans.Setup(p => p.MoveSubscribersAsync(plan.Id)).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = OpenSupersede(plan, Preview(5, 5));
        cut.Find("[data-testid=move-subscribers]").Change(true);

        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Contains("The new plan was created, but moving its subscribers failed", cut.Find(".alert-danger").TextContent));
        Assert.Contains("A new Gold plan was created", cut.Find(".alert-success").TextContent);
    }

    [Fact]
    public void CancellingTheConfirmationDoesNothingAtAll()
    {
        var plan = Plan("Gold", subscribers: 5);
        var cut = OpenSupersede(plan, Preview(5, 5));
        cut.Find("[data-testid=move-subscribers]").Change(true);

        cut.Find(".modal-footer .btn-secondary").Click();

        _plans.Verify(p => p.SupersedeAsync(It.IsAny<Guid>(), It.IsAny<SupersedePlanDto>()), Times.Never());
        _plans.Verify(p => p.MoveSubscribersAsync(It.IsAny<Guid>()), Times.Never());
    }

    // ---- "Move subscribers" on a plan that was replaced --------------------------------------------------------------------------

    private IRenderedComponent<Plans> RenderReplaced(PlanDto replaced, PlanDto newer)
    {
        var cut = RenderPage(newer, replaced);
        cut.Find("#show-inactive-plans").Change(true);
        return cut;
    }

    private static AngleSharp.Dom.IElement MoveButton(IRenderedComponent<Plans> cut) => cut.Find("[data-testid=plan-move-subscribers]");

    [Fact]
    public void OnlyAReplacedPlanThatHasHadSubscribersOffersMoveSubscribers()
    {
        var newer = Plan("Gold");
        var withPeople = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        var cut = RenderPage(newer, withPeople, Plan("Empty", active: false, subscribers: 0, replacedBy: Guid.NewGuid()), Plan("Live", subscribers: 3));
        cut.Find("#show-inactive-plans").Change(true);

        Assert.Single(cut.FindAll("[data-testid=plan-move-subscribers]"));           // not the one nobody was on, and not a plan nothing replaced
    }

    [Fact]
    public void MovingAskedForShowsWhoWouldMoveAndDoesItOnlyOnConfirmation()
    {
        var newer = Plan("Gold");
        var replaced = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        _plans.Setup(p => p.GetMovePreviewAsync(replaced.Id)).ReturnsAsync(Preview(5, 4, ("the newer version costs more on their billing term", 1)));
        _plans.Setup(p => p.MoveSubscribersAsync(replaced.Id)).ReturnsAsync(new PlanMoveResultDto { Moved = 4, Stayed = 1 });
        var cut = RenderReplaced(replaced, newer);

        MoveButton(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains("Move 4 of the 5 organization(s)", cut.Find("[data-testid=move-confirm]").TextContent));
        Assert.Contains("1 would stay", cut.Find("[data-testid=move-stay]").TextContent);
        _plans.Verify(p => p.MoveSubscribersAsync(It.IsAny<Guid>()), Times.Never());       // nothing yet

        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.Contains("4 organization(s) moved to the latest version; 1 stayed.", cut.Markup));
        _plans.Verify(p => p.MoveSubscribersAsync(replaced.Id), Times.Once());
    }

    [Fact]
    public void DecliningTheMoveMovesNobody()
    {
        var newer = Plan("Gold");
        var replaced = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        _plans.Setup(p => p.GetMovePreviewAsync(replaced.Id)).ReturnsAsync(Preview(5, 5));
        var cut = RenderReplaced(replaced, newer);
        MoveButton(cut).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=move-confirm]")));

        cut.Find(".modal-footer .btn-secondary").Click();

        _plans.Verify(p => p.MoveSubscribersAsync(It.IsAny<Guid>()), Times.Never());
        Assert.Empty(cut.FindAll("[data-testid=move-confirm]"));
    }

    [Fact]
    public void WhenNobodyIsLeftOnThatVersionItSaysSoInsteadOfAskingToMoveNobody()
    {
        var newer = Plan("Gold");
        var replaced = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        _plans.Setup(p => p.GetMovePreviewAsync(replaced.Id)).ReturnsAsync(Preview(0, 0));
        var cut = RenderReplaced(replaced, newer);

        MoveButton(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains("No organization is on this version any more.", cut.Find("[data-testid=plans-info]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=move-confirm]"));
    }

    [Fact]
    public void WhenNoneCanBeMovedItSaysWhyInsteadOfOfferingAButtonThatDoesNothing()
    {
        var newer = Plan("Gold");
        var replaced = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        _plans.Setup(p => p.GetMovePreviewAsync(replaced.Id)).ReturnsAsync(Preview(2, 0, ("the newer version keeps history for fewer days", 2)));
        var cut = RenderReplaced(replaced, newer);

        MoveButton(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains("2 × the newer version keeps history for fewer days", cut.Find("[data-testid=plans-info]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=move-confirm]"));
        _plans.Verify(p => p.MoveSubscribersAsync(It.IsAny<Guid>()), Times.Never());
    }

    [Fact]
    public void AFailedPreviewIsReportedAndNothingIsMoved()
    {
        var newer = Plan("Gold");
        var replaced = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        _plans.Setup(p => p.GetMovePreviewAsync(replaced.Id)).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderReplaced(replaced, newer);

        MoveButton(cut).Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".alert-danger")));
        _plans.Verify(p => p.MoveSubscribersAsync(It.IsAny<Guid>()), Times.Never());
    }

    [Fact]
    public void AFailedMoveIsReportedAndThePageStaysUsable()
    {
        var newer = Plan("Gold");
        var replaced = Plan("Gold", active: false, subscribers: 5, replacedBy: newer.Id);
        _plans.Setup(p => p.GetMovePreviewAsync(replaced.Id)).ReturnsAsync(Preview(5, 5));
        _plans.Setup(p => p.MoveSubscribersAsync(replaced.Id)).ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderReplaced(replaced, newer);
        MoveButton(cut).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-footer .btn-primary")));

        cut.Find(".modal-footer .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".alert-danger")));
        Assert.NotEmpty(cut.FindAll("[data-testid=plan-move-subscribers]"));
    }
}
