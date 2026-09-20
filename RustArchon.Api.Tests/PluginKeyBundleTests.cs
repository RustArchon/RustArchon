// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// The file signing keys are exported to. What matters: it round-trips; it is unreadable without the passphrase (and a wrong
/// passphrase looks exactly like a damaged file); changing ANY part of it - the encrypted contents or the header that describes
/// them - makes it unreadable; nothing inside is believed without checking (every key must really be a key of a sane size whose
/// fingerprint is the one claimed); and it never contains a private key in the clear.
/// </summary>
public class PluginKeyBundleTests
{
    private const string Passphrase = "correct horse battery staple";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    // Generating RSA keys is slow enough to matter, so a few are made once and shared.
    private static readonly Lazy<PluginKeyBundleKey[]> Keys = new(() => Enumerable.Range(0, 3).Select(i => NewKey(
        i == 0 ? PluginKeyState.Active : PluginKeyState.Retired)).ToArray());

    private static PluginKeyBundleKey NewKey(PluginKeyState state, int bits = 2048, string? fingerprintOverride = null)
    {
        using var rsa = RSA.Create(bits);
        return new PluginKeyBundleKey(
            fingerprintOverride ?? PluginKeyBundle.FingerprintOf(rsa),
            Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()),
            state,
            state == PluginKeyState.Active ? null : Now.AddDays(-3),
            state == PluginKeyState.Revoked ? Now.AddDays(-1) : null,
            state == PluginKeyState.Revoked ? "leaked" : null);
    }

    private static string Seal(params PluginKeyBundleKey[] keys) => PluginKeyBundle.Seal(keys, Passphrase, Now);

    private static PluginKeyOperationException Refusal(Action action) => Assert.Throws<PluginKeyOperationException>(action);

    /// <summary>The bundle with one top-level field replaced (a value written as JSON text).</summary>
    private static string With(string bundle, string field, string jsonValue)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(bundle)!.AsObject();
        node[field] = System.Text.Json.Nodes.JsonNode.Parse(jsonValue);
        return node.ToJsonString();
    }

    // ---- round trip ----------------------------------------------------------------------------------------

    [Fact]
    public void AnExportedBundleOpensWithThePassphraseAndGivesBackEveryKeyExactly()
    {
        var keys = Keys.Value;

        var opened = PluginKeyBundle.Open(Seal(keys), Passphrase);

        Assert.Equal(keys.Select(k => k.Fingerprint), opened.Select(k => k.Fingerprint));
        Assert.Equal(keys.Select(k => k.Pkcs8Base64), opened.Select(k => k.Pkcs8Base64));
        Assert.Equal(keys.Select(k => k.State), opened.Select(k => k.State));
        Assert.Equal(keys[1].RetiredAtUtc, opened[1].RetiredAtUtc);
    }

    [Fact]
    public void ARevokedKeyKeepsItsReasonAndTimeThroughTheBundle()
    {
        var revoked = NewKey(PluginKeyState.Revoked);

        var opened = PluginKeyBundle.Open(Seal(Keys.Value[0], revoked), Passphrase).Single(k => k.State == PluginKeyState.Revoked);

        Assert.Equal("leaked", opened.RevokedReason);
        Assert.Equal(revoked.RevokedAtUtc, opened.RevokedAtUtc);
    }

    [Fact]
    public void ThePrivateKeysAreNeverInTheFileInTheClear()
    {
        var keys = Keys.Value;

        var bundle = Seal(keys);

        Assert.All(keys, k => Assert.DoesNotContain(k.Pkcs8Base64[..40], bundle));
        Assert.DoesNotContain("PRIVATE", bundle);
        Assert.DoesNotContain(Passphrase, bundle);
    }

    [Fact]
    public void TheFingerprintsAreVisibleWithoutThePassphraseBecauseTheyAreNotSecret()
    {
        var keys = Keys.Value;

        var header = JsonDocument.Parse(Seal(keys)).RootElement.GetProperty("keys").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(keys.Select(k => k.Fingerprint), header);
    }

    [Fact]
    public void TheSameKeysSealedTwiceGiveDifferentFilesBecauseSaltAndNonceAreFresh()
    {
        var a = JsonDocument.Parse(Seal(Keys.Value)).RootElement;
        var b = JsonDocument.Parse(Seal(Keys.Value)).RootElement;

        Assert.NotEqual(a.GetProperty("salt").GetString(), b.GetProperty("salt").GetString());
        Assert.NotEqual(a.GetProperty("nonce").GetString(), b.GetProperty("nonce").GetString());
        Assert.NotEqual(a.GetProperty("ciphertext").GetString(), b.GetProperty("ciphertext").GetString());
    }

    [Fact]
    public void TheKeyDerivationIsSlowOnPurpose()
    {
        var iterations = JsonDocument.Parse(Seal(Keys.Value)).RootElement.GetProperty("iterations").GetInt32();

        Assert.Equal(600_000, iterations);
        Assert.Equal("PBKDF2-SHA256", JsonDocument.Parse(Seal(Keys.Value)).RootElement.GetProperty("kdf").GetString());
    }

    [Fact]
    public void ThePassphraseIsNormalizedSoTheSameWordTypedTwoWaysOpensTheFile()
    {
        var composed = "passéphrase-long-enough";          // é as one character
        var decomposed = "passéphrase-long-enough";       // e followed by a combining accent
        var bundle = PluginKeyBundle.Seal(Keys.Value, composed, Now);

        Assert.Equal(Keys.Value.Length, PluginKeyBundle.Open(bundle, decomposed).Count);
    }

    // ---- the passphrase ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void AWeakPassphraseIsRefusedWhenSealing(string passphrase)
    {
        Assert.Equal("passphrase_weak", Refusal(() => PluginKeyBundle.Seal(Keys.Value, passphrase, Now)).Code);
    }

    [Fact]
    public void ATooLongPassphraseIsRefused()
    {
        Assert.Equal("passphrase_weak", Refusal(() => PluginKeyBundle.Seal(Keys.Value, new string('a', PluginKeyBundle.MaxPassphraseLength + 1), Now)).Code);
    }

    [Fact]
    public void TheMinimumLengthIsAccepted()
    {
        Assert.True(PluginKeyBundle.IsAcceptablePassphrase(new string('a', PluginKeyBundle.MinPassphraseLength)));
        Assert.False(PluginKeyBundle.IsAcceptablePassphrase(null));
    }

    [Fact]
    public void NothingToExportIsRefused()
    {
        Assert.Equal("nothing_to_export", Refusal(() => PluginKeyBundle.Seal([], Passphrase, Now)).Code);
    }

    [Theory]
    [InlineData("wrong passphrase entirely")]
    [InlineData("Correct horse battery staple")]
    [InlineData("correct horse battery staple ")]
    [InlineData("x")]
    public void AWrongPassphraseIsUnreadableAndSaysNothingMore(string wrong)
    {
        var ex = Refusal(() => PluginKeyBundle.Open(Seal(Keys.Value), wrong));

        Assert.Equal("bundle_unreadable", ex.Code);
        Assert.Contains("wrong, or the file has been changed", ex.Message);           // one message for both: no oracle
    }

    [Fact]
    public void AnEmptyOrOverlongPassphraseWhenOpeningIsSimplyUnreadable()
    {
        var bundle = Seal(Keys.Value);

        Assert.Equal("bundle_unreadable", Refusal(() => PluginKeyBundle.Open(bundle, "")).Code);
        Assert.Equal("bundle_unreadable", Refusal(() => PluginKeyBundle.Open(bundle, new string('a', 5000))).Code);
    }

    // ---- tampering: any change makes it unreadable ---------------------------------------------------------

    private static string FlipOneByteOf(string bundle, string field)
    {
        var bytes = Convert.FromBase64String(JsonDocument.Parse(bundle).RootElement.GetProperty(field).GetString()!);
        bytes[bytes.Length / 2] ^= 0x01;
        return With(bundle, field, JsonSerializer.Serialize(Convert.ToBase64String(bytes)));
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("salt")]
    [InlineData("nonce")]
    public void ChangingASingleByteOfAnyEncryptedPartMakesTheFileUnreadable(string field)
    {
        var tampered = FlipOneByteOf(Seal(Keys.Value), field);

        Assert.Equal("bundle_unreadable", Refusal(() => PluginKeyBundle.Open(tampered, Passphrase)).Code);
    }

    [Fact]
    public void ChangingTheExportTimeInTheHeaderMakesTheFileUnreadable()
    {
        var tampered = With(Seal(Keys.Value), "exportedAtUtc", "\"2020-01-01T00:00:00.0000000Z\"");

        Assert.Equal("bundle_unreadable", Refusal(() => PluginKeyBundle.Open(tampered, Passphrase)).Code);
    }

    [Fact]
    public void ChangingTheIterationCountMakesTheFileUnreadable()
    {
        var tampered = With(Seal(Keys.Value), "iterations", "600001");

        Assert.Equal("bundle_unreadable", Refusal(() => PluginKeyBundle.Open(tampered, Passphrase)).Code);
    }

    [Fact]
    public void SwappingTheFingerprintListInTheHeaderMakesTheFileUnreadable()
    {
        var keys = Keys.Value;
        var tampered = With(Seal(keys), "keys", JsonSerializer.Serialize(new[] { keys[0].Fingerprint }));

        Assert.Equal("bundle_unreadable", Refusal(() => PluginKeyBundle.Open(tampered, Passphrase)).Code);
    }

    [Fact]
    public void ReorderingTheFingerprintListIsNoticedToo()
    {
        var keys = Keys.Value;
        var reordered = With(Seal(keys), "keys", JsonSerializer.Serialize(keys.Select(k => k.Fingerprint).Reverse()));

        Assert.Equal("bundle_unreadable", Refusal(() => PluginKeyBundle.Open(reordered, Passphrase)).Code);
    }

    // ---- what is a bundle at all ---------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"something-else\",\"version\":1}")]
    public void ThingsThatAreNotBundlesAreRefusedWithoutAnyOtherException(string text)
    {
        Assert.Equal("bundle_invalid", Refusal(() => PluginKeyBundle.Open(text, Passphrase)).Code);
    }

    [Fact]
    public void ABundleOfAnotherVersionIsRefusedAndSaysWhich()
    {
        var ex = Refusal(() => PluginKeyBundle.Open(With(Seal(Keys.Value), "version", "2"), Passphrase));

        Assert.Equal("bundle_invalid", ex.Code);
        Assert.Contains("version 2", ex.Message);
    }

    [Fact]
    public void AFileOverTheSizeLimitIsRefusedBeforeItIsParsed()
    {
        Assert.Equal("bundle_invalid", Refusal(() => PluginKeyBundle.Open(new string(' ', PluginKeyBundle.MaxBundleBytes + 1), Passphrase)).Code);
    }

    [Theory]
    [InlineData("iterations", "1")]
    [InlineData("iterations", "99999")]
    [InlineData("iterations", "2000001")]
    [InlineData("iterations", "2147483647")]
    [InlineData("kdf", "\"scrypt\"")]
    [InlineData("salt", "\"AAAA\"")]
    [InlineData("nonce", "\"AAAA\"")]
    [InlineData("tag", "\"AAAA\"")]
    [InlineData("ciphertext", "\"\"")]
    [InlineData("salt", "\"!!!not base64!!!\"")]
    [InlineData("keys", "[]")]
    [InlineData("keys", "null")]
    public void AHeaderThisPanelDoesNotAcceptIsRefusedNotBurnedCpuOn(string field, string value)
    {
        var bad = With(Seal(Keys.Value), field, value);

        Assert.Equal("bundle_invalid", Refusal(() => PluginKeyBundle.Open(bad, Passphrase)).Code);
    }

    [Fact]
    public void ABundleClaimingMoreKeysThanAnyRealOneIsRefused()
    {
        var many = PluginKeyBundle.Seal(Enumerable.Repeat(Keys.Value[0], PluginKeyBundle.MaxKeys + 1).ToArray(), Passphrase, Now);

        Assert.Equal("bundle_invalid", Refusal(() => PluginKeyBundle.Open(many, Passphrase)).Code);
    }

    // ---- nothing inside is believed -------------------------------------------------------------------------

    [Fact]
    public void AKeyThatDoesNotMatchItsClaimedFingerprintIsRefused()
    {
        var lying = NewKey(PluginKeyState.Retired, fingerprintOverride: "0123456789abcdef");

        var ex = Refusal(() => PluginKeyBundle.Open(Seal(Keys.Value[0], lying), Passphrase));

        Assert.Equal("bundle_keys_invalid", ex.Code);
        Assert.Contains("fingerprint", ex.Message);
    }

    [Fact]
    public void AKeyTooSmallToTrustIsRefused()
    {
        var small = NewKey(PluginKeyState.Retired, bits: 1024);

        var ex = Refusal(() => PluginKeyBundle.Open(Seal(Keys.Value[0], small), Passphrase));

        Assert.Equal("bundle_keys_invalid", ex.Code);
        Assert.Contains("1024", ex.Message);
    }

    [Fact]
    public void SomethingThatIsNotAPrivateKeyIsRefused()
    {
        var junk = new PluginKeyBundleKey("0123456789abcdef", Convert.ToBase64String([1, 2, 3, 4]), PluginKeyState.Retired, Now, null, null);

        Assert.Equal("bundle_keys_invalid", Refusal(() => PluginKeyBundle.Open(Seal(Keys.Value[0], junk), Passphrase)).Code);
    }

    [Fact]
    public void ANotBase64KeyIsRefused()
    {
        var junk = new PluginKeyBundleKey("0123456789abcdef", "@@@", PluginKeyState.Retired, Now, null, null);

        Assert.Equal("bundle_keys_invalid", Refusal(() => PluginKeyBundle.Open(Seal(Keys.Value[0], junk), Passphrase)).Code);
    }

    [Fact]
    public void TwoActiveKeysInOneBundleAreRefused()
    {
        var second = NewKey(PluginKeyState.Active);

        Assert.Equal("bundle_keys_invalid", Refusal(() => PluginKeyBundle.Open(Seal(Keys.Value[0], second), Passphrase)).Code);
    }

    [Fact]
    public void TheSameKeyTwiceIsRefused()
    {
        Assert.Equal("bundle_keys_invalid", Refusal(() => PluginKeyBundle.Open(Seal(Keys.Value[0], Keys.Value[0] with { State = PluginKeyState.Retired }), Passphrase)).Code);
    }

    [Fact]
    public void AnUnknownStateIsRefused()
    {
        var odd = Keys.Value[1] with { State = (PluginKeyState)9 };

        Assert.Equal("bundle_keys_invalid", Refusal(() => PluginKeyBundle.Open(Seal(Keys.Value[0], odd), Passphrase)).Code);
    }

    [Fact]
    public void ABundleWithNoActiveKeyIsValidBecauseItsOnlyABackupOfHistory()
    {
        var opened = PluginKeyBundle.Open(Seal(Keys.Value[1], Keys.Value[2]), Passphrase);

        Assert.DoesNotContain(opened, k => k.State == PluginKeyState.Active);
    }

    [Fact]
    public void ALongRevokedReasonIsCut()
    {
        var revoked = NewKey(PluginKeyState.Revoked) with { RevokedReason = new string('r', 5000) };

        var opened = PluginKeyBundle.Open(Seal(Keys.Value[0], revoked), Passphrase);

        Assert.Equal(500, opened.Single(k => k.State == PluginKeyState.Revoked).RevokedReason!.Length);
    }

    [Fact]
    public void TheFingerprintIsTheOneAPluginReports()
    {
        // The same computation the plugin's own fingerprint uses (SHA-256 of the raw modulus, first 8 bytes as hex).
        using var rsa = RSA.Create(2048);
        var modulus = rsa.ExportParameters(false).Modulus!;
        var expected = Convert.ToHexStringLower(SHA256.HashData(modulus)[..8]);

        Assert.Equal(expected, PluginKeyBundle.FingerprintOf(rsa));
    }
}
