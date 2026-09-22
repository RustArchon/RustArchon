// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using JumpStart.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Mapping;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The moderation inbox's endpoints. The repositories are tenant-filtered in production, so "not one of yours" reaches this
/// controller as a null from the server lookup; what is tested here is that every route honours that, and that a report can only be
/// read through the server it belongs to.
/// </summary>
public class ServerReportsControllerTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IRustServerRepository> _servers = new();
    private readonly Mock<IServerReportRepository> _reports = new();
    private readonly Mock<IServerReportNoteRepository> _notes = new();
    private readonly Mock<IReportAssigneeService> _assignees = new();
    private readonly Mock<IObjectStorage> _storage = new();
    private readonly IMapper _mapper =
        new MapperConfiguration(c => c.AddProfile<ServerReportMappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public ServerReportsControllerTests()
    {
        _servers.Setup(s => s.GetByIdAsync(ServerId, null)).ReturnsAsync(new RustServer { Id = ServerId });
        _reports.Setup(r => r.UpdateAsync(It.IsAny<ServerReport>())).ReturnsAsync((ServerReport r) => r);
        _notes.Setup(n => n.AddAsync(It.IsAny<ServerReportNote>())).ReturnsAsync((ServerReportNote n) => n);
    }

    private ServerReportsController Create(Guid? user = null)
    {
        var claims = user is { } id ? [new Claim(ClaimTypes.NameIdentifier, id.ToString())] : Array.Empty<Claim>();
        return new ServerReportsController(_servers.Object, _reports.Object, _notes.Object, _assignees.Object, _storage.Object, _mapper, new FixedClock(Now))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")) }
            }
        };
    }

    private ServerReport GivenReport(Guid? serverId = null, string? screenshotKey = null, ServerReportStatus status = ServerReportStatus.New)
    {
        var report = new ServerReport
        {
            Id = Guid.NewGuid(), RustServerId = serverId ?? ServerId, Subject = "s", Message = "m",
            ScreenshotObjectKey = screenshotKey, Status = status
        };
        _reports.Setup(r => r.GetByIdAsync(report.Id, null)).ReturnsAsync(report);
        return report;
    }

    // ---- list / count ----

    [Fact]
    public async Task TheListMapsTheReportsAndTellsWhetherEachHasAScreenshotWithoutExposingItsStorageKey()
    {
        var withPicture = new ServerReport { Id = Guid.NewGuid(), RustServerId = ServerId, ScreenshotObjectKey = "reports/x/y.jpg", NativePayload = "raw" };
        var without = new ServerReport { Id = Guid.NewGuid(), RustServerId = ServerId };
        _reports.Setup(r => r.GetForServerAsync(ServerId, 1, 25, null, null, null)).ReturnsAsync(new PagedResult<ServerReport>
        {
            Items = [withPicture, without], TotalCount = 2, PageNumber = 1, PageSize = 25
        });

        var ok = Assert.IsType<OkObjectResult>((await Create().List(ServerId)).Result);
        var list = Assert.IsType<ServerReportListDto>(ok.Value);

        Assert.Equal(2, list.TotalCount);
        Assert.True(list.Items[0].HasScreenshot);
        Assert.False(list.Items[1].HasScreenshot);
        Assert.DoesNotContain(typeof(ServerReportDto).GetProperties(), p => p.Name is "ScreenshotObjectKey" or "NativePayload" or "PluginPayload");
    }

    [Fact]
    public async Task TheListPassesItsFiltersThroughAndCapsThePageSize()
    {
        _reports.Setup(r => r.GetForServerAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ServerReportType?>(), It.IsAny<ServerReportStatus?>(), It.IsAny<string?>()))
            .ReturnsAsync(new PagedResult<ServerReport> { Items = [], TotalCount = 0, PageNumber = 1, PageSize = 100 });

        await Create().List(ServerId, pageNumber: 3, pageSize: 100_000, ServerReportType.Cheat, ServerReportStatus.New, "76561198000000001");

        _reports.Verify(r => r.GetForServerAsync(ServerId, 3, 100, ServerReportType.Cheat, ServerReportStatus.New, "76561198000000001"), Times.Once);
    }

    [Fact]
    public async Task EveryRouteForAServerThatIsNotTheCallersOwnIsANotFound()
    {
        var other = Guid.NewGuid();
        _servers.Setup(s => s.GetByIdAsync(other, null)).ReturnsAsync((RustServer?)null);
        var reportId = Guid.NewGuid();
        var controller = Create();

        Assert.IsType<NotFoundResult>((await controller.List(other)).Result);
        Assert.IsType<NotFoundResult>((await controller.Count(other)).Result);
        Assert.IsType<NotFoundResult>((await controller.Get(other, reportId)).Result);
        Assert.IsType<NotFoundResult>(await controller.Screenshot(other, reportId));
        Assert.IsType<NotFoundResult>((await controller.SetStatus(other, reportId, new UpdateServerReportStatusDto { Status = ServerReportStatus.Actioned })).Result);
        Assert.IsType<NotFoundResult>((await controller.Assignees(other, default)).Result);
        Assert.IsType<NotFoundResult>((await controller.Assign(other, reportId, new AssignServerReportDto { AssignedToUserId = null }, default)).Result);
        Assert.IsType<NotFoundResult>((await controller.GetNotes(other, reportId)).Result);
        Assert.IsType<NotFoundResult>((await controller.AddNote(other, reportId, new SaveServerReportNoteDto { Content = "x" })).Result);
        _notes.Verify(n => n.AddAsync(It.IsAny<ServerReportNote>()), Times.Never);
        _reports.Verify(r => r.GetForServerAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<ServerReportType?>(), It.IsAny<ServerReportStatus?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task TheCountIsHowManyAreStillNew()
    {
        _reports.Setup(r => r.CountNewAsync(ServerId)).ReturnsAsync(7);

        var ok = Assert.IsType<OkObjectResult>((await Create().Count(ServerId)).Result);

        Assert.Equal(7, Assert.IsType<ServerReportCountDto>(ok.Value).New);
    }

    // ---- one report ----

    [Fact]
    public async Task AReportIsOnlyReadableThroughTheServerItBelongsTo()
    {
        var elsewhere = GivenReport(serverId: Guid.NewGuid());
        var mine = GivenReport();

        var controller = Create();
        Assert.IsType<NotFoundResult>((await controller.Get(ServerId, elsewhere.Id)).Result);
        Assert.IsType<NotFoundResult>(await controller.Screenshot(ServerId, elsewhere.Id));
        Assert.IsType<OkObjectResult>((await controller.Get(ServerId, mine.Id)).Result);
    }

    [Fact]
    public async Task AReportThatDoesNotExistIsANotFound()
    {
        Assert.IsType<NotFoundResult>((await Create().Get(ServerId, Guid.NewGuid())).Result);
    }

    // ---- the screenshot ----

    [Theory]
    [InlineData("reports/s/r.jpg", "image/jpeg")]
    [InlineData("reports/s/r.png", "image/png")]
    public async Task TheScreenshotIsServedPrivatelyWithTheRightTypeAndNoSniffing(string key, string contentType)
    {
        var report = GivenReport(screenshotKey: key);
        _storage.Setup(s => s.GetAsync(key, default)).ReturnsAsync(new ObjectContent([1, 2, 3], "application/octet-stream"));
        var controller = Create();

        var result = Assert.IsType<FileContentResult>(await controller.Screenshot(ServerId, report.Id));

        Assert.Equal(contentType, result.ContentType);
        Assert.Equal([1, 2, 3], result.FileContents);
        Assert.StartsWith("private", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
    }

    [Fact]
    public async Task AReportWithNoScreenshotOrAMissingObjectIsANotFound()
    {
        var none = GivenReport();
        var missing = GivenReport(screenshotKey: "reports/s/gone.jpg");
        _storage.Setup(s => s.GetAsync("reports/s/gone.jpg", default)).ReturnsAsync((ObjectContent?)null);
        var controller = Create();

        Assert.IsType<NotFoundResult>(await controller.Screenshot(ServerId, none.Id));
        Assert.IsType<NotFoundResult>(await controller.Screenshot(ServerId, missing.Id));
    }

    // ---- status ----

    [Fact]
    public async Task ChangingTheStatusRecordsWhoAndWhen()
    {
        var reviewer = Guid.NewGuid();
        var report = GivenReport();

        var ok = Assert.IsType<OkObjectResult>(
            (await Create(reviewer).SetStatus(ServerId, report.Id, new UpdateServerReportStatusDto { Status = ServerReportStatus.Actioned })).Result);

        Assert.Equal(ServerReportStatus.Actioned, Assert.IsType<ServerReportDto>(ok.Value).Status);
        Assert.Equal(ServerReportStatus.Actioned, report.Status);
        Assert.Equal(reviewer, report.ReviewedByUserId);
        Assert.Equal(Now, report.ReviewedAtUtc);
    }

    [Fact]
    public async Task ReopeningAReportClearsWhoReviewedIt()
    {
        var report = GivenReport(status: ServerReportStatus.Dismissed);
        report.ReviewedByUserId = Guid.NewGuid();
        report.ReviewedAtUtc = Now.AddDays(-1);

        await Create(Guid.NewGuid()).SetStatus(ServerId, report.Id, new UpdateServerReportStatusDto { Status = ServerReportStatus.New });

        Assert.Equal(ServerReportStatus.New, report.Status);
        Assert.Null(report.ReviewedByUserId);
        Assert.Null(report.ReviewedAtUtc);
    }

    [Fact]
    public async Task AStatusThatDoesNotExistIsRefusedAndNothingIsSaved()
    {
        var report = GivenReport();

        var result = await Create().SetStatus(ServerId, report.Id, new UpdateServerReportStatusDto { Status = (ServerReportStatus)42 });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _reports.Verify(r => r.UpdateAsync(It.IsAny<ServerReport>()), Times.Never);
    }

    // ---- assignment ----

    [Fact]
    public async Task AReportCanBeAssignedToSomeoneWhoCanActOnIt()
    {
        var assignee = Guid.NewGuid();
        var report = GivenReport();
        _assignees.Setup(a => a.CanBeAssignedAsync(assignee, default)).ReturnsAsync(true);

        var ok = Assert.IsType<OkObjectResult>(
            (await Create().Assign(ServerId, report.Id, new AssignServerReportDto { AssignedToUserId = assignee }, default)).Result);

        Assert.Equal(assignee, Assert.IsType<ServerReportDto>(ok.Value).AssignedToUserId);
        Assert.Equal(assignee, report.AssignedToUserId);
    }

    [Fact]
    public async Task ARefusedAssigneeLeavesTheReportAsItWasAndSavesNothing()
    {
        var previous = Guid.NewGuid();
        var report = GivenReport();
        report.AssignedToUserId = previous;
        _assignees.Setup(a => a.CanBeAssignedAsync(It.IsAny<Guid>(), default)).ReturnsAsync(false);

        var result = await Create().Assign(ServerId, report.Id, new AssignServerReportDto { AssignedToUserId = Guid.NewGuid() }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(previous, report.AssignedToUserId);
        _reports.Verify(r => r.UpdateAsync(It.IsAny<ServerReport>()), Times.Never);
    }

    [Fact]
    public async Task AssigningNobodyTakesTheAssignmentBackWithoutAskingWhoIsAllowed()
    {
        var report = GivenReport();
        report.AssignedToUserId = Guid.NewGuid();

        var ok = Assert.IsType<OkObjectResult>(
            (await Create().Assign(ServerId, report.Id, new AssignServerReportDto { AssignedToUserId = null }, default)).Result);

        Assert.Null(Assert.IsType<ServerReportDto>(ok.Value).AssignedToUserId);
        Assert.Null(report.AssignedToUserId);
        _assignees.Verify(a => a.CanBeAssignedAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task AssigningAReportOfAnotherServerIsANotFoundAndSavesNothing()
    {
        var elsewhere = GivenReport(serverId: Guid.NewGuid());
        _assignees.Setup(a => a.CanBeAssignedAsync(It.IsAny<Guid>(), default)).ReturnsAsync(true);

        var result = await Create().Assign(ServerId, elsewhere.Id, new AssignServerReportDto { AssignedToUserId = Guid.NewGuid() }, default);

        Assert.IsType<NotFoundResult>(result.Result);
        _reports.Verify(r => r.UpdateAsync(It.IsAny<ServerReport>()), Times.Never);
    }

    [Fact]
    public async Task TheAssigneeListIsWhoTheServiceSays()
    {
        var people = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _assignees.Setup(a => a.ListAsync(default)).ReturnsAsync(people);

        var ok = Assert.IsType<OkObjectResult>((await Create().Assignees(ServerId, default)).Result);

        Assert.Equal(people, Assert.IsAssignableFrom<IReadOnlyList<Guid>>(ok.Value));
    }

    [Fact]
    public async Task AssigningDoesNotTouchTheStatusOrWhoReviewedIt()
    {
        var report = GivenReport(status: ServerReportStatus.Reviewing);
        var reviewer = Guid.NewGuid();
        report.ReviewedByUserId = reviewer;
        _assignees.Setup(a => a.CanBeAssignedAsync(It.IsAny<Guid>(), default)).ReturnsAsync(true);

        await Create(Guid.NewGuid()).Assign(ServerId, report.Id, new AssignServerReportDto { AssignedToUserId = Guid.NewGuid() }, default);

        Assert.Equal(ServerReportStatus.Reviewing, report.Status);
        Assert.Equal(reviewer, report.ReviewedByUserId);
    }

    // ---- notes ----

    [Fact]
    public async Task ANoteIsSavedAgainstTheReportTrimmedAndReturnedWithItsAuthor()
    {
        var report = GivenReport();
        var author = Guid.NewGuid();
        _notes.Setup(n => n.AddAsync(It.IsAny<ServerReportNote>())).ReturnsAsync((ServerReportNote n) =>
        {
            n.Id = Guid.NewGuid();
            n.CreatedById = author;
            n.CreatedOn = Now;
            return n;
        });

        var ok = Assert.IsType<OkObjectResult>(
            (await Create(author).AddNote(ServerId, report.Id, new SaveServerReportNoteDto { Content = "  Checked the demo; clean.  " })).Result);

        var dto = Assert.IsType<ServerReportNoteDto>(ok.Value);
        Assert.Equal("Checked the demo; clean.", dto.Content);
        Assert.Equal(author, dto.AuthorUserId);
        Assert.Equal(Now, dto.CreatedOn);
        _notes.Verify(n => n.AddAsync(It.Is<ServerReportNote>(x => x.ServerReportId == report.Id && x.Content == "Checked the demo; clean.")), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankNoteIsRefusedAndNothingIsSaved(string? content)
    {
        var report = GivenReport();

        var result = await Create().AddNote(ServerId, report.Id, new SaveServerReportNoteDto { Content = content! });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _notes.Verify(n => n.AddAsync(It.IsAny<ServerReportNote>()), Times.Never);
    }

    [Fact]
    public async Task ANoteOverTheLengthLimitIsRefusedAndNothingIsSaved()
    {
        var report = GivenReport();

        var result = await Create().AddNote(ServerId, report.Id, new SaveServerReportNoteDto { Content = new string('x', ServerReportNote.MaxContentLength + 1) });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _notes.Verify(n => n.AddAsync(It.IsAny<ServerReportNote>()), Times.Never);
    }

    [Fact]
    public async Task ANoteOnAReportOfAnotherServerIsANotFoundAndNothingIsSaved()
    {
        var elsewhere = GivenReport(serverId: Guid.NewGuid());

        var result = await Create().AddNote(ServerId, elsewhere.Id, new SaveServerReportNoteDto { Content = "x" });

        Assert.IsType<NotFoundResult>(result.Result);
        _notes.Verify(n => n.AddAsync(It.IsAny<ServerReportNote>()), Times.Never);
    }

    [Fact]
    public async Task TheNotesOfAReportAreListedOldestFirstAsTheRepositoryReturnsThem()
    {
        var report = GivenReport();
        var first = new ServerReportNote { Id = Guid.NewGuid(), ServerReportId = report.Id, Content = "one", CreatedById = Guid.NewGuid(), CreatedOn = Now };
        var second = new ServerReportNote { Id = Guid.NewGuid(), ServerReportId = report.Id, Content = "two", CreatedById = Guid.NewGuid(), CreatedOn = Now.AddMinutes(1) };
        _notes.Setup(n => n.GetForReportAsync(report.Id)).ReturnsAsync([first, second]);

        var ok = Assert.IsType<OkObjectResult>((await Create().GetNotes(ServerId, report.Id)).Result);

        var list = Assert.IsAssignableFrom<IReadOnlyList<ServerReportNoteDto>>(ok.Value);
        Assert.Equal(["one", "two"], list.Select(n => n.Content));
        Assert.Equal(first.CreatedById, list[0].AuthorUserId);
    }

    [Fact]
    public async Task NotesOfAReportOfAnotherServerAreNotReadable()
    {
        var elsewhere = GivenReport(serverId: Guid.NewGuid());

        Assert.IsType<NotFoundResult>((await Create().GetNotes(ServerId, elsewhere.Id)).Result);
        _notes.Verify(n => n.GetForReportAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void AssigningAndAddingNotesNeedTheManagePermissionWhileReadingNotesOnlyNeedsViewing()
    {
        static string? PermissionOf(string method) =>
            typeof(ServerReportsController).GetMethod(method)!
                .GetCustomAttributes(typeof(JumpStart.Authorization.RequirePermissionAttribute), inherit: false)
                .Cast<JumpStart.Authorization.RequirePermissionAttribute>().SingleOrDefault()?.Permission;

        Assert.Equal(Infrastructure.PermissionCatalog.ServerManageReports, PermissionOf(nameof(ServerReportsController.Assign)));
        Assert.Equal(Infrastructure.PermissionCatalog.ServerManageReports, PermissionOf(nameof(ServerReportsController.AddNote)));
        Assert.Equal(Infrastructure.PermissionCatalog.ServerManageReports, PermissionOf(nameof(ServerReportsController.Assignees)));
        Assert.Null(PermissionOf(nameof(ServerReportsController.GetNotes)));
    }

    [Fact]
    public void ViewingNeedsOneAndChangingNeedsAnotherPermission()
    {
        var attribute = typeof(ServerReportsController).GetCustomAttributes(typeof(JumpStart.Authorization.RequirePermissionAttribute), inherit: false)
            .Cast<JumpStart.Authorization.RequirePermissionAttribute>().Single();
        var setStatus = typeof(ServerReportsController).GetMethod(nameof(ServerReportsController.SetStatus))!
            .GetCustomAttributes(typeof(JumpStart.Authorization.RequirePermissionAttribute), inherit: false)
            .Cast<JumpStart.Authorization.RequirePermissionAttribute>().Single();

        Assert.Equal(Infrastructure.PermissionCatalog.ServerViewReports, attribute.Permission);
        Assert.Equal(Infrastructure.PermissionCatalog.ServerManageReports, setStatus.Permission);
    }
}
