// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="PluginScriptStamper"/> - how the plugin source becomes the file a Panel serves. The
/// plugin's own <c>ArchonIntegrity</c> verifies exactly what this produces, so the byte-level rules here (LF
/// endings, no BOM, one trailing newline, marker on its own last line) are a contract, not style.
/// </summary>
public class PluginScriptStamperTests
{
    private const string Modulus = "TU9EVUxVUw==";
    private const string Exponent = "AQAB";

    private static string Source(string body = "class P { }") =>
        $"const string M = \"{PluginScriptStamper.ModulusPlaceholder}\";\nconst string E = \"{PluginScriptStamper.ExponentPlaceholder}\";\n{body}\n";

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    // ---- stamping ------------------------------------------------------------------------------------------

    [Fact]
    public void ReplacesBothPlaceholdersWithTheKey()
    {
        var text = Text(PluginScriptStamper.Stamp(Source(), Modulus, Exponent));

        Assert.Contains($"const string M = \"{Modulus}\";", text);
        Assert.Contains($"const string E = \"{Exponent}\";", text);
        Assert.DoesNotContain("@@RUSTARCHON", text);
    }

    [Fact]
    public void NormalizesCrLfToLfSoTheBytesAreTheSameWhereverItWasCheckedOut()
    {
        var crlf = Source().Replace("\n", "\r\n");

        var stamped = PluginScriptStamper.Stamp(crlf, Modulus, Exponent);

        Assert.DoesNotContain((byte)'\r', stamped);
        Assert.Equal(PluginScriptStamper.Stamp(Source(), Modulus, Exponent), stamped);
    }

    [Fact]
    public void StripsAByteOrderMark()
    {
        var withBom = "﻿" + Source();

        var stamped = PluginScriptStamper.Stamp(withBom, Modulus, Exponent);

        Assert.NotEqual(0xEF, stamped[0]); // no UTF-8 BOM bytes (EF BB BF)
        Assert.Equal(PluginScriptStamper.Stamp(Source(), Modulus, Exponent), stamped);
    }

    [Fact]
    public void EndsWithExactlyOneNewline()
    {
        var withoutNewline = Source().TrimEnd('\n');

        var stamped = PluginScriptStamper.Stamp(withoutNewline, Modulus, Exponent);

        Assert.Equal((byte)'\n', stamped[^1]);
        Assert.NotEqual((byte)'\n', stamped[^2]);
    }

    [Fact]
    public void IsDeterministic()
    {
        Assert.Equal(
            PluginScriptStamper.Stamp(Source(), Modulus, Exponent),
            PluginScriptStamper.Stamp(Source(), Modulus, Exponent));
    }

    // ---- refusing a bad source ------------------------------------------------------------------------------

    [Fact]
    public void RefusesASourceMissingAPlaceholder()
    {
        var noExponent = $"const string M = \"{PluginScriptStamper.ModulusPlaceholder}\";\n";

        var ex = Assert.Throws<InvalidOperationException>(() => PluginScriptStamper.Stamp(noExponent, Modulus, Exponent));

        Assert.Contains(PluginScriptStamper.ExponentPlaceholder, ex.Message);
    }

    [Fact]
    public void RefusesASourceWithAPlaceholderTwice()
    {
        var twice = Source() + $"// {PluginScriptStamper.ModulusPlaceholder}\n";

        var ex = Assert.Throws<InvalidOperationException>(() => PluginScriptStamper.Stamp(twice, Modulus, Exponent));

        Assert.Contains("more than once", ex.Message);
    }

    [Theory]
    [InlineData(null, "AQAB")]
    [InlineData("", "AQAB")]
    [InlineData("TU9E", null)]
    [InlineData("TU9E", "")]
    public void RefusesAMissingKey(string? modulus, string? exponent)
    {
        Assert.ThrowsAny<ArgumentException>(() => PluginScriptStamper.Stamp(Source(), modulus!, exponent!));
    }

    [Fact]
    public void RefusesAnEmptySource()
    {
        Assert.ThrowsAny<ArgumentException>(() => PluginScriptStamper.Stamp("", Modulus, Exponent));
    }

    // ---- attaching the signature ----------------------------------------------------------------------------

    [Fact]
    public void AttachPutsTheMarkerAndBase64SignatureOnItsOwnFinalLine()
    {
        var payload = PluginScriptStamper.Stamp(Source(), Modulus, Exponent);
        var signature = new byte[] { 1, 2, 3, 250, 251, 252 };

        var file = PluginScriptStamper.Attach(payload, signature);

        var text = Text(file);
        var lines = text.Split('\n');
        Assert.Equal("", lines[^1]); // ends with a newline
        Assert.Equal(PluginScriptStamper.SignatureMarker + Convert.ToBase64String(signature), lines[^2]);
        Assert.Equal(payload, file.Take(payload.Length).ToArray()); // the payload is untouched, then the line
    }

    [Fact]
    public void TheMarkerIsTheExactTextThePluginLooksFor()
    {
        // The plugin's verifier hard-codes this string. If it changes here it must change there, or every served
        // script reads as "unsigned".
        Assert.Equal("// RUSTARCHON-SIG-V1: ", PluginScriptStamper.SignatureMarker);
    }

    // ---- fingerprint ---------------------------------------------------------------------------------------

    [Fact]
    public void TheFingerprintIsTheFirstEightBytesOfSha256OverTheModulusAsLowercaseHex()
    {
        var modulus = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var expected = Convert.ToHexString(SHA256.HashData(modulus)).ToLowerInvariant()[..16];

        var fingerprint = PluginScriptStamper.Fingerprint(Convert.ToBase64String(modulus));

        Assert.Equal(expected, fingerprint);
        Assert.Matches("^[0-9a-f]{16}$", fingerprint);
    }

    // ---- the real embedded source --------------------------------------------------------------------------

    [Fact]
    public void TheRealEmbeddedPluginSourceCanBeStamped()
    {
        // Guards the build: if someone removes a placeholder from the plugin, or types one twice, the Panel would
        // ship a script with no trusted key. This fails first.
        var source = new EmbeddedPluginScriptSource().ReadSource();

        var stamped = Text(PluginScriptStamper.Stamp(source, Modulus, Exponent));

        Assert.Contains($"\"{Modulus}\"", stamped);
        Assert.Contains($"\"{Exponent}\"", stamped);
        Assert.DoesNotContain("@@RUSTARCHON", stamped);
    }

    [Fact]
    public void TheRealEmbeddedUpdaterSourceCanBeStampedAndCarriesTheMarker()
    {
        var source = new EmbeddedPluginScriptSource().ReadUpdaterSource();

        var stamped = Text(PluginScriptStamper.Stamp(source, Modulus, Exponent));

        Assert.Contains($"\"{Modulus}\"", stamped);
        Assert.Contains($"\"{Exponent}\"", stamped);
        Assert.DoesNotContain("@@RUSTARCHON", stamped);
        Assert.Contains($"\"{PluginScriptStamper.SignatureMarker}\"", source);
    }

    [Fact]
    public void TheEmbeddedPluginSourceCarriesTheSignatureMarkerConstantTheStamperAndPluginAgreeOn()
    {
        var source = new EmbeddedPluginScriptSource().ReadSource();

        Assert.Contains($"\"{PluginScriptStamper.SignatureMarker}\"", source);
    }
}
