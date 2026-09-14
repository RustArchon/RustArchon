// Copyright ©2026 Scott Blomfield

using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Messaging.Contracts;
using RustArchon.Rcon;
using RustArchon.Worker.Configuration;
using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// End-to-end regression test for the recurring "a backend-initiated command showed up in the Panel's
/// Console tab" bug - see <see cref="RconCommandContext"/>'s remarks for the full incident. Runs a real
/// <see cref="ServerConnectionActor"/> against a real local WebSocket server this test fully controls
/// (not a mocked <c>RustWebRconClient</c>), so this actually exercises the real chain: userData
/// attached to a command, carried through a real socket round trip, and
/// <see cref="ServerConnectionActor.OnRawMessageReceived"/> carrying <c>Interactive</c> through onto
/// every published <see cref="RconFrameCaptured"/> - the one and only source the Panel's Console tab is
/// ever populated from. Every response is published now, interactive or not (see
/// <c>RconFrameCaptured</c>'s remarks for why suppression moved out of this layer entirely) - what this
/// test actually guards is that <see cref="RconFrameCaptured.Interactive"/> still carries the right
/// value through, since that's what every read/delivery boundary downstream relies on to keep a
/// background row away from an ordinary user.
/// </summary>
public class ServerConnectionActorInteractivityTests
{
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A minimal WebRCON-shaped server: accepts one connection, then echoes every request back as a
    /// response carrying the same Identifier and a recognizable Message, for as long as the test runs.
    /// Good enough to answer mod-framework auto-detection, the heartbeat-adjacent poll loops, and this
    /// test's own two commands - none of which need a real, semantically-correct WebRCON reply, just
    /// *a* reply so RustWebRconClient's pending-command wait resolves instead of timing out.
    /// </summary>
    private static async Task RunEchoServerAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var wsContext = await context.AcceptWebSocketAsync(null);
            var socket = wsContext.WebSocket;
            var buffer = new byte[16 * 1024];

            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                    var identifier = doc.RootElement.GetProperty("Identifier").GetInt32();
                    var message = doc.RootElement.GetProperty("Message").GetString() ?? string.Empty;

                    var responseJson = JsonSerializer.Serialize(new
                    {
                        Identifier = identifier,
                        Message = $"echo:{message}",
                        Type = "Generic",
                        Stacktrace = string.Empty
                    });
                    var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    await socket.SendAsync(responseBytes, WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
                // Client disposed/disconnected while we were mid-receive - nothing left to serve.
            }

            return;
        }
    }

    [Fact]
    public async Task InteractiveAndBackgroundCommandsBothPublishRconFrameCapturedWithTheRightInteractiveFlag()
    {
        var port = GetFreeTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var serverCts = new CancellationTokenSource();
        var serverTask = RunEchoServerAsync(listener, serverCts.Token);

        var publishEndpoint = new Mock<IPublishEndpoint>();
        // A long ReconnectTimeout/ErrorReconnectTimeout - this test only needs the one initial
        // connection to hold for its own short duration, not to exercise reconnect behavior (that's
        // RustWebRconClientBackoffTests' job).
        var reconnectOptions = new ReconnectOptions
        {
            ReconnectTimeout = TimeSpan.FromMinutes(5),
            ErrorReconnectTimeout = TimeSpan.FromMinutes(5)
        };

        await using var actor = new ServerConnectionActor(
            Guid.NewGuid(), Guid.NewGuid(), "127.0.0.1", port, "unused-password",
            Guid.NewGuid(), publishEndpoint.Object, reconnectOptions, NullLogger<ServerConnectionActor>.Instance);

        // Give the socket a moment to actually establish before sending anything through it.
        var connectDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < connectDeadline)
        {
            await Task.Delay(100);
        }

        var interactive = await actor.SendCommandAsync(
            "test.interactive.marker", TimeSpan.FromSeconds(5), CancellationToken.None,
            new RconCommandContext(Interactive: true));
        Assert.True(interactive.Success, $"Interactive command did not succeed: {interactive.Error}");

        var background = await actor.SendCommandAsync(
            "test.background.marker", TimeSpan.FromSeconds(5), CancellationToken.None,
            RconCommandContext.Background);
        Assert.True(background.Success, $"Background command did not succeed: {background.Error}");

        serverCts.Cancel();
        listener.Stop();
        await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(5)));

        // Both responses are published now - the one thing this design still guarantees is that each
        // carries the right Interactive flag, since that's what every downstream read/delivery boundary
        // (RustServersController.GetEvents, RconHub's group split) actually relies on to keep a
        // background row away from an ordinary user. A regression here - an interactive command's
        // response coming through marked Interactive: false, or vice versa - is exactly the shape of
        // bug this whole architecture exists to catch before it reaches a real customer.
        publishEndpoint.Verify(
            p => p.Publish(
                It.Is<RconFrameCaptured>(f =>
                    f.Message.Contains("test.interactive.marker")
                    && f.Direction == RconEventDirection.Received
                    && f.Interactive),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        publishEndpoint.Verify(
            p => p.Publish(
                It.Is<RconFrameCaptured>(f =>
                    f.Message.Contains("test.background.marker")
                    && f.Direction == RconEventDirection.Received
                    && !f.Interactive),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // The Sent side of the interactive command specifically - see ServerConnectionActor.SendCommandAsync's
        // remarks on why this publishes before the response is even awaited, with Identifier: 0.
        publishEndpoint.Verify(
            p => p.Publish(
                It.Is<RconFrameCaptured>(f =>
                    f.Message == "test.interactive.marker"
                    && f.Direction == RconEventDirection.Sent
                    && f.Interactive),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
