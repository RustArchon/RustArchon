// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RustArchon.Api.Data;
using RustArchon.Api.Services;

namespace RustArchon.Api.Tests;

/// <summary>
/// What is made of a downloaded plugin file: whether it is a single plugin source file that reads as C# a game server can compile, is the plugin that
/// was asked for and is not older than the update; a zip, which is only listed (nothing in it is applied until a person says which files go where);
/// or something that cannot be applied at all (a web page, not text, empty, a hostile archive).
/// </summary>
public class PluginFileInspectorTests
{
    private readonly PluginFileInspector _inspector = new();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Plugin(string name = "Blueprint Share", string version = "1.4.7", string className = "BlueprintShare", string bases = "RustPlugin", string attribute = "Info") => $$"""
        using System.Collections.Generic;
        using Oxide.Core;

        namespace Oxide.Plugins
        {
            [{{attribute}}("{{name}}", "Nomad Warrior", "{{version}}")]
            [Description("Shares blueprints with the team.")]
            class {{className}} : {{bases}}
            {
                private readonly List<int> _seen = new List<int>();

                void Init()
                {
                    Puts("hello");
                }
            }
        }
        """;

    private PluginFileInspection Inspect(string source, string name = "blueprintshare", string version = "1.4.7") =>
        _inspector.Inspect(Bytes(source), name, version);

    // ---- a plugin source file ------------------------------------------------------------------------------------

    [Fact]
    public void AnOrdinaryPluginFileIsValidAndSaysWhatItIs()
    {
        var content = Bytes(Plugin());

        var result = _inspector.Inspect(content, "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Valid, result.State);
        Assert.Equal("cs", result.Kind);
        Assert.Equal("BlueprintShare", result.ClassName);
        Assert.Equal("Blueprint Share", result.InfoName);
        Assert.Equal("Nomad Warrior", result.InfoAuthor);
        Assert.Equal("1.4.7", result.InfoVersion);
        Assert.Equal(content.LongLength, result.SizeBytes);
        Assert.Null(result.ZipEntries);
    }

    [Fact]
    public void TheHashIsTheSha256OfTheExactBytesInLowerCaseHex()
    {
        var content = Bytes(Plugin());

        var result = _inspector.Inspect(content, "blueprintshare", "1.4.7");

        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(64, result.Sha256.Length);
        Assert.Equal(result.Sha256, result.Sha256.ToLowerInvariant());
    }

    [Fact]
    public void TheSameBytesAlwaysGiveTheSameHashAndOneChangedByteGivesAnother()
    {
        var a = Bytes(Plugin());
        var b = Bytes(Plugin()).Concat(Bytes("// x")).ToArray();

        Assert.Equal(_inspector.Inspect(a, "blueprintshare", "1.4.7").Sha256, _inspector.Inspect(a, "blueprintshare", "1.4.7").Sha256);
        Assert.NotEqual(_inspector.Inspect(a, "blueprintshare", "1.4.7").Sha256, _inspector.Inspect(b, "blueprintshare", "1.4.7").Sha256);
    }

    [Theory]
    [InlineData("RustPlugin")]
    [InlineData("CovalencePlugin")]
    [InlineData("CarbonPlugin")]
    [InlineData("Oxide.Plugins.RustPlugin")]
    public void AnyOfThePluginBaseClassesCounts(string bases)
    {
        Assert.Equal(PluginFileValidationState.Valid, Inspect(Plugin(bases: bases)).State);
    }

    [Fact]
    public void TheNameMayMatchTheClassOrTheInfoNameInAnyCaseAndSpacing()
    {
        Assert.Equal(PluginFileValidationState.Valid, Inspect(Plugin(name: "Something Else", className: "BlueprintShare"), "blueprintshare").State);
        Assert.Equal(PluginFileValidationState.Valid, Inspect(Plugin(name: "Blueprint Share", className: "Other"), "blueprintshare").State);
        Assert.Equal(PluginFileValidationState.Valid, Inspect(Plugin(name: "NPC-Spawn", className: "NpcSpawn"), "npcspawn").State);
    }

    [Fact]
    public void AFileForADifferentPluginIsInvalid()
    {
        var result = Inspect(Plugin(name: "Raidable Bases", className: "RaidableBases"), "blueprintshare");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Contains("Raidable Bases", result.Reason);
    }

    [Theory]
    [InlineData("1.4.7", "1.4.7", true)]     // exactly the update
    [InlineData("1.5.0", "1.4.7", true)]     // newer than the update
    [InlineData("1.4.6", "1.4.7", false)]    // older: a stale download
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("1.4", "1.4.7", true)]       // not a version this can compare: not held against the file
    [InlineData("1.4.7", "2.0", true)]       // nor is one the update was written in
    public void AFileOlderThanTheUpdateIsInvalidAndOneItCannotComparePasses(string inFile, string expected, bool valid)
    {
        var result = Inspect(Plugin(version: inFile), version: expected);

        Assert.Equal(valid ? PluginFileValidationState.Valid : PluginFileValidationState.Invalid, result.State);
        if (!valid)
        {
            Assert.Contains("older than the update", result.Reason);
        }
    }

