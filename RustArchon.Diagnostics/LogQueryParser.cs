// Copyright ©2026 Scott Blomfield

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics;

/// <summary>A request that parsed: the query and the sources it names.</summary>
public sealed record ParsedLogRequest(LogQuery Query, IReadOnlyList<string> Sources);

public sealed record LogQueryParseResult(ParsedLogRequest? Request, string? Error)
{
    public bool Ok => Request is not null;
}

/// <summary>
/// Turns the request's parameters into a <see cref="LogQuery"/> and a source list. Shared by everything that serves the endpoint (the Panel's door
/// and the Api behind it) so they accept exactly the same thing. Strict on purpose: an unknown parameter or a malformed value is refused with a message
/// naming it, because the caller is a script that cannot see that "levl=debug" silently returned the wrong thing.
/// </summary>
public static class LogQueryParser
{
    public static readonly IReadOnlyList<string> DefaultSources = ["api", "worker", "panel"];

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "sources", "level", "category", "from", "to", "since", "text", "prop", "count", "after", "mode",
    };

    public static LogQueryParseResult Parse(IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, DateTimeOffset nowUtc)
    {
        foreach (var key in parameters.Keys)
        {
            if (!Known.Contains(key))
            {
                return Fail($"Unknown parameter '{key}'. Known: {string.Join(", ", Known.Order())}.");
            }
        }

        var sources = DefaultSources;
        if (Values(parameters, "sources") is { } sourceValues)
        {
            var parsedSources = ParseSources(sourceValues, out var sourceError);
            if (sourceError is not null)
            {
                return Fail(sourceError);
            }

            sources = parsedSources;
        }

        var level = LogLevel.Information;
        if (Single(parameters, "level") is { } levelText && !TryParseLevel(levelText, out level))
        {
            return Fail($"Parameter 'level': '{levelText}' is not trace, debug, information, warning, error, critical or 0-5.");
        }

        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        if (Single(parameters, "from") is { } fromText)
        {
            if (!TryParseTime(fromText, out var parsed))
            {
                return Fail($"Parameter 'from': '{fromText}' is not an ISO 8601 date and time.");
            }

            from = parsed;
        }

        if (Single(parameters, "to") is { } toText)
        {
            if (!TryParseTime(toText, out var parsed))
            {
                return Fail($"Parameter 'to': '{toText}' is not an ISO 8601 date and time.");
            }

            to = parsed;
        }

        if (Single(parameters, "since") is { } sinceText)
        {
            if (from is not null)
            {
                return Fail("Parameters 'from' and 'since' cannot be used together.");
            }

            if (!TryParseSince(sinceText, out var span))
            {
                return Fail($"Parameter 'since': '{sinceText}' is not a whole number followed by s, m, h or d (for example 15m).");
            }

            from = nowUtc - span;
        }

        if (from is not null && to is not null && from > to)
        {
            return Fail("Parameter 'from' must not be after 'to'.");
        }

        var count = LogQuery.DefaultCount;
        if (Single(parameters, "count") is { } countText && (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1))
        {
            return Fail($"Parameter 'count': '{countText}' is not a positive whole number.");
        }

        var summary = false;
        if (Single(parameters, "mode") is { } modeText)
        {
            switch (modeText.ToLowerInvariant())
            {
                case "entries":
                    break;
                case "summary":
                    summary = true;
                    break;
                default:
                    return Fail($"Parameter 'mode': '{modeText}' is not entries or summary.");
            }
        }

        Dictionary<string, string>? properties = null;
        if (Values(parameters, "prop") is { } propValues)
        {
            properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in propValues)
            {
                var colon = value.IndexOf(':');
                if (colon < 1)
                {
                    return Fail($"Parameter 'prop': '{value}' is not Key:Value.");
                }

                properties[value[..colon]] = value[(colon + 1)..];
            }
        }

        Dictionary<string, SourceCursor>? after = null;
        if (Values(parameters, "after") is { } afterValues)
        {
            after = new Dictionary<string, SourceCursor>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in afterValues)
            {
                var parts = value.Split(':');
                if (parts.Length < 3
                    || !long.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
                    || string.IsNullOrEmpty(parts[^2]))
                {
                    return Fail($"Parameter 'after': '{value}' is not source:instance:sequence.");
                }

                var source = string.Join(':', parts[..^2]).ToLowerInvariant();
                if (source.Length == 0)
                {
                    return Fail($"Parameter 'after': '{value}' has no source name.");
                }

                after[source] = new SourceCursor(parts[^2], seq);
            }
        }

        var query = new LogQuery
        {
            MinLevel = level,
            Category = Single(parameters, "category"),
            FromUtc = from,
            ToUtc = to,
            Text = Single(parameters, "text"),
            Properties = properties,
            Count = count,
            After = after,
            Summary = summary,
        };
        return new LogQueryParseResult(new ParsedLogRequest(query, sources), null);
    }

    private static LogQueryParseResult Fail(string error) => new(null, error);

    private static IReadOnlyList<string>? Values(IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, string key) =>
        parameters.TryGetValue(key, out var values) ? values : null;

    private static string? Single(IReadOnlyDictionary<string, IReadOnlyList<string>> parameters, string key) =>
        parameters.TryGetValue(key, out var values) && values.Count > 0 ? values[^1] : null;

    private static List<string> ParseSources(IReadOnlyList<string> values, out string? error)
    {
        var result = new List<string>();
        error = null;
        foreach (var value in values)
        {
            foreach (var raw in value.Split(','))
            {
                var name = raw.Trim().ToLowerInvariant();
                if (name.Length == 0)
                {
                    error = "Parameter 'sources': an empty source name (check for a stray comma).";
                    return result;
                }

                if (name is not ("api" or "worker" or "panel") && !IsWorkerInstance(name))
                {
                    if (!name.StartsWith("plugin:", StringComparison.Ordinal) || !Guid.TryParse(name["plugin:".Length..], out var serverId))
                    {
                        error = $"Parameter 'sources': '{raw.Trim()}' is not api, worker, worker:<instance>, panel or plugin:<server id>.";
                        return result;
                    }

                    name = "plugin:" + serverId.ToString("D");
                }

                if (!result.Contains(name))
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    // One Worker instance by the name the response gave it (worker:<first characters of its id>), which is how a paging caller narrows to what it is following.
    private static bool IsWorkerInstance(string name) =>
        name.StartsWith("worker:", StringComparison.Ordinal) && name.Length is > 7 and <= 7 + 32 && IsLowerHexTail(name.AsSpan(7));

    private static bool IsLowerHexTail(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseLevel(string text, out LogLevel level)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "trace": level = LogLevel.Trace; return true;
            case "debug": level = LogLevel.Debug; return true;
            case "info" or "information": level = LogLevel.Information; return true;
            case "warn" or "warning": level = LogLevel.Warning; return true;
            case "err" or "error": level = LogLevel.Error; return true;
            case "critical": level = LogLevel.Critical; return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 5)
        {
            level = (LogLevel)number;
            return true;
        }

        level = default;
        return false;
    }

    private static bool TryParseTime(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private static bool TryParseSince(string text, out TimeSpan span)
    {
        span = default;
        if (text.Length < 2 || !long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount > 10_000)
        {
            return false;
        }

        switch (text[^1])
        {
            case 's': span = TimeSpan.FromSeconds(amount); return true;
            case 'm': span = TimeSpan.FromMinutes(amount); return true;
            case 'h': span = TimeSpan.FromHours(amount); return true;
            case 'd': span = TimeSpan.FromDays(amount); return true;
            default: return false;
        }
    }
}
