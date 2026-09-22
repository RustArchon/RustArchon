// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Correlate;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Mapping;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="RustServersController.Create"/> - specifically that a duplicate-name
/// unique-constraint violation (<c>IX_RustServer_TenantId_Name</c>) is caught and turned into a clean
/// 409 rather than left to reach the caller as a raw, unhandled <see cref="DbUpdateException"/>/500.
/// Runs against a real (throwaway, Testcontainers-hosted) Postgres, not EF Core's InMemory provider -
/// the whole point is exercising the real unique-constraint violation and its real
/// <c>Npgsql.PostgresException.ConstraintName</c>, which InMemory doesn't produce at all. See
/// <see cref="PostgresFixture"/>.
/// </summary>
public class RustServersControllerDuplicateNameTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    /// <summary>A fixed ambient tenant, for both the DbContext (so AddAsync populates TenantId) and the
    /// controller's own ITenantContext dependency (so GetPlanLimitStatusAsync resolves consistently).</summary>
    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<RustServerMappingProfile>(), NullLoggerFactory.Instance)
            .CreateMapper();

    private async Task<RustServersController> CreateControllerAsync(Guid tenantId)
    {
        var tenantContext = new FixedTenantContext(tenantId);
        var context = new ApiDbContext(postgres.Options, tenantContext);

        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();

        var repository = new RustServerRepository(context);

        var rconCredentialProtector = new Mock<IRconCredentialProtector>();
        rconCredentialProtector.Setup(p => p.Protect(It.IsAny<string>())).Returns<string>(s => s);

        var subscriptionRepository = new Mock<ISubscriptionRepository>();
        subscriptionRepository.Setup(r => r.GetForTenantAsync(tenantId)).ReturnsAsync((Subscription?)null);
        subscriptionRepository.Setup(r => r.GetCurrentTermAsync(tenantId)).ReturnsAsync((SubscriptionPeriod?)null);

        return new RustServersController(
            repository,
            CreateMapper(),
            NullLogger<RustServersController>.Instance,
            Mock.Of<ICorrelationContextAccessor>(),
            rconCredentialProtector.Object,
            Mock.Of<IApiKeyProtector>(),
            Mock.Of<IPublishEndpoint>(),
            Mock.Of<IRequestClient<SendRconCommand>>(),
            tenantContext,
            Mock.Of<IRconEventRepository>(),
            Mock.Of<IPlayerSessionRepository>(),
            Mock.Of<IPlayerKillEventRepository>(),
            Mock.Of<IServerInfoSnapshotRepository>(),
            Mock.Of<IConnectionLogRepository>(),
            Mock.Of<IServerPluginRepository>(),
            Mock.Of<IServerPluginStatusRepository>(),
            Mock.Of<IPluginScriptService>(),
            Mock.Of<IPluginUpdateService>(),
            subscriptionRepository.Object,
            Mock.Of<IServerPollService>());
    }

    private static CreateRustServerDto NewServerDto(string name) => new()
    {
        Name = name,
        Host = "192.0.2.30",
        Port = 28016,
        RconPassword = "unused"
    };

    [Fact]
    public async Task ADuplicateNameReturnsAConflictNotAnUnhandled500()
    {
        var tenantId = Guid.NewGuid();
        var controller = await CreateControllerAsync(tenantId);

        var first = await controller.Create(NewServerDto("Duplicate Name"));
        Assert.IsType<CreatedAtActionResult>(first.Result);

        var second = await controller.Create(NewServerDto("Duplicate Name"));

        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        Assert.Equal("A server named 'Duplicate Name' already exists.", conflict.Value);
    }

    [Fact]
    public async Task DifferentNamesInTheSameTenantBothSucceed()
    {
        var tenantId = Guid.NewGuid();
        var controller = await CreateControllerAsync(tenantId);

        var first = await controller.Create(NewServerDto("Server One"));
        var second = await controller.Create(NewServerDto("Server Two"));

        Assert.IsType<CreatedAtActionResult>(first.Result);
        Assert.IsType<CreatedAtActionResult>(second.Result);
    }

    [Fact]
    public async Task TheSameNameInDifferentTenantsBothSucceed()
    {
        var controllerA = await CreateControllerAsync(Guid.NewGuid());
        var controllerB = await CreateControllerAsync(Guid.NewGuid());

        var first = await controllerA.Create(NewServerDto("Shared Name"));
        var second = await controllerB.Create(NewServerDto("Shared Name"));

        Assert.IsType<CreatedAtActionResult>(first.Result);
        Assert.IsType<CreatedAtActionResult>(second.Result);
    }
}
