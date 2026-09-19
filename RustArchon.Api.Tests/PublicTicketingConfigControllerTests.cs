// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using JumpStart.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>Tests for <see cref="PublicTicketingConfigController"/>.</summary>
public class PublicTicketingConfigControllerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Get_ReturnsOnlyActiveQueues_AndTheCaptchaConfig()
    {
        var context = new ApiDbContext(postgres.Options, tenantContext: null);

        var activeQueue = new Queue
        {
            Id = Guid.NewGuid(), Name = "Support", Slug = $"support-{Guid.NewGuid()}", IsActive = true,
            CreatedById = Guid.Empty, CreatedOn = DateTimeOffset.UtcNow
        };
        var inactiveQueue = new Queue
        {
            Id = Guid.NewGuid(), Name = "Retired", Slug = $"retired-{Guid.NewGuid()}", IsActive = false,
            CreatedById = Guid.Empty, CreatedOn = DateTimeOffset.UtcNow
        };
        context.Set<Queue>().AddRange(activeQueue, inactiveQueue);
        await context.SaveChangesAsync();

        var settingsCache = new Mock<IPlatformSettingsCache>();
        settingsCache
            .Setup(c => c.GetStringAsync(PlatformSettingsRegistry.CaptchaProvider))
            .ReturnsAsync(PlatformSettingsRegistry.CaptchaProviders.Turnstile);
        settingsCache
            .Setup(c => c.GetStringAsync(PlatformSettingsRegistry.CaptchaSiteKey))
            .ReturnsAsync("public-site-key");

        var controller = new PublicTicketingConfigController(settingsCache.Object, new QueueRepository(context));

        var result = await controller.Get(CancellationToken.None);

        var config = Assert.IsType<PublicTicketingConfigDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(PlatformSettingsRegistry.CaptchaProviders.Turnstile, config.CaptchaProvider);
        Assert.Equal("public-site-key", config.CaptchaSiteKey);
        Assert.Single(config.Queues);
        Assert.Equal(activeQueue.Id, config.Queues[0].Id);
    }

    [Fact]
    public async Task Get_WithNoCaptchaProviderConfiguredYet_DefaultsToNone()
    {
        var context = new ApiDbContext(postgres.Options, tenantContext: null);

        var settingsCache = new Mock<IPlatformSettingsCache>();
        settingsCache
            .Setup(c => c.GetStringAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        var controller = new PublicTicketingConfigController(settingsCache.Object, new QueueRepository(context));

        var result = await controller.Get(CancellationToken.None);

        var config = Assert.IsType<PublicTicketingConfigDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(PlatformSettingsRegistry.CaptchaProviders.None, config.CaptchaProvider);
    }
}
