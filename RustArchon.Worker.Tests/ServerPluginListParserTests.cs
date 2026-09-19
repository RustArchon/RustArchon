// Copyright ©2026 Scott Blomfield

using System.Linq;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// Covers <see cref="ServerPluginListParser"/> - the seam between <c>RustArchon.Rcon</c>'s Oxide/Carbon
/// plugin-list parsers and what the Worker publishes. The two cases the actor's probe order depends on
/// most are "a reply that isn't a plugin list parses to empty, not an exception" (how an unknown
/// command on the other framework looks) and "a real list parses to the right name/author/version".
/// </summary>
public class ServerPluginListParserTests
{
    // Column widths for a Carbon `c.plugins` table. Carbon pads every cell to its column, so a data row
    // is exactly as long as the header - RustArchon.Rcon's parser relies on that to accept a row.
    private static readonly int[] CarbonWidths = [3, 20, 22, 9, 11, 12, 13, 10, 17, 14, 12];

    private static string CarbonRow(params string[] cells) =>
        string.Concat(cells.Zip(CarbonWidths, (cell, width) => cell.PadRight(width)));

    private static readonly string CarbonHeader = CarbonRow(
        "#", "package", "author", "version", "hook time", "hook fires", "hook memory",
        "hook lag", "hook exceptions", "compile time", "uptime");

    private static string CarbonPlugin(string name, string author, string version) => CarbonRow(
        string.Empty, name, author, version, "0ms", "3", "1.2mb", string.Empty, string.Empty, "254ms [80ms]", "14h20m23s");

    [Fact]
    public void Oxide_ParsesNameAuthorAndVersion()
    {
        const string response =
            "Listing 3 plugins:\n" +
            "  01 \"Better Chat\" (5.2.14) by LaserHydra (0.03s) - BetterChat.cs\n" +
            "  02 \"Kits\" (4.0.0) by Gachl/Steenamaroo (0.10s) - Kits.cs\n" +
            "  04 \"Stack Size Controller\" (4.1.1) by AnExiledDev (51.74s) - StackSizeController.cs";

        var plugins = ServerPluginListParser.Parse(ServerModFramework.Oxide, response);

        Assert.Equal(
            [
                new ServerPluginInfo("Better Chat", "LaserHydra", "5.2.14"),
                new ServerPluginInfo("Kits", "Gachl/Steenamaroo", "4.0.0"),
                new ServerPluginInfo("Stack Size Controller", "AnExiledDev", "4.1.1")
            ],
            plugins);
    }

    [Fact]
    public void Oxide_ParsesWithWindowsLineEndings()
    {
        const string response =
            "Listing 1 plugins:\r\n" +
            "  01 \"Kits\" (4.0.0) by Gachl (0.10s) - Kits.cs\r\n";

        var plugins = ServerPluginListParser.Parse(ServerModFramework.Oxide, response);

        Assert.Equal([new ServerPluginInfo("Kits", "Gachl", "4.0.0")], plugins);
    }

    [Fact]
    public void Oxide_UnknownCommandReply_IsEmptyNotAnError()
    {
        Assert.Empty(ServerPluginListParser.Parse(ServerModFramework.Oxide, "Unknown command: o.plugins"));
    }

    [Fact]
    public void Carbon_ParsesNameAuthorAndVersion()
    {
        var response = string.Join('\n',
            CarbonHeader,
            CarbonPlugin("Better Chat", "LaserHydra", "v5.2.14"),
            CarbonPlugin("Kits", "Gachl/Steenamaroo", "v4.0.0"));

        var plugins = ServerPluginListParser.Parse(ServerModFramework.Carbon, response);

        Assert.Equal(
            [
                new ServerPluginInfo("Better Chat", "LaserHydra", "v5.2.14"),
                new ServerPluginInfo("Kits", "Gachl/Steenamaroo", "v4.0.0")
            ],
            plugins);
    }

    [Fact]
    public void Carbon_TrailingNewline_DoesNotThrow()
    {
        // Regression: an empty line has no first character, and the parser used to index line[0] on it.
        var response = string.Join('\n', CarbonHeader, CarbonPlugin("Kits", "Gachl", "v4.0.0")) + "\n";

        var plugins = ServerPluginListParser.Parse(ServerModFramework.Carbon, response);

        Assert.Equal([new ServerPluginInfo("Kits", "Gachl", "v4.0.0")], plugins);
    }

    [Fact]
    public void Carbon_HeaderOnly_IsEmpty()
    {
        Assert.Empty(ServerPluginListParser.Parse(ServerModFramework.Carbon, CarbonHeader));
    }

    [Fact]
    public void Carbon_UnknownCommandReply_IsEmptyNotAnError()
    {
        Assert.Empty(ServerPluginListParser.Parse(ServerModFramework.Carbon, "Unknown command: c.plugins"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankResponse_IsEmpty(string? response)
    {
        Assert.Empty(ServerPluginListParser.Parse(ServerModFramework.Oxide, response));
        Assert.Empty(ServerPluginListParser.Parse(ServerModFramework.Carbon, response));
    }

    [Fact]
    public void NoFramework_IsAlwaysEmpty()
    {
        Assert.Empty(ServerPluginListParser.Parse(
            ServerModFramework.None, "  01 \"Kits\" (4.0.0) by Gachl (0.10s) - Kits.cs"));
    }
}
