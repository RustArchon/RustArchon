// Copyright ©2026 Scott Blomfield

using System.Text;
using System.Text.Json;

namespace RustArchon.Diagnostics;

/// <summary>
/// Combines an already written NDJSON response (from the Api: its own, the Worker's and the plugins' sources) with a locally produced
/// <see cref="LogResult"/> (the Panel's own source) into one response: entries interleaved by time, sources and summaries side by side, one final
/// <c>_meta</c>. The upstream's own entry lines are passed on byte for byte.
/// </summary>
public static class LogNdjsonMerger
{
    private sealed record Line(DateTimeOffset Time, string Source, long Seq, string? Raw, LogEntry? Local);

    /// <exception cref="InvalidDataException">The upstream lines are not a response of this format (not JSON, or no <c>_meta</c> line).</exception>
    public static async Task MergeAsync(IReadOnlyList<string> upstreamLines, LogResult local, Stream output, CancellationToken cancellationToken)
    {
        var entries = new List<Line>();
        JsonElement? meta = null;
        JsonElement? summary = null;
        var documents = new List<JsonDocument>();

        try
        {
            foreach (var raw in upstreamLines)
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(raw);
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("The upstream log response contained a line that is not JSON.", ex);
                }

                documents.Add(document);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("The upstream log response contained a line that is not a JSON object.");
                }

                if (root.TryGetProperty("_meta", out var metaElement))
                {
                    meta = metaElement;
                }
                else if (root.TryGetProperty("_summary", out var summaryElement))
                {
                    summary = summaryElement;
                }
                else
                {
                    entries.Add(new Line(
                        root.TryGetProperty("ts", out var ts) && ts.TryGetDateTimeOffset(out var time) ? time : DateTimeOffset.MinValue,
                        root.TryGetProperty("source", out var source) ? source.GetString() ?? string.Empty : string.Empty,
                        root.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var number) ? number : 0,
                        raw,
                        null));
                }
            }

            if (meta is null)
            {
                throw new InvalidDataException("The upstream log response had no _meta line, so it cannot be combined.");
            }

            var upstreamReturned = meta.Value.TryGetProperty("returned", out var returned) && returned.TryGetInt32(out var count) ? count : 0;
            var generated = meta.Value.TryGetProperty("generated", out var generatedElement) && generatedElement.TryGetDateTimeOffset(out var at) ? at : local.GeneratedAtUtc;
            var upstreamSources = meta.Value.TryGetProperty("sources", out var sourcesElement) && sourcesElement.ValueKind == JsonValueKind.Array
                ? sourcesElement.EnumerateArray().ToList()
                : [];

            if (local.Summary)
            {
                LogNdjsonWriter.WriteLine(output, w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("_summary");
                    if (summary is { ValueKind: JsonValueKind.Array } upstreamSummary)
                    {
                        foreach (var item in upstreamSummary.EnumerateArray())
                        {
                            item.WriteTo(w);
                        }
                    }

                    foreach (var item in local.Summaries)
                    {
                        LogNdjsonWriter.WriteSourceSummary(w, item);
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                });
            }
            else
            {
                entries.AddRange(local.Entries.Select(e => new Line(e.TimestampUtc, e.Source, e.Seq, null, e)));
                entries.Sort(static (a, b) =>
                {
                    var byTime = a.Time.CompareTo(b.Time);
                    if (byTime != 0)
                    {
                        return byTime;
                    }

                    var bySource = string.CompareOrdinal(a.Source, b.Source);
                    return bySource != 0 ? bySource : a.Seq.CompareTo(b.Seq);
                });

                foreach (var line in entries)
                {
                    if (line.Raw is not null)
                    {
                        output.Write(Encoding.UTF8.GetBytes(line.Raw));
                        output.WriteByte((byte)'\n');
                    }
                    else
                    {
                        LogNdjsonWriter.WriteLine(output, w => LogNdjsonWriter.WriteEntry(w, line.Local!));
                    }
                }
            }

            var sourceWriters = upstreamSources.Select(s => (Action<Utf8JsonWriter>)(w => s.WriteTo(w)))
                .Concat(local.Sources.Select(s => (Action<Utf8JsonWriter>)(w => LogNdjsonWriter.WriteSourceMeta(w, s))));
            LogNdjsonWriter.WriteLine(output, w => LogNdjsonWriter.WriteMeta(w, generated, local.Summary, upstreamReturned + local.Entries.Count, sourceWriters));
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            foreach (var document in documents)
            {
                document.Dispose();
            }
        }
    }
}
