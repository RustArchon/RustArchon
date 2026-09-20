// Copyright ©2026 Scott Blomfield

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oxide.Plugins;
using ArchonPlugin = Oxide.Plugins.RustArchon;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// Tests for <see cref="ArchonIntegrity"/>: the plugin's check that its own file carries a signature from the Panel
/// it was downloaded from. Files here are signed by .NET 10 the way the Api signs them (RSA-2048, SHA-256,
/// PKCS#1 v1.5, marker line last), so this is also the cross-runtime contract the game server's Mono runtime has to
/// keep - which the first live load then proves for real.
/// </summary>
public sealed class RustArchonIntegrityTests : IDisposable
{
    private const string Marker = "// RUSTARCHON-SIG-V1: ";
    private const string Source = "namespace X\n{\n    class Plugin { }\n}\n";

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rustarchon-integrity-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Modulus(RSA? key = null) => Convert.ToBase64String((key ?? _key).ExportParameters(false).Modulus!);
    private string Exponent(RSA? key = null) => Convert.ToBase64String((key ?? _key).ExportParameters(false).Exponent!);

    private byte[] Signed(string source = Source, RSA? signer = null)
    {
        var payload = Encoding.UTF8.GetBytes(source);
        var signature = (signer ?? _key).SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return payload.Concat(Encoding.ASCII.GetBytes(Marker + Convert.ToBase64String(signature) + "\n")).ToArray();
    }

    private ArchonIntegrity.Result Check(byte[] file, RSA? trusted = null) =>
        ArchonIntegrity.Check(file, Modulus(trusted), Exponent(trusted));

    // ---- the good case ------------------------------------------------------------------------------------

    [Fact]
    public void AFileSignedByTheStampedKeyIsValid()
    {
        var result = Check(Signed());

        Assert.Equal("valid", result.State);
        Assert.Matches("^[0-9a-f]{16}$", result.KeyFingerprint);
    }

    [Fact]
    public void ATrailingCarriageReturnAfterTheSignatureLineStillVerifies()
    {
        var file = Signed();
        var withCrLf = file.Take(file.Length - 1).Concat(new byte[] { (byte)'\r', (byte)'\n' }).ToArray();

        Assert.Equal("valid", Check(withCrLf).State);
    }

    [Fact]
    public void TheMarkerTextInsideTheSourceIsNotMistakenForTheSignatureLine()
    {
        // The plugin's own source contains the marker text in a constant, mid-line. Only a line that STARTS with it
        // counts, so the real one at the end is the one verified.
        var source = "class P { const string M = \"// RUSTARCHON-SIG-V1: \"; }\n";

        Assert.Equal("valid", Check(Signed(source)).State);
    }

    [Fact]
    public void TheLastSignatureLineWinsAndAnEarlierOneIsJustSignedContent()
    {
        var source = Source + Marker + "AAAA\n";

        Assert.Equal("valid", Check(Signed(source)).State);
    }

    [Fact]
    public void TheFingerprintIsStableAndDiffersBetweenKeys()
    {
        using var other = RSA.Create(2048);

        var first = Check(Signed()).KeyFingerprint;
        var again = Check(Signed()).KeyFingerprint;
        var different = Check(Signed(signer: other), other).KeyFingerprint;

        Assert.Equal(first, again);
        Assert.NotEqual(first, different);
    }

    // ---- tampering, re-encoding, wrong key ---------------------------------------------------------------

    [Fact]
    public void AlteringASingleByteOfTheSourceMakesItInvalid()
    {
        var file = Signed();
        file[5] ^= 1;

        Assert.Equal("invalid", Check(file).State);
    }

    [Fact]
    public void AlteringTheSignatureMakesItInvalid()
    {
        var file = Signed();
        var at = Encoding.ASCII.GetString(file).LastIndexOf(Marker, StringComparison.Ordinal) + Marker.Length + 10;
        file[at] = file[at] == (byte)'A' ? (byte)'B' : (byte)'A';

        Assert.Equal("invalid", Check(file).State);
    }

    [Fact]
    public void AFileSignedByADifferentKeyIsInvalid()
    {
        using var attacker = RSA.Create(2048);

        var result = Check(Signed(signer: attacker)); // verified against OUR key

        Assert.Equal("invalid", result.State);
    }

