// Copyright ©2026 Scott Blomfield

using System.IO;
using System.Linq;
using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// The "failed plugins" section of Carbon's <c>c.plugins</c> reply, read from a reply captured on a live Carbon server (after a Rust update broke
/// four plugins): which files did not load, where, and why - with the messages Carbon wrapped onto further lines put back together.
/// </summary>
public class CarbonFailedPluginParserTests
{
    private static string Real() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "carbon-plugins-with-failures.txt"));

    [Fact]
    public void ARealRepliesFailedSectionYieldsEveryReasonForEveryFile()
    {
        Assert.True(CarbonFailedPluginParser.TryParse(Real(), out var failures));

        Assert.Equal(6, failures.Count);
        Assert.Equal(["BotReSpawn.cs", "BotReSpawn.cs", "BotReSpawn.cs", "CopyPaste.cs", "MapNoteTeleport.cs", "ScrapHeliStorage.cs"], failures.Select(f => f.File).ToArray());
        Assert.Equal(4, failures.Select(f => f.File).Distinct().Count());     // the header says "failed plugins (4)": four files
    }

    [Fact]
    public void ThePlaceInTheFileIsTheLineAndColumnCarbonPrinted()
    {
        CarbonFailedPluginParser.TryParse(Real(), out var failures);

        Assert.Equal((1436, 59), (failures[0].Line, failures[0].Column));
        Assert.Equal((3673, 215), (failures[2].Line, failures[2].Column));       // a three digit column
        Assert.Equal((101, 9), (failures[5].Line, failures[5].Column));
    }

    [Fact]
    public void AShortMessageIsTakenAsItIs()
    {
        CarbonFailedPluginParser.TryParse(Real(), out var failures);

        Assert.Equal("The name 'RustNavMesh' does not exist in the current context", failures[1].Message);
        Assert.Equal("No overload for method 'SendNetworkUpdateImmediate' takes 1 arguments", failures[5].Message);
    }

    [Fact]
    public void AMessageCutAtTheColumnEdgeWithDotsIsJoinedBackWithTheDotsDropped()
    {
        CarbonFailedPluginParser.TryParse(Real(), out var failures);

        // "could be found" was cut mid word as "coul..." and carried on as "d be found ..." on the next line.
        var copyPaste = failures.Single(f => f.File == "CopyPaste.cs").Message;
        Assert.Equal(
            "'Sprinkler' does not contain a definition for 'TurnOn' and no accessible extension method 'TurnOn' accepting a first argument of type 'Sprinkler' could be found (are you missing a using directive or an assembly reference?)",
            copyPaste);
        Assert.DoesNotContain("...", copyPaste);
    }

    [Fact]
    public void AMessageCutAtASpaceIsJoinedWithoutAnExtraOrMissingSpace()
    {
        CarbonFailedPluginParser.TryParse(Real(), out var failures);

        var mapNote = failures.Single(f => f.File == "MapNoteTeleport.cs").Message;
        Assert.Equal(
            "'BasePlayer' does not contain a definition for 'flyhackPauseTime' and no accessible extension method 'flyhackPauseTime' accepting a first argument of type 'BasePlayer' could be found (are you missing a using directive or an assembly reference?)",
            mapNote);
    }

    [Fact]
    public void TheSameReplyWithWindowsLineEndingsReadsTheSame()
    {
        CarbonFailedPluginParser.TryParse(Real(), out var lf);

        Assert.True(CarbonFailedPluginParser.TryParse(Real().Replace("\n", "\r\n"), out var crlf));

        Assert.Equal(lf, crlf);
    }

    [Fact]
    public void NoFailedPluginsIsAnEmptyListNotAnUnknown()
    {
        var reply = "#  package  author  version\n1  Scripts (1)\n   Better Chat  LaserHydra  v5.2.15\n*  unloaded plugins (0)\n*  failed plugins (0)\n";

        Assert.True(CarbonFailedPluginParser.TryParse(reply, out var failures));

        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown command: c.plugins")]
    [InlineData("[]")]
    [InlineData("#  package  author  version\n1  Scripts (1)\n   Better Chat  LaserHydra  v5.2.15\n")]      // a Carbon list with no failed section: not told
    public void AReplyWithNoFailedSectionIsNotTold(string? reply)
    {
        Assert.False(CarbonFailedPluginParser.TryParse(reply, out var failures));

        Assert.Empty(failures);
    }

    [Fact]
    public void ALineThatIsNeitherARowNorAContinuationIsIgnored()
    {
        var reply = "*  failed plugins (1)   line      stacktrace\n   Odd.cs               10:5      A real problem\nsomething unexpected\n   Another.cs           3:1       Another problem\n";

        CarbonFailedPluginParser.TryParse(reply, out var failures);

        Assert.Equal(["A real problem", "Another problem"], failures.Select(f => f.Message).ToArray());
    }

    [Fact]
    public void AnythingAfterTheFailedSectionsNextSectionIsNotRead()
    {
        var reply = "*  failed plugins (1)   line      stacktrace\n   Odd.cs               10:5      A real problem\n*  something else\n   Later.cs             1:1       Not a failure\n";

        CarbonFailedPluginParser.TryParse(reply, out var failures);

        Assert.Equal(["Odd.cs"], failures.Select(f => f.File).ToArray());
    }

    [Fact]
    public void AMessageThatItselfContainsSomethingLikeAPositionIsStillOneMessage()
    {
        var reply = "*  failed plugins (1)   line      stacktrace\n   Odd.cs               10:5      Cannot convert at 12:34 to int, and cut he...\n                                  re it goes on at 56:78 too\n";

        CarbonFailedPluginParser.TryParse(reply, out var failures);

        var only = Assert.Single(failures);
        Assert.Equal("Cannot convert at 12:34 to int, and cut here it goes on at 56:78 too", only.Message);
    }

    [Fact]
    public void AFileNameWithSpacesIsKeptWhole()
    {
        var reply = "*  failed plugins (1)   line      stacktrace\n   Chinook Drop Randomizer.cs 10:5   Missing something\n";

        CarbonFailedPluginParser.TryParse(reply, out var failures);

        Assert.Equal("Chinook Drop Randomizer.cs", Assert.Single(failures).File);
    }

    [Fact]
    public void ARunawayReplyIsBoundedInCountAndInMessageLength()
    {
        var many = string.Concat(Enumerable.Range(0, 500).Select(i => $"   File{i}.cs              1:1       problem {i}\n"));
        var longMessage = "   Long.cs              1:1       " + new string('x', 5000) + "\n";

        CarbonFailedPluginParser.TryParse("*  failed plugins (500)   line      stacktrace\n" + many, out var counted);
        CarbonFailedPluginParser.TryParse("*  failed plugins (1)   line      stacktrace\n" + longMessage, out var limited);

        Assert.Equal(CarbonFailedPluginParser.MaxFailures, counted.Count);
        Assert.Equal(CarbonFailedPluginParser.MaxMessageLength, Assert.Single(limited).Message.Length);
        Assert.EndsWith("...", limited[0].Message);
    }
}
