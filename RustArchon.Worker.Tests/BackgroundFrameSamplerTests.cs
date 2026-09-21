// Copyright ©2026 Scott Blomfield

using System;
using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// Which of a background poll's non-empty answers the console record keeps: the first, then a sample (or a change), and always anything that
/// reports a problem. The data itself is stored elsewhere; the record only has to show the poll happens.
/// </summary>
public class BackgroundFrameSamplerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly BackgroundPoll Info = new("serverinfo", PollStorage.Sampled);
    private static readonly BackgroundPoll Cupboards = new("archon.tcs 0", PollStorage.OnChange);

    [Fact]
    public void TheFirstAnswerOfAPollIsKept()
    {
        var sampler = new BackgroundFrameSampler();

        Assert.True(sampler.ShouldStore(Info, "{\"Uptime\":1}", null, T0));
        Assert.True(new BackgroundFrameSampler().ShouldStore(Cupboards, "[]x", null, T0));
    }

    [Fact]
    public void ASampledPollKeepsOneAnswerPerSampleWhateverTheyContain()
    {
        var sampler = new BackgroundFrameSampler();
        Assert.True(sampler.ShouldStore(Info, "{\"Uptime\":1}", null, T0));

        // A minute later, and every minute after: different text each time (uptime moves), none kept.
        for (var minute = 1; minute < 10; minute++)
        {
            Assert.False(sampler.ShouldStore(Info, $"{{\"Uptime\":{minute + 1}}}", null, T0.AddMinutes(minute)), $"minute {minute}");
        }

        Assert.True(sampler.ShouldStore(Info, "{\"Uptime\":11}", null, T0 + BackgroundFrameSampler.SampleEvery));
        Assert.False(sampler.ShouldStore(Info, "{\"Uptime\":12}", null, T0 + BackgroundFrameSampler.SampleEvery + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AnOnChangePollKeepsAnAnswerOnlyWhenItDiffersOrTheHeartbeatIsDue()
    {
        var sampler = new BackgroundFrameSampler();
        Assert.True(sampler.ShouldStore(Cupboards, "same", null, T0));

        Assert.False(sampler.ShouldStore(Cupboards, "same", null, T0.AddMinutes(1)));
        Assert.False(sampler.ShouldStore(Cupboards, "same", null, T0.AddMinutes(30)));
        Assert.True(sampler.ShouldStore(Cupboards, "different", null, T0.AddMinutes(31)));       // a change is kept at once
        Assert.False(sampler.ShouldStore(Cupboards, "different", null, T0.AddMinutes(32)));
        Assert.True(sampler.ShouldStore(Cupboards, "different", null, T0.AddMinutes(31) + BackgroundFrameSampler.HeartbeatEvery));
    }

    [Fact]
    public void ChangingBackIsAChangeToo()
    {
        var sampler = new BackgroundFrameSampler();
        sampler.ShouldStore(Cupboards, "a", null, T0);
        sampler.ShouldStore(Cupboards, "b", null, T0.AddMinutes(1));

        Assert.True(sampler.ShouldStore(Cupboards, "a", null, T0.AddMinutes(2)));
    }

    [Fact]
    public void EachPollHasItsOwnClockAndItsOwnLastAnswer()
    {
        var sampler = new BackgroundFrameSampler();
        sampler.ShouldStore(Info, "x", null, T0);

        Assert.True(sampler.ShouldStore(new BackgroundPoll("archon.stats", PollStorage.Sampled), "x", null, T0.AddMinutes(1)));
        Assert.True(sampler.ShouldStore(new BackgroundPoll("archon.tcs 100", PollStorage.OnChange), "x", null, T0.AddMinutes(1)));
        Assert.False(sampler.ShouldStore(Info, "x", null, T0.AddMinutes(1)));
    }

    [Theory]
    [InlineData(PollStorage.Sampled)]
    [InlineData(PollStorage.OnChange)]
    public void AnAnswerWithAStackTraceIsAlwaysKept(PollStorage storage)
    {
        var sampler = new BackgroundFrameSampler();
        var poll = new BackgroundPoll("p", storage);
        sampler.ShouldStore(poll, "answer", null, T0);

        Assert.True(sampler.ShouldStore(poll, "answer", "at Foo.Bar()", T0.AddSeconds(5)));
        Assert.True(sampler.ShouldStore(poll, "answer", "at Foo.Bar()", T0.AddSeconds(10)));
    }

    [Theory]
    [InlineData(PollStorage.Sampled)]
    [InlineData(PollStorage.OnChange)]
    public void APluginErrorReplyIsAlwaysKept(PollStorage storage)
    {
        var sampler = new BackgroundFrameSampler();
        var poll = new BackgroundPoll("p", storage);
        sampler.ShouldStore(poll, "{\"v\":1,\"ok\":true,\"data\":{}}", null, T0);
        const string failure = "{\"v\":1,\"ok\":false,\"error\":{\"code\":\"busy\"}}";

        Assert.True(sampler.ShouldStore(poll, failure, null, T0.AddSeconds(5)));
        Assert.True(sampler.ShouldStore(poll, failure, null, T0.AddSeconds(10)));
    }

    [Theory]
    [InlineData("archon.events.drain", "events", true, false)]
    [InlineData("archon.events.drain", "events", false, true)]
    [InlineData("archon.positions.drain", "samples", true, false)]
    [InlineData("archon.positions.drain", "samples", false, true)]
    public void ADrainThatSaysItLostEventsOrWasResetIsAlwaysKept(string key, string items, bool lost, bool reset)
    {
        var sampler = new BackgroundFrameSampler();
        var poll = new BackgroundPoll(key, PollStorage.Sampled);
        string Drain(bool l, bool r, int head) =>
            $"{{\"v\":1,\"ok\":true,\"data\":{{\"format\":1,\"bootId\":5,\"head\":{head},\"cursor\":{head},\"lost\":{l.ToString().ToLowerInvariant()},\"reset\":{r.ToString().ToLowerInvariant()},\"{items}\":[{{\"s\":{head}}}]}}}}";
        Assert.True(sampler.ShouldStore(poll, Drain(false, false, 10), null, T0));
        Assert.False(sampler.ShouldStore(poll, Drain(false, false, 11), null, T0.AddSeconds(5)));

        Assert.True(sampler.ShouldStore(poll, Drain(lost, reset, 12), null, T0.AddSeconds(10)));
        Assert.True(sampler.ShouldStore(poll, Drain(lost, reset, 13), null, T0.AddSeconds(15)));
    }

    [Fact]
    public void AMissingAnswerDoesNotThrow()
    {
        var sampler = new BackgroundFrameSampler();

        Assert.True(sampler.ShouldStore(Info, null, null, T0));
        Assert.False(sampler.ShouldStore(Info, null, null, T0.AddSeconds(1)));
    }
}
