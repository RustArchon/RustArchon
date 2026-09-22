// Copyright ©2026 Scott Blomfield

using System.Text;
using Oxide.Plugins;
using RustArchon.Shared.PluginZips;

namespace RustArchon.Plugin.Tests;

/// <summary>
/// The plugin cannot reference the shared library (it is one file the game server compiles), so it carries its OWN copy of the folder-rule algorithm.
/// These tests hold that copy to the same answers as <see cref="ZipMapping"/> - the original the Api and the panel use: every archive and rule set
/// from the original's own tests, plus several hundred generated ones from a fixed seed, compared entry by entry and problem by problem, and the
/// command argument's decoder on good and bad input. If the two ever disagree, the plugin would apply something the person was never shown.
/// </summary>
public sealed class RustArchonZipMappingParityTests
{
    private static ZipMappingRule Folder(string source, string role, string sub = "", bool keep = false) =>
        new() { IsFolder = true, Source = source, Role = role, SubFolder = sub, KeepExisting = keep };

    private static ZipMappingRule File(string source, string role, string sub = "", bool keep = false) =>
        new() { IsFolder = false, Source = source, Role = role, SubFolder = sub, KeepExisting = keep };

    private static readonly ZipEntryInfo[] Archive =
    [
        new("en/plugins/test.cs", 100), new("en/configs/test.json", 20), new("en/images/test/one.jpg", 1000), new("en/images/test/two.jpg", 2000),
        new("ru/plugins/test.cs", 110), new("ru/configs/test.json", 21), new("ru/images/test/one.jpg", 1001), new("ru/images/test/two.jpg", 2001)
    ];

