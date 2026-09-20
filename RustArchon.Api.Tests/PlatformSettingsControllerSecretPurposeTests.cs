// Copyright ©2026 Scott Blomfield

using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
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
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="PlatformSettingsController"/>'s <c>SecretPurposeFor</c> mapping - caught live
/// via <c>TicketingWebhookSecret</c>: it's registered in <see cref="PlatformSettingsRegistry"/> as a
/// <see cref="PlatformSettingValueType.Secret"/> setting but had no matching case in
/// <c>SecretPurposeFor</c>, so saving it from the admin UI threw a 500 instead of succeeding. Runs
/// against a real (throwaway, Testcontainers-hosted) Postgres, seeded through the real registry, so this
/// covers every <em>current and future</em> Secret setting automatically rather than re-litigating this
/// one key by name - see <see cref="PostgresFixture"/>.
/// </summary>
public class PlatformSettingsControllerSecretPurposeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<PlatformSettingMappingProfile>(), NullLoggerFactory.Instance)
            .CreateMapper();

    [Fact]
    public async Task EverySecretSettingTheRegistryDeclares_CanActuallyBeSaved()
    {
        var context = new ApiDbContext(postgres.Options, tenantContext: null);
        await PlatformSettingsRegistry.EnsureDefaultsAsync(
            context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger.Instance);

        var repository = new PlatformSettingRepository(context);

        var apiKeyProtector = new Mock<IApiKeyProtector>();
        apiKeyProtector
            .Setup(p => p.Protect(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((_, plaintext) => $"protected:{plaintext}");

        var controller = new PlatformSettingsController(
            repository,
            Mock.Of<IPlatformSettingsCache>(),
            Mock.Of<IAppGenerationCache>(),
            apiKeyProtector.Object,
            Mock.Of<IRequestClient<SendTestEmail>>(),
            CreateMapper());

        var secretKeys = (await repository.GetAllAsync())
            .Where(s => s.ValueType == RustArchon.Api.Data.PlatformSettingValueType.Secret)
            // Deliberately not editable here: it is rotated on the Plugin admin page (see the test below).
            .Where(s => s.Key != PlatformSettingsRegistry.PluginSigningKey)
            .Select(s => s.Key)
            .ToList();

        Assert.NotEmpty(secretKeys);

        foreach (var key in secretKeys)
        {
            var result = await controller.UpdateValue(key, new UpdatePlatformSettingValueDto { Value = "a-test-value" });

            Assert.True(
                result.Result is OkObjectResult,
                $"Saving Secret setting '{key}' failed - is it missing from PlatformSettingsController.SecretPurposeFor?");
        }
    }

    [Fact]
    public async Task ThePluginSigningKeyCannotBeOverwrittenThroughTheGenericSettingsEndpoint()
    {
        // Typing over it would silently strand every plugin already installed from this Panel; rotating it on the Plugin
        // admin page keeps the old key. The stored value must be untouched by the refused call.
        var context = new ApiDbContext(postgres.Options, tenantContext: null);
        await PlatformSettingsRegistry.EnsureDefaultsAsync(
            context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger.Instance);
        var repository = new PlatformSettingRepository(context);
        var before = (await repository.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value;
        var controller = new PlatformSettingsController(
            repository, Mock.Of<IPlatformSettingsCache>(), Mock.Of<IAppGenerationCache>(), Mock.Of<IApiKeyProtector>(),
            Mock.Of<IRequestClient<SendTestEmail>>(), CreateMapper());

        var result = await controller.UpdateValue(PlatformSettingsRegistry.PluginSigningKey, new UpdatePlatformSettingValueDto { Value = "junk" });

        var refused = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Plugin page", refused.Value!.ToString());
        Assert.Equal(before, (await new PlatformSettingRepository(new ApiDbContext(postgres.Options, tenantContext: null))
            .GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value);
    }
}
