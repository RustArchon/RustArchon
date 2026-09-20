// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

public class EmptyPollReplyTests
{
    private static string Reply(string items = "events", string body = "[]", string lost = "false", string reset = "false", string ok = "true") =>
        "{\"v\":1,\"ok\":" + ok + ",\"data\":{\"format\":1,\"bootId\":7,\"head\":3,\"cursor\":3,\"lost\":" + lost + ",\"reset\":" + reset + ",\"" + items + "\":" + body + "}}";

    [Fact]
    public void AnEventsDrainWithNothingNewIsEmpty() => Assert.True(EmptyPollReply.IsEmpty(Reply()));

    [Fact]
    public void APositionsDrainWithNothingNewIsEmpty() => Assert.True(EmptyPollReply.IsEmpty(Reply(items: "samples")));

    [Fact]
    public void AReplyCarryingItemsIsNotEmpty()
    {
        Assert.False(EmptyPollReply.IsEmpty(Reply(body: "[{\"s\":3}]")));
        Assert.False(EmptyPollReply.IsEmpty(Reply(items: "samples", body: "[{\"s\":3}]")));
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    [InlineData("true", "true")]
    public void AReplyThatSaysEventsWereLostOrTheBufferWasResetIsNotEmptyEvenWithNoItems(string lost, string reset) =>
        Assert.False(EmptyPollReply.IsEmpty(Reply(lost: lost, reset: reset)));

    [Fact]
    public void AnErrorReplyIsNotEmpty() =>
        Assert.False(EmptyPollReply.IsEmpty("{\"v\":1,\"ok\":false,\"err\":\"usage\",\"bootId\":1}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("[]")]
    [InlineData(" [] ")]
    [InlineData("[ ]")]
    [InlineData("[\n]")]
    public void NothingAtAllOrAnEmptyListIsEmpty(string? message) => Assert.True(EmptyPollReply.IsEmpty(message));

    [Theory]
    [InlineData("[0]")]
    [InlineData("[[]]")]
    [InlineData("{}")]
    [InlineData("[] extra")]
    [InlineData("0")]
    [InlineData("Unknown command: archon.events.drain")]
    [InlineData("not json but has \"bootId\" in it")]
    [InlineData("{\"v\":1,\"ok\":true,\"data\":{\"world\":{\"known\":true}}}")]
    [InlineData("[{\"SteamID\":\"1\",\"DisplayName\":\"x\"}]")]
    [InlineData("{\"v\":2,\"ok\":true,\"data\":{\"format\":1,\"bootId\":7,\"head\":3,\"cursor\":3,\"lost\":false,\"reset\":false,\"events\":[]}}")]
    public void AnythingElseIsNotEmpty(string? message) => Assert.False(EmptyPollReply.IsEmpty(message));
}