    [Fact]
    public void ConvertingLineEndingsToCrLfBreaksTheSignature()
    {
        // What an FTP client in ASCII mode can do to a text file. It must read as invalid, never valid.
        var text = Encoding.UTF8.GetString(Signed());
        var converted = Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n"));

        Assert.Equal("invalid", Check(converted).State);
    }

    [Fact]
    public void ASignatureLineThatIsNotBase64IsInvalidNotAnError()
    {
        var file = Encoding.UTF8.GetBytes(Source + Marker + "this is !!! not base64\n");

        Assert.Equal("invalid", Check(file).State);
    }

    [Fact]
    public void AnEmptySignatureIsInvalid()
    {
        var file = Encoding.UTF8.GetBytes(Source + Marker + "\n");

        Assert.Equal("invalid", Check(file).State);
    }

    // ---- unsigned ------------------------------------------------------------------------------------------

    [Fact]
    public void AStampedFileWithNoSignatureLineIsUnsignedButStillReportsItsKey()
    {
        var result = Check(Encoding.UTF8.GetBytes(Source));

        Assert.Equal("unsigned", result.State);
        Assert.NotEmpty(result.KeyFingerprint);
    }

    [Fact]
    public void TheMarkerOnlyMidLineIsNotASignature()
    {
        var file = Encoding.UTF8.GetBytes("class P { const string M = \"" + Marker + "AAAA\"; }\n");

        Assert.Equal("unsigned", Check(file).State);
    }

    [Fact]
    public void ADeveloperCopyWithTheUnreplacedPlaceholdersIsUnsignedEvenIfItHasASignatureLine()
    {
        var result = ArchonIntegrity.Check(Signed(), ArchonIntegrity.TrustedModulus, ArchonIntegrity.TrustedExponent);

        Assert.Equal("unsigned", result.State);
        Assert.Equal("", result.KeyFingerprint);
    }

    [Theory]
    [InlineData(null, "AQAB")]
    [InlineData("", "AQAB")]
    [InlineData("abc@def", "AQAB")]
    [InlineData("abc", "")]
    [InlineData("abc", "@@RUSTARCHON_TRUSTED_EXPONENT@@")]
    public void OnlyACompleteRealStampCountsAsStamped(string? modulus, string exponent)
    {
        Assert.False(ArchonIntegrity.IsStamped(modulus!, exponent));
    }

    // ---- reading the file ---------------------------------------------------------------------------------

    [Fact]
    public void ANullPathIsUnlocated()
    {
        Assert.Equal("unlocated", ArchonIntegrity.CheckFile(null!, Modulus(), Exponent()).State);
    }

    [Fact]
    public void AMissingFileIsAnErrorNotACrash()
    {
        var result = ArchonIntegrity.CheckFile(Path.Combine(_directory, "nope.cs"), Modulus(), Exponent());

        Assert.Equal("error", result.State);
    }

    [Fact]
    public void ReadsAndVerifiesARealFileOnDisk()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "RustArchon.cs");
        File.WriteAllBytes(path, Signed());

        Assert.Equal("valid", ArchonIntegrity.CheckFile(path, Modulus(), Exponent()).State);
    }

    // ---- what the plugin reports -------------------------------------------------------------------------

    [Fact]
    public void HelloReportsTheSigningState()
    {
        Directory.CreateDirectory(_directory);
        var script = Path.Combine(_directory, "RustArchon.cs");
        File.WriteAllText(script, Source);
        var plugin = new ArchonPlugin
        {
            SettingsFilePath = Path.Combine(_directory, "RustArchon", "settings.txt"),
            ScriptFilePath = script
        };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(plugin, null);
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdHello(arg);

        var signing = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement.GetProperty("data").GetProperty("signing");
        // The source in the test tree keeps its placeholders, so it is an unsigned developer copy.
        Assert.Equal("unsigned", signing.GetProperty("state").GetString());
        Assert.Equal("", signing.GetProperty("keyFingerprint").GetString());
    }

    [Fact]
    public void HelloReportsUnlocatedWhenTheScriptCannotBeFound()
    {
        var plugin = new ArchonPlugin { SettingsFilePath = Path.Combine(_directory, "RustArchon", "settings.txt") };
        typeof(ArchonPlugin).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(plugin, null);
        var arg = ConsoleSystem.Arg.WithArgs();

        plugin.CmdHello(arg);

        var state = JsonDocument.Parse(Assert.Single(arg.Replies)).RootElement
            .GetProperty("data").GetProperty("signing").GetProperty("state").GetString();
        Assert.Equal("unlocated", state);
    }
}
