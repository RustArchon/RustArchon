// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Rcon;
using RustArchon.Rcon.EventArgs;

namespace RustArchon.Worker.Tests;

/// <summary>
/// Tests for <see cref="RustWebRconClient"/>'s reconnect backoff - a real, self-inflicted problem this
/// fixes: a fixed 10s retry with no backoff hammering a real Rust server through many hours of a
/// debugging session is suspected of having tripped some rate/abuse detection on that server's host.
/// See the class's own remarks on why mutating <c>Websocket.Client.WebsocketClient.ErrorReconnectTimeout</c>
/// from a disconnection handler is safe here - confirmed by hand (see
/// <see cref="WebsocketClientBackoffExplorationTests"/>) that the library reads it fresh on every
/// reconnect it schedules, not once at construction.
/// </summary>
public class RustWebRconClientBackoffTests
{
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task ConsecutiveFailuresDoubleTheDelayUpToTheCap()
    {
        var port = GetFreeTcpPort();
        var timestamps = new List<DateTimeOffset>();

        using var client = new RustWebRconClient(
            "Test", "127.0.0.1", port, "password",
            errorReconnectTimeout: TimeSpan.FromSeconds(1),
            maxErrorReconnectTimeout: TimeSpan.FromSeconds(10));

        client.ConnectionChanged += (_, args) =>
        {
            if (!args.IsConnected)
            {
                timestamps.Add(DateTimeOffset.UtcNow);
            }
        };

        client.Connect(out _);

        // Nothing listens on this port at all - every attempt fails immediately at the TCP level. Cap
        // set well above 1s*2*2*2=8s so all three measured gaps (reflecting the 2s/4s/8s escalation
        // steps - each Socket_OnClose escalates the interval used for the *next* attempt, so the very
        // first gap already reflects the second step, not the base) land below the cap with room to
        // keep growing distinctly, rather than plateauing partway through like a too-low cap would.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while (timestamps.Count < 4 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.True(timestamps.Count >= 4, $"Expected at least 4 failures, got {timestamps.Count}.");

        // 1s -> 2s -> 4s (capped) - each gap meaningfully larger than the last, up to the cap.
        var gap1 = (timestamps[1] - timestamps[0]).TotalMilliseconds;
        var gap2 = (timestamps[2] - timestamps[1]).TotalMilliseconds;
        var gap3 = (timestamps[3] - timestamps[2]).TotalMilliseconds;

        Assert.True(gap2 > gap1 + 500, $"gap2 ({gap2}ms) should be meaningfully larger than gap1 ({gap1}ms).");
        Assert.True(gap3 > gap2 + 500, $"gap3 ({gap3}ms) should be meaningfully larger than gap2 ({gap2}ms).");
    }

    [Fact]
    public async Task ABackoffResetsToTheBaseIntervalAfterARealSuccessfulConnection()
    {
        var port = GetFreeTcpPort();
        var connectedEvents = new List<DateTimeOffset>();
        var disconnectedEvents = new List<DateTimeOffset>();

        // A minimal WebSocket server this test fully controls: doesn't listen at all for the first
        // stretch (letting the client fail-and-back-off a couple of times), then starts accepting
        // (letting one connection through and immediately closing it), then stops again - so the test
        // can check whether the failure right after that success used the base interval or stayed
        // elevated from the earlier failures.
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        using var client = new RustWebRconClient(
            "Test", "127.0.0.1", port, "password",
            errorReconnectTimeout: TimeSpan.FromSeconds(1),
            maxErrorReconnectTimeout: TimeSpan.FromSeconds(30));

        client.ConnectionChanged += (_, args) =>
        {
            if (args.IsConnected)
            {
                connectedEvents.Add(DateTimeOffset.UtcNow);
            }
            else
            {
                disconnectedEvents.Add(DateTimeOffset.UtcNow);
            }
        };

        client.Connect(out _);

        // Let it fail (and back off) a couple of times against a port nothing is listening on yet.
        var backoffDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (disconnectedEvents.Count < 2 && DateTimeOffset.UtcNow < backoffDeadline)
        {
            await Task.Delay(200);
        }
        Assert.True(disconnectedEvents.Count >= 2, "Expected at least 2 failures before the server ever started.");

        // Now start accepting - one connection through, closed immediately after the handshake.
        listener.Start();
        using var acceptCts = new CancellationTokenSource();
        var acceptTask = Task.Run(async () =>
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(acceptCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                await Task.Delay(200, acceptCts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                await wsContext.WebSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
            }
        }, acceptCts.Token);

        var connectDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (connectedEvents.Count < 1 && DateTimeOffset.UtcNow < connectDeadline)
        {
            await Task.Delay(200);
        }
        Assert.True(connectedEvents.Count >= 1, "Expected the client to connect successfully at least once.");

        listener.Stop();
        acceptCts.Cancel();

        var disconnectedBeforeSuccess = disconnectedEvents.Count;
        var successTime = connectedEvents[0];

        // The failure right after the success should follow shortly - at roughly the base interval
        // (1s), not the elevated one the earlier failures had built up to.
        var afterSuccessDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (disconnectedEvents.Count <= disconnectedBeforeSuccess && DateTimeOffset.UtcNow < afterSuccessDeadline)
        {
            await Task.Delay(200);
        }
        Assert.True(disconnectedEvents.Count > disconnectedBeforeSuccess,
            "Expected another failure after the server stopped accepting again.");

        var gapAfterSuccess = (disconnectedEvents[disconnectedBeforeSuccess] - successTime).TotalMilliseconds;

        // Generous upper bound: if the reset didn't happen, this would still be sitting at whatever the
        // pre-success backoff had grown to (4s+ after two doublings from a 1s base) - well past 3s.
        Assert.True(gapAfterSuccess < 3000,
            $"Gap after the successful connection was {gapAfterSuccess}ms - backoff doesn't appear to have reset to the base interval.");
    }
}
