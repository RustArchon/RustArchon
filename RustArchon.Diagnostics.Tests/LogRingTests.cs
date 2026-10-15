// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics.Tests;

public class LogRingTests
{
    private static LogRing NewRing(int capacity = 5) => new("api", "boot1", capacity);

    private static void Add(LogRing ring, int count, LogLevel level = LogLevel.Information)
    {
        for (var i = 0; i < count; i++)
        {
            ring.Append(DateTimeOffset.UnixEpoch.AddSeconds(i), level, "Cat", "message", null, null);
        }
    }

    private static long[] Seqs(LogPage page) => page.Entries.Select(e => e.Seq).ToArray();

    [Fact]
    public void Append_assigns_consecutive_sequence_numbers_from_one()
    {
        var ring = NewRing();

        var first = ring.Append(DateTimeOffset.UnixEpoch, LogLevel.Debug, "Cat", "a", null, null);
        var second = ring.Append(DateTimeOffset.UnixEpoch, LogLevel.Debug, "Cat", "b", null, null);

        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal("api", first.Source);
        Assert.Equal("boot1", first.InstanceId);
    }

    [Fact]
    public void ReadAfter_zero_returns_everything_held_in_order()
    {
        var ring = NewRing();
        Add(ring, 3);

        var page = ring.ReadAfter("boot1", 0, 100);

        Assert.Equal(new long[] { 1, 2, 3 }, Seqs(page));
        Assert.Equal(3, page.ReturnedThrough);
        Assert.Equal(0, page.Lost);
        Assert.False(page.Reset);
    }

    [Fact]
    public void ReadAfter_a_cursor_returns_only_later_entries()
    {
        var ring = NewRing();
        Add(ring, 4);

        Assert.Equal(new long[] { 3, 4 }, Seqs(ring.ReadAfter("boot1", 2, 100)));
    }

    [Fact]
    public void ReadAfter_stops_at_max_and_reports_where_it_stopped()
    {
        var ring = NewRing();
        Add(ring, 5);

        var page = ring.ReadAfter("boot1", 0, 2);

        Assert.Equal(new long[] { 1, 2 }, Seqs(page));
        Assert.Equal(2, page.ReturnedThrough);
        Assert.Equal(5, page.Head);
    }

    [Fact]
    public void Capacity_is_bounded_and_the_oldest_entries_drop()
    {
        var ring = NewRing(3);
        Add(ring, 10);

        var page = ring.ReadAfter("boot1", 0, 100);

        Assert.Equal(new long[] { 8, 9, 10 }, Seqs(page));
        Assert.Equal(8, page.Oldest);
        Assert.Equal(10, page.Head);
    }

    [Fact]
    public void A_cursor_behind_the_oldest_held_entry_reports_how_many_were_lost()
    {
        var ring = NewRing(3);
        Add(ring, 10);

        var page = ring.ReadAfter("boot1", 4, 100);

        Assert.Equal(3, page.Lost);
        Assert.Equal(new long[] { 8, 9, 10 }, Seqs(page));
    }

    [Fact]
    public void A_cursor_exactly_at_the_edge_of_what_was_evicted_loses_nothing()
    {
        var ring = NewRing(3);
        Add(ring, 10);

        var page = ring.ReadAfter("boot1", 7, 100);

        Assert.Equal(0, page.Lost);
        Assert.Equal(new long[] { 8, 9, 10 }, Seqs(page));
    }

    [Fact]
    public void A_cursor_from_another_instance_is_a_reset_and_reads_from_the_oldest()
    {
        var ring = NewRing(3);
        Add(ring, 5);

        var page = ring.ReadAfter("old-boot", 999, 100);

        Assert.True(page.Reset);
        Assert.Equal(new long[] { 3, 4, 5 }, Seqs(page));
    }

    [Fact]
    public void A_null_instance_with_cursor_zero_is_a_first_read_not_a_reset()
    {
        var ring = NewRing();
        Add(ring, 2);

        var page = ring.ReadAfter(null, 0, 100);

        Assert.False(page.Reset);
        Assert.Equal(2, page.Entries.Count);
    }

    [Fact]
    public void A_filter_narrows_the_entries_but_the_cursor_still_advances_past_the_ones_it_skipped()
    {
        var ring = NewRing(10);
        Add(ring, 3, LogLevel.Debug);
        Add(ring, 1, LogLevel.Error);
        Add(ring, 2, LogLevel.Debug);

        var page = ring.ReadAfter("boot1", 0, 100, e => e.Level >= LogLevel.Warning);

        Assert.Equal(new long[] { 4 }, Seqs(page));
        Assert.Equal(6, page.ReturnedThrough);
    }

    [Fact]
    public void A_filtered_page_that_fills_up_reports_the_last_entry_it_returned_not_the_last_it_scanned()
    {
        var ring = NewRing(10);
        Add(ring, 6, LogLevel.Error);

        var page = ring.ReadAfter("boot1", 0, 2, e => e.Level >= LogLevel.Warning);

        Assert.Equal(new long[] { 1, 2 }, Seqs(page));
        Assert.Equal(2, page.ReturnedThrough);
    }

    [Fact]
    public void Paging_with_the_returned_cursor_yields_every_entry_exactly_once()
    {
        var ring = NewRing(100);
        Add(ring, 25);

        var seen = new List<long>();
        long cursor = 0;
        while (true)
        {
            var page = ring.ReadAfter("boot1", cursor, 4);
            if (page.Entries.Count == 0)
            {
                break;
            }

            seen.AddRange(Seqs(page));
            cursor = page.ReturnedThrough;
        }

        Assert.Equal(Enumerable.Range(1, 25).Select(i => (long)i), seen);
    }

