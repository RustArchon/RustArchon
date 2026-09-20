// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// The plugin's <c>archon.positions.drain</c> reply: the same contract as the combat drain with the items under
/// <c>samples</c>. What matters is the same too: a reply that is not fully understood is never stored, the cursor
/// arithmetic the drain loop relies on is right, and the samples are passed on as the raw JSON the plugin sent.
/// </summary>
public class ArchonPositionsParserTests
{
    private static string Reply(
        string samples = "[]", long bootId = 42, long head = 0, long cursor = 0, string lost = "false", string reset = "false",
        string format = "1", string envelope = "1", string ok = "true", string itemsName = "samples") =>
        "{\"v\":" + envelope + ",\"ok\":" + ok + ",\"data\":{\"format\":" + format + ",\"bootId\":" + bootId + ",\"head\":" + head
        + ",\"cursor\":" + cursor + ",\"lost\":" + lost + ",\"reset\":" + reset + ",\"" + itemsName + "\":" + samples + "}}";

    private static ArchonEventsDrain Parse(string reply)
    {
        Assert.True(ArchonPositionsParser.TryParse(reply, out var drain, out var failure), failure);
        return drain!;
    }

    private static string Failure(string? reply)
    {
        Assert.False(ArchonPositionsParser.TryParse(reply, out var drain, out var failure));
        Assert.Null(drain);
        return failure!;
    }

    [Fact]
    public void AnEmptyDrainIsValidAndKeepsTheCursor()
    {
        var drain = Parse(Reply(head: 7, cursor: 7));

        Assert.Equal(0, drain.Count);
        Assert.Equal(7, drain.Cursor);
        Assert.Equal(7, drain.Head);
        Assert.Equal(42, drain.BootId);
    }

    [Fact]
    public void SamplesAreCountedWithTheirFirstAndLastSequence()
    {
        var samples = """[{"s":5,"t":1,"p":"1","x":1.0,"y":2.0,"z":3.0,"r":90},{"s":6,"t":2,"p":"1","x":2.0,"y":2.0,"z":3.0,"r":90},{"s":9,"t":3,"p":"1","x":3.0,"y":2.0,"z":3.0,"r":0}]""";

        var drain = Parse(Reply(samples, head: 9, cursor: 9));

        Assert.Equal(3, drain.Count);
        Assert.Equal(5, drain.FirstSequence);
        Assert.Equal(9, drain.LastSequence);
    }

    [Fact]
    public void TheSamplesArePassedOnAsTheExactRawJsonNotReSerialized()
    {
        var samples = """[{"s":1,"t":1,"p":"1","x":1.0,"y":2.0,"z":3.0,"r":90,"n":"Ünï \"q\"","e":"on"}]""";

        Assert.Equal(samples, Parse(Reply(samples, head: 1, cursor: 1)).EventsJson);
    }

    [Fact]
    public void AnEventsReplyIsNotAPositionsReplyBecauseTheItemsAreUnderTheWrongName()
    {
        Assert.Contains("samples", Failure(Reply(itemsName: "events")));
    }

    [Fact]
    public void AndAPositionsReplyIsNotAnEventsReply()
    {
        Assert.False(ArchonEventsParser.TryParse(Reply(), out var drain, out var failure));
        Assert.Null(drain);
        Assert.Contains("events", failure);
    }

    [Fact]
    public void LostAndResetAreCarriedThrough()
    {
        var drain = Parse(Reply(lost: "true", reset: "true", head: 3, cursor: 0));

        Assert.True(drain.Lost);
        Assert.True(drain.Reset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Unknown command: archon.positions.drain")]
    [InlineData("[]")]
    [InlineData("null")]
    public void AnythingThatIsNotAnEnvelopeObjectFails(string? reply)
    {
        Assert.NotEmpty(Failure(reply));
    }

    [Fact]
    public void AnErrorReplyFailsWithItsReason()
    {
        Assert.Contains("usage", Failure("{\"v\":1,\"ok\":false,\"err\":\"usage\",\"message\":\"x\"}"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public void AnUnknownFormatFails(string format)
    {
        Assert.Contains("format", Failure(Reply(format: format)));
    }

    [Fact]
    public void SamplesOutOfSequenceFailTheWholeBatch()
    {
        var samples = """[{"s":5},{"s":5}]""";

        Assert.Contains("order", Failure(Reply(samples, head: 5, cursor: 5)));
    }

    [Fact]
    public void ASampleWithNoSequenceFailsTheWholeBatch()
    {
        Assert.Contains("sequence", Failure(Reply("""[{"x":1}]""", head: 1, cursor: 1)));
    }

    [Fact]
    public void ACursorBehindTheLastSampleFails()
    {
        Assert.Contains("cursor", Failure(Reply("""[{"s":5}]""", head: 5, cursor: 4)));
    }

    [Fact]
    public void ASampleThatIsNotAnObjectFails()
    {
        Assert.NotEmpty(Failure(Reply("[1]", head: 1, cursor: 1)));
    }
}
