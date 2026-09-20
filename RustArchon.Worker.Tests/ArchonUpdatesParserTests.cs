// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

public class ArchonUpdatesParserTests
{
    private static string Notice(string n = "Backpacks", string c = "3.1.0", string l = "3.2.4", string u = "https://umod.org/plugins/backpacks", string m = "uMod", string f = "1800000000000", string s = "1800003600000", string t = "2") =>
        "{\"n\":" + Json(n) + ",\"c\":" + Json(c) + ",\"l\":" + Json(l) + ",\"u\":" + Json(u) + ",\"m\":" + Json(m) + ",\"f\":" + f + ",\"s\":" + s + ",\"t\":" + t + "}";

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string Reply(string updates, string envelope = "1", string ok = "true", string format = "1") =>
        "{\"v\":" + envelope + ",\"ok\":" + ok + ",\"data\":{\"format\":" + format + ",\"count\":0,\"updates\":[" + updates + "]}}";

    [Fact]
    public void ANoticeIsReadWithEveryFieldItCarries()
    {
        Assert.True(ArchonUpdatesParser.TryParse(Reply(Notice()), out var updates, out var failure), failure);

        var n = Assert.Single(updates!);
        Assert.Equal("Backpacks", n.Name);
        Assert.Equal("3.1.0", n.CurrentVersion);
        Assert.Equal("3.2.4", n.LatestVersion);
        Assert.Equal("https://umod.org/plugins/backpacks", n.Url);
        Assert.Equal("uMod", n.Marketplace);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1800000000000), n.FirstSeenUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1800003600000), n.LastSeenUtc);
        Assert.Equal(2, n.TimesSeen);
    }

    [Fact]
    public void ManyNoticesKeepTheirOrder()
    {
        Assert.True(ArchonUpdatesParser.TryParse(Reply(Notice("A") + "," + Notice("B") + "," + Notice("C")), out var updates, out _));

        Assert.Equal(["A", "B", "C"], updates!.Select(n => n.Name).ToArray());
    }

    [Fact]
    public void AnEmptyTableIsValidAndEmpty()
    {
        Assert.True(ArchonUpdatesParser.TryParse(Reply(""), out var updates, out _));

        Assert.Empty(updates!);
    }

    [Fact]
    public void EmptyFieldsOtherThanTheNameAreAccepted()
    {
        Assert.True(ArchonUpdatesParser.TryParse(Reply(Notice(c: "", l: "", u: "", m: "")), out var updates, out _));

        Assert.Equal("", Assert.Single(updates!).Url);
    }

    [Fact]
    public void ExtraFieldsFromANewerPluginAreIgnored()
    {
        var reply = Reply(Notice().TrimEnd('}') + ",\"future\":{\"x\":1}}").Replace("\"updates\"", "\"newThing\":1,\"updates\"");

        Assert.True(ArchonUpdatesParser.TryParse(reply, out var updates, out var failure), failure);
        Assert.Single(updates!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown command: archon.updates")]
    [InlineData("[]")]
    [InlineData("42")]
    public void ThingsThatAreNotAnEnvelopeAreRefused(string? message)
    {
        Assert.False(ArchonUpdatesParser.TryParse(message, out var updates, out var failure));
        Assert.Null(updates);
        Assert.NotNull(failure);
    }

    [Fact]
    public void AnErrorReplyIsRefusedWithItsReason()
    {
        Assert.False(ArchonUpdatesParser.TryParse("{\"v\":1,\"ok\":false,\"err\":\"boom\",\"message\":\"x\"}", out _, out var failure));

        Assert.Contains("boom", failure);
    }

    [Theory]
    [InlineData("2", "true", "1")]      // an envelope version we do not know
    [InlineData("1", "true", "2")]      // a notice format we do not know
    [InlineData("1", "false", "1")]
    public void AnUnsupportedVersionOrFormatIsRefused(string envelope, string ok, string format)
    {
        Assert.False(ArchonUpdatesParser.TryParse(Reply(Notice(), envelope, ok, format), out _, out _));
    }

    [Fact]
    public void AMissingUpdatesArrayIsRefused()
    {
        Assert.False(ArchonUpdatesParser.TryParse("{\"v\":1,\"ok\":true,\"data\":{\"format\":1,\"count\":0}}", out _, out var failure));

        Assert.Contains("updates", failure);
    }

    [Theory]
    [InlineData("{\"n\":\"\",\"c\":\"1\",\"l\":\"2\",\"u\":\"\",\"m\":\"\",\"f\":1,\"s\":1,\"t\":1}")]                    // no name
    [InlineData("{\"c\":\"1\",\"l\":\"2\",\"u\":\"\",\"m\":\"\",\"f\":1,\"s\":1,\"t\":1}")]                              // name missing
    [InlineData("{\"n\":\"A\",\"c\":1,\"l\":\"2\",\"u\":\"\",\"m\":\"\",\"f\":1,\"s\":1,\"t\":1}")]                      // version is a number
    [InlineData("{\"n\":\"A\",\"c\":\"1\",\"l\":\"2\",\"u\":\"\",\"m\":\"\",\"f\":\"1\",\"s\":1,\"t\":1}")]              // time is text
    [InlineData("{\"n\":\"A\",\"c\":\"1\",\"l\":\"2\",\"u\":\"\",\"m\":\"\",\"f\":-1,\"s\":1,\"t\":1}")]                 // time before 1970
    [InlineData("{\"n\":\"A\",\"c\":\"1\",\"l\":\"2\",\"u\":\"\",\"m\":\"\",\"f\":1,\"s\":99999999999999999,\"t\":1}")]  // time out of range
    [InlineData("{\"n\":\"A\",\"c\":\"1\",\"l\":\"2\",\"u\":\"\",\"m\":\"\",\"f\":1,\"s\":1}")]                          // count missing
    [InlineData("\"just a string\"")]
    [InlineData("7")]
    public void OneBadNoticeFailsTheWholeReplySoNothingHalfTrueIsStored(string bad)
    {
        Assert.False(ArchonUpdatesParser.TryParse(Reply(Notice() + "," + bad), out var updates, out _));
        Assert.Null(updates);
    }

    [Fact]
    public void MoreThanTheLimitOfNoticesIsRefused()
    {
        var many = string.Join(",", Enumerable.Range(0, ArchonUpdatesParser.MaxNotices + 1).Select(i => Notice("p" + i)));

        Assert.False(ArchonUpdatesParser.TryParse(Reply(many), out _, out var failure));
        Assert.Contains("too many", failure);
    }

    [Fact]
    public void ExactlyTheLimitIsAccepted()
    {
        var many = string.Join(",", Enumerable.Range(0, ArchonUpdatesParser.MaxNotices).Select(i => Notice("p" + i)));

        Assert.True(ArchonUpdatesParser.TryParse(Reply(many), out var updates, out _));
        Assert.Equal(ArchonUpdatesParser.MaxNotices, updates!.Count);
    }

    [Fact]
    public void ATimesSeenBeyondAnIntIsClamped()
    {
        Assert.True(ArchonUpdatesParser.TryParse(Reply(Notice(t: "99999999999")), out var updates, out _));

        Assert.Equal(int.MaxValue, Assert.Single(updates!).TimesSeen);
    }

    [Fact]
    public void QuotesAndUnicodeInTheTextSurvive()
    {
        Assert.True(ArchonUpdatesParser.TryParse(Reply(Notice(n: "Bad\"Name\\", u: "https://a/?q=\"x\"&é=1")), out var updates, out _));

        var n = Assert.Single(updates!);
        Assert.Equal("Bad\"Name\\", n.Name);
        Assert.Equal("https://a/?q=\"x\"&é=1", n.Url);
    }

    // ---- the empty-poll filter must know an empty table, and only that ------------------------------------

    [Fact]
    public void AnEmptyUpdatesTableIsAnEmptyPollReplyButOneWithNoticesIsNot()
    {
        Assert.True(EmptyPollReply.IsEmpty(Reply("")));
        Assert.False(EmptyPollReply.IsEmpty(Reply(Notice())));
    }

    [Fact]
    public void AnUpdatesReplyThatIsNotUnderstoodIsNotDroppedAsEmpty()
    {
        Assert.False(EmptyPollReply.IsEmpty(Reply("", format: "9")));
        Assert.False(EmptyPollReply.IsEmpty("{\"v\":1,\"ok\":false,\"err\":\"x\",\"updates\":[]}"));
    }
}
