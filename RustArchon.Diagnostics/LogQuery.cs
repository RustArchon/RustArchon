// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics;

/// <summary>Where a caller left off in one source: the run of the process and the last sequence number it has seen.</summary>
public sealed record SourceCursor(string InstanceId, long Seq);

/// <summary>
/// What a caller asks for. <see cref="Count"/> is per source, because a cursor is per source: cutting a merged list short would leave
/// some source's cursor pointing past entries the caller never received.
/// </summary>
public sealed record LogQuery
{
    public const int DefaultCount = 1000;

    public const int MaxCount = 5000;

    /// <summary>Entries of this level and above. Callers choose their own verbosity, down to Debug.</summary>
    public LogLevel MinLevel { get; init; } = LogLevel.Information;

    /// <summary>Category prefix, case-insensitive (for example <c>RustArchon.Api.Services</c>).</summary>
    public string? Category { get; init; }

    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Case-insensitive text that must appear in the message, the exception or the category.</summary>
    public string? Text { get; init; }

    /// <summary>Structured properties that must be present with this value (case-insensitive), for example ServerId or Plugin.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    /// <summary>Most entries to return per source; clamped to 1..<see cref="MaxCount"/>.</summary>
    public int Count { get; init; } = DefaultCount;

    /// <summary>Per source name, where the caller left off. A source with no cursor returns its most recent entries.</summary>
    public IReadOnlyDictionary<string, SourceCursor>? After { get; init; }

    /// <summary>Return counts instead of entries.</summary>
    public bool Summary { get; init; }

    public int EffectiveCount => Math.Clamp(Count, 1, MaxCount);
}
