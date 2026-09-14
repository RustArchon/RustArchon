// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="RconEventRepository.GetForServerAsync"/>'s <c>includeNonInteractive</c>
/// filter - the one place standing between a non-interactive (potentially privileged) <see cref="RconEvent"/>
/// row and an ordinary tenant user's response. See <see cref="RconEvent"/>'s own remarks: this table
/// persists everything unconditionally, so this filter - not "was it ever written" - is what actually
/// keeps a background row from reaching a caller who shouldn't see it.
/// </summary>
public class RconEventRepositoryTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static RconEvent NewEvent(Guid serverId, bool interactive, RconEventDirection direction, string message) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        RustServerId = serverId,
        CapturedAtUtc = DateTimeOffset.UtcNow,
        Identifier = 0,
        Type = string.Empty,
        Message = message,
        Interactive = interactive,
        Direction = direction
    };

    [Fact]
    public async Task DefaultsToExcludingNonInteractiveRows()
    {
        await using var context = CreateContext();
        var serverId = Guid.NewGuid();
        context.Set<RconEvent>().AddRange(
            NewEvent(serverId, interactive: true, RconEventDirection.Received, "operator command"),
            NewEvent(serverId, interactive: false, RconEventDirection.Received, "serverinfo poll - secret data"));
        await context.SaveChangesAsync();

        var repository = new RconEventRepository(context);
        var result = await repository.GetForServerAsync(serverId, new QueryOptions<RconEvent> { PageNumber = 1, PageSize = 100 });

        var item = Assert.Single(result.Items);
        Assert.Equal("operator command", item.Message);
    }

    [Fact]
    public async Task IncludeNonInteractiveReturnsEverything()
    {
        await using var context = CreateContext();
        var serverId = Guid.NewGuid();
        context.Set<RconEvent>().AddRange(
            NewEvent(serverId, interactive: true, RconEventDirection.Received, "operator command"),
            NewEvent(serverId, interactive: false, RconEventDirection.Received, "serverinfo poll - secret data"));
        await context.SaveChangesAsync();

        var repository = new RconEventRepository(context);
        var result = await repository.GetForServerAsync(
            serverId, new QueryOptions<RconEvent> { PageNumber = 1, PageSize = 100 }, includeNonInteractive: true);

        Assert.Equal(2, result.Items.Count());
    }

    [Fact]
    public async Task SentAndReceivedRowsForTheSameCommandAreBothPersistedAndReturned()
    {
        await using var context = CreateContext();
        var serverId = Guid.NewGuid();
        context.Set<RconEvent>().AddRange(
            NewEvent(serverId, interactive: true, RconEventDirection.Sent, "playerlist"),
            NewEvent(serverId, interactive: true, RconEventDirection.Received, "[{\"SteamId\":\"1\"}]"));
        await context.SaveChangesAsync();

        var repository = new RconEventRepository(context);
        var result = await repository.GetForServerAsync(serverId, new QueryOptions<RconEvent> { PageNumber = 1, PageSize = 100 });

        Assert.Equal(2, result.Items.Count());
        Assert.Contains(result.Items, e => e.Direction == RconEventDirection.Sent);
        Assert.Contains(result.Items, e => e.Direction == RconEventDirection.Received);
    }
}
