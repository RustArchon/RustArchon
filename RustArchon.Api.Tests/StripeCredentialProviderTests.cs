// Copyright ©2026 Scott Blomfield

using System.Threading.Tasks;
using Moq;
using RustArchon.Api.Billing;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

public class StripeCredentialProviderTests
{
    [Fact]
    public async Task GetSecretKeyAsyncDecryptsTheStoredValueUnderTheStripeSecretKeyPurpose()
    {
        var repository = new Mock<IPlatformSettingRepository>();
        repository.Setup(r => r.GetByKeyAsync(PlatformSettingsRegistry.StripeSecretKey))
            .ReturnsAsync(new PlatformSetting { Key = PlatformSettingsRegistry.StripeSecretKey, Value = "ciphertext" });

        var protector = new Mock<IApiKeyProtector>();
        protector.Setup(p => p.Unprotect(ApiKeyProtectorPurposes.StripeSecretKey, "ciphertext"))
            .Returns("rk_test_123");

        var provider = new StripeCredentialProvider(repository.Object, protector.Object);

        Assert.Equal("rk_test_123", await provider.GetSecretKeyAsync());
    }

    [Fact]
    public async Task GetWebhookSecretAsyncDecryptsTheStoredValueUnderTheWebhookSecretPurpose()
    {
        var repository = new Mock<IPlatformSettingRepository>();
        repository.Setup(r => r.GetByKeyAsync(PlatformSettingsRegistry.StripeWebhookSecret))
            .ReturnsAsync(new PlatformSetting { Key = PlatformSettingsRegistry.StripeWebhookSecret, Value = "ciphertext" });

        var protector = new Mock<IApiKeyProtector>();
        protector.Setup(p => p.Unprotect(ApiKeyProtectorPurposes.StripeWebhookSecret, "ciphertext"))
            .Returns("whsec_123");

        var provider = new StripeCredentialProvider(repository.Object, protector.Object);

        Assert.Equal("whsec_123", await provider.GetWebhookSecretAsync());
    }

    [Fact]
    public async Task GetSecretKeyAsyncReturnsEmptyWhenNeverConfigured()
    {
        // No row at all - the state PlatformSettingsRegistry seeds until an admin sets one.
        var repository = new Mock<IPlatformSettingRepository>();
        repository.Setup(r => r.GetByKeyAsync(PlatformSettingsRegistry.StripeSecretKey))
            .ReturnsAsync(new PlatformSetting { Key = PlatformSettingsRegistry.StripeSecretKey, Value = string.Empty });

        var provider = new StripeCredentialProvider(repository.Object, Mock.Of<IApiKeyProtector>());

        Assert.Equal(string.Empty, await provider.GetSecretKeyAsync());
    }

    [Fact]
    public async Task GetSecretKeyAsyncReturnsEmptyWhenTheRowDoesNotExistAtAll()
    {
        var repository = new Mock<IPlatformSettingRepository>();
        repository.Setup(r => r.GetByKeyAsync(PlatformSettingsRegistry.StripeSecretKey))
            .ReturnsAsync((PlatformSetting?)null);

        var provider = new StripeCredentialProvider(repository.Object, Mock.Of<IApiKeyProtector>());

        Assert.Equal(string.Empty, await provider.GetSecretKeyAsync());
    }
}
