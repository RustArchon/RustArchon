// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// A report's notes and assignee against a real Postgres: notes stay with their report, in order, and inside their organization; the
/// author and organization are filled in by the repository; and removing a server's reports takes their notes with them (the cascade is
/// the database's, because <see cref="ServerReportRepository.DeleteForServerAcrossTenantsAsync"/> never loads the notes).
/// </summary>
public class ServerReportNoteTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed class FixedUserContext(Guid userId) : IUserContext
    {
        public Task<Guid?> GetCurrentUserIdAsync() => Task.FromResult<Guid?>(userId);
    }

    private sealed record Harness(
        ApiDbContext Context, ServerReportRepository Reports, ServerReportNoteRepository Notes, Guid TenantId, Guid ServerId, Guid UserId);

    private async Task<Harness> CreateAsync(Guid? tenant = null, Guid? server = null, Guid? user = null)
    {
        var tenantId = tenant ?? Guid.NewGuid();
        var userId = user ?? Guid.NewGuid();
        var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        if (!await context.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
        {
            context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Report notes tenant {tenantId}", IsActive = true });
            await context.SaveChangesAsync();
        }

        var userContext = new FixedUserContext(userId);
        return new Harness(
            context, new ServerReportRepository(context, userContext), new ServerReportNoteRepository(context, userContext),
            tenantId, server ?? Guid.NewGuid(), userId);
    }

    private static async Task<ServerReport> AddReportAsync(Harness h, Guid? server = null) =>
        await h.Reports.AddAsync(new ServerReport
        {
            RustServerId = server ?? h.ServerId, ReceivedAtUtc = DateTimeOffset.UtcNow, Subject = "Cheating", Message = "aimbot"
        });

    [Fact]
    public async Task ANoteRecordsWhoWroteItAndWhichOrganizationItBelongsTo()
    {
        var h = await CreateAsync();
        var report = await AddReportAsync(h);

        var note = await h.Notes.AddAsync(new ServerReportNote { ServerReportId = report.Id, Content = "Watched the demo." });

        var stored = await h.Context.ServerReportNotes.AsNoTracking().SingleAsync(n => n.Id == note.Id);
        Assert.Equal(h.UserId, stored.CreatedById);
        Assert.Equal(h.TenantId, stored.TenantId);
        Assert.Equal("Watched the demo.", stored.Content);
        Assert.NotEqual(default, stored.CreatedOn);
    }

    [Fact]
    public async Task AReportsNotesComeBackOldestFirstAndOnlyThatReportsOwn()
    {
        var h = await CreateAsync();
        var report = await AddReportAsync(h);
        var other = await AddReportAsync(h);

        var first = await h.Notes.AddAsync(new ServerReportNote { ServerReportId = report.Id, Content = "first" });
        await h.Notes.AddAsync(new ServerReportNote { ServerReportId = other.Id, Content = "someone else's report" });
        await Task.Delay(5);
        var second = await h.Notes.AddAsync(new ServerReportNote { ServerReportId = report.Id, Content = "second" });

        var notes = await h.Notes.GetForReportAsync(report.Id);

        Assert.Equal([first.Id, second.Id], notes.Select(n => n.Id));
    }

    [Fact]
    public async Task AnotherOrganizationCannotReadTheNotes()
    {
        var mine = await CreateAsync();
        var report = await AddReportAsync(mine);
        await mine.Notes.AddAsync(new ServerReportNote { ServerReportId = report.Id, Content = "private to us" });

        var theirs = await CreateAsync();

        Assert.Empty(await theirs.Notes.GetForReportAsync(report.Id));
    }

    [Fact]
    public async Task RemovingAServersReportsRemovesTheirNotesToo()
    {
        var h = await CreateAsync();
        var doomed = await AddReportAsync(h);
        var kept = await AddReportAsync(h, server: Guid.NewGuid());
        await h.Notes.AddAsync(new ServerReportNote { ServerReportId = doomed.Id, Content = "goes with the server" });
        var survivor = await h.Notes.AddAsync(new ServerReportNote { ServerReportId = kept.Id, Content = "stays" });

        Assert.Equal(1, await h.Reports.DeleteForServerAcrossTenantsAsync(h.ServerId));

        var remaining = await h.Context.ServerReportNotes.AcrossAllTenants().AsNoTracking()
            .Where(n => n.ServerReportId == doomed.Id || n.ServerReportId == kept.Id).ToListAsync();
        Assert.Equal([survivor.Id], remaining.Select(n => n.Id));
    }

    [Fact]
    public async Task AReportStartsUnassignedAndTheAssigneeIsStored()
    {
        var h = await CreateAsync();
        var report = await AddReportAsync(h);
        Assert.Null(report.AssignedToUserId);

        var assignee = Guid.NewGuid();
        report.AssignedToUserId = assignee;
        await h.Reports.UpdateAsync(report);

        var stored = await h.Reports.GetByIdAsync(report.Id, null);
        Assert.Equal(assignee, stored!.AssignedToUserId);

        stored.AssignedToUserId = null;
        await h.Reports.UpdateAsync(stored);
        Assert.Null((await h.Reports.GetByIdAsync(report.Id, null))!.AssignedToUserId);
    }
}
