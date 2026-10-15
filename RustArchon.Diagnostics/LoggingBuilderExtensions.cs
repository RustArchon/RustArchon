// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics;

public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Captures every log entry at Debug or above into <paramref name="ring"/> and registers the ring for injection. A rule naming this provider
    /// takes precedence over the rules written for all providers, so the configured levels (which exist to keep the console readable) do not
    /// thin out what the diagnostic buffer holds. Framework chatter below Warning is still left out (<see cref="LogCaptureDefaults"/>), so a few seconds of
    /// SQL commands cannot push the entries that matter out of a small buffer.
    /// </summary>
    public static ILoggingBuilder AddDiagnosticRing(this ILoggingBuilder builder, LogRing ring, Func<string, LogLevel, bool>? exclude = null)
    {
        builder.Services.TryAddSingleton(ring);
        builder.AddProvider(new RingLoggerProvider(
            ring, exclude: (category, level) => LogCaptureDefaults.IsFrameworkNoise(category, level) || (exclude?.Invoke(category, level) ?? false)));
        builder.AddFilter<RingLoggerProvider>(category: null, LogLevel.Debug);
        return builder;
    }
}
