// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using RustArchon.Shared.PluginZips;

namespace RustArchon.Api.Tests;

/// <summary>
/// What a person's folder rules make of a plugin's zip archive: where each file goes, what is skipped, and everything that would make the mapping unsafe
/// or ambiguous (a file no rule covers, two files landing on one path, a program file, a path that could escape its folder). The plugin on the game
/// server has its own copy of this algorithm; its tests hold it to the same answers.
/// </summary>
public class ZipMappingTests
{
    private static readonly IReadOnlyList<ZipEntryInfo> Archive =
    [
        new("en/plugins/test.cs", 100), new("en/configs/test.json", 20), new("en/images/test/one.jpg", 1000), new("en/images/test/two.jpg", 2000),
        new("ru/plugins/test.cs", 110), new("ru/configs/test.json", 21), new("ru/images/test/one.jpg", 1001), new("ru/images/test/two.jpg", 2001)
    ];

    private static ZipMappingRule Folder(string source, string role, string sub = "", bool keep = false) =>
        new() { IsFolder = true, Source = source, Role = role, SubFolder = sub, KeepExisting = keep };

    private static ZipMappingRule File(string source, string role, string sub = "", bool keep = false) =>
        new() { IsFolder = false, Source = source, Role = role, SubFolder = sub, KeepExisting = keep };