    [Fact]
    public void ReadTail_returns_the_most_recent_entries_in_ascending_order()
    {
        var ring = NewRing(10);
        Add(ring, 6);

        var page = ring.ReadTail(3);

        Assert.Equal(new long[] { 4, 5, 6 }, Seqs(page));
        Assert.Equal(6, page.ReturnedThrough);
    }

    [Fact]
    public void ReadTail_applies_the_filter_before_counting()
    {
        var ring = NewRing(10);
        Add(ring, 2, LogLevel.Error);
        Add(ring, 4, LogLevel.Debug);

        var page = ring.ReadTail(5, e => e.Level >= LogLevel.Error);

        Assert.Equal(new long[] { 1, 2 }, Seqs(page));
        Assert.Equal(6, page.ReturnedThrough);
    }

    [Fact]
    public void An_empty_ring_reads_as_empty_with_no_loss()
    {
        var page = NewRing().ReadAfter("boot1", 0, 10);

        Assert.Empty(page.Entries);
        Assert.Equal(0, page.Head);
        Assert.Equal(0, page.Oldest);
        Assert.Equal(0, page.Lost);
    }

    private static LogEntry Remote(long seq, string instance = "boot1", LogLevel level = LogLevel.Information) =>
        new("worker", instance, seq, DateTimeOffset.UnixEpoch, level, "Cat", "message", null, new Dictionary<string, string>());

    [Fact]
    public void Ingest_keeps_the_sequence_number_the_origin_assigned()
    {
        var ring = new LogRing("worker", "boot1", 10);

        Assert.True(ring.Ingest(Remote(1)));
        Assert.True(ring.Ingest(Remote(2)));

        Assert.Equal(2, ring.Head);
        Assert.Equal(new long[] { 1, 2 }, Seqs(ring.ReadAfter("boot1", 0, 10)));
    }

    [Fact]
    public void Ingest_ignores_a_duplicate_or_older_sequence_number()
    {
        var ring = new LogRing("worker", "boot1", 10);
        ring.Ingest(Remote(1));
        ring.Ingest(Remote(2));

        Assert.False(ring.Ingest(Remote(2)));
        Assert.False(ring.Ingest(Remote(1)));

        Assert.Equal(new long[] { 1, 2 }, Seqs(ring.ReadAfter("boot1", 0, 10)));
    }

    [Fact]
    public void Ingest_refuses_an_entry_from_another_instance()
    {
        var ring = new LogRing("worker", "boot1", 10);

        Assert.False(ring.Ingest(Remote(1, "other")));
        Assert.Equal(0, ring.Head);
    }

    [Fact]
    public void A_sequence_gap_from_a_lost_batch_is_reported_as_lost_not_skipped_silently()
    {
        var ring = new LogRing("worker", "boot1", 10);
        ring.Ingest(Remote(1));
        ring.Ingest(Remote(2));
        ring.Ingest(Remote(5));
        ring.Ingest(Remote(6));

        var page = ring.ReadAfter("boot1", 0, 100);

        Assert.Equal(new long[] { 1, 2, 5, 6 }, Seqs(page));
        Assert.Equal(2, page.Lost);
        Assert.Equal(6, page.ReturnedThrough);
    }

    [Fact]
    public void A_gap_is_reported_correctly_after_the_ring_has_wrapped_over_the_missing_slots()
    {
        var ring = new LogRing("worker", "boot1", 4);
        ring.Ingest(Remote(1));
        ring.Ingest(Remote(2));
        ring.Ingest(Remote(9));
        ring.Ingest(Remote(10));

        var page = ring.ReadAfter("boot1", 0, 100);

        Assert.Equal(new long[] { 9, 10 }, Seqs(page));
        Assert.Equal(8, page.Lost);
    }

    [Fact]
    public void A_stale_entry_left_in_a_slot_by_an_earlier_lap_is_not_mistaken_for_the_missing_one()
    {
        var ring = new LogRing("worker", "boot1", 4);
        for (long i = 1; i <= 4; i++)
        {
            ring.Ingest(Remote(i));
        }

        ring.Ingest(Remote(6));

        var page = ring.ReadAfter("boot1", 0, 100);

        Assert.Equal(new long[] { 3, 4, 6 }, Seqs(page));
        Assert.Equal(3, page.Lost);
    }

    [Fact]
    public async Task Concurrent_writers_and_a_paging_reader_see_every_entry_once_and_in_order()
    {
        var ring = NewRing(100_000);
        const int writers = 4;
        const int perWriter = 5_000;

        var writing = Enumerable.Range(0, writers).Select(_ => Task.Run(() => Add(ring, perWriter))).ToArray();

        var seen = new List<long>();
        long cursor = 0;
        while (seen.Count < writers * perWriter)
        {
            var page = ring.ReadAfter("boot1", cursor, 256);
            seen.AddRange(Seqs(page));
            cursor = page.ReturnedThrough;
            Assert.Equal(0, page.Lost);
        }

        await Task.WhenAll(writing);
        Assert.Equal(Enumerable.Range(1, writers * perWriter).Select(i => (long)i), seen);
    }

    [Fact]
    public void Memory_stays_flat_after_many_times_the_capacity_is_written()
    {
        var ring = NewRing(1_000);
        Add(ring, 1_000);
        GC.Collect();
        var before = GC.GetTotalMemory(true);

        Add(ring, 200_000);
        GC.Collect();
        var after = GC.GetTotalMemory(true);

        Assert.True(after - before < 2_000_000, "retained memory grew by " + (after - before) + " bytes");
        Assert.Equal(1_000, ring.ReadAfter("boot1", 0, int.MaxValue).Entries.Count);
    }
}
