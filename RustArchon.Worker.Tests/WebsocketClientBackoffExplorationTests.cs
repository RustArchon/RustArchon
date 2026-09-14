// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Websocket.Client;

namespace RustArchon.Worker.Tests;

/// <summary>
/// A regression guard for the assumption <c>RustWebRconClient</c>'s reconnect backoff (see its own
/// Socket_OnClose/Socket_OnOpen remarks) depends on: that <see cref="WebsocketClient.ErrorReconnectTimeout"/>
/// is read fresh each time the library schedules its next reconnect attempt, not captured once and
/// reused. Tests the raw library directly, independent of RustWebRconClient, so a future
/// Websocket.Client version that changed this would fail here first, distinctly from the
/// RustWebRconClient-level tests failing for a less obvious reason. Points at a closed local port so
/// every "connection" fails immediately and consistently.
/// </summary>
public class WebsocketClientBackoffExplorationTests
{
    [Fact]
    public async Task MutatingErrorReconnectTimeoutFromDisconnectionHappenedChangesTheNextWait()
    {
        var uri = new Uri("ws://127.0.0.1:1/nothing-listens-here");
        using var client = new WebsocketClient(uri)
        {
            ErrorReconnectTimeout = TimeSpan.FromSeconds(1),
            ReconnectTimeout = null
        };

        var current = TimeSpan.FromSeconds(1);
        var maxAttempts = 4;
        var timestamps = new List<DateTimeOffset>();

        using var sub = client.DisconnectionHappened.Subscribe(_ =>
        {
            timestamps.Add(DateTimeOffset.UtcNow);
            // Double it, same shape the real backoff will use.
            current = current * 2;
            client.ErrorReconnectTimeout = current;
        });

        // Start(), not StartOrFail() - matches RustWebRconClient's own usage. StartOrFail() throws on
        // the very first failed attempt instead of handing off to the library's own reconnect loop.
        var startTask = client.Start();
        await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(5)));

        // Let it fail and retry a handful of times, then give up waiting - no Stop() call, since a
        // client that never once connected may not shut down cleanly; the `using` disposal handles it.
        // Generous deadline: base 1s doubling to 2s/4s/8s, plus each attempt's own connect-failure
        // overhead (empirically ~2.3s against a closed port on this machine) on top of every gap.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while (timestamps.Count < maxAttempts && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.True(timestamps.Count >= maxAttempts,
            $"Expected at least {maxAttempts} disconnection events, got {timestamps.Count}.");

        // Gaps between consecutive attempts should grow roughly the way ErrorReconnectTimeout was
        // mutated (1s -> 2s -> 4s waits), plus a roughly constant per-attempt connect-failure overhead.
        // Comparing by difference, not ratio, since that fixed overhead would otherwise swamp a
        // ratio-based check - if the library ignored the mutation, every gap would stay flat (just the
        // constant overhead, no growth); if it's reading the mutated value, each gap should be at least
        // ~500ms larger than the last (real wall-clock timing, so a generous margin under the ~1s step).
        for (var i = 1; i < maxAttempts - 1; i++)
        {
            var gap = (timestamps[i + 1] - timestamps[i]).TotalMilliseconds;
            var previousGap = (timestamps[i] - timestamps[i - 1]).TotalMilliseconds;

            Assert.True(gap > previousGap + 500,
                $"Attempt {i + 1}: gap {gap}ms was not meaningfully larger than the previous gap {previousGap}ms - " +
                "ErrorReconnectTimeout mutation doesn't appear to affect subsequent scheduling.");
        }
    }
}
