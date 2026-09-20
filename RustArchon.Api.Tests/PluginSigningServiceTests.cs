// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="PluginSigningService"/> and <see cref="PluginScriptService"/>: the key is created once,
/// used consistently, and - above all - never silently replaced, because a new key would strand every plugin
/// already installed from this Panel.
/// </summary>
public class PluginSigningServiceTests
{
    private const string Purpose = ApiKeyProtectorPurposes.PluginSigningKey;

    private readonly Mock<IPlatformSettingRepository> _settings = new();
    private readonly Mock<IApiKeyProtector> _protector = new();
    private PlatformSetting? _row = new() { Key = PlatformSettingsRegistry.PluginSigningKey, Value = "" };

    public PluginSigningServiceTests()
    {
        // A reversible fake "encryption" that still lets the tests see the purpose was the signing key's own.
        _protector.Setup(p => p.Protect(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((purpose, plain) => $"enc[{purpose}]:{plain}");
        _protector.Setup(p => p.Unprotect(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((purpose, stored) =>
            {
                var prefix = $"enc[{purpose}]:";
                return stored.StartsWith(prefix, StringComparison.Ordinal)
                    ? stored[prefix.Length..]
                    : throw new CryptographicException("wrong purpose or corrupt");
            });

        _settings.Setup(s => s.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey)).ReturnsAsync(() => _row);
        _settings.Setup(s => s.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, It.IsAny<string>()))
            .ReturnsAsync((string _, string value) =>
            {
                if (_row is null || !string.IsNullOrEmpty(_row.Value)) { return false; }
                _row.Value = value;
                return true;
            });
    }

    private PluginSigningService Create() =>
        new(_settings.Object, _protector.Object, NullLogger<PluginSigningService>.Instance);

    private static string StoredKey(int bits) =>
        "enc[" + Purpose + "]:" + Convert.ToBase64String(RSA.Create(bits).ExportPkcs8PrivateKey());

    // ---- creating and reusing the key ----------------------------------------------------------------------

    [Fact]
    public async Task TheFirstUseGeneratesAndStoresAKeyEncryptedUnderItsOwnPurpose()
    {
        var key = await Create().GetPublicKeyAsync();

        Assert.False(string.IsNullOrEmpty(_row!.Value));
        Assert.StartsWith($"enc[{Purpose}]:", _row.Value);
        _protector.Verify(p => p.Protect(Purpose, It.IsAny<string>()), Times.Once);
        Assert.Equal("AQAB", key.ExponentBase64);
        Assert.Matches("^[0-9a-f]{16}$", key.Fingerprint);
    }

    [Fact]
    public async Task TheKeyIsGeneratedAtLeast2048Bits()
    {
        var key = await Create().GetPublicKeyAsync();

        Assert.True(Convert.FromBase64String(key.ModulusBase64).Length >= 256);
    }

    [Fact]
    public async Task ASecondUseReturnsTheSameKeyAndGeneratesNothingNew()
    {
        var service = Create();
        var first = await service.GetPublicKeyAsync();

        var second = await service.GetPublicKeyAsync();

        Assert.Equal(first, second);
        _settings.Verify(s => s.SetValueIfEmptyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AnExistingKeyIsUsedAndNothingIsWritten()
    {
        _row!.Value = StoredKey(2048);

        var key = await Create().GetPublicKeyAsync();

        Assert.False(string.IsNullOrEmpty(key.ModulusBase64));
        _settings.Verify(s => s.SetValueIfEmptyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ASignatureVerifiesUnderThePublicKeyFromTheSameService()
    {
        var service = Create();
        var payload = Encoding.UTF8.GetBytes("the plugin source");

        var signature = await service.SignAsync(payload);
        var key = await service.GetPublicKeyAsync();

        using var verifier = RSA.Create();
        verifier.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromBase64String(key.ModulusBase64),
            Exponent = Convert.FromBase64String(key.ExponentBase64)
        });
        Assert.True(verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(verifier.VerifyData(Encoding.UTF8.GetBytes("altered"), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    // ---- the first-download race -------------------------------------------------------------------------

    [Fact]
    public async Task WhenAnotherInstanceWinsTheRaceItsKeyIsUsedNotOurs()
    {
        // Our set-if-empty loses because a competing instance stored a key between our read and our write.
        var winner = StoredKey(2048);
        _settings.Setup(s => s.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, It.IsAny<string>()))
            .ReturnsAsync(() => { _row!.Value = winner; return false; });

        var key = await Create().GetPublicKeyAsync();

        // The key returned is the winner's, not the one this call generated.
        using var expected = RSA.Create();
        expected.ImportPkcs8PrivateKey(Convert.FromBase64String(winner[$"enc[{Purpose}]:".Length..]), out _);
        Assert.Equal(Convert.ToBase64String(expected.ExportParameters(false).Modulus!), key.ModulusBase64);
    }

    // ---- never silently replaced ---------------------------------------------------------------------------

    [Fact]
    public async Task AStoredKeyThatCannotBeReadIsAnErrorNotAReplacement()
    {
        _row!.Value = "enc[" + Purpose + "]:this is not a key";

        var ex = await Assert.ThrowsAsync<PluginSigningKeyException>(() => Create().GetPublicKeyAsync());

        Assert.Contains("not replaced", ex.Message);
        _settings.Verify(s => s.SetValueIfEmptyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Equal("enc[" + Purpose + "]:this is not a key", _row.Value); // untouched
    }

    [Fact]
    public async Task AStoredKeyEncryptedForADifferentPurposeIsAnErrorNotAReplacement()
    {
        _row!.Value = "enc[some other purpose]:" + Convert.ToBase64String(RSA.Create(2048).ExportPkcs8PrivateKey());

        await Assert.ThrowsAsync<PluginSigningKeyException>(() => Create().SignAsync([1, 2, 3]));

        _settings.Verify(s => s.SetValueIfEmptyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AStoredKeyThatIsTooSmallIsRefused()
    {
        _row!.Value = StoredKey(1024);

        var ex = await Assert.ThrowsAsync<PluginSigningKeyException>(() => Create().GetPublicKeyAsync());

        Assert.Contains("2048", ex.Message);
    }

    [Fact]
    public async Task AMissingSettingRowIsAnError()
    {
        _row = null;

        await Assert.ThrowsAsync<PluginSigningKeyException>(() => Create().GetPublicKeyAsync());
    }

    [Fact]
    public async Task IfTheKeyCannotBeStoredItIsAnErrorNotAnUnsignedScript()
    {
        _settings.Setup(s => s.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, It.IsAny<string>()))
            .ReturnsAsync(false); // and the row stays empty

        await Assert.ThrowsAsync<PluginSigningKeyException>(() => Create().GetPublicKeyAsync());
    }

    // ---- the finished script -------------------------------------------------------------------------------

    private static string SourceWithPlaceholders(string version = "1.2.3") =>
        $"[Info(\"RustArchon\", \"RustArchon\", \"{version}\")]\nconst string M = \"{PluginScriptStamper.ModulusPlaceholder}\";\nconst string E = \"{PluginScriptStamper.ExponentPlaceholder}\";\n";

    [Fact]
    public async Task TheScriptCarriesTheStampedKeyAndASignatureThatVerifies()
    {
        var signing = Create();
        var source = new Mock<IPluginScriptSource>();
        source.Setup(s => s.ReadSourceAsync()).ReturnsAsync(SourceWithPlaceholders());

        var script = await new PluginScriptService(signing, source.Object).BuildAsync();

        var key = await signing.GetPublicKeyAsync();
        var text = Encoding.UTF8.GetString(script.Bytes);
        Assert.Contains($"\"{key.ModulusBase64}\"", text);
        Assert.Equal(key.Fingerprint, script.KeyFingerprint);

        var markerAt = text.LastIndexOf("\n" + PluginScriptStamper.SignatureMarker, StringComparison.Ordinal) + 1;
        var signature = Convert.FromBase64String(text[(markerAt + PluginScriptStamper.SignatureMarker.Length)..].Trim());
        using var verifier = RSA.Create();
        verifier.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromBase64String(key.ModulusBase64),
            Exponent = Convert.FromBase64String(key.ExponentBase64)
        });
        Assert.True(verifier.VerifyData(script.Bytes[..markerAt], signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task TheScriptReportsThePluginVersionFromItsInfoAttribute()
    {
        var source = new Mock<IPluginScriptSource>();
        source.Setup(s => s.ReadSourceAsync()).ReturnsAsync(SourceWithPlaceholders("4.5.6"));

        var script = await new PluginScriptService(Create(), source.Object).BuildAsync();

        Assert.Equal("4.5.6", script.PluginVersion);
    }

    [Fact]
    public async Task ASourceWithoutTheInfoAttributeStillBuildsWithNoVersion()
    {
        var source = new Mock<IPluginScriptSource>();
        source.Setup(s => s.ReadSourceAsync()).ReturnsAsync(
            $"const string M = \"{PluginScriptStamper.ModulusPlaceholder}\";\nconst string E = \"{PluginScriptStamper.ExponentPlaceholder}\";\n");

        var script = await new PluginScriptService(Create(), source.Object).BuildAsync();

        Assert.Null(script.PluginVersion);
    }

    [Fact]
    public async Task ABrokenSourceFailsTheBuildInsteadOfShippingAnUnstampedScript()
    {
        var source = new Mock<IPluginScriptSource>();
        source.Setup(s => s.ReadSourceAsync()).ReturnsAsync("class P { }\n"); // no placeholders

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PluginScriptService(Create(), source.Object).BuildAsync());
    }
}
