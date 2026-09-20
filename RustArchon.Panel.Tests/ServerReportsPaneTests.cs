// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Pages.Servers;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The Reports tab's inbox. Everything a report says came from a game server, so it is shown and never followed; a 403 only ever hides or
/// disables the part that needs the permission; and the picture is fetched only for the report that is open.
/// </summary>
public class ServerReportsPaneTests : BunitContext
{
    private readonly Mock<IRustServerApiClient> _client = new();
    private readonly Guid _serverId = Guid.NewGuid();
    private int _lastNewCount = -1;

    public ServerReportsPaneTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(ReportTestSupport.Localizer());
        GivenReports();
        _client.Setup(c => c.GetReportCountAsync(_serverId)).ReturnsAsync(new ServerReportCountDto { New = 2 });
    }

    private static ServerReportDto Report(
        string subject = "Cheating", ServerReportType type = ServerReportType.Cheat, ServerReportStatus status = ServerReportStatus.New,
        string message = "aimbot", string? reporter = "Reporter", string? target = "Cheater", bool screenshot = false,
        string? pluginDetail = null, bool parseFailed = false, ServerReportSource source = ServerReportSource.Native) => new()
    {
        Id = Guid.NewGuid(), RustServerId = Guid.Empty, ReceivedAtUtc = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
        Type = type, Status = status, Subject = subject, Message = message, ReporterName = reporter, ReporterSteamId = "76561198000000002",
        TargetName = target, TargetSteamId = "76561198000000001", Position = "(10, 20, 30)", MinutesPlayed = 42,
        HasScreenshot = screenshot, PluginDetailJson = pluginDetail, ParseFailed = parseFailed, Source = source
    };

    private void GivenReports(params ServerReportDto[] reports) =>
        _client.Setup(c => c.GetReportsAsync(_serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ServerReportType?>(), It.IsAny<ServerReportStatus?>(), It.IsAny<string?>()))
            .ReturnsAsync(new ServerReportListDto { Items = [.. reports], TotalCount = reports.Length, PageNumber = 1, PageSize = 25 });

    private IRenderedComponent<ServerReportsPane> RenderPane(int refresh = 0) =>
        Render<ServerReportsPane>(p => p
            .Add(x => x.ServerId, _serverId)
            .Add(x => x.RefreshToken, refresh)
            .Add(x => x.NewCountChanged, EventCallback.Factory.Create<int>(this, n => _lastNewCount = n)));

    private static void OpenFirst(IRenderedComponent<ServerReportsPane> cut) => cut.Find("[data-testid=report-row]").Click();

    // ---- the list ----

    [Fact]
    public void ListsEachReportWithItsTypeReporterTargetSubjectAndStatus()
    {
        GivenReports(Report("Cheating", ServerReportType.Cheat), Report("Lag", ServerReportType.Bug, ServerReportStatus.Reviewing, reporter: "Bob", target: null));

        var cut = RenderPane();

        var rows = cut.FindAll("[data-testid=report-row]");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Cheat", rows[0].TextContent);
        Assert.Contains("Reporter", rows[0].TextContent);
        Assert.Contains("Cheater", rows[0].TextContent);
        Assert.Contains("Cheating", rows[0].TextContent);
        Assert.Contains("New", rows[0].TextContent);
        Assert.Contains("Bug", rows[1].TextContent);
        Assert.Contains("Reviewing", rows[1].TextContent);
    }

    [Fact]
    public void AnEmptyInboxSaysSo()
    {
        var cut = RenderPane();

        Assert.Contains("No reports yet", cut.Find("[data-testid=reports-empty]").TextContent);
    }

    [Fact]
    public void AReportWithNoNamesShowsTheSteamIdInstead()
    {
        GivenReports(Report(reporter: null, target: null));

        var cut = RenderPane();

        var row = cut.Find("[data-testid=report-row]").TextContent;
        Assert.Contains("76561198000000002", row);
        Assert.Contains("76561198000000001", row);
    }

    [Fact]
    public void ACallerWhoMayNotReadReportsSeesAReasonNotAnError()
    {
        _client.Setup(c => c.GetReportsAsync(_serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ServerReportType?>(), It.IsAny<ServerReportStatus?>(), It.IsAny<string?>()))
            .ThrowsAsync(ReportTestSupport.Forbidden);

        var cut = RenderPane();

        cut.WaitForElement("[data-testid=reports-forbidden]");
        Assert.Empty(cut.FindAll("[data-testid=reports-error]"));
        Assert.Empty(cut.FindAll("[data-testid=reports-table]"));
    }

    [Fact]
    public void AFailureToLoadIsShownAsAnError()
    {
        _client.Setup(c => c.GetReportsAsync(_serverId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ServerReportType?>(), It.IsAny<ServerReportStatus?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderPane();

        Assert.Contains("Failed to load", cut.WaitForElement("[data-testid=reports-error]").TextContent);
    }

    [Fact]
    public void ThePageIsToldHowManyAreStillNewForTheTabsBadge()
    {
        GivenReports(Report());

        RenderPane();

        Assert.Equal(2, _lastNewCount);
    }

    [Fact]
    public void ABadgeCountThatCannotBeReadDoesNotBreakTheList()
    {
        GivenReports(Report());
        _client.Setup(c => c.GetReportCountAsync(_serverId)).ThrowsAsync(new InvalidOperationException("boom"));

        var cut = RenderPane();

        Assert.Single(cut.FindAll("[data-testid=report-row]"));
        Assert.Empty(cut.FindAll("[data-testid=reports-error]"));
    }

    // ---- filters and paging ----

    [Fact]
    public void FilteringByTypeAsksForThatTypeFromTheFirstPage()
    {
        GivenReports(Report());
        var cut = RenderPane();

        cut.Find("[data-testid=reports-type-filter]").Change(((int)ServerReportType.Abuse).ToString());

        cut.WaitForAssertion(() => _client.Verify(c => c.GetReportsAsync(_serverId, 1, 25, ServerReportType.Abuse, null, null), Times.Once));
    }

    [Fact]
    public void FilteringByStatusAsksForThatStatus()
    {
        GivenReports(Report());
        var cut = RenderPane();

        cut.Find("[data-testid=reports-status-filter]").Change(((int)ServerReportStatus.Dismissed).ToString());

        cut.WaitForAssertion(() => _client.Verify(c => c.GetReportsAsync(_serverId, 1, 25, null, ServerReportStatus.Dismissed, null), Times.Once));
    }

    [Fact]
    public void ChoosingAllTypesAgainRemovesTheFilter()
    {
        GivenReports(Report());
        var cut = RenderPane();
        cut.Find("[data-testid=reports-type-filter]").Change("3");

        cut.Find("[data-testid=reports-type-filter]").Change("");

        cut.WaitForAssertion(() => _client.Verify(c => c.GetReportsAsync(_serverId, 1, 25, null, null, null), Times.AtLeast(2)));
    }

    [Fact]
    public void ANonsenseFilterValueIsIgnoredNotSentToTheApi()
    {
        GivenReports(Report());
        var cut = RenderPane();

        cut.Find("[data-testid=reports-type-filter]").Change("999");

        cut.WaitForAssertion(() => _client.Verify(c => c.GetReportsAsync(_serverId, 1, 25, null, null, null), Times.AtLeast(2)));
    }

    [Fact]
    public void MoreThanAPageOfReportsShowsPagingThatMovesThroughThem()
    {
        _client.Setup(c => c.GetReportsAsync(_serverId, It.IsAny<int>(), 25, It.IsAny<ServerReportType?>(), It.IsAny<ServerReportStatus?>(), It.IsAny<string?>()))
            .ReturnsAsync(new ServerReportListDto { Items = [Report()], TotalCount = 60, PageNumber = 1, PageSize = 25 });
        var cut = RenderPane();

        Assert.True(cut.Find("[data-testid=reports-prev]").HasAttribute("disabled"));
        cut.Find("[data-testid=reports-next]").Click();

        cut.WaitForAssertion(() => _client.Verify(c => c.GetReportsAsync(_serverId, 2, 25, null, null, null), Times.Once));
    }

    [Fact]
    public void ASinglePageShowsNoPaging()
    {
        GivenReports(Report());

        var cut = RenderPane();

        Assert.Empty(cut.FindAll("[data-testid=reports-next]"));
    }

    [Fact]
    public void ARefreshFromTheServerReloadsTheList()
    {
        GivenReports(Report("First"));
        var cut = RenderPane(refresh: 0);
        GivenReports(Report("First"), Report("Second"));

        cut.Render(p => p.Add(x => x.RefreshToken, 1));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=report-row]").Count));
    }

    [Fact]
    public void AReRenderWithTheSameRefreshTokenDoesNotReload()
    {
        GivenReports(Report());
        var cut = RenderPane(refresh: 3);

        cut.Render(p => p.Add(x => x.RefreshToken, 3));

        _client.Verify(c => c.GetReportsAsync(_serverId, 1, 25, null, null, null), Times.Once);
    }

    // ---- one report ----

    [Fact]
    public void OpeningAReportShowsEverythingItSaid()
    {
        GivenReports(Report(message: "he was flying", source: ServerReportSource.Native | ServerReportSource.Plugin));
        var cut = RenderPane();

        OpenFirst(cut);

        var detail = cut.Find("[data-testid=report-detail]").TextContent;
        Assert.Contains("he was flying", detail);
        Assert.Contains("(10, 20, 30)", detail);
        Assert.Contains("42", detail);
        Assert.Contains("The game server and the RustArchon plugin", detail);
        Assert.Contains("76561198000000001", detail);
    }

    [Fact]
    public void ThePlayersAreLinkedToTheirRecordOnThisServer()
    {
        GivenReports(Report());
        var cut = RenderPane();

        OpenFirst(cut);

        var hrefs = cut.FindAll("[data-testid=report-detail] a").Select(a => a.GetAttribute("href")).ToList();
        Assert.Contains($"/servers/{_serverId}/players/76561198000000001", hrefs);
        Assert.Contains($"/servers/{_serverId}/players/76561198000000002", hrefs);
    }

    /// <summary>The text came from a game server: a report reading like markup must render as text, never as markup.</summary>
    [Fact]
    public void MarkupInAReportIsShownAsTextNeverExecuted()
    {
        GivenReports(Report(subject: "<img src=x onerror=alert(1)>", message: "<script>alert('xss')</script>", reporter: "<b>bold</b>", target: null));
        var cut = RenderPane();

        OpenFirst(cut);

        Assert.Empty(cut.FindAll("[data-testid=reports-pane] script"));
        Assert.Empty(cut.FindAll("[data-testid=reports-pane] img[onerror]"));
        Assert.Empty(cut.FindAll("[data-testid=reports-pane] b"));
        Assert.Contains("<script>alert('xss')</script>", cut.Find("[data-testid=report-message]").TextContent);
    }

    [Fact]
    public void AReportThatCouldNotBeReadSaysSo()
    {
        GivenReports(Report(parseFailed: true));
        var cut = RenderPane();

        OpenFirst(cut);

        cut.Find("[data-testid=report-parse-failed]");
    }

    [Fact]
    public void ThePluginsExtraDetailIsPrintedAsIndentedText()
    {
        GivenReports(Report(pluginDetail: "{\"nearby\":3,\"team\":[\"a\",\"b\"]}"));
        var cut = RenderPane();

        OpenFirst(cut);

        var detail = cut.Find("[data-testid=report-plugin-detail]").TextContent;
        Assert.Contains("\"nearby\": 3", detail);
        Assert.Contains("\n", detail);
    }

    [Fact]
    public void PluginDetailThatIsNotJsonIsStillShownAsText()
    {
        GivenReports(Report(pluginDetail: "not json at all"));
        var cut = RenderPane();

        OpenFirst(cut);

        Assert.Contains("not json at all", cut.Find("[data-testid=report-plugin-detail]").TextContent);
    }

    [Fact]
    public void AReportWithNoPluginDetailShowsNoSuchSection()
    {
        GivenReports(Report(pluginDetail: null));
        var cut = RenderPane();

        OpenFirst(cut);

        Assert.Empty(cut.FindAll("[data-testid=report-plugin-detail]"));
    }

    // ---- the screenshot ----

    [Fact]
    public void TheScreenshotIsFetchedOnlyForTheReportThatIsOpen()
    {
        var withPicture = Report(screenshot: true);
        GivenReports(withPicture, Report(screenshot: true));
        _client.Setup(c => c.GetReportScreenshotAsync(_serverId, withPicture.Id)).ReturnsAsync(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF]) });
        var cut = RenderPane();

        _client.Verify(c => c.GetReportScreenshotAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        OpenFirst(cut);

        cut.WaitForAssertion(() => _client.Verify(c => c.GetReportScreenshotAsync(_serverId, withPicture.Id), Times.Once));
        JSInterop.VerifyInvoke("reportScreenshot.show");
    }

    [Fact]
    public void AReportWithNoScreenshotNeverAsksForOne()
    {
        GivenReports(Report(screenshot: false));
        var cut = RenderPane();

        OpenFirst(cut);

        _client.Verify(c => c.GetReportScreenshotAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        Assert.Empty(cut.FindAll("[data-testid=report-screenshot]"));
    }

    [Fact]
    public void AScreenshotThatCannotBeLoadedSaysSoAndTheRestOfTheReportStillShows()
    {
        var report = Report(screenshot: true, message: "still readable");
        GivenReports(report);
        _client.Setup(c => c.GetReportScreenshotAsync(_serverId, report.Id)).ThrowsAsync(new InvalidOperationException("gone"));
        var cut = RenderPane();

        OpenFirst(cut);

        cut.WaitForElement("[data-testid=report-screenshot-error]");
        Assert.Contains("still readable", cut.Find("[data-testid=report-message]").TextContent);
    }

    // ---- changing status ----

    [Fact]
    public void EachButtonLeadsToADifferentStatusThanTheCurrentOne()
    {
        GivenReports(Report(status: ServerReportStatus.Reviewing));
        var cut = RenderPane();

        OpenFirst(cut);

        Assert.Empty(cut.FindAll("[data-testid=report-set-reviewing]"));
        cut.Find("[data-testid=report-set-new]");
        cut.Find("[data-testid=report-set-actioned]");
        cut.Find("[data-testid=report-set-dismissed]");
    }

    [Fact]
    public void ChangingTheStatusSendsItAndUpdatesTheRowAndTheBadge()
    {
        var report = Report();
        GivenReports(report);
        _client.Setup(c => c.SetReportStatusAsync(_serverId, report.Id, It.Is<UpdateServerReportStatusDto>(d => d.Status == ServerReportStatus.Actioned)))
            .ReturnsAsync(() => { var updated = Report(); updated.Id = report.Id; updated.Status = ServerReportStatus.Actioned; return updated; });
        _client.Setup(c => c.GetReportCountAsync(_serverId)).ReturnsAsync(new ServerReportCountDto { New = 0 });
        var cut = RenderPane();
        OpenFirst(cut);

        cut.Find("[data-testid=report-set-actioned]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Actioned", cut.Find("[data-testid=report-status]").TextContent));
        Assert.Equal(0, _lastNewCount);
    }

    [Fact]
    public void ACallerWhoMayViewButNotChangeIsToldSoAndNothingChanges()
    {
        var report = Report();
        GivenReports(report);
        _client.Setup(c => c.SetReportStatusAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdateServerReportStatusDto>()))
            .ThrowsAsync(ReportTestSupport.Forbidden);
        var cut = RenderPane();
        OpenFirst(cut);

        cut.Find("[data-testid=report-set-dismissed]").Click();

        cut.WaitForElement("[data-testid=report-status-forbidden]");
        Assert.Contains("New", cut.Find("[data-testid=report-status]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=reports-forbidden]")); // the inbox itself is still readable
    }

    [Fact]
    public void AFailedStatusChangeIsShownAsAnError()
    {
        GivenReports(Report());
        _client.Setup(c => c.SetReportStatusAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdateServerReportStatusDto>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cut = RenderPane();
        OpenFirst(cut);

        cut.Find("[data-testid=report-set-actioned]").Click();

        Assert.Contains("Failed to change", cut.WaitForElement("[data-testid=reports-error]").TextContent);
    }

    // ---- the address card ----

    [Fact]
    public void TheForwardingCardIsAlwaysThereAboveTheInbox()
    {
        var cut = RenderPane();

        cut.Find("[data-testid=report-forwarding-card]");
    }
}
