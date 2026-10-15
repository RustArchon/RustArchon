// Copyright ©2026 Scott Blomfield

namespace RustArchon.Diagnostics;

/// <summary>One place entries can be read from: a process's own ring, a copy kept of another process's, or something fetched on demand.</summary>
public interface ILogSource
{
    string Name { get; }

    /// <summary>
    /// Reads at most <paramref name="max"/> entries that pass <paramref name="filter"/>. With no <paramref name="after"/> cursor it returns
    /// the most recent matches; with one, the matches after it, oldest first. Returns an unavailable result, with a reason, when the source
    /// cannot be read; never an empty page that would pass for "nothing happened".
    /// </summary>
    ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken);
}

/// <summary>A source's answer: a page, or the reason there is none. <see cref="Warning"/> qualifies a page that was returned (for example, that the source has gone quiet).</summary>
public sealed record LogSourceResult(string Name, LogPage? Page, string? UnavailableReason, string? Warning = null)
{
    public bool Available => Page is not null;

    public static LogSourceResult Of(string name, LogPage page) => new(name, page, null);

    public static LogSourceResult Unavailable(string name, string reason) => new(name, null, reason);
}

/// <summary>Reads a <see cref="LogRing"/>.</summary>
public sealed class RingLogSource(string name, LogRing ring) : ILogSource
{
    public string Name { get; } = name;

    public ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken)
    {
        var page = after is null ? ring.ReadTail(max, filter) : ring.ReadAfter(after.InstanceId, after.Seq, max, filter);
        return ValueTask.FromResult(LogSourceResult.Of(Name, page));
    }
}

/// <summary>A source that cannot be read, named with the reason, so it shows up in a response instead of silently missing from it.</summary>
public sealed class UnavailableLogSource(string name, string reason) : ILogSource
{
    public string Name { get; } = name;

    public ValueTask<LogSourceResult> ReadAsync(SourceCursor? after, int max, Func<LogEntry, bool> filter, CancellationToken cancellationToken) =>
        ValueTask.FromResult(LogSourceResult.Unavailable(Name, reason));
}
