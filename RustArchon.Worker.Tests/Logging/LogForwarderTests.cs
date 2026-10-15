// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Diagnostics;
using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Logging;

namespace RustArchon.Worker.Tests.Logging;

public class LogForwarderTests
{
    private sealed class FakePublisher : ILogBatchPublisher
    {
        public List<WorkerLogBatch> Batches { get; } = [];

        public int FailNext { get; set; }

        public Task PublishAsync(WorkerLogBatch batch, CancellationToken cancellationToken)
        {
            if (FailNext > 0)
            {
                FailNext--;
                throw new InvalidOperationException("broker down");
            }

            Batches.Add(batch);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger : ILogger<LogForwarder>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static LogRing NewRing(int capacity = 5000) => new("worker", "w1", capacity);

    private static void Add(LogRing ring, int count, string message = "message", LogLevel level = LogLevel.Information)
    {
        for (var i = 0; i < count; i++)
        {
            ring.Append(DateTimeOffset.UnixEpoch, level, "Cat", message, null, new Dictionary<string, string> { ["ServerId"] = "s1" });
        }
    }

    private static (LogForwarder Forwarder, FakePublisher Publisher, CapturingLogger Logger, MutableTime Time) Create(LogRing ring)
    {
        var publisher = new FakePublisher();
        var logger = new CapturingLogger();
        var time = new MutableTime();
        return (new LogForwarder(ring, publisher, logger, time), publisher, logger, time);
    }

    [Fact]
    public async Task NewEntriesArePublishedInOrderWithTheirOriginSequenceNumbers()
    {
        var ring = NewRing();
        Add(ring, 3);
        var (forwarder, publisher, _, _) = Create(ring);

        var sent = await forwarder.ForwardOnceAsync(CancellationToken.None);

        Assert.Equal(3, sent);
        var batch = Assert.Single(publisher.Batches);
        Assert.Equal("w1", batch.InstanceId);
        Assert.Equal(new long[] { 1, 2, 3 }, batch.Entries.Select(e => e.Seq));
        Assert.Equal((int)LogLevel.Information, batch.Entries[0].Level);
        Assert.Equal("Cat", batch.Entries[0].Category);
        Assert.Equal("s1", batch.Entries[0].Properties["ServerId"]);
    }

    [Fact]
    public async Task OnlyEntriesAddedSinceTheLastBatchAreSentNextTime()
    {
        var ring = NewRing();
        Add(ring, 2);
        var (forwarder, publisher, _, _) = Create(ring);
        await forwarder.ForwardOnceAsync(CancellationToken.None);

        Add(ring, 2);
        await forwarder.ForwardOnceAsync(CancellationToken.None);

        Assert.Equal(2, publisher.Batches.Count);
        Assert.Equal(new long[] { 3, 4 }, publisher.Batches[1].Entries.Select(e => e.Seq));
    }

    [Fact]
    public async Task NothingNewMeansNothingPublished()
    {
        var ring = NewRing();
        var (forwarder, publisher, _, _) = Create(ring);

        var sent = await forwarder.ForwardOnceAsync(CancellationToken.None);

        Assert.Equal(0, sent);
        Assert.Empty(publisher.Batches);
    }

    [Fact]
    public async Task ABatchHoldsNoMoreThanTheEntryLimitAndTheRestFollowsWithoutGapsOrRepeats()
    {
        var ring = NewRing();
        Add(ring, LogForwarder.MaxEntriesPerBatch + 50);
        var (forwarder, publisher, _, _) = Create(ring);

        await forwarder.ForwardOnceAsync(CancellationToken.None);
        await forwarder.ForwardOnceAsync(CancellationToken.None);

        Assert.Equal(LogForwarder.MaxEntriesPerBatch, publisher.Batches[0].Entries.Count);
        Assert.Equal(50, publisher.Batches[1].Entries.Count);
        var all = publisher.Batches.SelectMany(b => b.Entries.Select(e => e.Seq)).ToList();
        Assert.Equal(Enumerable.Range(1, LogForwarder.MaxEntriesPerBatch + 50).Select(i => (long)i), all);
    }

    [Fact]
    public async Task ABatchIsTrimmedToTheSizeLimitWithoutLosingTheRest()
    {
        var ring = NewRing();
        var big = new string('x', 100_000);
        Add(ring, 5, big);
        var (forwarder, publisher, _, _) = Create(ring);

        while (await forwarder.ForwardOnceAsync(CancellationToken.None) > 0)
        {
        }

        Assert.True(publisher.Batches.Count > 1);
        Assert.All(publisher.Batches, b => Assert.True(b.Entries.Sum(e => e.Message.Length) <= LogForwarder.MaxCharactersPerBatch + 100_000));
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, publisher.Batches.SelectMany(b => b.Entries.Select(e => e.Seq)));
    }

    [Fact]
    public async Task AFailedPublishDropsThatBatchAndTheNextEntriesStillGoOut()
    {
        var ring = NewRing();
        Add(ring, 2);
        var (forwarder, publisher, _, _) = Create(ring);
        publisher.FailNext = 1;

        var first = await forwarder.ForwardOnceAsync(CancellationToken.None);
        Add(ring, 2);
        await forwarder.ForwardOnceAsync(CancellationToken.None);

        Assert.Equal(0, first);
        var batch = Assert.Single(publisher.Batches);
        Assert.Equal(new long[] { 3, 4 }, batch.Entries.Select(e => e.Seq));
    }

    [Fact]
    public async Task AFailedPublishIsLoggedAsAWarningAtMostOncePerMinute()
    {
        var ring = NewRing();
        var (forwarder, publisher, logger, time) = Create(ring);
        publisher.FailNext = 3;

        Add(ring, 1);
        await forwarder.ForwardOnceAsync(CancellationToken.None);
        Add(ring, 1);
        await forwarder.ForwardOnceAsync(CancellationToken.None);
        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logger.Entries[0].Level);

        time.Now = time.Now.AddSeconds(61);
        Add(ring, 1);
        await forwarder.ForwardOnceAsync(CancellationToken.None);

        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains("2", logger.Entries[1].Message);
    }

    [Theory]
    [InlineData("MassTransit", LogLevel.Information, true)]
    [InlineData("MassTransit.ReceiveTransport", LogLevel.Debug, true)]
    [InlineData("MassTransit.ReceiveTransport", LogLevel.Warning, false)]
    [InlineData("MassTransit.ReceiveTransport", LogLevel.Error, false)]
    [InlineData("RustArchon.Worker.Connections.ServerConnectionActor", LogLevel.Debug, false)]
    [InlineData("Microsoft.Hosting.Lifetime", LogLevel.Information, false)]
    public void TheBusOwnChatterBelowWarningIsLeftOutSoForwardingCannotFeedItself(string category, LogLevel level, bool excluded)
    {
        Assert.Equal(excluded, WorkerLogging.ExcludeFromCapture(category, level));
    }

    [Fact]
    public async Task AnEntryThatHasAlreadyScrolledOutOfTheRingLeavesAVisibleSequenceGap()
    {
        var ring = NewRing(3);
        Add(ring, 10);
        var (forwarder, publisher, _, _) = Create(ring);

        await forwarder.ForwardOnceAsync(CancellationToken.None);

        var batch = Assert.Single(publisher.Batches);
        Assert.Equal(new long[] { 8, 9, 10 }, batch.Entries.Select(e => e.Seq));
    }

    [Fact]
    public void TheForwarderStaysQuietWithNullLogger()
    {
        _ = new LogForwarder(NewRing(), new FakePublisher(), NullLogger<LogForwarder>.Instance);
    }
}
