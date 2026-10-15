// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics;

/// <summary>
/// One captured log entry. <see cref="Seq"/> rises by exactly one per entry within a (<see cref="Source"/>, <see cref="InstanceId"/>) pair and is
/// assigned where the entry was created, so a reader can tell with certainty whether it has seen every entry.
/// </summary>
public sealed record LogEntry(
    string Source,
    string InstanceId,
    long Seq,
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, string> Properties);
