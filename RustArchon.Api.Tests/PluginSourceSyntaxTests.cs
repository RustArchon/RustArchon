// Copyright ©2026 Scott Blomfield

using System.Linq;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Reading a plugin source as C# 7.3, syntax only. The two real plugin files must read clean (or nothing could ever be uploaded), and the
/// mistakes that really kill a plugin on the game server - a typo, and syntax newer than its compiler - must be found and pointed at.
/// </summary>
public class PluginSourceSyntaxTests
{
    private static readonly EmbeddedPluginScriptSource Embedded = new();

    private static string Wrap(string body) =>
        "using System;\nnamespace Oxide.Plugins\n{\n    public class Sample\n    {\n" + body + "\n    }\n}\n";

    [Fact]
    public void TheMainPluginAsBuiltReadsCleanAsCSharp73()
    {
        var problems = PluginSourceSyntax.Problems(Embedded.ReadSource(), out var total);

        Assert.Empty(problems);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TheUpdaterAsBuiltReadsCleanAsCSharp73()
    {
        var problems = PluginSourceSyntax.Problems(Embedded.ReadUpdaterSource(), out var total);

        Assert.Empty(problems);
        Assert.Equal(0, total);
    }

    [Fact]
    public void AnOrdinarySourceIsClean()
    {
        Assert.Empty(PluginSourceSyntax.Problems(Wrap("        public int Add(int a, int b) { return a + b; }"), out var total));
        Assert.Equal(0, total);
    }

    [Fact]
    public void AMissingBraceIsFoundAndTheLineIsSaid()
    {
        var source = Wrap("        public int Add(int a, int b) { return a + b; \n        public int Two() { return 2; }");

        var problems = PluginSourceSyntax.Problems(source, out var total);

        Assert.NotEmpty(problems);
        Assert.True(total >= 1);
        Assert.All(problems, p => Assert.Matches(@"^line \d+: ", p));
    }

    [Fact]
    public void ALineNumberIsTheOneInTheFile()
    {
        // Wrap puts five lines above the body, so the bad statement (the third body line) is on line 8 of this source.
        var source = Wrap("        public int A() { return 1; }\n        public int B() { return 2; }\n        public int C() { int x = ; return x; }");

        var problems = PluginSourceSyntax.Problems(source, out _);

        Assert.Contains(problems, p => p.StartsWith("line 8:"));
    }

    [Theory]
    [InlineData("        public string Name(int x) { return x switch { 1 => \"a\", _ => \"b\" }; }")]           // switch expression, C# 8
    [InlineData("        public void A() { using var s = new System.IO.MemoryStream(); }")]                    // using declaration, C# 8
    [InlineData("        public void A(string a) { a ??= \"x\"; }")]                                            // null-coalescing assignment, C# 8
    [InlineData("        public string A(string? a) { return a; }")]                                            // nullable annotation, C# 8
    [InlineData("        public int[] A(int[] a) { return a[1..]; }")]                                          // range, C# 8
    [InlineData("        public bool A(object a) { return a is not null; }")]                                   // pattern not, C# 9
    public void SyntaxNewerThanTheGameServersCompilerIsRefused(string member)
    {
        var problems = PluginSourceSyntax.Problems(Wrap(member), out var total);

        Assert.NotEmpty(problems);
        Assert.True(total >= 1);
    }

    [Theory]
    [InlineData("        public int A(int x) { var (a, b) = (x, x); return a + b; }")]                          // tuples, C# 7
    [InlineData("        public string A(object o) { if (o is string s) { return s; } return null; }")]        // pattern matching, C# 7
    [InlineData("        public int A() { int Local(int x) { return x; } return Local(1); }")]                  // local function, C# 7
    [InlineData("        public string A(string s) { return $\"{s}!\"; }")]                                     // interpolation, C# 6
    [InlineData("        public string A(string s) { return s?.Trim(); }")]                                     // null-conditional, C# 6
    [InlineData("        public int A(out int x) { x = 1; return x; }\n        public void B() { A(out var y); }")] // out var, C# 7
    public void SyntaxTheGameServersCompilerAcceptsIsClean(string member)
    {
        var problems = PluginSourceSyntax.Problems(Wrap(member), out var total);

        Assert.Empty(problems);
        Assert.Equal(0, total);
    }

    [Fact]
    public void OnlyTheFirstFewAreListedButAllAreCounted()
    {
        var many = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"        public int M{i}() {{ int x = ; return x; }}"));

        var problems = PluginSourceSyntax.Problems(Wrap(many), out var total);

        Assert.Equal(PluginSourceSyntax.MaxListed, problems.Count);
        Assert.True(total >= 12);
    }

    [Fact]
    public void ACompileErrorThatNeedsTheGamesLibrariesIsNotSomethingItCanSee()
    {
        // A call to a member that does not exist is a meaning problem, not a reading one; that is what the plugin's own real-game tests and
        // the Updater's rollback are for. This pins that the check is not pretending otherwise.
        var problems = PluginSourceSyntax.Problems(Wrap("        public void A() { BasePlayer.ThatDoesNotExist(); }"), out var total);

        Assert.Empty(problems);
        Assert.Equal(0, total);
    }
}
