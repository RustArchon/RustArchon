// Copyright ©2026 Scott Blomfield

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics;

/// <summary>
/// Writes a <see cref="LogResult"/> as newline-delimited JSON: one entry per line, then (in summary mode, first) a summary line, and a
/// final <c>_meta</c> line saying how each source fared and where each cursor now stands. Text is redacted once more on the way out, so
/// an entry that reached a buffer some way other than the logger provider still leaves clean.
/// </summary>
public static class LogNdjsonWriter
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "information",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "none",
    };

    public static async Task WriteAsync(LogResult result, Stream output, CancellationToken cancellationToken)
    {
        // Rendered in memory first and handed over asynchronously: a web server's response stream refuses synchronous writes, which is what the
        // JSON writer does, so writing to it directly fails on the first real request.
        using var buffer = new MemoryStream();
        if (result.Summary)
        {
            WriteLine(buffer, w => WriteSummary(w, result));
        }
        else
        {
            foreach (var entry in result.Entries)
            {
                WriteLine(buffer, w => WriteEntry(w, entry));
            }
        }

        WriteLine(buffer, w => WriteMeta(w, result.GeneratedAtUtc, result.Summary, result.Entries.Count, result.Sources.Select(s => (Action<Utf8JsonWriter>)(w2 => WriteSourceMeta(w2, s)))));
        buffer.Position = 0;
        await buffer.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    internal static void WriteLine(Stream output, Action<Utf8JsonWriter> write)
    {
        using (var writer = new Utf8JsonWriter(output))
        {
            write(writer);
        }

        output.Write(NewLine);
    }

    internal static void WriteEntry(Utf8JsonWriter w, LogEntry entry)
    {
        w.WriteStartObject();
        w.WriteString("source", entry.Source);
        w.WriteString("instance", entry.InstanceId);
        w.WriteNumber("seq", entry.Seq);
        w.WriteString("ts", entry.TimestampUtc);
        w.WriteString("level", LevelName(entry.Level));
        w.WriteString("category", entry.Category);
        w.WriteString("message", LogRedactor.Redact(entry.Message));
        if (entry.Exception is not null)
        {
            w.WriteString("exception", LogRedactor.Redact(entry.Exception));
        }

        w.WriteStartObject("props");
        foreach (var property in entry.Properties)
        {
            w.WriteString(property.Key, RedactProperty(property.Key, property.Value));
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static string RedactProperty(string key, string value)
    {
        var redacted = LogRedactor.Redact(key + "=" + value);
        return redacted.StartsWith(key + "=", StringComparison.Ordinal) ? redacted[(key.Length + 1)..] : redacted;
    }

    private static void WriteSummary(Utf8JsonWriter w, LogResult result)
    {
        w.WriteStartObject();
        w.WriteStartArray("_summary");
        foreach (var summary in result.Summaries)
        {
            WriteSourceSummary(w, summary);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static void WriteSourceSummary(Utf8JsonWriter w, SourceSummary summary)
    {
        w.WriteStartObject();
        w.WriteString("source", summary.Source);
        w.WriteNumber("total", summary.Total);
        w.WriteStartObject("byLevel");
        foreach (var level in summary.ByLevel.OrderBy(p => p.Key))
        {
            w.WriteNumber(LevelName(level.Key), level.Value);
        }

        w.WriteEndObject();
        w.WriteStartArray("topCategories");
        foreach (var category in summary.TopCategories)
        {
            w.WriteStartObject();
            w.WriteString("category", category.Key);
            w.WriteNumber("count", category.Value);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        if (summary.FirstUtc is not null)
        {
            w.WriteString("first", summary.FirstUtc.Value);
            w.WriteString("last", summary.LastUtc!.Value);
        }

        w.WriteEndObject();
    }

    internal static void WriteMeta(
        Utf8JsonWriter w, DateTimeOffset generatedAtUtc, bool summary, int returned, IEnumerable<Action<Utf8JsonWriter>> writeSources)
    {
        w.WriteStartObject();
        w.WriteStartObject("_meta");
        w.WriteString("generated", generatedAtUtc);
        w.WriteBoolean("summary", summary);
        w.WriteNumber("returned", returned);
        w.WriteStartArray("sources");
        foreach (var writeSource in writeSources)
        {
            writeSource(w);
        }

        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    internal static void WriteSourceMeta(Utf8JsonWriter w, LogSourceResult source)
    {
        w.WriteStartObject();
        w.WriteString("name", source.Name);
        w.WriteBoolean("available", source.Available);
        if (source.Page is { } page)
        {
            w.WriteString("instance", page.InstanceId);
            w.WriteNumber("oldest", page.Oldest);
            w.WriteNumber("head", page.Head);
            w.WriteNumber("returnedThrough", page.ReturnedThrough);
            w.WriteNumber("lost", page.Lost);
            w.WriteBoolean("reset", page.Reset);
            w.WriteNumber("returned", page.Entries.Count);
            if (source.Warning is not null)
            {
                w.WriteString("warning", LogRedactor.Redact(source.Warning));
            }
        }
        else
        {
            w.WriteString("reason", LogRedactor.Redact(source.UnavailableReason));
        }

        w.WriteEndObject();
    }
}
