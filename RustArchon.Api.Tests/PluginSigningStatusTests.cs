// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// The plugin's signing state end to end through the Api: normalization of what a plugin on someone else's server
/// reports, storage, and the read-only look-up of this Panel's own key fingerprint.
/// </summary>
public class PluginSigningStatusTests
{
    // ---- PluginSigningStates -------------------------------------------------------------------------------

    [Theory]
    [InlineData("valid", "valid")]
    [InlineData("INVALID", "invalid")]
    [InlineData(" unsigned ", "unsigned")]
    [InlineData("unlocated", "unlocated")]
    [InlineData("error", "error")]
    [InlineData("unknown", "unknown")]
    [InlineData("trusted", "unknown")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void NormalizeMapsToAKnownStateOrUnknown(string? sent, string expected)
    {
        Assert.Equal(expected, PluginSigningStates.Normalize(sent));
    }

    [Theory]
    [InlineData("0123456789abcdef", "0123456789abcdef")]
    [InlineData("0123456789ABCDEF", "0123456789abcdef")]
    [InlineData(" 0123456789abcdef ", "0123456789abcdef")]
    [InlineData("0123456789abcde", "")]
    [InlineData("0123456789abcdefff", "")]
    [InlineData("zzzzzzzzzzzzzzzz", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeFingerprintKeepsOnlyASixteenHexCharacterValue(string? sent, string expected)
    {
        Assert.Equal(expected, PluginSigningStates.NormalizeFingerprint(sent));
    }

    // ---- the consumer stores it, normalized ------------------------------------------------------------------

    private static async Task<ServerPluginStatus> ConsumeAsync(ServerPluginHandshakeCaptured message)
    {
        ServerPluginStatus? stored = null;
        var statuses = new Mock<IServerPluginStatusRepository>();
        statuses.Setup(r => r.UpsertAsync(message.TenantId, message.ServerId, It.IsAny<ServerPluginStatus>()))
            .Callback<Guid, Guid, ServerPluginStatus>((_, _, s) => stored = s)
            .Returns(Task.CompletedTask);
        var context = Mock.Of<ConsumeContext<ServerPluginHandshakeCaptured>>(c => c.Message == message);

        await new ServerPluginHandshakeCapturedConsumer(statuses.Object, Mock.Of<IPluginSettingsSynchronizer>()).Consume(context);

        return stored!;
    }

    private static ServerPluginHandshakeCaptured Handshake(string state, string fingerprint) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, "0.1.0", ["config"], true, true, true, DateTimeOffset.UtcNow, state, fingerprint);

    [Fact]
    public async Task TheConsumerStoresAValidStateAndFingerprint()
    {
        var stored = await ConsumeAsync(Handshake("valid", "0123456789abcdef"));

        Assert.Equal("valid", stored.SigningState);
        Assert.Equal("0123456789abcdef", stored.SigningKeyFingerprint);
    }

    [Fact]
    public async Task TheConsumerNeverStoresAnUnrecognizedStateOrAMalformedFingerprint()
    {
        var stored = await ConsumeAsync(Handshake(new string('x', 400), "<b>not a fingerprint</b>"));

        Assert.Equal("unknown", stored.SigningState);
        Assert.Equal("", stored.SigningKeyFingerprint);
    }

    [Fact]
    public async Task AMessageFromAnOlderWorkerWithNoSigningFieldsIsStoredAsUnknown()
    {
        // Published before the fields existed: they deserialize as null, not as the record's defaults.
        var stored = await ConsumeAsync(Handshake(null!, null!));

        Assert.Equal("unknown", stored.SigningState);
        Assert.Equal("", stored.SigningKeyFingerprint);
    }

    [Fact]
    public void ANewStatusRowDefaultsToUnknownWithNoKey()
    {
        var status = new ServerPluginStatus();

        Assert.Equal("unknown", status.SigningState);
        Assert.Equal("", status.SigningKeyFingerprint);
    }

    // ---- looking up this Panel's key without creating one -----------------------------------------------------

    private readonly Mock<IPlatformSettingRepository> _settings = new();
    private readonly Mock<IApiKeyProtector> _protector = new();
    private PlatformSetting? _row = new() { Key = PlatformSettingsRegistry.PluginSigningKey, Value = "" };

    private PluginSigningService CreateSigning()
    {
        _protector.Setup(p => p.Protect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, plain) => "enc:" + plain);
        _protector.Setup(p => p.Unprotect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, stored) => stored["enc:".Length..]);
        _settings.Setup(s => s.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey)).ReturnsAsync(() => _row);
        return new PluginSigningService(_settings.Object, _protector.Object, NullLogger<PluginSigningService>.Instance);
    }

    [Fact]
    public async Task TryGetFingerprintIsNullAndCreatesNothingWhenThereIsNoKey()
    {
        var fingerprint = await CreateSigning().TryGetFingerprintAsync();

        Assert.Null(fingerprint);
        // The whole point: a plain read must not be what mints a key nobody asked for.
        _settings.Verify(s => s.SetValueIfEmptyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _protector.Verify(p => p.Protect(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TryGetFingerprintReturnsTheSameFingerprintAsTheKeyAThatWasStored()
    {
        using var key = RSA.Create(2048);
        _row!.Value = "enc:" + Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        var expected = PluginScriptStamper.Fingerprint(Convert.ToBase64String(key.ExportParameters(false).Modulus!));

        Assert.Equal(expected, await CreateSigning().TryGetFingerprintAsync());
    }

    [Fact]
    public async Task TryGetFingerprintIsNullNotAnExceptionWhenTheStoredKeyIsUnusable()
    {
        _row!.Value = "enc:this is not a key";

        Assert.Null(await CreateSigning().TryGetFingerprintAsync());
        _settings.Verify(s => s.SetValueIfEmptyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TryGetFingerprintIsNullWhenTheSettingRowIsMissing()
    {
        _row = null;

        Assert.Null(await CreateSigning().TryGetFingerprintAsync());
    }
}
