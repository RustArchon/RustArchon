// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The plugins Carbon says failed to load, against a real Postgres: each poll makes a server's stored reasons match the report exactly (a fixed
/// plugin disappears), an older report never brings a fixed one back, a report that says nothing leaves what is stored, a server that is not
/// running Carbon has none, and the Panel's read is the server's own and no other's.
/// </summary>
public class PluginLoadFailureTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private static async Task<Guid> SeedTenantAsync(ApiDbContext context)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Failure tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();
        return tenantId;
    }

    private static PluginLoadFailure Failure(string file, int line = 1, string message = "It broke") =>
        new() { FileName = file, Line = line, Column = 2, Message = message };

    private static Task<List<PluginLoadFailure>> StoredAsync(ApiDbContext context, Guid serverId) =>
        context.Set<PluginLoadFailure>().AcrossAllTenants().Where(f => f.RustServerId == serverId)
            .OrderBy(f => f.FileName).ThenBy(f => f.Line).ToListAsync();

    // ---- the repository ------------------------------------------------------------------------------------

    [Fact]
    public async Task ReplacingStoresTheReasonsForThatServerAndTenantWithNoAmbientTenant()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        await new PluginLoadFailureRepository(context).ReplaceForServerAsync(tenant, server, [Failure("B.cs", 9), Failure("A.cs", 5)], at);

        var stored = await StoredAsync(context, server);
        Assert.Equal(["A.cs", "B.cs"], stored.Select(f => f.FileName));
        Assert.All(stored, f =>
        {
            Assert.Equal(tenant, f.TenantId);
            Assert.Equal(at, f.CapturedAtUtc);
        });
    }

    [Fact]
    public async Task TheStoredRowsAreMadeToMatchTheNewReportSoAFixedPluginDisappears()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();
        var repository = new PluginLoadFailureRepository(context);
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);
        await repository.ReplaceForServerAsync(tenant, server, [Failure("A.cs"), Failure("B.cs")], first);

        await repository.ReplaceForServerAsync(tenant, server, [Failure("B.cs", 7, "Now it says something else")], first.AddMinutes(5));

        var stored = await StoredAsync(context, server);
        var only = Assert.Single(stored);
        Assert.Equal("B.cs", only.FileName);
        Assert.Equal("Now it says something else", only.Message);
    }

    [Fact]
    public async Task AnEmptyReportClearsWhatWasStored()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();
        var repository = new PluginLoadFailureRepository(context);
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);
        await repository.ReplaceForServerAsync(tenant, server, [Failure("A.cs")], first);

        await repository.ReplaceForServerAsync(tenant, server, [], first.AddMinutes(5));

        Assert.Empty(await StoredAsync(context, server));
    }

    [Fact]
    public async Task AReportOlderThanWhatIsStoredIsIgnoredSoAFixedPluginIsNotBroughtBack()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();
        var repository = new PluginLoadFailureRepository(context);
        var newer = DateTimeOffset.UtcNow;
        await repository.ReplaceForServerAsync(tenant, server, [Failure("Still.cs")], newer);

        await repository.ReplaceForServerAsync(tenant, server, [Failure("Old.cs"), Failure("Fixed.cs")], newer.AddMinutes(-10));

        Assert.Equal(["Still.cs"], (await StoredAsync(context, server)).Select(f => f.FileName));
    }

    [Fact]
    public async Task OneServersReportNeverTouchesAnotherServersRowsOrAnotherTenants()
    {
        await using var context = CreateContext();
        var tenantA = await SeedTenantAsync(context);
        var tenantB = await SeedTenantAsync(context);
        var serverA = Guid.NewGuid();
        var serverB = Guid.NewGuid();
        var repository = new PluginLoadFailureRepository(context);
        var at = DateTimeOffset.UtcNow;
        await repository.ReplaceForServerAsync(tenantA, serverA, [Failure("A.cs")], at);
        await repository.ReplaceForServerAsync(tenantB, serverB, [Failure("B.cs")], at);

        await repository.ReplaceForServerAsync(tenantA, serverA, [], at.AddMinutes(1));

        Assert.Empty(await StoredAsync(context, serverA));
        Assert.Equal(["B.cs"], (await StoredAsync(context, serverB)).Select(f => f.FileName));
    }

    // ---- the consumer --------------------------------------------------------------------------------------

    private static ServerPluginsCaptured Captured(
        Guid tenant, Guid server, ServerModFramework framework, IReadOnlyList<ServerPluginFailure>? failures, DateTimeOffset? at = null) =>
        new(server, tenant, framework, [new ServerPluginInfo("Kits", "k1lly0u", "4.0.0")], at ?? DateTimeOffset.UtcNow, failures);

    private static Task ConsumeAsync(ApiDbContext context, ServerPluginsCaptured message)
    {
        var consumer = new ServerPluginsCapturedConsumer(new ServerPluginRepository(context), new PluginLoadFailureRepository(context));
        return consumer.Consume(Mock.Of<ConsumeContext<ServerPluginsCaptured>>(c => c.Message == message));
    }

    [Fact]
    public async Task ACarbonReportStoresItsFailuresBoundedInLength()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();
        var longMessage = new string('m', 900);

        await ConsumeAsync(context, Captured(tenant, server, ServerModFramework.Carbon,
            [new ServerPluginFailure("BotReSpawn.cs", 1436, 59, "Cannot convert"), new ServerPluginFailure(new string('f', 400) + ".cs", 1, 1, longMessage)]));

        var stored = await StoredAsync(context, server);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, f => f.FileName == "BotReSpawn.cs" && f.Line == 1436 && f.Column == 59 && f.Message == "Cannot convert");
        Assert.All(stored, f => Assert.True(f.FileName.Length <= 260 && f.Message.Length <= 500));
    }

    [Fact]
    public async Task ARunawayNumberOfFailuresIsCapped()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();
        var many = Enumerable.Range(0, 400).Select(i => new ServerPluginFailure($"P{i}.cs", i, 1, "x")).ToList();

        await ConsumeAsync(context, Captured(tenant, server, ServerModFramework.Carbon, many));

        Assert.Equal(ServerPluginsCapturedConsumer.MaxFailures, (await StoredAsync(context, server)).Count);
    }

    [Fact]
    public async Task ACarbonReportWithNoFailuresClearsThemButOneThatSaysNothingLeavesThemAlone()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow.AddMinutes(-30);
        await ConsumeAsync(context, Captured(tenant, server, ServerModFramework.Carbon, [new ServerPluginFailure("A.cs", 1, 1, "x")], t));

        await ConsumeAsync(context, Captured(tenant, server, ServerModFramework.Carbon, null, t.AddMinutes(5)));       // an older Worker, or a reply with no section
        Assert.Single(await StoredAsync(context, server));

        await ConsumeAsync(context, Captured(tenant, server, ServerModFramework.Carbon, [], t.AddMinutes(10)));         // "failed plugins (0)"
        Assert.Empty(await StoredAsync(context, server));
    }

    [Theory]
    [InlineData(ServerModFramework.Oxide)]
    [InlineData(ServerModFramework.None)]
    public async Task AServerThatIsNoLongerRunningCarbonHasNoFailedPlugins(ServerModFramework framework)
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow.AddMinutes(-30);
        await ConsumeAsync(context, Captured(tenant, server, ServerModFramework.Carbon, [new ServerPluginFailure("A.cs", 1, 1, "x")], t));

        await ConsumeAsync(context, Captured(tenant, server, framework, null, t.AddMinutes(5)));

        Assert.Empty(await StoredAsync(context, server));
    }

    [Fact]
    public async Task ThePluginRowsAreStillReplacedWhateverTheFailuresSay()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();

        await ConsumeAsync(context, Captured(tenant, server, ServerModFramework.Carbon, []));

        Assert.Equal(["Kits"], (await context.Set<ServerPlugin>().AcrossAllTenants().Where(p => p.RustServerId == server).ToListAsync()).Select(p => p.Name));
    }

    // ---- the Panel's read ----------------------------------------------------------------------------------

    [Fact]
    public async Task TheServersFailuresAreReturnedAsTextFieldsAndAnUnknownServerIsA404()
    {
        var serverId = Guid.NewGuid();
        var servers = new Mock<IRustServerRepository>();
        servers.Setup(s => s.GetByIdAsync(serverId, null)).ReturnsAsync(new RustServer { Id = serverId });
        var failures = new Mock<IPluginLoadFailureRepository>();
        var at = DateTimeOffset.UtcNow;
        failures.Setup(f => f.GetForServerAsync(serverId)).ReturnsAsync([new PluginLoadFailure { FileName = "A.cs", Line = 3, Column = 4, Message = "<b>x</b>", CapturedAtUtc = at }]);
        var controller = new ServerPluginFailuresController(servers.Object, failures.Object);

        var found = Assert.IsType<OkObjectResult>((await controller.Get(serverId)).Result);
        var other = await controller.Get(Guid.NewGuid());

        var dto = Assert.Single(Assert.IsType<List<ServerPluginFailureDto>>(found.Value));
        Assert.Equal(("A.cs", 3, 4, "<b>x</b>", at), (dto.FileName, dto.Line, dto.Column, dto.Message, dto.CapturedAtUtc));
        Assert.IsType<NotFoundResult>(other.Result);
        failures.Verify(f => f.GetForServerAsync(It.Is<Guid>(g => g != serverId)), Times.Never);       // another server's id never reaches the rows
    }
}
