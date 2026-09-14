// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using Correlate;
using JumpStart.Data;
using JumpStart.Repositories;
using JumpStart.Services.Authentication.Controllers;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="RustServersController.GetEvents"/>'s <c>includeNonInteractive</c> handling -
/// specifically that the flag is only ever honored for a caller whose own token carries
/// <see cref="TokenController.ActingAsClaimType"/>, and silently ignored (never a 403 that would
/// confirm unfiltered data exists) for anyone else. This is the one server-side check that keeps a
/// potentially privileged, worker-initiated <see cref="RconEvent"/> row away from an ordinary tenant
/// user's response - see <see cref="RconEvent"/>'s remarks.
/// </summary>
public class RustServersControllerGetEventsFilteringTests
{
    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<Mapping.RconEventMappingProfile>(), NullLoggerFactory.Instance)
            .CreateMapper();

    private sealed class NullTenantContext : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(null);
    }

    private static RustServersController CreateController(
        Mock<IRconEventRepository> rconEventRepository, Guid serverId, bool actingAsTenant)
    {
        var rustServerRepository = new Mock<IRustServerRepository>();
        rustServerRepository
            .Setup(r => r.GetByIdAsync(serverId, null))
            .ReturnsAsync(new RustServer { Id = serverId, Name = "Test Server", Host = "192.0.2.10", Port = 28016 });

        var subscriptionRepository = Mock.Of<ISubscriptionRepository>();

        var controller = new RustServersController(
            rustServerRepository.Object,
            CreateMapper(),
            NullLogger<RustServersController>.Instance,
            Mock.Of<ICorrelationContextAccessor>(),
            Mock.Of<IRconCredentialProtector>(),
            Mock.Of<IApiKeyProtector>(),
            Mock.Of<IPublishEndpoint>(),
            Mock.Of<IRequestClient<SendRconCommand>>(),
            new NullTenantContext(),
            rconEventRepository.Object,
            Mock.Of<IPlayerSessionRepository>(),
            Mock.Of<IPlayerKillEventRepository>(),
            Mock.Of<IServerInfoSnapshotRepository>(),
            Mock.Of<IConnectionLogRepository>(),
            subscriptionRepository);

        // ControllerBase.User reads through ControllerContext.HttpContext.User - not set by the
        // constructor above at all outside a real MVC request pipeline, so this test wires it up
        // directly, the same way the controller sees it inside one.
        var claims = actingAsTenant
            ? new[] { new Claim(TokenController.ActingAsClaimType, "true") }
            : Array.Empty<Claim>();
        var identity = new ClaimsIdentity(claims, authenticationType: "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }

    private static Mock<IRconEventRepository> CreateEmptyRepository()
    {
        var repository = new Mock<IRconEventRepository>();
        repository
            .Setup(r => r.GetForServerAsync(
                It.IsAny<Guid>(), It.IsAny<QueryOptions<RconEvent>>(), It.IsAny<bool?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<bool>()))
            .ReturnsAsync(new PagedResult<RconEvent> { Items = new List<RconEvent>(), TotalCount = 0, PageNumber = 1, PageSize = 100 });
        return repository;
    }

    [Fact]
    public async Task AnOrdinaryUserRequestingUnfilteredEventsSilentlyGetsTheFilteredResultInstead()
    {
        var serverId = Guid.NewGuid();
        var repository = CreateEmptyRepository();
        var controller = CreateController(repository, serverId, actingAsTenant: false);

        var result = await controller.GetEvents(serverId, includeNonInteractive: true);

        Assert.IsType<OkObjectResult>(result.Result);
        repository.Verify(r => r.GetForServerAsync(
            serverId, It.IsAny<QueryOptions<RconEvent>>(), It.IsAny<bool?>(),
            It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), includeNonInteractive: false),
            Times.Once);
    }

    [Fact]
    public async Task ASiteAdminActingAsThisTenantRequestingUnfilteredEventsActuallyGetsThem()
    {
        var serverId = Guid.NewGuid();
        var repository = CreateEmptyRepository();
        var controller = CreateController(repository, serverId, actingAsTenant: true);

        var result = await controller.GetEvents(serverId, includeNonInteractive: true);

        Assert.IsType<OkObjectResult>(result.Result);
        repository.Verify(r => r.GetForServerAsync(
            serverId, It.IsAny<QueryOptions<RconEvent>>(), It.IsAny<bool?>(),
            It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), includeNonInteractive: true),
            Times.Once);
    }

    [Fact]
    public async Task ASiteAdminActingAsThisTenantWhoDidNotAskForUnfilteredEventsStillGetsTheFilteredResult()
    {
        var serverId = Guid.NewGuid();
        var repository = CreateEmptyRepository();
        var controller = CreateController(repository, serverId, actingAsTenant: true);

        var result = await controller.GetEvents(serverId);

        Assert.IsType<OkObjectResult>(result.Result);
        repository.Verify(r => r.GetForServerAsync(
            serverId, It.IsAny<QueryOptions<RconEvent>>(), It.IsAny<bool?>(),
            It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), includeNonInteractive: false),
            Times.Once);
    }
}
