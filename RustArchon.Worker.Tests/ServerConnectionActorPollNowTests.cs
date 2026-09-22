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
using RustArchon.Worker.Configuration;
using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// <see cref="ServerConnectionActor.PollNowAsync"/> - the on-demand poll behind <see cref="PollServerNow"/>, used so a person pressing Refresh (or the
/// Api, right after an update settles) sees the server's true state within seconds instead of waiting out the plugin-list poll's five-minute schedule.
/// Runs a real actor against a real local WebSocket server this test fully controls, the same way <see cref="ServerConnectionActorInteractivityTests"/>
/// does - not a mocked RCON client, so this exercises the real round trip.
/// </summary>
public class ServerConnectionActorPollNowTests
{
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A minimal WebRCON-shaped server: accepts one connection, echoes every request back. Good enough for the plugin-list probe (both
    /// c.plugins and o.plugins get a reply neither parser recognizes, so the poll succeeds with an empty, framework-less list) without needing a
    /// semantically real plugin list - what these tests are about is whether the poll runs and publishes at all, not what it finds.</summary>
    private static async Task RunEchoServerAsync(HttpListener listener, CancellationToken cancellationToken)
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

        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        var buffer = new byte[16 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                var identifier = doc.RootElement.GetProperty("Identifier").GetInt32();
                var message = doc.RootElement.GetProperty("Message").GetString() ?? string.Empty;

                var responseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    Identifier = identifier, Message = $"echo:{message}", Type = "Generic", Stacktrace = string.Empty
                }));
                await socket.SendAsync(responseBytes, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
            // The client disposed mid-receive when the test tears down - nothing left to serve.
        }
    }

    private static readonly ReconnectOptions LongLived = new() { ReconnectTimeout = TimeSpan.FromMinutes(5), ErrorReconnectTimeout = TimeSpan.FromMinutes(5) };

    [Fact]
    public async Task APollOfAConnectedServerPublishesThePluginListAndReportsConnected()
    {
        var port = GetFreeTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var serverCts = new CancellationTokenSource();
        var serverTask = RunEchoServerAsync(listener, serverCts.Token);

        var publishEndpoint = new Mock<IPublishEndpoint>();
        await using var actor = new ServerConnectionActor(
            Guid.NewGuid(), Guid.NewGuid(), "127.0.0.1", port, "unused-password",
            Guid.NewGuid(), publishEndpoint.Object, LongLived, NullLogger<ServerConnectionActor>.Instance);

        await WaitForConnectionAsync();

        var connected = await actor.PollNowAsync([PollServerNowKinds.Plugins], CancellationToken.None);

        serverCts.Cancel();
        listener.Stop();
        await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(connected);
        publishEndpoint.Verify(p => p.Publish(It.IsAny<ServerPluginsCaptured>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ARequestForNothingRecognizedAsksTheServerNothing()
    {
        var port = GetFreeTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var serverCts = new CancellationTokenSource();
        var serverTask = RunEchoServerAsync(listener, serverCts.Token);

        var publishEndpoint = new Mock<IPublishEndpoint>();
        await using var actor = new ServerConnectionActor(
            Guid.NewGuid(), Guid.NewGuid(), "127.0.0.1", port, "unused-password",
            Guid.NewGuid(), publishEndpoint.Object, LongLived, NullLogger<ServerConnectionActor>.Instance);

        await WaitForConnectionAsync();

        // "updates" before any archon.hello has ever run leaves the updates poll disabled (fail closed - see
        // ServerConnectionActor._updatesPollEnabled's remarks), so this asks for nothing at all.
        var connected = await actor.PollNowAsync([PollServerNowKinds.Updates], CancellationToken.None);

        serverCts.Cancel();
        listener.Stop();
        await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.False(connected);
        publishEndpoint.Verify(p => p.Publish(It.IsAny<ServerPluginsCaptured>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task APollOfAServerThatIsNotConnectedReportsNotConnectedAndPublishesNothing()
    {
        var port = GetFreeTcpPort(); // nothing is listening on it

        var publishEndpoint = new Mock<IPublishEndpoint>();
        await using var actor = new ServerConnectionActor(
            Guid.NewGuid(), Guid.NewGuid(), "127.0.0.1", port, "unused-password",
            Guid.NewGuid(), publishEndpoint.Object, LongLived, NullLogger<ServerConnectionActor>.Instance);

        var connected = await actor.PollNowAsync([PollServerNowKinds.Plugins], CancellationToken.None);

        Assert.False(connected);
        publishEndpoint.Verify(p => p.Publish(It.IsAny<ServerPluginsCaptured>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnUnknownPollNameIsSimplyIgnored()
    {
        var port = GetFreeTcpPort();
        var publishEndpoint = new Mock<IPublishEndpoint>();
        await using var actor = new ServerConnectionActor(
            Guid.NewGuid(), Guid.NewGuid(), "127.0.0.1", port, "unused-password",
            Guid.NewGuid(), publishEndpoint.Object, LongLived, NullLogger<ServerConnectionActor>.Instance);

        var connected = await actor.PollNowAsync(["something-nobody-asked-for"], CancellationToken.None);

        Assert.False(connected);
    }

    private static async Task WaitForConnectionAsync()
    {
        // Same fixed settle as ServerConnectionActorInteractivityTests - the socket needs a moment to actually establish.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }
}
