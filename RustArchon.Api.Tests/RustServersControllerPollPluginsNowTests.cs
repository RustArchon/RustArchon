// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using AutoMapper;
using Correlate;
using JumpStart.Data;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
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
/// <c>POST api/rustservers/{id}/plugins/poll</c> (<see cref="RustServersController.PollPluginsNow"/>): the Refresh button's on-demand poll. What it does
/// is <see cref="IServerPollService"/>'s (tested on its own in <see cref="ServerPollServiceTests"/>); this is only the endpoint's own shape - which
/// polls it asks for, the outcome-to-text mapping, and the unknown-server case.
/// </summary>
public class RustServersControllerPollPluginsNowTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<RustServerMappingProfile>(), NullLoggerFactory.Instance).CreateMapper();

    private async Task<(RustServersController Controller, Mock<IServerPollService> Poll)> BuildAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new FixedTenantContext(tenantId);
        var context = new ApiDbContext(postgres.Options, tenantContext);
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();

        var rconCredentialProtector = new Mock<IRconCredentialProtector>();
        rconCredentialProtector.Setup(p => p.Protect(It.IsAny<string>())).Returns<string>(s => s);
        var subscriptionRepository = new Mock<ISubscriptionRepository>();
        subscriptionRepository.Setup(r => r.GetForTenantAsync(tenantId)).ReturnsAsync((Subscription?)null);
        subscriptionRepository.Setup(r => r.GetCurrentTermAsync(tenantId)).ReturnsAsync((SubscriptionPeriod?)null);
        var poll = new Mock<IServerPollService>();

        var controller = new RustServersController(
            new RustServerRepository(context),
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
            poll.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        return (controller, poll);
    }

    private static async Task<Guid> CreateServerAsync(RustServersController controller, string name)
    {
        var created = await controller.Create(new CreateRustServerDto { Name = name, Host = "192.0.2.41", Port = 28016, RconPassword = "unused" });
        return Assert.IsType<RustServerDto>(Assert.IsType<CreatedAtActionResult>(created.Result).Value).Id;
    }

    [Theory]
    [InlineData(ServerPollOutcome.Polled, "polled")]
    [InlineData(ServerPollOutcome.NotConnected, "not_connected")]
    [InlineData(ServerPollOutcome.NoWorker, "no_worker")]
    [InlineData(ServerPollOutcome.RateLimited, "rate_limited")]
    public async Task TheOutcomeIsReportedInWords(ServerPollOutcome outcome, string expected)
    {
        var (controller, poll) = await BuildAsync();
        var id = await CreateServerAsync(controller, "Poll " + Guid.NewGuid().ToString("N")[..6]);
        poll.Setup(p => p.PollNowAsync(id, It.IsAny<IReadOnlyList<string>>())).ReturnsAsync(outcome);

        var result = Assert.IsType<OkObjectResult>((await controller.PollPluginsNow(id)).Result);

        Assert.Equal(expected, Assert.IsType<ServerPollResultDto>(result.Value).Outcome);
    }

    [Fact]
    public async Task ItAsksForBothThePluginListAndTheUpdateNotices()
    {
        var (controller, poll) = await BuildAsync();
        var id = await CreateServerAsync(controller, "Poll " + Guid.NewGuid().ToString("N")[..6]);
        poll.Setup(p => p.PollNowAsync(id, It.IsAny<IReadOnlyList<string>>())).ReturnsAsync(ServerPollOutcome.Polled);

        await controller.PollPluginsNow(id);

        poll.Verify(p => p.PollNowAsync(id, It.Is<IReadOnlyList<string>>(
            polls => polls.Contains(PollServerNowKinds.Plugins) && polls.Contains(PollServerNowKinds.Updates))), Times.Once());
    }

    [Fact]
    public async Task AnUnknownServerIsNotFoundAndNothingIsAsked()
    {
        var (controller, poll) = await BuildAsync();

        Assert.IsType<NotFoundResult>((await controller.PollPluginsNow(Guid.NewGuid())).Result);

        poll.Verify(p => p.PollNowAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>()), Times.Never());
    }
}