    private static readonly ZipMappingRule[] EnglishOnly =
    [
        Folder("en/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config), Folder("en/images/", ZipRoles.Data), Folder("ru/", ZipRoles.Skip)
    ];

    private static ArchonZipMapping.Rule ToPlugin(ZipMappingRule r) =>
        new() { IsFolder = r.IsFolder, Source = r.Source, Role = r.Role, SubFolder = r.SubFolder, KeepExisting = r.KeepExisting };

    /// <summary>Runs both, and fails with the case's name if any entry or any problem differs.</summary>
    private static void AssertSame(string name, IReadOnlyList<ZipEntryInfo> entries, IReadOnlyList<ZipMappingRule> rules)
    {
        var original = ZipMapping.Resolve(entries, rules);
        var port = ArchonZipMapping.Resolve(entries.Select(e => new ArchonZipMapping.EntryInfo(e.Path, e.Size)).ToList(), rules.Select(ToPlugin).ToList());

        Assert.True(original.Entries.Count == port.Entries.Count, $"{name}: {original.Entries.Count} entries vs {port.Entries.Count}");
        for (var i = 0; i < original.Entries.Count; i++)
        {
            var a = original.Entries[i];
            var b = port.Entries[i];
            Assert.True((a.Path, a.Size, a.Action.ToString(), a.Role, a.Destination, a.KeepExisting) == (b.Path, b.Size, b.Action.ToString(), b.Role, b.Destination, b.KeepExisting),
                $"{name}: entry {i} differs: original ({a.Path}, {a.Size}, {a.Action}, {a.Role}, {a.Destination}, {a.KeepExisting}) plugin ({b.Path}, {b.Size}, {b.Action}, {b.Role}, {b.Destination}, {b.KeepExisting})");
        }

        var expectedProblems = original.Problems.Select(p => p.Code + "|" + p.Path).Order(StringComparer.Ordinal).ToList();
        var actualProblems = port.Problems.Select(p => p.Code + "|" + p.Path).Order(StringComparer.Ordinal).ToList();
        Assert.True(expectedProblems.SequenceEqual(actualProblems),
            $"{name}: problems differ: original [{string.Join("; ", expectedProblems)}] plugin [{string.Join("; ", actualProblems)}]");
    }

    // ---- every case the original's own tests use -----------------------------------------------------------------------

    public static IEnumerable<object[]> OriginalCases()
    {
        object[] Case(string name, IReadOnlyList<ZipEntryInfo> entries, IReadOnlyList<ZipMappingRule> rules) => [name, entries, rules];

        ZipMappingRule[] Without(string source) => EnglishOnly.Where(r => r.Source != source).ToArray();

        yield return Case("the example", Archive, EnglishOnly);
        yield return Case("only plugins", Archive, [Folder("en/plugins/", ZipRoles.Plugins)]);
        yield return Case("both languages", Archive,
        [
            Folder("en/plugins/", ZipRoles.Plugins), Folder("ru/plugins/", ZipRoles.Plugins), Folder("en/configs/", ZipRoles.Config),
            Folder("ru/configs/", ZipRoles.Config), Folder("en/images/", ZipRoles.Data), Folder("ru/images/", ZipRoles.Data)
        ]);
        yield return Case("file skip inside folder", Archive, [.. EnglishOnly, File("en/images/test/two.jpg", ZipRoles.Skip)]);
        yield return Case("file elsewhere", Archive, [.. EnglishOnly, File("en/images/test/two.jpg", ZipRoles.Data, "extra")]);
        yield return Case("deepest wins", Archive, [.. EnglishOnly, Folder("en/images/test/", ZipRoles.Data, "pictures")]);
        yield return Case("rule order one", Archive, [.. EnglishOnly, Folder("en/images/test/", ZipRoles.Data, "pictures")]);
        yield return Case("rule order two", Archive, [Folder("en/images/test/", ZipRoles.Data, "pictures"), .. EnglishOnly.Reverse()]);
        yield return Case("whole folder names", [new ZipEntryInfo("en/plugins-old/x.cs", 1)], [Folder("en/plugins", ZipRoles.Plugins)]);
        yield return Case("sub folder", Archive, [.. Without("en/images/"), Folder("en/images/", ZipRoles.Data, "images")]);
        yield return Case("keep existing", Archive, [.. Without("en/configs/"), Folder("en/configs/", ZipRoles.Config, keep: true)]);

        foreach (var name in new[] { "thing.dll", "thing.DLL", "native.so", "native.dylib", "run.exe", "run.sh", "run.ps1", "run.bat" })
        {
            yield return Case("program " + name, [new ZipEntryInfo("en/plugins/" + name, 1)], [Folder("en/plugins/", ZipRoles.Plugins)]);
        }

        yield return Case("skipped program", [new ZipEntryInfo("extension/Oxide.Ext.Discord.dll", 1)], [Folder("extension/", ZipRoles.Skip)]);
        yield return Case("cs outside plugins", [new ZipEntryInfo("en/plugins/test.cs", 1)], [Folder("en/plugins/", ZipRoles.Data)]);

        foreach (var path in new[] { "../evil.cs", "en/../../evil.cs", "/etc/passwd", "C:/Windows/x.cs", "en/pl:ugins/x.cs", "en//x.cs", "en/./x.cs", "en/x.cs.", "en/pl*ugins/x.cs" })
        {
            yield return Case("unsafe " + path, [new ZipEntryInfo(path, 1)], [Folder("en/", ZipRoles.Plugins), Folder("", ZipRoles.Plugins)]);
        }

        yield return Case("backslashes", [new ZipEntryInfo("en\\plugins\\test.cs", 1)], [Folder("en/plugins/", ZipRoles.Plugins)]);

        foreach (var sub in new[] { "..", "a/../b", "a:b", "a/./b", "a//b", "/abs", "a\\b", "a/b/" })
        {
            yield return Case("sub " + sub, Archive, [.. Without("en/images/"), Folder("en/images/", ZipRoles.Data, sub)]);
        }

        foreach (var (source, role) in new[] { ("en/plugins/", "root"), ("en/plugins/", ""), ("en/plugins/", "PLUGINS"), ("", "plugins"), ("/", "plugins") })
        {
            yield return Case($"unknown role {source}|{role}", Archive, [Folder(source, role)]);
        }

        yield return Case("skip with sub", Archive, [Folder("ru/", ZipRoles.Skip, "x")]);
        yield return Case("case collisions", [new ZipEntryInfo("a/Test.cs", 1), new ZipEntryInfo("b/test.cs", 1)], [Folder("a/", ZipRoles.Plugins), Folder("b/", ZipRoles.Plugins)]);
        yield return Case("too many rules", Archive, Enumerable.Range(0, ZipMapping.MaxRules + 1).Select(i => Folder($"f{i}/", ZipRoles.Data)).ToList());
        yield return Case("no entries", [], EnglishOnly);
        yield return Case("new file in mapped folder", [.. Archive, new ZipEntryInfo("en/images/test/three.jpg", 5), new ZipEntryInfo("ru/images/test/three.jpg", 5)], EnglishOnly);

        foreach (var added in new[] { "de/plugins/test.cs", "readme.txt", "lang/en/test.json" })
        {
            yield return Case("new top level " + added, [.. Archive, new ZipEntryInfo(added, 5)], EnglishOnly);
        }

        yield return Case("file gone", Archive.Where(e => e.Path != "en/images/test/two.jpg").ToList(), EnglishOnly);
        yield return Case("lang and plugins", [new ZipEntryInfo("lang/en/Test.json", 3), new ZipEntryInfo("Test.cs", 4)], [Folder("lang/", ZipRoles.Lang), File("Test.cs", ZipRoles.Plugins)]);
    }

    [Theory]
    [MemberData(nameof(OriginalCases))]
    public void ThePluginsCopyAnswersLikeTheOriginalOnEveryCaseTheOriginalIsTestedWith(string name, IReadOnlyList<ZipEntryInfo> entries, IReadOnlyList<ZipMappingRule> rules)
    {
        AssertSame(name, entries, rules);
    }

    // ---- generated cases -------------------------------------------------------------------------------------------------

    private static readonly string[] Segments =
    [
        "en", "ru", "plugins", "configs", "images", "test", "a", "b", "data", "lang", "é", "Test", "sub",
        "..", ".", "", "pl:ugins", "bad*", "end.", "sp ace", " lead", "back\\slash", "q?", "\u007f", "tab\t"
    ];

    private static readonly string[] FileNames =
    [
        "x.cs", "Test.cs", "test.cs", "TEST.CS", "one.jpg", "run.dll", "tool.SH", "native.so", ".dll", "noext", "a.json", "b.png", "end.", "x.exe", "y.CMD", "z.ps1", "cs", "notes.txt.cs"
    ];

    private static readonly string[] Roles = [ZipRoles.Plugins, ZipRoles.Config, ZipRoles.Data, ZipRoles.Lang, ZipRoles.Skip, ZipRoles.Skip, "root", "PLUGINS", ""];

    private static string RandomPath(Random random)
    {
        var depth = random.Next(0, 4);
        var parts = new List<string>();
        for (var i = 0; i < depth; i++)
        {
            // Mostly ordinary folders, occasionally a bad one.
            parts.Add(random.Next(10) < 8 ? Segments[random.Next(0, 13)] : Segments[random.Next(Segments.Length)]);
        }
        parts.Add(random.Next(20) == 0 ? Segments[random.Next(Segments.Length)] : FileNames[random.Next(FileNames.Length)]);

        var path = string.Join(random.Next(15) == 0 ? "\\" : "/", parts);
        return random.Next(20) == 0 ? "/" + path : path;
    }

    private static ZipMappingRule RandomRule(Random random, IReadOnlyList<ZipEntryInfo> entries)
    {
        var isFolder = random.Next(3) != 0;
        string source;
        var pick = random.Next(10);
        if (pick < 6 && entries.Count > 0)
        {
            // A folder or file taken from one of the archive's own paths, so rules actually match.
            var path = entries[random.Next(entries.Count)].Path.Replace('\\', '/').TrimStart('/');
            var pieces = path.Split('/');
            var keep = isFolder ? random.Next(0, pieces.Length) : pieces.Length;
            source = string.Join("/", pieces.Take(keep));
        }
        else if (pick < 9)
        {
            source = RandomPath(random);
        }
        else
        {
            source = new[] { "", "/", "//", ".", "..", "en", "en/" }[random.Next(7)];
        }

        if (isFolder && random.Next(2) == 0) { source += "/"; }

        var sub = random.Next(4) == 0 ? RandomPath(random) : string.Empty;
        return new ZipMappingRule { IsFolder = isFolder, Source = source, Role = Roles[random.Next(Roles.Length)], SubFolder = sub, KeepExisting = random.Next(4) == 0 };
    }

    private static (List<ZipEntryInfo> Entries, List<ZipMappingRule> Rules) RandomCase(Random random)
    {
        var entries = new List<ZipEntryInfo>();
        var count = random.Next(0, 13);
        for (var i = 0; i < count; i++)
        {
            entries.Add(new ZipEntryInfo(RandomPath(random), random.Next(0, 5000)));
        }
        if (random.Next(8) == 0 && entries.Count > 0) { entries.Add(entries[random.Next(entries.Count)]); }          // the same name twice

        var rules = new List<ZipMappingRule>();
        var ruleCount = random.Next(8) == 0 ? random.Next(58, 66) : random.Next(0, 9);
        for (var i = 0; i < ruleCount; i++) { rules.Add(RandomRule(random, entries)); }
        return (entries, rules);
    }

    [Fact]
    public void ThePluginsCopyAnswersLikeTheOriginalOnSeveralHundredGeneratedCases()
    {
        var random = new Random(20260921);
        var installed = 0;
        var problems = 0;
        const int cases = 800;
        for (var i = 0; i < cases; i++)
        {
            var (entries, rules) = RandomCase(random);
            AssertSame("generated case " + i, entries, rules);

            var result = ZipMapping.Resolve(entries, rules);
            installed += result.Installed.Count();
            problems += result.Problems.Count;
        }

        // The generator must actually exercise both the good and the refused paths, or agreeing proves little.
        Assert.True(installed > 200, "installed " + installed);
        Assert.True(problems > 200, "problems " + problems);
    }

    [Fact]
    public void GeneratedCasesThatAreFullyValidAreAlsoInTheMix()
    {
        // Rules built to cover every file of a valid archive: the common case has to agree too, not only the mess.
        var random = new Random(77);
        var valid = 0;
        for (var i = 0; i < 300; i++)
        {
            var languages = new[] { "en", "ru", "de" }.Take(random.Next(1, 4)).ToArray();
            var entries = new List<ZipEntryInfo>();
            foreach (var language in languages)
            {
                entries.Add(new ZipEntryInfo($"{language}/plugins/test.cs", random.Next(1, 999)));
                entries.Add(new ZipEntryInfo($"{language}/configs/test.json", random.Next(1, 99)));
                for (var n = 0; n < random.Next(0, 4); n++) { entries.Add(new ZipEntryInfo($"{language}/images/test/{n}.jpg", random.Next(1, 9999))); }
            }

            var chosen = languages[random.Next(languages.Length)];
            var rules = new List<ZipMappingRule>();
            foreach (var language in languages)
            {
                if (language == chosen)
                {
                    rules.Add(Folder($"{language}/plugins/", ZipRoles.Plugins));
                    rules.Add(Folder($"{language}/configs/", ZipRoles.Config, keep: random.Next(2) == 0));
                    rules.Add(Folder($"{language}/images/", ZipRoles.Data, random.Next(2) == 0 ? "images" : ""));
                }
                else
                {
                    rules.Add(Folder($"{language}/", ZipRoles.Skip));
                }
            }

            AssertSame("valid case " + i, entries, rules);
            if (ZipMapping.Resolve(entries, rules).IsValid) { valid++; }
        }

        Assert.Equal(300, valid);
    }

    // ---- the small pieces ------------------------------------------------------------------------------------------------

    [Fact]
    public void PathSafetyNormalizingAndProgramFilesAgreeOnEverythingGenerated()
    {
        var random = new Random(4242);
        for (var i = 0; i < 2000; i++)
        {
            var path = random.Next(6) == 0 ? new string(Enumerable.Range(0, random.Next(0, 6)).Select(_ => "a/.\\:* x\u007f\t".ToCharArray()[random.Next(10)]).ToArray()) : RandomPath(random);

            Assert.True(ZipMapping.NormalizePath(path) == ArchonZipMapping.NormalizePath(path), "normalize " + path);
            Assert.True(ZipMapping.IsSafePath(path) == ArchonZipMapping.IsSafePath(path), "safe " + path);
            var normalized = ZipMapping.NormalizePath(path);
            if (ZipMapping.IsSafePath(normalized))
            {
                Assert.True(ZipMapping.IsExecutable(normalized) == ArchonZipMapping.IsExecutable(normalized), "executable " + normalized);
            }
        }

        Assert.True(ArchonZipMapping.IsSafePath(new string('a', ZipMapping.MaxPathLength)));
        Assert.False(ArchonZipMapping.IsSafePath(new string('a', ZipMapping.MaxPathLength + 1)));
    }

    // ---- the command argument --------------------------------------------------------------------------------------------

    private static string Raw(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void AssertSameDecode(string name, string? argument)
    {
        var original = ZipMapping.Decode(argument);
        var port = ArchonZipMapping.Decode(argument);

        Assert.True((original == null) == (port == null), $"{name}: original {(original == null ? "null" : "rules")}, plugin {(port == null ? "null" : "rules")}");
        if (original == null) { return; }

        Assert.Equal(original.Count, port!.Count);
        for (var i = 0; i < original.Count; i++)
        {
            Assert.True((original[i].IsFolder, original[i].Source, original[i].Role, original[i].SubFolder, original[i].KeepExisting)
                == (port[i].IsFolder, port[i].Source, port[i].Role, port[i].SubFolder, port[i].KeepExisting), $"{name}: rule {i}");
        }
    }

    [Fact]
    public void RulesEncodedByTheOriginalAreDecodedTheSameByThePlugin()
    {
        var random = new Random(99);
        var decoded = 0;
        for (var i = 0; i < 300; i++)
        {
            var rules = Enumerable.Range(0, random.Next(0, 12)).Select(_ => RandomRule(random, Archive)).ToList();
            var encoded = ZipMapping.Encode(rules);
            if (encoded == null) { continue; }

            AssertSameDecode("encoded " + i, encoded);
            if (ArchonZipMapping.Decode(encoded) != null) { decoded++; }
        }

        AssertSameDecode("the example", ZipMapping.Encode(EnglishOnly));
        Assert.True(decoded > 100, "decoded " + decoded);
    }

    [Fact]
    public void ArgumentsThatAreNotRulesAreRefusedByBothAlike()
    {
        AssertSameDecode("null", null);
        AssertSameDecode("empty", "");
        AssertSameDecode("not base64", "not base64 !!");
        AssertSameDecode("bytes", "AAAA");
        AssertSameDecode("one char", "A");
        AssertSameDecode("four fields", Raw("F\ten/\tdata\n"));
        AssertSameDecode("wrong kind", Raw("X\ten/\tdata\t\t0\n"));
        AssertSameDecode("wrong keep", Raw("F\ten/\tdata\t\t2\n"));
        AssertSameDecode("fine", Raw("F\ten/\tdata\t\t0\n"));
        AssertSameDecode("no rules", Raw("\n\n"));
        AssertSameDecode("six fields", Raw("F\ten/\tdata\t\t0\textra\n"));
        AssertSameDecode("no trailing newline", Raw("F\ten/\tdata\t\t0"));
        AssertSameDecode("blank lines between", Raw("F\ten/\tdata\t\t0\n\n\nE\ta.cs\tplugins\t\t1\n"));
        AssertSameDecode("bad utf8", Convert.ToBase64String(new byte[] { 0xFF, 0xFE, 0x0A }).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        AssertSameDecode("standard base64 alphabet", "+/+/");
        AssertSameDecode("padded", Convert.ToBase64String(Encoding.UTF8.GetBytes("F\ten/\tdata\t\t0\n")));
        AssertSameDecode("sixty rules", Raw(string.Concat(Enumerable.Range(0, 60).Select(i => $"F\tf{i}/\tdata\t\t0\n"))));
        AssertSameDecode("sixty one rules", Raw(string.Concat(Enumerable.Range(0, 61).Select(i => $"F\tf{i}/\tdata\t\t0\n"))));
        AssertSameDecode("longest allowed", new string('A', ZipMapping.MaxEncodedLength));
        AssertSameDecode("one too long", new string('A', ZipMapping.MaxEncodedLength + 1));
        AssertSameDecode("whitespace inside", "Rlx\ten/");

        var random = new Random(5);
        const string alphabet = "ABCabc0123-_=+/ !\t\n";
        for (var i = 0; i < 300; i++)
        {
            var junk = new string(Enumerable.Range(0, random.Next(1, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            AssertSameDecode("junk " + i, junk);
        }
    }

    [Fact]
    public void ThePluginsLimitsAreTheOriginalsLimits()
    {
        Assert.Equal(ZipMapping.MaxRules, ArchonZipMapping.MaxRules);
        Assert.Equal(ZipMapping.MaxPathLength, ArchonZipMapping.MaxPathLength);
        Assert.Equal(ZipMapping.MaxEncodedLength, ArchonZipMapping.MaxEncodedLength);
        Assert.Equal(ZipRoles.Plugins, ArchonZipMapping.RolePlugins);
        Assert.Equal(ZipRoles.Config, ArchonZipMapping.RoleConfig);
        Assert.Equal(ZipRoles.Data, ArchonZipMapping.RoleData);
        Assert.Equal(ZipRoles.Lang, ArchonZipMapping.RoleLang);
        Assert.Equal(ZipRoles.Skip, ArchonZipMapping.RoleSkip);
        Assert.Equal(ZipMappingProblemCodes.BadRule, ArchonZipMapping.CodeBadRule);
        Assert.Equal(ZipMappingProblemCodes.Unassigned, ArchonZipMapping.CodeUnassigned);
        Assert.Equal(ZipMappingProblemCodes.UnsafePath, ArchonZipMapping.CodeUnsafePath);
        Assert.Equal(ZipMappingProblemCodes.Executable, ArchonZipMapping.CodeExecutable);
        Assert.Equal(ZipMappingProblemCodes.Collision, ArchonZipMapping.CodeCollision);
        Assert.Equal(ZipMappingProblemCodes.CodeOutsidePlugins, ArchonZipMapping.CodeOutsidePlugins);
        // The plugin enforces its own hard limits, and they are the same figures the Api enforces.
        Assert.Equal(ZipMapping.MaxArchiveBytes, ArchonThirdParty.MaxFileBytes);
        Assert.Equal(ZipMapping.MaxInstallBytes, ArchonThirdParty.MaxInstallBytes);
    }
}
