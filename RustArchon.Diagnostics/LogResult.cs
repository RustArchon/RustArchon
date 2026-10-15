// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics;

/// <summary>Everything one query produced: the entries, how each source fared, and (in summary mode) the counts.</summary>
public sealed record LogResult(
    DateTimeOffset GeneratedAtUtc,
    bool Summary,
    IReadOnlyList<LogEntry> Entries,
    IReadOnlyList<LogSourceResult> Sources,
    IReadOnlyList<SourceSummary> Summaries);

/// <summary>Counts for one source over the filtered range.</summary>
public sealed record SourceSummary(
    string Source,
    long Total,
    IReadOnlyDictionary<LogLevel, long> ByLevel,
    IReadOnlyList<KeyValuePair<string, long>> TopCategories,
    DateTimeOffset? FirstUtc,
    DateTimeOffset? LastUtc);
