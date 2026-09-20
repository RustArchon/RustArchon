// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>The plugin's <c>archon.tcs</c> reply: a page it does not fully understand is dropped whole.</summary>
public class ArchonTcsParserTests
{
    private static string Reply(
        string tcs = "[]", string ready = "true", int total = 0, int offset = 0, int next = 0, string format = "1",
        string envelope = "1", string ok = "true") =>
        "{\"v\":" + envelope + ",\"ok\":" + ok + ",\"data\":{\"format\":" + format + ",\"ready\":" + ready + ",\"total\":" + total
        + ",\"offset\":" + offset + ",\"next\":" + next + ",\"tcs\":" + tcs + "}}";

    private static ArchonTcsPage Parse(string reply)
    {
        Assert.True(ArchonTcsParser.TryParse(reply, out var page, out var failure), failure);
        return page!;
    }

    private static string Failure(string? reply)
    {
        Assert.False(ArchonTcsParser.TryParse(reply, out var page, out var failure));
        Assert.Null(page);
        return failure!;
    }

    [Fact]
    public void AnEmptyListIsValid()
    {
        var page = Parse(Reply());

        Assert.Empty(page.TcsRaw);
        Assert.True(page.Ready);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public void EachCupboardIsKeptAsItsOwnExactRawJson()
    {
        var a = "{\"i\":1,\"x\":1.5,\"y\":2.0,\"z\":3.0,\"o\":\"76561198000000001\",\"a\":[{\"i\":\"76561198000000001\",\"n\":\"Ünï \\\"q\\\"\"}]}";
        var b = "{\"i\":2,\"x\":4.0,\"y\":5.0,\"z\":6.0,\"o\":\"76561198000000002\",\"a\":[]}";

        var page = Parse(Reply("[" + a + "," + b + "]", total: 2, next: 2));

        Assert.Equal([a, b], page.TcsRaw.ToArray());
        Assert.Equal(2, page.Next);
    }

    [Fact]
    public void ReadyFalseIsCarriedSoAPartialListIsNeverPassedOffAsComplete()
    {
        Assert.False(Parse(Reply(ready: "false")).Ready);
    }

    [Fact]
    public void ExtraFieldsFromANewerPluginAreIgnored()
    {
        var reply = "{\"v\":1,\"ok\":true,\"x\":1,\"data\":{\"format\":1,\"ready\":true,\"total\":0,\"offset\":0,\"next\":0,\"tcs\":[],\"future\":{}}}";

        Assert.Empty(Parse(reply).TcsRaw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Unknown command: archon.tcs")]
    [InlineData("[]")]
    [InlineData("null")]
    public void AnythingThatIsNotAnEnvelopeObjectFails(string? reply)
    {
        Assert.NotEmpty(Failure(reply));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("\"1\"")]
    public void AnUnsupportedEnvelopeVersionFails(string envelope)
    {
        Assert.Contains("envelope", Failure(Reply(envelope: envelope)));
    }

    [Fact]
    public void AnErrorReplyFailsWithItsReason()
    {
        Assert.Contains("usage", Failure("{\"v\":1,\"ok\":false,\"err\":\"usage\",\"message\":\"x\"}"));
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void OnlyABooleanTrueOkCounts(string ok)
    {
        Assert.NotEmpty(Failure(Reply(ok: ok)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public void AnUnknownFormatFails(string format)
    {
        Assert.Contains("format", Failure(Reply(format: format)));
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void ReadyMustBeARealBoolean(string ready)
    {
        Assert.NotEmpty(Failure(Reply(ready: ready)));
    }

    [Theory]
    [InlineData(-1, 0, 0)]   // total
    [InlineData(0, -1, 0)]   // offset
    [InlineData(0, 0, -1)]   // next
    [InlineData(5, 3, 2)]    // next behind offset
    public void ImpossibleCountsFail(int total, int offset, int next)
    {
        Assert.NotEmpty(Failure(Reply(total: total, offset: offset, next: next)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    public void TcsThatIsNotAnArrayFails(string tcs)
    {
        Assert.Contains("tcs", Failure(Reply(tcs)));
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[[]]")]
    [InlineData("[\"x\"]")]
    public void ACupboardThatIsNotAnObjectFailsTheWholePage(string tcs)
    {
        Assert.Contains("object", Failure(Reply(tcs)));
    }

    [Fact]
    public void NumbersTooBigForAnIntFailInsteadOfThrowing()
    {
        var reply = "{\"v\":1,\"ok\":true,\"data\":{\"format\":1,\"ready\":true,\"total\":99999999999,\"offset\":0,\"next\":0,\"tcs\":[]}}";

        Assert.NotEmpty(Failure(reply));
    }

    [Fact]
    public void MissingDataFails()
    {
        Assert.Contains("data", Failure("{\"v\":1,\"ok\":true}"));
    }
}
