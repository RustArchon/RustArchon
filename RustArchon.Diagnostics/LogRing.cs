// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics;

/// <summary>
/// A bounded, thread-safe buffer of the most recent log entries of one source, with gap-detecting reads.
/// </summary>
/// <remarks>
/// Sequence numbers start at 1 and rise by one per appended entry, so a reader holding a cursor can tell exactly how many entries it
/// missed (<see cref="LogPage.Lost"/>) when the ring has wrapped past it. A cursor is only meaningful together with the
/// <see cref="InstanceId"/> it came from; a different instance (the process restarted) is reported as a reset, never silently re-read.
/// Same contract as the plugin's <c>ArchonRing</c> drain.
/// </remarks>
public sealed class LogRing
{
    private static readonly IReadOnlyDictionary<string, string> NoProperties = new Dictionary<string, string>();

    private readonly object _gate = new();
    private readonly LogEntry?[] _ring;
    private long _next = 1;

    public LogRing(string source, string instanceId, int capacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Source = source;
        InstanceId = instanceId;
        _ring = new LogEntry?[capacity];
    }

    public string Source { get; }

    public string InstanceId { get; }

    /// <summary>The highest sequence number assigned so far (0 when nothing has been appended).</summary>
    public long Head
    {
        get
        {
            lock (_gate)
            {
                return _next - 1;
            }
        }
    }

    public LogEntry Append(DateTimeOffset timestampUtc, LogLevel level, string category, string message,
        string? exception, IReadOnlyDictionary<string, string>? properties)
    {
        lock (_gate)
        {
            var entry = new LogEntry(Source, InstanceId, _next, timestampUtc, level, category, message, exception, properties ?? NoProperties);
            _ring[(int)((_next - 1) % _ring.Length)] = entry;
            _next++;
            return entry;
        }
    }

    /// <summary>
    /// Stores an entry that was numbered where it was created (another process's), keeping its sequence number. Returns false for an entry of
    /// another instance or one at or below the highest number already held (a duplicate or a late arrival). Numbers skipped over are never
    /// invented: they stay holes, and reads report them as lost.
    /// </summary>
    public bool Ingest(LogEntry entry)
    {
        if (entry.InstanceId != InstanceId)
        {
            return false;
        }

        lock (_gate)
        {
            if (entry.Seq < _next)
            {
                return false;
            }

            _ring[(int)((entry.Seq - 1) % _ring.Length)] = entry;
            _next = entry.Seq + 1;
            return true;
        }
    }

    /// <summary>
    /// The entries after <paramref name="cursor"/> that pass <paramref name="filter"/>, oldest first, at most <paramref name="max"/>.
    /// <paramref name="instanceId"/> is the instance the cursor came from (null with cursor 0 for a first read).
    /// </summary>
    public LogPage ReadAfter(string? instanceId, long cursor, int max, Func<LogEntry, bool>? filter = null)
    {
        lock (_gate)
        {
            var head = _next - 1;
            var oldest = OldestLocked(head);
            var reset = instanceId is null ? cursor != 0 : instanceId != InstanceId;
            if (reset || cursor < 0)
            {
                cursor = 0;
            }

            cursor = Math.Min(cursor, head);
            var lost = reset || head == 0 ? 0 : Math.Max(0, oldest - (cursor + 1));
            var entries = new List<LogEntry>();
            var through = cursor;

            if (max > 0)
            {
                for (var seq = Math.Max(cursor + 1, oldest); seq <= head; seq++)
                {
                    var entry = _ring[(int)((seq - 1) % _ring.Length)];
                    through = seq;
                    if (entry is null || entry.Seq != seq)
                    {
                        // Never received (a lost batch), or a slot still holding an earlier lap's entry.
                        lost++;
                        continue;
                    }

                    if (filter is null || filter(entry))
                    {
                        entries.Add(entry);
                        if (entries.Count >= max)
                        {
                            break;
                        }
                    }
                }
            }

            return new LogPage(entries, InstanceId, oldest, head, through, lost, reset);
        }
    }

    /// <summary>The most recent <paramref name="max"/> entries that pass <paramref name="filter"/>, ascending by sequence.</summary>
    public LogPage ReadTail(int max, Func<LogEntry, bool>? filter = null)
    {
        lock (_gate)
        {
            var head = _next - 1;
            var oldest = OldestLocked(head);
            var entries = new List<LogEntry>();

            if (max > 0)
            {
                for (var seq = head; seq >= oldest && seq >= 1; seq--)
                {
                    var entry = _ring[(int)((seq - 1) % _ring.Length)];
                    if (entry is null || entry.Seq != seq)
                    {
                        continue;
                    }

                    if (filter is null || filter(entry))
                    {
                        entries.Add(entry);
                        if (entries.Count >= max)
                        {
                            break;
                        }
                    }
                }

                entries.Reverse();
            }

            return new LogPage(entries, InstanceId, oldest, head, head, 0, false);
        }
    }

    private long OldestLocked(long head) => head == 0 ? 0 : head - Math.Min(head, _ring.Length) + 1;
}
