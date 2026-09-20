// Copyright ©2026 Scott Blomfield

using System.Security.Cryptography;
using System.Text;
using Oxide.Plugins;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// The plugin's own check of a bridged file: last line signed by the PREVIOUS key (for the Updater), the line above it
/// signed by the file's own embedded key. The main plugin must call that valid; the strict last-line check must not.
/// Both plugins carry a copy of this code and must agree on every case.
/// </summary>
public sealed class RustArchonIntegrityBridgeTests : IDisposable
{
    private const string Marker = "// RUSTARCHON-SIG-V1: ";
    private readonly RSA _old = RSA.Create(2048);
    private readonly RSA _new = RSA.Create(2048);
    private readonly RSA _other = RSA.Create(2048);

    public void Dispose()
    {
        _old.Dispose();
        _new.Dispose();
        _other.Dispose();
    }

    private static string Mod(RSA key) => Convert.ToBase64String(key.ExportParameters(false).Modulus!);
    private static string Exp(RSA key) => Convert.ToBase64String(key.ExportParameters(false).Exponent!);
    private static string Sign(RSA key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    private static byte[] Line(string base64) => Encoding.ASCII.GetBytes(Marker + base64 + "\n");

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("namespace X\n{\n    class Plugin { }\n}\n");

    private byte[] Bridged(RSA? cosigner = null, RSA? outer = null)
    {
        var withCosignature = Payload.Concat(Line(Sign(cosigner ?? _new, Payload))).ToArray();
        return withCosignature.Concat(Line(Sign(outer ?? _old, withCosignature))).ToArray();
    }

    // ---- the main plugin's check ---------------------------------------------------------------------------

    [Fact]
    public void ABridgedFileIsValidUnderItsEmbeddedNewKey()
    {
        var result = ArchonIntegrity.Check(Bridged(), Mod(_new), Exp(_new));

        Assert.Equal("valid", result.State);
        Assert.Equal(ArchonIntegrity.Fingerprint(Mod(_new)), result.KeyFingerprint);
    }

    [Fact]
    public void ABridgedFileIsAlsoValidToTheOldKeyBecauseThatIsWhatAnOlderUpdaterChecks()
    {
        Assert.Equal("valid", ArchonIntegrity.Check(Bridged(), Mod(_old), Exp(_old)).State);
    }

    [Fact]
    public void ABridgedFileIsNotValidUnderAnUnrelatedKey()
    {
        Assert.Equal("invalid", ArchonIntegrity.Check(Bridged(), Mod(_other), Exp(_other)).State);
    }

    [Fact]
    public void TheCosignatureMustBeTheNewKeysOwn()
    {
        var file = Bridged(cosigner: _other);

        Assert.Equal("invalid", ArchonIntegrity.Check(file, Mod(_new), Exp(_new)).State);
    }

    [Fact]
    public void AlteringTheSourceBreaksBothSignaturesSoNothingIsValid()
    {
        var file = Bridged();
        file[10] ^= 1;

        Assert.Equal("invalid", ArchonIntegrity.Check(file, Mod(_new), Exp(_new)).State);
        Assert.Equal("invalid", ArchonIntegrity.Check(file, Mod(_old), Exp(_old)).State);
    }

    [Fact]
    public void ACosignatureThatIsNotDirectlyAboveTheLastLineDoesNotCount()
    {
        // A blank line between them: the shape is exact, so nothing can be slipped in between the two signatures.
        var cosigned = Payload.Concat(Line(Sign(_new, Payload))).ToArray();
        var spaced = cosigned.Concat(Encoding.ASCII.GetBytes("\n")).ToArray();
        var file = spaced.Concat(Line(Sign(_old, spaced))).ToArray();

        Assert.Equal("invalid", ArchonIntegrity.Check(file, Mod(_new), Exp(_new)).State);
    }

    [Fact]
    public void AnOrdinarySingleSignedFileIsStillValidAndNeedsNoCosignature()
    {
        var file = Payload.Concat(Line(Sign(_new, Payload))).ToArray();

        Assert.Equal("valid", ArchonIntegrity.Check(file, Mod(_new), Exp(_new)).State);
        Assert.Equal("unsigned", ArchonIntegrity.CheckCosignature(file, Mod(_new), Exp(_new)).State);
    }

    // ---- the two strict halves -----------------------------------------------------------------------------

    [Fact]
    public void TheLastLineCheckSeesOnlyTheOldKeysSignatureOnABridgedFile()
    {
        var file = Bridged();

        Assert.Equal("valid", ArchonIntegrity.CheckLastLine(file, Mod(_old), Exp(_old)).State);
        Assert.Equal("invalid", ArchonIntegrity.CheckLastLine(file, Mod(_new), Exp(_new)).State);
    }

    [Fact]
    public void TheCosignatureCheckSeesOnlyTheNewKeysSignature()
    {
        var file = Bridged();

        Assert.Equal("valid", ArchonIntegrity.CheckCosignature(file, Mod(_new), Exp(_new)).State);
        Assert.Equal("invalid", ArchonIntegrity.CheckCosignature(file, Mod(_old), Exp(_old)).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("@@RUSTARCHON_TRUSTED_MODULUS@@")]
    public void NeitherStrictCheckTrustsAKeyThatIsNotStamped(string modulus)
    {
        var file = Bridged();

        Assert.Equal("unsigned", ArchonIntegrity.CheckLastLine(file, modulus, "AQAB").State);
        Assert.Equal("unsigned", ArchonIntegrity.CheckCosignature(file, modulus, "AQAB").State);
    }

    [Fact]
    public void AFileWithOnlyTheOneLineHasNoLineAboveIt()
    {
        var file = Line(Sign(_new, []));

        Assert.Equal("unsigned", ArchonIntegrity.CheckCosignature(file, Mod(_new), Exp(_new)).State);
    }

    // ---- the Updater's copy of the code must agree ---------------------------------------------------------

    [Fact]
    public void TheUpdatersCopyAgreesWithTheMainPluginsOnEveryBridgeCase()
    {
        var tampered = Bridged();
        tampered[10] ^= 1;
        var cases = new Dictionary<string, byte[]>
        {
            ["bridge"] = Bridged(),
            ["wrong cosigner"] = Bridged(cosigner: _other),
            ["wrong outer"] = Bridged(outer: _other),
            ["tampered"] = tampered,
            ["single"] = Payload.Concat(Line(Sign(_new, Payload))).ToArray(),
            ["one line only"] = Line(Sign(_new, [])),
            ["no signature"] = Payload,
        };

        foreach (var (name, file) in cases)
        {
            foreach (var key in new[] { _old, _new, _other })
            {
                var main = ArchonIntegrity.Check(file, Mod(key), Exp(key));
                var updater = UpdaterIntegrity.Check(file, Mod(key), Exp(key));
                Assert.True(main.State == updater.State && main.KeyFingerprint == updater.KeyFingerprint, $"{name} Check");

                var mainLast = ArchonIntegrity.CheckLastLine(file, Mod(key), Exp(key));
                var updaterLast = UpdaterIntegrity.CheckLastLine(file, Mod(key), Exp(key));
                Assert.True(mainLast.State == updaterLast.State, $"{name} CheckLastLine");

                var mainCo = ArchonIntegrity.CheckCosignature(file, Mod(key), Exp(key));
                var updaterCo = UpdaterIntegrity.CheckCosignature(file, Mod(key), Exp(key));
                Assert.True(mainCo.State == updaterCo.State, $"{name} CheckCosignature");
            }
        }
    }
}
