// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Diagnostics;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

public class WorkerLogStoreTests
{
    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static WorkerLogEntry Entry(long seq, int level = (int)LogLevel.Information, string message = "m") =>
        new(seq, new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), level, "Cat", message, null, new Dictionary<string, string>());

    private static WorkerLogBatch Batch(string instance, params long[] seqs) => new(instance, seqs.Select(s => Entry(s)).ToList());

    private static async Task<LogSourceResult> Read(ILogSource source, SourceCursor? after = null) =>
        await source.ReadAsync(after, 1000, _ => true, CancellationToken.None);

    [Fact]
    public async Task EntriesAreServedUnderTheSequenceNumbersTheWorkerAssigned()
    {
        var store = new WorkerLogStore();
        store.Ingest(Batch("aaaaaaaa1111", 5, 6, 7));

        var source = Assert.Single(store.Sources());
        var result = await Read(source);

        Assert.Equal(WorkerLogStore.SourceName("aaaaaaaa1111"), source.Name);
        Assert.Equal(new long[] { 5, 6, 7 }, result.Page!.Entries.Select(e => e.Seq));
        Assert.All(result.Page.Entries, e => Assert.Equal(source.Name, e.Source));
        Assert.Equal("aaaaaaaa1111", result.Page.InstanceId);
    }

    [Fact]
    public void TheSourceNameIsWorkerPlusTheStartOfTheInstanceId()
    {
        Assert.Equal("worker:aaaaaaaa", WorkerLogStore.SourceName("aaaaaaaa1111"));
    }

    [Fact]
    public async Task EachWorkerInstanceIsItsOwnSource()
    {
        var store = new WorkerLogStore();
        store.Ingest(Batch("aaaaaaaa1111", 1, 2));
        store.Ingest(Batch("bbbbbbbb2222", 1));

        var sources = store.Sources();

        Assert.Equal(2, sources.Count);
        Assert.Equal(
            new[] { "worker:aaaaaaaa", "worker:bbbbbbbb" },
            sources.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, (await Read(sources.Single(s => s.Name == "worker:aaaaaaaa"))).Page!.Entries.Count);
    }

    [Fact]
    public async Task ALostBatchShowsAsAGapNotASilentSkip()
    {
        var store = new WorkerLogStore();
        store.Ingest(Batch("aaaaaaaa1111", 1, 2, 3));
        store.Ingest(Batch("aaaaaaaa1111", 6, 7, 8));

        var result = await Read(store.Sources().Single(), new SourceCursor("aaaaaaaa1111", 0));

        Assert.Equal(new long[] { 1, 2, 3, 6, 7, 8 }, result.Page!.Entries.Select(e => e.Seq));
        Assert.Equal(2, result.Page.Lost);
    }

    [Fact]
    public async Task ARedeliveredBatchDoesNotDuplicateEntries()
    {
        var store = new WorkerLogStore();
        var batch = Batch("aaaaaaaa1111", 1, 2, 3);

        var first = store.Ingest(batch);
        var again = store.Ingest(batch);

        Assert.Equal(3, first);
        Assert.Equal(0, again);
        Assert.Equal(3, (await Read(store.Sources().Single())).Page!.Entries.Count);
    }

    [Fact]
    public async Task WithNoWorkerHeardFromThereIsOneUnavailableSourceThatSaysSo()
    {
        var sources = new WorkerLogStore().Sources();

        var source = Assert.Single(sources);
        Assert.Equal("worker", source.Name);
        var result = await source.ReadAsync(null, 10, _ => true, CancellationToken.None);
        Assert.False(result.Available);
        Assert.Contains("RabbitMQ", result.UnavailableReason);
    }

    [Fact]
    public async Task ASourceThatHasGoneQuietCarriesAWarningButStillReturnsWhatItHolds()
    {
        var time = new MutableTime();
        var store = new WorkerLogStore(time);
        store.Ingest(Batch("aaaaaaaa1111", 1));

        var fresh = await Read(store.Sources().Single());
        time.Now = time.Now.Add(WorkerLogStore.StaleAfter).AddSeconds(1);
        var stale = await Read(store.Sources().Single());

        Assert.Null(fresh.Warning);
        Assert.NotNull(stale.Warning);
        Assert.Contains("RabbitMQ", stale.Warning);
        Assert.Single(stale.Page!.Entries);
    }

    [Fact]
    public void OnlyTheMostRecentlyHeardFromInstancesAreKept()
    {
        var time = new MutableTime();
        var store = new WorkerLogStore(time);
        for (var i = 0; i < WorkerLogStore.MaxInstances + 2; i++)
        {
            store.Ingest(Batch(i.ToString("D4") + "iiiixxxx", 1));
            time.Now = time.Now.AddSeconds(1);
        }

        var names = store.Sources().Select(s => s.Name).ToList();

        Assert.Equal(WorkerLogStore.MaxInstances, names.Count);
        Assert.DoesNotContain("worker:0000iiii", names);
        Assert.DoesNotContain("worker:0001iiii", names);
        Assert.Contains("worker:0009iiii", names);
    }

    [Fact]
    public async Task AnUnknownLevelIsClampedRatherThanTrusted()
    {
        var store = new WorkerLogStore();
        store.Ingest(new WorkerLogBatch("aaaaaaaa1111", [Entry(1, 99), Entry(2, -4)]));

        var entries = (await Read(store.Sources().Single())).Page!.Entries;

        Assert.Equal(LogLevel.Critical, entries[0].Level);
        Assert.Equal(LogLevel.Debug, entries[1].Level);
    }

    [Fact]
    public async Task ABatchWithAnEmptyInstanceOrNoEntriesIsIgnored()
    {
        var store = new WorkerLogStore();

        Assert.Equal(0, store.Ingest(new WorkerLogBatch("", [Entry(1)])));
        Assert.Equal(0, store.Ingest(new WorkerLogBatch("aaaaaaaa1111", [])));

        Assert.False((await Read(store.Sources().Single())).Available);
    }
}
