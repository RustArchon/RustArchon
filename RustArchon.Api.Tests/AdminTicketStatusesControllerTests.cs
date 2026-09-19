// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="AdminTicketStatusesController"/> - the admin ticket-status management page.
/// Runs against a real (throwaway, Testcontainers-hosted) Postgres - see <see cref="PostgresFixture"/>.
/// </summary>
public class AdminTicketStatusesControllerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<(AdminTicketStatusesController Controller, ApiDbContext Context)> CreateControllerAsync()
    {
        var context = new ApiDbContext(postgres.Options, tenantContext: null);
        await TicketStatusSeeder.EnsureDefaultsAsync(context, NullLogger.Instance);

        var controller = new AdminTicketStatusesController(new TicketStatusRepository(context));

        return (controller, context);
    }

    // Every test in this class shares one Postgres container (see PostgresFixture's own remarks) and
    // isn't reset between tests, so a status one test creates is still there for the next - names below
    // carry a Guid suffix for the same reason Queue-seeding tests elsewhere in this project suffix their
    // slugs, and List's own assertions check for specific rows rather than an exact count.

    [Fact]
    public async Task List_IncludesTheSevenSeededStatuses()
    {
        var (controller, _) = await CreateControllerAsync();

        var result = await controller.List(CancellationToken.None);

        var list = Assert.IsAssignableFrom<IReadOnlyList<TicketStatusDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(list.Count >= 7);
        Assert.Contains(list, s => s.Slug == TicketStatusSeeder.Slugs.Resolved && s.IsProtected && !s.IsClosed);
        Assert.Contains(list, s => s.Slug == TicketStatusSeeder.Slugs.Reopened && s.IsProtected && !s.IsClosed);
        Assert.Contains(list, s => s.Slug == TicketStatusSeeder.Slugs.Cancelled && s.IsProtected && s.IsClosed);
    }

    [Fact]
    public async Task Create_AddsANonProtectedStatusWithASlugDerivedFromItsName()
    {
        var (controller, _) = await CreateControllerAsync();
        var name = $"Escalated! {Guid.NewGuid():N}";

        var result = await controller.Create(
            new CreateTicketStatusRequestDto { Name = name, IsClosed = false, DisplayOrder = 15 },
            CancellationToken.None);

        var dto = Assert.IsType<TicketStatusDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.StartsWith("escalated-", dto.Slug);
        Assert.False(dto.IsProtected);
    }

    [Fact]
    public async Task Create_WithANameThatCollidesOnSlug_DisambiguatesWithASuffix()
    {
        var (controller, _) = await CreateControllerAsync();
        var name = $"Escalated {Guid.NewGuid():N}";

        var first = await controller.Create(
            new CreateTicketStatusRequestDto { Name = name, IsClosed = false, DisplayOrder = 15 },
            CancellationToken.None);
        var firstSlug = ((TicketStatusDto)((OkObjectResult)first.Result!).Value!).Slug;

        var result = await controller.Create(
            new CreateTicketStatusRequestDto { Name = name, IsClosed = false, DisplayOrder = 16 },
            CancellationToken.None);

        var dto = Assert.IsType<TicketStatusDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.NotEqual(firstSlug, dto.Slug);
        Assert.StartsWith(firstSlug, dto.Slug);
    }

    [Fact]
    public async Task Update_OnAProtectedStatus_AllowsRenameButRefusesDeactivation()
    {
        // Renames back to its seeded name at the end - Resolved is shared with every other test in this
        // class (see this class's own remarks on why), and List_IncludesTheSixSeededStatuses relies on
        // its seeded name/IsClosed staying put regardless of run order.
        var (controller, context) = await CreateControllerAsync();
        var resolved = await context.Set<TicketStatus>().FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Resolved);
        var originalDisplayOrder = resolved.DisplayOrder;

        try
        {
            var renamed = await controller.Update(
                resolved.Id,
                new UpdateTicketStatusRequestDto
                {
                    Name = "Fixed", IsClosed = false, DisplayOrder = originalDisplayOrder, IsActive = true
                },
                CancellationToken.None);
            var renamedDto = Assert.IsType<TicketStatusDto>(Assert.IsType<OkObjectResult>(renamed.Result).Value);
            Assert.Equal("Fixed", renamedDto.Name);

            var deactivated = await controller.Update(
                resolved.Id,
                new UpdateTicketStatusRequestDto
                {
                    Name = "Fixed", IsClosed = false, DisplayOrder = originalDisplayOrder, IsActive = false
                },
                CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(deactivated.Result);
        }
        finally
        {
            await controller.Update(
                resolved.Id,
                new UpdateTicketStatusRequestDto
                {
                    Name = "Resolved", IsClosed = false, DisplayOrder = originalDisplayOrder, IsActive = true
                },
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Delete_OnAProtectedStatus_ReturnsBadRequest()
    {
        var (controller, context) = await CreateControllerAsync();
        var submitted = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Submitted);

        var result = await controller.Delete(submitted.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>Cancelled is protected even though no code ever assigns it automatically - it's meant
    /// to be a permanent part of the lifecycle, not something an admin could delete out from under old
    /// ticket history. See <c>TicketStatusSeeder</c>'s remarks.</summary>
    [Fact]
    public async Task Delete_OnCancelled_ReturnsBadRequest()
    {
        var (controller, context) = await CreateControllerAsync();
        var cancelled = await context.Set<TicketStatus>()
            .FirstAsync(s => s.Slug == TicketStatusSeeder.Slugs.Cancelled);

        var result = await controller.Delete(cancelled.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Delete_OnAStatusStillAssignedToATicket_ReturnsBadRequest()
    {
        var (controller, context) = await CreateControllerAsync();
        var custom = await controller.Create(
            new CreateTicketStatusRequestDto
            {
                Name = $"Escalated {Guid.NewGuid():N}", IsClosed = false, DisplayOrder = 15
            },
            CancellationToken.None);
        var customDto = ((TicketStatusDto)((OkObjectResult)custom.Result!).Value!);

        var queue = new Queue
        {
            Id = Guid.NewGuid(), Name = "Support", Slug = $"support-{Guid.NewGuid()}", IsActive = true,
            CreatedById = Guid.Empty, CreatedOn = DateTimeOffset.UtcNow
        };
        context.Set<Queue>().Add(queue);
        context.Set<Ticket>().Add(new Ticket
        {
            Id = Guid.NewGuid(), TenantId = null, SubmitterEmail = "customer@example.com",
            SubmitterName = "A Customer", QueueId = queue.Id, Subject = "Help", StatusId = customDto.Id,
            SubmittedOn = DateTimeOffset.UtcNow, CreatedOn = DateTimeOffset.UtcNow, CreatedById = Guid.Empty
        });
        await context.SaveChangesAsync();

        var result = await controller.Delete(customDto.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Delete_OnAnUnusedNonProtectedStatus_Succeeds()
    {
        var (controller, _) = await CreateControllerAsync();
        var custom = await controller.Create(
            new CreateTicketStatusRequestDto
            {
                Name = $"Escalated {Guid.NewGuid():N}", IsClosed = false, DisplayOrder = 15
            },
            CancellationToken.None);
        var customDto = ((TicketStatusDto)((OkObjectResult)custom.Result!).Value!);

        var result = await controller.Delete(customDto.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }
}
