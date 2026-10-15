// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RustArchon.Api.Tests;

/// <summary>Keeps what is logged, so a test can check that a decision was said, with the facts a reader needs.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