    private static readonly ZipMappingRule[] EnglishOnly =
    [
        Folder("en/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config), Folder("en/images/", ZipRoles.Data), Folder("ru/", ZipRoles.Skip)
    ];

    private static Dictionary<string, string> Destinations(ZipMappingResult result) =>
        result.Installed.ToDictionary(e => e.Path, e => e.Destination);

    // ---- the example -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheEnglishFoldersGoToTheirPlacesAndRussianIsIgnored()
    {
        var result = ZipMapping.Resolve(Archive, EnglishOnly);

        Assert.True(result.IsValid, string.Join("; ", result.Problems.Select(p => p.Message)));
        Assert.Equal(new Dictionary<string, string>
        {
            ["en/plugins/test.cs"] = "plugins/test.cs",
            ["en/configs/test.json"] = "config/test.json",
            ["en/images/test/one.jpg"] = "data/test/one.jpg",             // the structure below the mapped folder is kept
            ["en/images/test/two.jpg"] = "data/test/two.jpg"
        }, Destinations(result));
        Assert.Equal(4, result.Entries.Count(e => e.Action == ZipEntryAction.Skip));
        Assert.Equal(1000 + 2000 + 100 + 20, result.InstallBytes);
    }

    [Fact]
    public void EveryFileHasToBeInstalledOrSkippedNothingIsGuessed()
    {
        var result = ZipMapping.Resolve(Archive, [Folder("en/plugins/", ZipRoles.Plugins)]);

        Assert.False(result.IsValid);
        Assert.False(result.CoversEveryFile);
        Assert.Equal(7, result.Problems.Count(p => p.Code == ZipMappingProblemCodes.Unassigned));
    }

    [Fact]
    public void BothLanguagesCannotBeInstalledOverEachOther()
    {
        var result = ZipMapping.Resolve(Archive,
        [
            Folder("en/plugins/", ZipRoles.Plugins), Folder("ru/plugins/", ZipRoles.Plugins),
            Folder("en/configs/", ZipRoles.Config), Folder("ru/configs/", ZipRoles.Config),
            Folder("en/images/", ZipRoles.Data), Folder("ru/images/", ZipRoles.Data)
        ]);

        Assert.Equal(4, result.Problems.Count(p => p.Code == ZipMappingProblemCodes.Collision));
        Assert.All(result.Problems, p => Assert.Equal(ZipMappingProblemCodes.Collision, p.Code));
    }

    // ---- precedence --------------------------------------------------------------------------------------------------

    [Fact]
    public void AFileRuleBeatsTheFolderRuleSoOneFileCanBeSkippedInsideAMappedFolder()
    {
        var result = ZipMapping.Resolve(Archive, [.. EnglishOnly, File("en/images/test/two.jpg", ZipRoles.Skip)]);

        Assert.True(result.IsValid);
        Assert.Equal(["en/configs/test.json", "en/images/test/one.jpg", "en/plugins/test.cs"], result.Installed.Select(e => e.Path).Order());
    }

    [Fact]
    public void AFileRuleCanSendOneFileSomewhereElse()
    {
        var result = ZipMapping.Resolve(Archive, [.. EnglishOnly, File("en/images/test/two.jpg", ZipRoles.Data, "extra")]);

        Assert.Equal("data/extra/two.jpg", Destinations(result)["en/images/test/two.jpg"]);          // only the file's own name is kept
    }

    [Fact]
    public void TheDeepestFolderRuleWins()
    {
        var result = ZipMapping.Resolve(Archive, [.. EnglishOnly, Folder("en/images/test/", ZipRoles.Data, "pictures")]);

        Assert.Equal("data/pictures/one.jpg", Destinations(result)["en/images/test/one.jpg"]);
    }

    [Fact]
    public void RuleOrderDoesNotMatter()
    {
        var one = ZipMapping.Resolve(Archive, [.. EnglishOnly, Folder("en/images/test/", ZipRoles.Data, "pictures")]);
        var two = ZipMapping.Resolve(Archive, [Folder("en/images/test/", ZipRoles.Data, "pictures"), .. EnglishOnly.Reverse()]);

        Assert.Equal(Destinations(one), Destinations(two));
    }

    [Fact]
    public void AFolderRuleOnlyCoversWholeFolderNamesNotAPrefixOfAName()
    {
        // "en/plugins/" must not cover "en/plugins-old/x.cs".
        var result = ZipMapping.Resolve([new ZipEntryInfo("en/plugins-old/x.cs", 1)], [Folder("en/plugins", ZipRoles.Plugins)]);

        Assert.False(result.CoversEveryFile);
    }

    [Fact]
    public void ASubFolderIsPutInsideTheRolesFolder()
    {
        var result = ZipMapping.Resolve(Archive, [.. EnglishOnly.Where(r => r.Source != "en/images/"), Folder("en/images/", ZipRoles.Data, "images")]);

        Assert.Equal("data/images/test/one.jpg", Destinations(result)["en/images/test/one.jpg"]);
    }

    [Fact]
    public void KeepExistingIsCarriedThroughToTheEntry()
    {
        var result = ZipMapping.Resolve(Archive, [.. EnglishOnly.Where(r => r.Source != "en/configs/"), Folder("en/configs/", ZipRoles.Config, keep: true)]);

        Assert.True(result.Entries.Single(e => e.Path == "en/configs/test.json").KeepExisting);
        Assert.False(result.Entries.Single(e => e.Path == "en/plugins/test.cs").KeepExisting);
    }

    // ---- what is refused ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en/plugins/thing.dll")]
    [InlineData("en/plugins/thing.DLL")]
    [InlineData("en/plugins/native.so")]
    [InlineData("en/plugins/native.dylib")]
    [InlineData("en/plugins/run.exe")]
    [InlineData("en/plugins/run.sh")]
    [InlineData("en/plugins/run.ps1")]
    [InlineData("en/plugins/run.bat")]
    public void AProgramFileThatWouldBeInstalledIsAProblem(string path)
    {
        var result = ZipMapping.Resolve([new ZipEntryInfo(path, 1)], [Folder("en/plugins/", ZipRoles.Plugins)]);

        Assert.Equal(ZipMappingProblemCodes.Executable, Assert.Single(result.Problems).Code);
    }

    [Fact]
    public void AProgramFileThatIsSkippedIsNotAProblem()
    {
        var result = ZipMapping.Resolve([new ZipEntryInfo("extension/Oxide.Ext.Discord.dll", 1)], [Folder("extension/", ZipRoles.Skip)]);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ASourceFileAnywhereButThePluginsFolderIsAProblem()
    {
        var result = ZipMapping.Resolve([new ZipEntryInfo("en/plugins/test.cs", 1)], [Folder("en/plugins/", ZipRoles.Data)]);

        Assert.Equal(ZipMappingProblemCodes.CodeOutsidePlugins, Assert.Single(result.Problems).Code);
    }

    [Theory]
    [InlineData("../evil.cs")]
    [InlineData("en/../../evil.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x.cs")]
    [InlineData("en/pl:ugins/x.cs")]
    [InlineData("en//x.cs")]
    [InlineData("en/./x.cs")]
    [InlineData("en/x.cs.")]
    [InlineData("en/pl*ugins/x.cs")]
    public void AnArchivePathThatCouldEscapeItsFolderIsNeverInstalled(string path)
    {
        var result = ZipMapping.Resolve([new ZipEntryInfo(path, 1)], [Folder("en/", ZipRoles.Plugins), Folder("", ZipRoles.Plugins)]);

        Assert.Contains(result.Problems, p => p.Code is ZipMappingProblemCodes.UnsafePath or ZipMappingProblemCodes.BadRule);
        Assert.Empty(result.Installed);
    }

    [Fact]
    public void BackslashesInAnArchivePathAreTreatedAsSeparators()
    {
        var result = ZipMapping.Resolve([new ZipEntryInfo("en\\plugins\\test.cs", 1)], [Folder("en/plugins/", ZipRoles.Plugins)]);

        Assert.Equal("plugins/test.cs", Assert.Single(result.Installed).Destination);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a:b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    public void ASubFolderThatCouldEscapeTheRolesFolderIsARefusedRule(string sub)
    {
        var result = ZipMapping.Resolve(Archive, [.. EnglishOnly.Where(r => r.Source != "en/images/"), Folder("en/images/", ZipRoles.Data, sub)]);

        Assert.Contains(result.Problems, p => p.Code == ZipMappingProblemCodes.BadRule);
    }

    [Theory]
    [InlineData("/abs", "data/abs/test/one.jpg")]
    [InlineData("a\\b", "data/a/b/test/one.jpg")]
    [InlineData("a/b/", "data/a/b/test/one.jpg")]
    public void ASubFolderIsWrittenTheWayPathsAre(string sub, string expected)
    {
        // Backslashes are separators and a leading or trailing slash is dropped - the same tidying archive paths get, and never a way out of the folder.
        var result = ZipMapping.Resolve(Archive, [.. EnglishOnly.Where(r => r.Source != "en/images/"), Folder("en/images/", ZipRoles.Data, sub)]);

        Assert.True(result.IsValid);
        Assert.Equal(expected, Destinations(result)["en/images/test/one.jpg"]);
    }

    [Theory]
    [InlineData("en/plugins/", "root")]
    [InlineData("en/plugins/", "")]
    [InlineData("en/plugins/", "PLUGINS")]
    [InlineData("", "plugins")]
    [InlineData("/", "plugins")]
    public void ARuleWithAnUnknownDestinationOrNoSourceIsRefused(string source, string role)
    {
        var result = ZipMapping.Resolve(Archive, [Folder(source, role)]);

        Assert.Contains(result.Problems, p => p.Code == ZipMappingProblemCodes.BadRule);
    }

    [Fact]
    public void ASkipRuleCannotCarryASubFolder()
    {
        var result = ZipMapping.Resolve(Archive, [Folder("ru/", ZipRoles.Skip, "x")]);

        Assert.Contains(result.Problems, p => p.Code == ZipMappingProblemCodes.BadRule);
    }

    [Fact]
    public void CollisionsAreFoundWithoutRegardToCaseSinceServersRunOnWindowsToo()
    {
        var result = ZipMapping.Resolve([new ZipEntryInfo("a/Test.cs", 1), new ZipEntryInfo("b/test.cs", 1)], [Folder("a/", ZipRoles.Plugins), Folder("b/", ZipRoles.Plugins)]);

        Assert.Equal(ZipMappingProblemCodes.Collision, Assert.Single(result.Problems).Code);
    }

    [Fact]
    public void TooManyRulesAreRefused()
    {
        var rules = Enumerable.Range(0, ZipMapping.MaxRules + 1).Select(i => Folder($"f{i}/", ZipRoles.Data)).ToList();

        Assert.Contains(ZipMapping.Resolve(Archive, rules).Problems, p => p.Code == ZipMappingProblemCodes.BadRule);
    }

    [Fact]
    public void ADirectoryEntryIsNotAFileAndIsNotListed()
    {
        // The Api only lists files; a mapping is about files.
        Assert.Empty(ZipMapping.Resolve([], EnglishOnly).Entries);
    }

    // ---- what a saved mapping has to satisfy for a new version --------------------------------------------------------

    [Fact]
    public void ANewFileInsideAMappedFolderIsStillCovered()
    {
        var next = Archive.Append(new ZipEntryInfo("en/images/test/three.jpg", 5)).Append(new ZipEntryInfo("ru/images/test/three.jpg", 5)).ToList();

        var result = ZipMapping.Resolve(next, EnglishOnly);

        Assert.True(result.CoversEveryFile);
        Assert.Contains("en/images/test/three.jpg", Destinations(result).Keys);
    }

    [Theory]
    [InlineData("de/plugins/test.cs")]                 // a new top-level folder
    [InlineData("readme.txt")]                         // a new top-level file
    [InlineData("lang/en/test.json")]
    public void ANewTopLevelFolderOrFileIsNotCoveredSoASavedMappingStops(string added)
    {
        var next = Archive.Append(new ZipEntryInfo(added, 5)).ToList();

        Assert.False(ZipMapping.Resolve(next, EnglishOnly).CoversEveryFile);
    }

    [Fact]
    public void AFileThatIsGoneIsNotAProblem()
    {
        var result = ZipMapping.Resolve(Archive.Where(e => e.Path != "en/images/test/two.jpg").ToList(), EnglishOnly);

        Assert.True(result.IsValid);
    }

    // ---- as one argument for a console command -------------------------------------------------------------------------

    [Fact]
    public void RulesSurviveTheTripThroughTheCommandArgument()
    {
        List<ZipMappingRule> rules = [.. EnglishOnly, File("en/images/test/two.jpg", ZipRoles.Data, "extra", keep: true)];

        var encoded = ZipMapping.Encode(rules)!;
        var decoded = ZipMapping.Decode(encoded)!;

        Assert.Equal(rules.Count, decoded.Count);
        for (var i = 0; i < rules.Count; i++)
        {
            Assert.Equal((rules[i].IsFolder, rules[i].Source, rules[i].Role, rules[i].SubFolder, rules[i].KeepExisting),
                (decoded[i].IsFolder, decoded[i].Source, decoded[i].Role, decoded[i].SubFolder, decoded[i].KeepExisting));
        }
    }

    [Fact]
    public void TheArgumentHasNothingInItThatCouldSplitACommand()
    {
        var encoded = ZipMapping.Encode([Folder("en/plugins/", ZipRoles.Plugins), Folder("é/ü ñ/", ZipRoles.Data, "a b")])!;

        Assert.Matches("^[A-Za-z0-9_-]+$", encoded);
    }

    [Fact]
    public void RulesThatCannotBeSentAreNotEncoded()
    {
        Assert.Null(ZipMapping.Encode([Folder("a\tb/", ZipRoles.Data)]));
        Assert.Null(ZipMapping.Encode([Folder("a\nb/", ZipRoles.Data)]));
        Assert.Null(ZipMapping.Encode(Enumerable.Range(0, ZipMapping.MaxRules + 1).Select(i => Folder($"f{i}/", ZipRoles.Data)).ToList()));
        Assert.Null(ZipMapping.Encode(Enumerable.Range(0, 40).Select(i => Folder(new string('a', 200) + i + "/", ZipRoles.Data)).ToList()));      // too long for a command
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]                                            // decodes to bytes that are not rule lines
    public void AnArgumentThatIsNotEncodedRulesIsRefused(string? argument)
    {
        Assert.Null(ZipMapping.Decode(argument));
    }

    [Fact]
    public void AnArgumentWithARuleOfTheWrongShapeIsRefused()
    {
        string Raw(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(ZipMapping.Decode(Raw("F\ten/\tdata\n")));                        // four fields
        Assert.Null(ZipMapping.Decode(Raw("X\ten/\tdata\t\t0\n")));                   // not a folder or a file
        Assert.Null(ZipMapping.Decode(Raw("F\ten/\tdata\t\t2\n")));                   // not 0 or 1
        Assert.NotNull(ZipMapping.Decode(Raw("F\ten/\tdata\t\t0\n")));
    }
}
