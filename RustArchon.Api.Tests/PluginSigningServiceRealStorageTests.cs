// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// <see cref="PluginSigningService"/> against the REAL things it depends on: a real Postgres, the real repository,
/// and real Data Protection encryption. The mocked tests in <see cref="PluginSigningServiceTests"/> cannot see
/// storage limits, and this exists because of exactly that: the first live download failed with "value too long for
/// type character varying(1000)" - an encrypted RSA-2048 key is about 2.3 KB, and every mocked test passed.
/// </summary>
public class PluginSigningServiceRealStorageTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private ApiDbContext CreateContext() => new(postgres.Options);

    private static async Task ClearKeyAsync(ApiDbContext context)
    {
        await PlatformSettingsRegistry.EnsureDefaultsAsync(
            context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger.Instance);
        var repository = new PlatformSettingRepository(context);
        var setting = (await repository.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!;
        setting.Value = "";
        await repository.UpdateAsync(setting);
    }

    private static PluginSigningService NewService(ApiDbContext context) =>
        new(new PlatformSettingRepository(context), new ApiKeyProtector(Provider), NullLogger<PluginSigningService>.Instance);

    [Fact]
    public async Task TheFirstUseStoresARealEncryptedKeyThatFitsTheColumn()
    {
        await using var context = CreateContext();
        await ClearKeyAsync(context);

        var key = await NewService(context).GetPublicKeyAsync(); // throws "value too long" if the column is too narrow

        await using var fresh = CreateContext();
        var stored = (await new PlatformSettingRepository(fresh).GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value;
        Assert.False(string.IsNullOrEmpty(stored));
        Assert.True(stored.Length > 1000, $"the stored key is {stored.Length} chars - the old 1000 limit could never have held it");
        Assert.True(stored.Length <= 8000);
        Assert.Matches("^[0-9a-f]{16}$", key.Fingerprint);
    }

    [Fact]
    public async Task TheKeySurvivesTheRoundTripThroughTheDatabaseAndEncryption()
    {
        await using var first = CreateContext();
        await ClearKeyAsync(first);
        var created = await NewService(first).GetPublicKeyAsync();

        await using var second = CreateContext(); // a different instance/context reading what was stored
        var reloaded = await NewService(second).GetPublicKeyAsync();

        Assert.Equal(created, reloaded);
    }

    [Fact]
    public async Task ASignatureMadeFromTheStoredKeyVerifiesUnderItsPublicKey()
    {
        await using var context = CreateContext();
        await ClearKeyAsync(context);
        var service = NewService(context);
        var payload = Encoding.UTF8.GetBytes("plugin source");

        var signature = await service.SignAsync(payload);
        var key = await service.GetPublicKeyAsync();

        using var verifier = RSA.Create();
        verifier.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromBase64String(key.ModulusBase64),
            Exponent = Convert.FromBase64String(key.ExponentBase64)
        });
        Assert.True(verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task TheKeyIsStoredEncryptedNotAsPlainPkcs8()
    {
        await using var context = CreateContext();
        await ClearKeyAsync(context);
        var key = await NewService(context).GetPublicKeyAsync();

        await using var fresh = CreateContext();
        var stored = (await new PlatformSettingRepository(fresh).GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value;

        // Plain PKCS#8 base64 for an RSA key starts with "MIIE"; the Data Protection wrapper does not.
        Assert.DoesNotContain("MIIE", stored);
        Assert.False(string.IsNullOrEmpty(key.ModulusBase64));
    }

    [Fact]
    public async Task ASecretSettingCanHoldUpTo8000Characters()
    {
        await using var context = CreateContext();
        await ClearKeyAsync(context);
        var repository = new PlatformSettingRepository(context);
        var longest = new string('x', 8000);

        var wrote = await repository.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, longest);

        Assert.True(wrote);
        await using var fresh = CreateContext();
        Assert.Equal(8000, (await new PlatformSettingRepository(fresh).GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value.Length);
    }
}
