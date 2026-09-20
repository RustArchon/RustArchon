// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// The plugin's <c>archon.events.drain</c> reply. What matters: a reply the Worker does not fully understand is never
/// stored (so nothing wrong reaches the database), the cursor arithmetic the drain loop depends on is right, and the
/// events are passed on as the raw JSON the plugin sent.
/// </summary>
public class ArchonEventsParserTests
{
    private static string Reply(
        string events = "[]", long bootId = 42, long head = 0, long cursor = 0, string lost = "false", string reset = "false",
        string format = "1", string envelope = "1", string ok = "true") =>
        "{\"v\":" + envelope + ",\"ok\":" + ok + ",\"data\":{\"format\":" + format + ",\"bootId\":" + bootId + ",\"head\":" + head
        + ",\"cursor\":" + cursor + ",\"lost\":" + lost + ",\"reset\":" + reset + ",\"events\":" + events + "}}";

    private static ArchonEventsDrain Parse(string reply)
    {
        Assert.True(ArchonEventsParser.TryParse(reply, out var drain, out var failure), failure);
        return drain!;
    }

    private static string Failure(string? reply)
    {
        Assert.False(ArchonEventsParser.TryParse(reply, out var drain, out var failure));
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
    public void EventsAreCountedWithTheirFirstAndLastSequence()
    {
        var events = """[{"s":5,"k":"hit"},{"s":6,"k":"hit"},{"s":9,"k":"death"}]""";

        var drain = Parse(Reply(events, head: 9, cursor: 9));

        Assert.Equal(3, drain.Count);
        Assert.Equal(5, drain.FirstSequence);
        Assert.Equal(9, drain.LastSequence);
    }

    [Fact]
    public void TheEventsArePassedOnAsTheExactRawJsonNotReSerialized()
    {
        var events = """[{"s":1,"k":"hit","an":"Ünï \"q\"","d":12.5}]""";

        var drain = Parse(Reply(events, head: 1, cursor: 1));

        Assert.Equal(events, drain.EventsJson);
    }

    [Fact]
    public void LostAndResetFlagsAreCarried()
    {
        var drain = Parse(Reply(lost: "true", reset: "true"));

        Assert.True(drain.Lost);
        Assert.True(drain.Reset);
    }

    [Fact]
    public void ExtraFieldsFromANewerPluginAreIgnored()
    {
        var reply = """{"v":1,"ok":true,"newThing":1,"data":{"format":1,"bootId":1,"head":0,"cursor":0,"lost":false,"reset":false,"events":[],"future":{"x":1}}}""";

        Assert.Equal(0, Parse(reply).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown command: archon.events.drain")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    public void AnythingThatIsNotAnEnvelopeObjectFails(string? reply)
    {
        Assert.NotEmpty(Failure(reply));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("\"1\"")]
    public void AnUnsupportedEnvelopeVersionFails(string envelope)
    {
        Assert.Contains("envelope", Failure(Reply(envelope: envelope)));
    }

    [Fact]
    public void AnErrorReplyFailsWithTheReason()
    {
        var reply = """{"v":1,"ok":false,"err":"usage","message":"archon.events.drain <bootId> <cursor> [max]"}""";

        Assert.Contains("usage", Failure(reply));
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
    [InlineData("2")]
    [InlineData("0")]
    public void AnUnknownEventFormatIsNotStoredBecauseItCouldNotBeReadBackLater(string format)
    {
        Assert.Contains("format", Failure(Reply(format: format)));
    }

    [Theory]
    [InlineData(0, 0, 0)]    // bootId must be positive
    [InlineData(-5, 0, 0)]
    [InlineData(1, -1, 0)]   // head and cursor cannot be negative
    [InlineData(1, 0, -1)]
    public void ImpossibleBootHeadOrCursorValuesFail(long bootId, long head, long cursor)
    {
        Assert.NotEmpty(Failure(Reply(bootId: bootId, head: head, cursor: cursor)));
    }

    [Theory]
    [InlineData("\"false\"")]
    [InlineData("0")]
    [InlineData("null")]
    public void FlagsMustBeRealBooleans(string flag)
    {
        Assert.NotEmpty(Failure(Reply(lost: flag)));
        Assert.NotEmpty(Failure(Reply(reset: flag)));
    }

    [Fact]
    public void AnEventWithoutASequenceNumberFailsTheWholeBatch()
    {
        Assert.Contains("sequence", Failure(Reply("""[{"s":1},{"k":"hit"}]""", head: 2, cursor: 2)));
    }

    [Theory]
    [InlineData("""[{"s":0}]""")]
    [InlineData("""[{"s":-1}]""")]
    [InlineData("""[{"s":"1"}]""")]
    [InlineData("""[1]""")]
    [InlineData("""[null]""")]
    public void AnEventWithAnInvalidSequenceFailsTheWholeBatch(string events)
    {
        Assert.NotEmpty(Failure(Reply(events, head: 1, cursor: 1)));
    }

    [Fact]
    public void EventsOutOfOrderOrRepeatedFailBecauseTheCursorCouldNotBeTrusted()
    {
        Assert.Contains("order", Failure(Reply("""[{"s":3},{"s":2}]""", head: 3, cursor: 3)));
        Assert.Contains("order", Failure(Reply("""[{"s":2},{"s":2}]""", head: 2, cursor: 2)));
    }

    [Fact]
    public void ACursorBehindTheLastEventFailsSoTheSameEventsAreNeverAskedForAgainAndStoredTwice()
    {
        Assert.Contains("cursor", Failure(Reply("""[{"s":1},{"s":2}]""", head: 2, cursor: 1)));
    }

    [Fact]
    public void EventsMissingOrNotAnArrayFail()
    {
        var missing = """{"v":1,"ok":true,"data":{"format":1,"bootId":1,"head":0,"cursor":0,"lost":false,"reset":false}}""";
        var notArray = """{"v":1,"ok":true,"data":{"format":1,"bootId":1,"head":0,"cursor":0,"lost":false,"reset":false,"events":{}}}""";

        Assert.Contains("events", Failure(missing));
        Assert.Contains("events", Failure(notArray));
    }

    [Fact]
    public void NumbersTooBigForALongFailInsteadOfThrowing()
    {
        var reply = """{"v":1,"ok":true,"data":{"format":1,"bootId":99999999999999999999,"head":0,"cursor":0,"lost":false,"reset":false,"events":[]}}""";

        Assert.NotEmpty(Failure(reply));
    }

    [Fact]
    public void MissingDataFails()
    {
        Assert.Contains("data", Failure("""{"v":1,"ok":true}"""));
        Assert.Contains("data", Failure("""{"v":1,"ok":true,"data":[]}"""));
    }
}
