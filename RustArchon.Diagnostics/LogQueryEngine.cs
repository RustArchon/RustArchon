// Copyright ©2026 Scott Blomfield

namespace RustArchon.Diagnostics;

/// <summary>
/// Runs a <see cref="LogQuery"/> over a set of <see cref="ILogSource"/>s: applies the filters, honours each source's cursor, merges the
/// entries by time, and reports how every source fared (including those that could not be read).
/// </summary>
public sealed class LogQueryEngine(TimeProvider? timeProvider = null)
{
    private const int TopCategoryCount = 50;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Func<LogEntry, bool> BuildFilter(LogQuery query)
    {
        var minLevel = query.MinLevel;
        var category = query.Category;
        var from = query.FromUtc;
        var to = query.ToUtc;
        var text = string.IsNullOrEmpty(query.Text) ? null : query.Text;
        var properties = query.Properties is { Count: > 0 } ? query.Properties.ToList() : null;

        return entry =>
        {
            if (entry.Level < minLevel)
            {
                return false;
            }

            if (category is not null && !entry.Category.StartsWith(category, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if ((from is not null && entry.TimestampUtc < from) || (to is not null && entry.TimestampUtc > to))
            {
                return false;
            }

            if (text is not null
                && !entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
                && entry.Exception?.Contains(text, StringComparison.OrdinalIgnoreCase) != true
                && !entry.Category.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (properties is not null)
            {
                foreach (var wanted in properties)
                {
                    var found = entry.Properties.FirstOrDefault(p => string.Equals(p.Key, wanted.Key, StringComparison.OrdinalIgnoreCase));
                    if (found.Key is null || !string.Equals(found.Value, wanted.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return true;
        };
    }

    public async Task<LogResult> ExecuteAsync(LogQuery query, IReadOnlyList<ILogSource> sources, CancellationToken cancellationToken)
    {
        var filter = BuildFilter(query);
        var max = query.Summary ? int.MaxValue : query.EffectiveCount;

        var results = await Task.WhenAll(sources.Select(source => ReadOneAsync(source, query, max, filter, cancellationToken)));

        var summaries = new List<SourceSummary>();
        var entries = new List<LogEntry>();
        foreach (var result in results.Where(r => r.Page is not null))
        {
            if (query.Summary)
            {
                summaries.Add(Summarize(result.Name, result.Page!.Entries));
            }
            else
            {
                entries.AddRange(result.Page!.Entries);
            }
        }

        entries.Sort(static (a, b) =>
        {
            var byTime = a.TimestampUtc.CompareTo(b.TimestampUtc);
            if (byTime != 0)
            {
                return byTime;
            }

            var bySource = string.CompareOrdinal(a.Source, b.Source);
            return bySource != 0 ? bySource : a.Seq.CompareTo(b.Seq);
        });

        return new LogResult(_time.GetUtcNow(), query.Summary, entries, results, summaries);
    }

    private static async Task<LogSourceResult> ReadOneAsync(
        ILogSource source, LogQuery query, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken)
    {
        SourceCursor? after = null;
        if (!query.Summary)
        {
            query.After?.TryGetValue(source.Name, out after);
        }

        try
        {
            return await source.ReadAsync(after, max, filter, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One broken source (an unreachable game server, say) must not take the rest of the answer down; it is named instead.
            return LogSourceResult.Unavailable(source.Name, LogRedactor.Redact(ex.GetType().Name + ": " + ex.Message));
        }
    }

    private static SourceSummary Summarize(string source, IReadOnlyList<LogEntry> entries)
    {
        var byLevel = entries.GroupBy(e => e.Level).ToDictionary(g => g.Key, g => (long)g.Count());
        var topCategories = entries
            .GroupBy(e => e.Category, StringComparer.Ordinal)
            .Select(g => new KeyValuePair<string, long>(g.Key, g.Count()))
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(TopCategoryCount)
            .ToList();

        return new SourceSummary(
            source,
            entries.Count,
            byLevel,
            topCategories,
            entries.Count == 0 ? null : entries.Min(e => e.TimestampUtc),
            entries.Count == 0 ? null : entries.Max(e => e.TimestampUtc));
    }
}
