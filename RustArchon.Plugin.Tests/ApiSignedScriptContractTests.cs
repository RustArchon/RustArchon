// Copyright ©2026 Scott Blomfield

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Oxide.Plugins;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// The one place the two halves of the signing scheme meet in a test: the script the REAL Api produces (its real
/// stamper, its real signing service, the real embedded plugin source) is checked by the plugin's REAL
/// <see cref="ArchonIntegrity"/> code. If either side changes the format, this fails - long before a server does.
/// The game server's Mono runtime is still the final judge, which the live load proves.
/// </summary>
public sealed partial class ApiSignedScriptContractTests
{
    private readonly PlatformSetting _row = new() { Key = PlatformSettingsRegistry.PluginSigningKey, Value = "" };

    private PluginSigningService NewSigningService()
    {
        var protector = new Mock<IApiKeyProtector>();
        protector.Setup(p => p.Protect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, plain) => "enc:" + plain);
        protector.Setup(p => p.Unprotect(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, stored) => stored["enc:".Length..]);

        var settings = new Mock<IPlatformSettingRepository>();
        settings.Setup(s => s.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey)).ReturnsAsync(_row);
        settings.Setup(s => s.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, It.IsAny<string>()))
            .ReturnsAsync((string _, string value) =>
            {
                if (!string.IsNullOrEmpty(_row.Value)) { return false; }
                _row.Value = value;
                return true;
            });

        return new PluginSigningService(settings.Object, protector.Object, NullLogger<PluginSigningService>.Instance);
    }

    private async Task<(byte[] File, PluginPublicKey Key)> BuildRealScriptAsync()
    {
        var signing = NewSigningService();
        var script = await new PluginScriptService(signing, new EmbeddedPluginScriptSource()).BuildAsync();
        return (script.Bytes, await signing.GetPublicKeyAsync());
    }

    [Fact]
    public async Task TheScriptTheApiServesVerifiesUnderThePluginsOwnIntegrityCheck()
    {
        var (file, key) = await BuildRealScriptAsync();

        var result = ArchonIntegrity.Check(file, key.ModulusBase64, key.ExponentBase64);

        Assert.Equal("valid", result.State);
        Assert.Equal(key.Fingerprint, result.KeyFingerprint); // both sides define the fingerprint identically
    }

    [Fact]
    public async Task TheKeyStampedIntoTheServedFileIsTheOneItWasSignedWith()
    {
        // What the installed plugin would actually hold in its own constants: read them back out of the served file
        // and verify the file against THOSE, not against a value handed in from outside.
        var (file, key) = await BuildRealScriptAsync();
        var text = Encoding.UTF8.GetString(file);

        var modulus = Extract(text, "TrustedModulus");
        var exponent = Extract(text, "TrustedExponent");

        Assert.Equal(key.ModulusBase64, modulus);
        Assert.Equal(key.ExponentBase64, exponent);
        Assert.True(ArchonIntegrity.IsStamped(modulus, exponent));
        Assert.Equal("valid", ArchonIntegrity.Check(file, modulus, exponent).State);
    }

    [Fact]
    public async Task AlteringAnyByteOfTheServedScriptBreaksTheSignature()
    {
        var (file, key) = await BuildRealScriptAsync();
        var altered = (byte[])file.Clone();
        altered[altered.Length / 2] ^= 1;

        Assert.Equal("invalid", ArchonIntegrity.Check(altered, key.ModulusBase64, key.ExponentBase64).State);
    }

    [Fact]
    public async Task AScriptSignedByAnotherPanelIsInvalidUnderThisPanelsKey()
    {
        var (file, _) = await BuildRealScriptAsync();
        using var otherPanel = RSA.Create(2048);
        var other = otherPanel.ExportParameters(false);

        var result = ArchonIntegrity.Check(file, Convert.ToBase64String(other.Modulus!), Convert.ToBase64String(other.Exponent!));

        Assert.Equal("invalid", result.State);
    }

    [Fact]
    public async Task ConvertingTheServedFilesLineEndingsToCrLfBreaksItRatherThanPassing()
    {
        var (file, key) = await BuildRealScriptAsync();
        var crlf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file).Replace("\n", "\r\n"));

        Assert.Equal("invalid", ArchonIntegrity.Check(crlf, key.ModulusBase64, key.ExponentBase64).State);
    }

    [Fact]
    public async Task TheServedScriptStillContainsExactlyOneSignatureLineAtTheEnd()
    {
        var (file, _) = await BuildRealScriptAsync();
        var text = Encoding.UTF8.GetString(file);

        var lines = text.Split('\n');
        Assert.Equal("", lines[^1]);
        Assert.StartsWith(ArchonIntegrity.SignatureMarker, lines[^2]);
        // Only the last line begins with the marker; the constant in the source is mid-line.
        Assert.Equal(1, lines.Count(l => l.StartsWith(ArchonIntegrity.SignatureMarker, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheStamperAndThePluginAgreeOnTheMarkerAndThePlaceholders()
    {
        Assert.Equal(PluginScriptStamper.SignatureMarker, ArchonIntegrity.SignatureMarker);
        Assert.Equal(PluginScriptStamper.ModulusPlaceholder, ArchonIntegrity.TrustedModulus);
        Assert.Equal(PluginScriptStamper.ExponentPlaceholder, ArchonIntegrity.TrustedExponent);
    }

    private static string Extract(string text, string constantName)
    {
        var match = Regex.Match(text, "const string " + constantName + " = \"([^\"]*)\";");
        Assert.True(match.Success, $"{constantName} not found in the served file");
        return match.Groups[1].Value;
    }
}
