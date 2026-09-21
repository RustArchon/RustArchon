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
/// The signing audit trail: the moment the deployment's first key is made is written to the audit log (the rotation reminder counts from it),
/// a failing audit line never stops the key being made, and a source signed by hand is signed exactly as a served one is.
/// </summary>
public class PluginSigningAuditTests
{
    private readonly Mock<IPlatformSettingRepository> _settings = new();
    private readonly Mock<IApiKeyProtector> _protector = new();
    private readonly Mock<IPluginAdminAudit> _audit = new();
    private PlatformSetting _row = new() { Key = PlatformSettingsRegistry.PluginSigningKey, Value = "" };

    private PluginSigningService Create()
    {
        _protector.Setup(p => p.Protect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, plain) => "enc:" + plain);
        _protector.Setup(p => p.Unprotect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, stored) => stored["enc:".Length..]);
        _settings.Setup(s => s.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey)).ReturnsAsync(() => _row);
        _settings.Setup(s => s.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, It.IsAny<string>()))
            .ReturnsAsync((string _, string value) =>
            {
                if (!string.IsNullOrEmpty(_row.Value))
                {
                    return false;
                }

                _row = new PlatformSetting { Key = PlatformSettingsRegistry.PluginSigningKey, Value = value };
                return true;
            });
        return new PluginSigningService(_settings.Object, _protector.Object, NullLogger<PluginSigningService>.Instance, null, _audit.Object);
    }

    [Fact]
    public async Task TheFirstKeyBeingMadeIsWrittenToTheAuditLogWithItsFingerprintAndNoActor()
    {
        var signing = Create();

        var key = await signing.GetPublicKeyAsync();

        _audit.Verify(a => a.RecordAsync(PluginAdminEventKind.KeyGenerated, key.Fingerprint, "system", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AKeyThatAlreadyExistsIsNotMadeAgainAndWritesNoAuditLine()
    {
        var signing = Create();
        await signing.GetPublicKeyAsync();
        _audit.Invocations.Clear();

        await signing.GetPublicKeyAsync();
        await signing.SignAsync([1, 2, 3]);

        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnAuditLineThatFailsDoesNotStopTheKeyBeingUsed()
    {
        _audit.Setup(a => a.RecordAsync(It.IsAny<PluginAdminEventKind>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("the database is busy"));
        var signing = Create();

        var key = await signing.GetPublicKeyAsync();
        var signature = await signing.SignAsync(Encoding.UTF8.GetBytes("payload"));

        Assert.Equal(16, key.Fingerprint.Length);
        Assert.Equal(256, signature.Length);
    }

    [Fact]
    public async Task ASourceSignedByHandIsStampedAndSignedExactlyAsAServedOneIs()
    {
        var signing = Create();
        var source = "[Info(\"RustArchon\", \"x\", \"9.9.9\")]\nclass A { string m = \"" + PluginScriptStamper.ModulusPlaceholder + "\"; string e = \"" + PluginScriptStamper.ExponentPlaceholder + "\"; }\n";
        var scripts = new PluginScriptService(signing, Mock.Of<IPluginScriptSource>());

        var script = await scripts.SignSourceAsync(source, PluginReleaseKind.Main);

        var key = await signing.GetPublicKeyAsync();
        Assert.Equal(key.Fingerprint, script.KeyFingerprint);
        Assert.Equal("9.9.9", script.PluginVersion);
        var text = Encoding.UTF8.GetString(script.Bytes);
        Assert.DoesNotContain(PluginScriptStamper.ModulusPlaceholder, text);
        Assert.Contains(key.ModulusBase64, text);
        Assert.Contains(PluginScriptStamper.SignatureMarker, text);
    }
}
