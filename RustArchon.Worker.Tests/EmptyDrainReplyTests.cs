// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

public class EmptyDrainReplyTests
{
    private static string Reply(string items = "events", string body = "[]", string lost = "false", string reset = "false", string ok = "true") =>
        "{\"v\":1,\"ok\":" + ok + ",\"data\":{\"format\":1,\"bootId\":7,\"head\":3,\"cursor\":3,\"lost\":" + lost + ",\"reset\":" + reset + ",\"" + items + "\":" + body + "}}";

    [Fact]
    public void AnEventsDrainWithNothingNewIsEmpty() => Assert.True(EmptyDrainReply.IsEmpty(Reply()));

    [Fact]
    public void APositionsDrainWithNothingNewIsEmpty() => Assert.True(EmptyDrainReply.IsEmpty(Reply(items: "samples")));

    [Fact]
    public void AReplyCarryingItemsIsNotEmpty()
    {
        Assert.False(EmptyDrainReply.IsEmpty(Reply(body: "[{\"s\":3}]")));
        Assert.False(EmptyDrainReply.IsEmpty(Reply(items: "samples", body: "[{\"s\":3}]")));
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    [InlineData("true", "true")]
    public void AReplyThatSaysEventsWereLostOrTheBufferWasResetIsNotEmptyEvenWithNoItems(string lost, string reset) =>
        Assert.False(EmptyDrainReply.IsEmpty(Reply(lost: lost, reset: reset)));

    [Fact]
    public void AnErrorReplyIsNotEmpty() =>
        Assert.False(EmptyDrainReply.IsEmpty("{\"v\":1,\"ok\":false,\"err\":\"usage\",\"bootId\":1}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown command: archon.events.drain")]
    [InlineData("not json but has \"bootId\" in it")]
    [InlineData("[{\"SteamID\":\"1\",\"DisplayName\":\"x\"}]")]
    [InlineData("{\"v\":2,\"ok\":true,\"data\":{\"format\":1,\"bootId\":7,\"head\":3,\"cursor\":3,\"lost\":false,\"reset\":false,\"events\":[]}}")]
    public void AnythingElseIsNotEmpty(string? message) => Assert.False(EmptyDrainReply.IsEmpty(message));
}