    [Fact]
    public void ATextFileWithAByteOrderMarkIsStillRead()
    {
        var content = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(Plugin())).ToArray();

        Assert.Equal(PluginFileValidationState.Valid, _inspector.Inspect(content, "blueprintshare", "1.4.7").State);
    }

    // ---- not a plugin ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnEmptyDownloadIsInvalid()
    {
        var result = _inspector.Inspect([], "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Contains("empty", result.Reason);
        Assert.Equal(64, result.Sha256.Length);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>Sign in</title></head><body>Please log in</body></html>")]
    [InlineData("  \n<html lang=\"en\"><body>404 Not Found</body></html>")]
    [InlineData("<!doctype HTML>\n<title>x</title>")]
    public void AWebPageSavedInPlaceOfTheFileIsInvalid(string page)
    {
        var result = Inspect(page);

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Contains("web page", result.Reason);
    }

    [Fact]
    public void BinaryBytesAreNotText()
    {
        var result = _inspector.Inspect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52], "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Contains("not text", result.Reason);
    }

    [Fact]
    public void ABodyThatIsMostlyControlCharactersOrUndecodableBytesIsNotText()
    {
        var controls = Enumerable.Repeat((byte)0x01, 200).ToArray();
        var junk = Enumerable.Repeat((byte)0xC3, 200).ToArray();       // never followed by a continuation byte

        Assert.Contains("not text", _inspector.Inspect(controls, "blueprintshare", "1.4.7").Reason);
        Assert.Contains("not text", _inspector.Inspect(junk, "blueprintshare", "1.4.7").Reason);
    }

    [Fact]
    public void AStrayWindows1252ByteInAnOtherwiseNormalFileIsNotHeldAgainstIt()
    {
        // The real BlueprintShare on uMod has an em dash saved as the single byte 0x97, which is not valid UTF-8; Carbon compiles it fine.
        var source = Plugin().Replace("Puts(\"hello\");", "Puts(\"hello XX world\");");
        var at = source.IndexOf("XX", StringComparison.Ordinal);
        var content = Bytes(source[..at]).Concat(new byte[] { 0x97 }).Concat(Bytes(source[(at + 2)..])).ToArray();

        var result = _inspector.Inspect(content, "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Valid, result.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATextFileSavedAsUtf16WithAByteOrderMarkIsStillRead(bool littleEndian)
    {
        var encoding = littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode;
        var content = encoding.GetPreamble().Concat(encoding.GetBytes(Plugin())).ToArray();

        Assert.Equal(PluginFileValidationState.Valid, _inspector.Inspect(content, "blueprintshare", "1.4.7").State);
    }

    [Fact]
    public void AFileThatDoesNotParseAsCSharpIsInvalidWithTheLineAndWhatIsWrong()
    {
        var result = Inspect(Plugin().Replace("Puts(\"hello\");", "Puts(\"hello\""));

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Equal("cs", result.Kind);
        Assert.Contains("line ", result.Reason);
        Assert.Contains("compile", result.Reason);
    }

    [Fact]
    public void ATruncatedFileIsInvalid()
    {
        var whole = Plugin();

        var result = Inspect(whole[..(whole.Length - 20)]);

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
    }

    [Fact]
    public void ModernSyntaxIsNotHeldAgainstAFileBecauseCarbonCompilesIt()
    {
        // Found by trying the real uMod files: MonumentAddons and RaidableBases use target-typed new (C# 9) and are running on Carbon. A
        // language-version limit here would refuse them, so the file is read as the newest C# and only real syntax errors count.
        var modern = Plugin().Replace(
            "Puts(\"hello\");",
            "var s = 1 switch { 1 => \"a\", _ => \"b\" }; List<int> l = new(); string? maybe = null; var range = s[..1]; Puts(s + l.Count + maybe + range);");

        Assert.Equal(PluginFileValidationState.Valid, Inspect(modern).State);
    }

    [Fact]
    public void AGameTypeThatIsNotDefinedHereIsNotHeldAgainstTheFile()
    {
        // The Api does not have the game's libraries, so it cannot know this is fine - and must not say it is not.
        var source = Plugin().Replace("Puts(\"hello\");", "var p = BasePlayer.FindByID(1UL); if (p != null) p.ChatMessage(\"hi\");");

        Assert.Equal(PluginFileValidationState.Valid, Inspect(source).State);
    }

    [Fact]
    public void AFileWithNoInfoAttributeIsNotAPlugin()
    {
        var result = Inspect(Plugin().Replace("[Info(\"Blueprint Share\", \"Nomad Warrior\", \"1.4.7\")]", string.Empty));

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Contains("[Info]", result.Reason);
    }

    [Fact]
    public void AnInfoAttributeOnAClassThatIsNotAPluginDoesNotMakeItOne()
    {
        var result = Inspect(Plugin(bases: "SomethingElse"));

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
    }

    [Fact]
    public void InfoAlsoWorksWrittenAsInfoAttribute()
    {
        Assert.Equal(PluginFileValidationState.Valid, Inspect(Plugin(attribute: "InfoAttribute")).State);
    }

    [Fact]
    public void AFileThatIsJustSomeOtherTextIsInvalid()
    {
        var result = Inspect("hello, this is not a plugin");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
    }

    [Fact]
    public void TextTakenFromTheFileIsKeptShort()
    {
        var result = Inspect(Plugin(name: new string('n', 400), className: "BlueprintShare"), "blueprintshare");

        Assert.Equal(PluginFileValidationState.Valid, result.State);
        Assert.True(result.Reason.Length <= 300);
    }

    // ---- a zip -----------------------------------------------------------------------------------------------------

    /// <summary>An archive with these files. A name ending in a slash is a folder. A file's content is <paramref name="contents"/> for its name, else a line of text.</summary>
    private static byte[] Zip(string[] entries, IDictionary<string, string>? contents = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(name.EndsWith('/') ? string.Empty : contents is not null && contents.TryGetValue(name, out var text) ? text : "content of " + name);
            }
        }

        return stream.ToArray();
    }

    private static byte[] Zip(params string[] entries) => Zip(entries, new Dictionary<string, string>
    {
        // Real plugin source wherever the archive has a source file, unless a test says otherwise.
        ["en/BlueprintShare.cs"] = Plugin(), ["ru/BlueprintShare.cs"] = Plugin(), ["ok.cs"] = Plugin(), ["BlueprintShare.cs"] = Plugin()
    });

    [Fact]
    public void AZipIsListedWithSizesAndItsPluginFileIsReadOnceAndItNeedsSomeonesInstructions()
    {
        var content = Zip("en/BlueprintShare.cs", "ru/BlueprintShare.cs", "BlueprintShare.json", "images/icon.png", "images/");

        var result = _inspector.Inspect(content, "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.NeedsInstructions, result.State);
        Assert.Equal("zip", result.Kind);
        Assert.Equal(["en/BlueprintShare.cs", "ru/BlueprintShare.cs", "BlueprintShare.json", "images/icon.png"], result.ZipEntries!.Select(e => e.Path));     // the folder is not a file
        Assert.Equal(Bytes(Plugin()).Length, result.ZipEntries!.First().Size);
        Assert.Contains("4 files", result.Reason);
        Assert.Contains("says which go where", result.Reason);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(("BlueprintShare", "Blueprint Share", "1.4.7"), (result.ClassName, result.InfoName, result.InfoVersion));
        Assert.Equal(["en/BlueprintShare.cs", "ru/BlueprintShare.cs"], result.ZipSourceFindings!.Select(f => f.Path));
        Assert.All(result.ZipSourceFindings!, f => Assert.Equal(("BlueprintShare", null), (f.Class, f.Problem)));
    }

    [Fact]
    public void AnArchivesOtherSourceFilesAreReadToo()
    {
        var content = Zip(["en/BlueprintShare.cs", "en/Helper.cs", "en/Broken.cs"], new Dictionary<string, string>
        {
            ["en/BlueprintShare.cs"] = Plugin(), ["en/Helper.cs"] = "namespace X { class Helper { } }", ["en/Broken.cs"] = "class {"
        });

        var result = _inspector.Inspect(content, "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.NeedsInstructions, result.State);
        var byPath = result.ZipSourceFindings!.ToDictionary(f => f.Path);
        Assert.Null(byPath["en/Helper.cs"].Class);                    // a source file that is not a plugin: fine
        Assert.Null(byPath["en/Helper.cs"].Problem);
        Assert.Contains("does not read as C#", byPath["en/Broken.cs"].Problem);        // recorded, and refused if someone tries to install it
    }

    [Fact]
    public void AnArchiveWithNoPluginFileForThePluginAskedForIsInvalid()
    {
        var result = _inspector.Inspect(Zip(["Other.cs", "data.json"], new Dictionary<string, string> { ["Other.cs"] = Plugin("Other", className: "Other") }), "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Contains("no plugin file for the plugin asked for", result.Reason);
    }

    [Fact]
    public void AnArchiveWhosePluginFileDoesNotReadAsCSharpIsInvalidAndSaysSo()
    {
        var result = _inspector.Inspect(Zip(["BlueprintShare.cs"], new Dictionary<string, string> { ["BlueprintShare.cs"] = "class BlueprintShare : RustPlugin {" }), "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
    }

    [Fact]
    public void AnArchiveWhosePluginIsOlderThanTheUpdateIsInvalid()
    {
        var result = _inspector.Inspect(Zip(["BlueprintShare.cs"], new Dictionary<string, string> { ["BlueprintShare.cs"] = Plugin(version: "1.0.0") }), "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Contains("older than the update", result.Reason);
    }

    [Fact]
    public void ProgramFilesInAnArchiveAreOnlyListedNotJudgedSoTheyCanBeSkipped()
    {
        var result = _inspector.Inspect(Zip("BlueprintShare.cs", "extension/Oxide.Ext.Discord.dll"), "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.NeedsInstructions, result.State);
        Assert.Contains("extension/Oxide.Ext.Discord.dll", result.ZipEntries!.Select(e => e.Path));
    }

    [Fact]
    public void ASourceFileThatIsHugeIsNotReadAndSaysSo()
    {
        var huge = new string('/', PluginFileInspector.MaxZipSourceBytes + 10);
        var result = _inspector.Inspect(Zip(["BlueprintShare.cs", "Big.cs"], new Dictionary<string, string> { ["BlueprintShare.cs"] = Plugin(), ["Big.cs"] = huge }), "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.NeedsInstructions, result.State);
        Assert.Equal("too large to check", result.ZipSourceFindings!.Single(f => f.Path == "Big.cs").Problem);
    }

    [Fact]
    public void TheListingAndFindingsSurviveBeingKeptOnTheRow()
    {
        var result = _inspector.Inspect(Zip("BlueprintShare.cs", "images/icon.png"), "blueprintshare", "1.4.7");

        var listing = ZipListing.Parse(ZipListing.Format(result.ZipEntries!));
        var findings = ZipListing.ParseFindings(ZipListing.FormatFindings(result.ZipSourceFindings!));

        Assert.Equal(result.ZipEntries, listing);
        Assert.Equal(result.ZipSourceFindings, findings);
    }

    [Theory]
    [InlineData("a/b.cs", "a/b.cs", 0)]                       // an older row: a bare path per line
    public void AListingKeptBeforeSizesWereRecordedStillReads(string line, string path, long size)
    {
        Assert.Equal([new RustArchon.Shared.PluginZips.ZipEntryInfo(path, size)], ZipListing.Parse(line));
        Assert.Empty(ZipListing.Parse(null));
        Assert.Empty(ZipListing.ParseFindings("not json"));
    }

    [Fact]
    public void AZipWithAFileThatWouldBreakOutOfItsFolderIsInvalid()
    {
        foreach (var bad in new[] { "../evil.cs", "en/../../evil.cs", "/etc/passwd", "\\windows\\x.cs", "C:\\x.cs" })
        {
            var result = _inspector.Inspect(Zip("ok.cs", bad), "blueprintshare", "1.4.7");

            Assert.Equal(PluginFileValidationState.Invalid, result.State);
            Assert.Contains("not safe", result.Reason);
        }
    }

    [Fact]
    public void ADownloadThatLooksLikeAZipButIsNotOneIsInvalid()
    {
        var result = _inspector.Inspect([(byte)'P', (byte)'K', 3, 4, 1, 2, 3, 4, 5, 6, 7, 8], "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Equal("zip", result.Kind);
        Assert.Contains("cannot be read", result.Reason);
    }

    [Fact]
    public void AnArchiveWithMoreFilesThanAnyPluginNeedsIsInvalidBeforeAnyOfItIsRead()
    {
        var content = Zip(Enumerable.Range(0, RustArchon.Shared.PluginZips.ZipMapping.MaxArchiveFiles + 1).Select(i => $"f{i:D4}.txt").Prepend("BlueprintShare.cs").ToArray());

        var result = _inspector.Inspect(content, "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Contains("more than 2000 files", result.Reason);
    }

    [Fact]
    public void TheFileCountComesFromTheCentralDirectoryItselfNotTheCountThePackageClaims()
    {
        // A hostile archive can put any number in its end-of-directory header. Here the header says 1 file and the directory lists 2001.
        var content = Zip(Enumerable.Range(0, RustArchon.Shared.PluginZips.ZipMapping.MaxArchiveFiles + 1).Select(i => $"f{i:D4}.txt").Prepend("BlueprintShare.cs").ToArray());
        var eocd = content.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4b, 5, 6 });
        content[eocd + 8] = 1; content[eocd + 9] = 0;          // entries on this disk
        content[eocd + 10] = 1; content[eocd + 11] = 0;        // entries in total

        var result = _inspector.Inspect(content, "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
        Assert.Contains("more than 2000 files", result.Reason);
    }

    [Fact]
    public void AnArchiveWhoseDirectoryPointsNowhereUsefulIsUnreadableNotAnExplosion()
    {
        var content = Zip("BlueprintShare.cs");
        var eocd = content.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4b, 5, 6 });
        for (var i = 0; i < 4; i++) { content[eocd + 16 + i] = 0xFF; }        // the directory "starts" past the end of the file

        var result = _inspector.Inspect(content, "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.Invalid, result.State);
    }

    [Fact]
    public void AZipWithAHugeNumberOfFilesListsOnlyTheFirstFiveHundred()
    {
        var content = Zip(Enumerable.Range(0, 600).Select(i => $"f{i:D3}.txt").Prepend("BlueprintShare.cs").ToArray());

        var result = _inspector.Inspect(content, "blueprintshare", "1.4.7");

        Assert.Equal(PluginFileValidationState.NeedsInstructions, result.State);
        Assert.Equal(PluginDownloadLookup.MaxZipEntries, result.ZipEntries!.Count);
        Assert.Contains("601 files", result.Reason);
        Assert.Contains("first 500", result.Reason);
    }
}
