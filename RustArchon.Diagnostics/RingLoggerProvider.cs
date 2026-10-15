// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics;

/// <summary>
/// Writes every log entry at Debug or above into a <see cref="LogRing"/>, whatever the console shows, redacting secrets before they are
/// stored. Add it next to the normal providers; it never writes anywhere else.
/// </summary>
public sealed class RingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    public const int MaxMessageLength = 4096;

    public const int MaxExceptionLength = 8192;

    public const int MaxPropertyLength = 512;

    // Work done on any one field is bounded before the (regex) redaction runs.
    private const int RedactionInputLimit = 65536;

    private const string TruncatedSuffix = " ...[truncated]";

    private readonly LogRing _ring;
    private readonly TimeProvider _time;
    private readonly Func<string, LogLevel, bool>? _exclude;
    private IExternalScopeProvider? _scopes;

    /// <param name="ring">Where entries go.</param>
    /// <param name="timeProvider">Clock for entry timestamps; the system clock when null.</param>
    /// <param name="exclude">Categories and levels to leave out, for example the message bus's own chatter on a process that forwards its logs over that bus.</param>
    public RingLoggerProvider(LogRing ring, TimeProvider? timeProvider = null, Func<string, LogLevel, bool>? exclude = null)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _time = timeProvider ?? TimeProvider.System;
        _exclude = exclude;
    }

    public ILogger CreateLogger(string categoryName) => new RingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    internal static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var input = text.Length > RedactionInputLimit ? text[..RedactionInputLimit] : text;
        var redacted = LogRedactor.Redact(input);
        return redacted.Length > maxLength || text.Length > RedactionInputLimit ? redacted[..Math.Min(redacted.Length, maxLength)] + TruncatedSuffix : redacted;
    }

    private static string CleanProperty(string key, object? value)
    {
        var text = value?.ToString() ?? string.Empty;

        // The name may be what marks the value as secret (a property called Password), so redact them together and keep only the value part.
        var redacted = Clean(key + "=" + text, MaxPropertyLength + key.Length + 1);
        return redacted.StartsWith(key + "=", StringComparison.Ordinal) ? redacted[(key.Length + 1)..] : redacted;
    }

    private sealed class RingLogger(RingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Debug && logLevel != LogLevel.None && (owner._exclude is null || !owner._exclude(category, logLevel));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message;
            try
            {
                message = formatter(state, exception);
            }
            catch (Exception formatterFailure)
            {
                // A broken format string must not take the caller down, and must not vanish: it is recorded as the message.
                message = "[log formatter threw " + formatterFailure.GetType().Name + ": " + formatterFailure.Message + "]";
            }

            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key != "{OriginalFormat}")
                    {
                        properties[pair.Key] = CleanProperty(pair.Key, pair.Value);
                    }
                }
            }

            owner._scopes?.ForEachScope(
                static (scope, props) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs)
                    {
                        foreach (var pair in scopePairs)
                        {
                            if (pair.Key != "{OriginalFormat}" && !props.ContainsKey(pair.Key))
                            {
                                props[pair.Key] = CleanProperty(pair.Key, pair.Value);
                            }
                        }
                    }
                },
                properties);

            owner._ring.Append(
                owner._time.GetUtcNow(),
                logLevel,
                category,
                Clean(message, MaxMessageLength),
                exception is null ? null : Clean(exception.ToString(), MaxExceptionLength),
                properties);
        }
    }
}
