// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RustArchon.Rcon;

namespace RustArchon.Worker.Tests;

/// <summary>
/// What comes back on <see cref="RustWebRconClient.MessageReceived"/> as <c>UserData</c> when the server answers a command after the
/// caller stopped waiting for it. A busy server (a save stalling the console for longer than the Worker's 8 second timeout) does that,
/// and the Worker files any reply with no context as something a person typed - which put empty background polls in the Console tab.
/// Runs against a real socket: a fake WebRCON server the test controls, replying exactly when it decides to.
/// </summary>
public class RustWebRconClientLateReplyTests : IAsyncLifetime
{
    private sealed class Marker(string name)
    {
        public override string ToString() => name;
    }

    /// <summary>A minimal WebRCON server: records what it is asked and answers only when told to.</summary>
    private sealed class FakeRconServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private WebSocket? _socket;

        public FakeRconServer()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        }

        public int Port { get; }

        public ConcurrentQueue<(int Identifier, string Message)> Received { get; } = new();

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    var accepted = await context.AcceptWebSocketAsync(null);
                    _socket = accepted.WebSocket;

                    var buffer = new byte[8192];
                    while (!_stop.IsCancellationRequested && _socket.State == WebSocketState.Open)
                    {
                        var text = new StringBuilder();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _socket.ReceiveAsync(buffer, _stop.Token);
                            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        using var request = JsonDocument.Parse(text.ToString());
                        Received.Enqueue((request.RootElement.GetProperty("Identifier").GetInt32(), request.RootElement.GetProperty("Message").GetString() ?? ""));
                    }
                }
                catch (Exception)
                {
                    // The test finished, or the client went away - nothing to report from a fake.
                }
            });
        }

        public async Task ReplyAsync(int identifier, string message = "late")
        {
            var json = JsonSerializer.Serialize(new { Message = message, Identifier = identifier, Type = "Generic", Stacktrace = "" });
            await _socket!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
        }
    }

    private readonly FakeRconServer _server = new();
    private readonly RustWebRconClient _client;
    private readonly ConcurrentQueue<(int Identifier, object? UserData)> _seen = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RustWebRconClientLateReplyTests()
    {
        _client = new RustWebRconClient("Test", "127.0.0.1", _server.Port, "password", errorReconnectTimeout: TimeSpan.FromSeconds(1));
        _client.ConnectionChanged += (_, args) =>
        {
            if (args.IsConnected)
            {
                _connected.TrySetResult();
            }
        };
        _client.MessageReceived += (_, args) => _seen.Enqueue((args.Response.Identifier, args.UserData));
    }

    public async Task InitializeAsync()
    {
        _server.Start();
        _client.Connect(out _);
        await _connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        return Task.CompletedTask;
    }

    private async Task<int> IdentifierOfAsync(string command, int skip = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var match = _server.Received.Where(r => r.Message == command).Skip(skip).Select(r => (int?)r.Identifier).FirstOrDefault();
            if (match is { } id)
            {
                return id;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"The fake server never received '{command}'.");
    }

    private async Task<object?> UserDataOfReplyAsync(int identifier, int occurrence = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var matches = _seen.Where(s => s.Identifier == identifier).ToList();
            if (matches.Count > occurrence)
            {
                return matches[occurrence].UserData;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"No reply with identifier {identifier} was broadcast.");
    }

    [Fact]
    public async Task ALateReplyStillCarriesTheContextOfTheCommandThatTimedOut()
    {
        var context = new Marker("background poll");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            _client.SendCommandAsync("test.slow", TimeSpan.FromMilliseconds(200), userData: context));
        var identifier = await IdentifierOfAsync("test.slow");

        await _server.ReplyAsync(identifier); // the caller gave up long ago

        Assert.Same(context, await UserDataOfReplyAsync(identifier));
    }

    [Fact]
    public async Task ALateReplyIsRecognisedOnceOnly()
    {
        var context = new Marker("background poll");
        await Assert.ThrowsAsync<TimeoutException>(() =>
            _client.SendCommandAsync("test.slow", TimeSpan.FromMilliseconds(200), userData: context));
        var identifier = await IdentifierOfAsync("test.slow");

        await _server.ReplyAsync(identifier);
        await _server.ReplyAsync(identifier);

        Assert.Same(context, await UserDataOfReplyAsync(identifier, occurrence: 0));
        Assert.Null(await UserDataOfReplyAsync(identifier, occurrence: 1));
    }

    [Fact]
    public async Task AReplyInTimeCarriesItsContextAsBefore()
    {
        var context = new Marker("typed by a person");

        var sending = _client.SendCommandAsync("test.quick", TimeSpan.FromSeconds(5), userData: context);
        await _server.ReplyAsync(await IdentifierOfAsync("test.quick"), "done");
        var response = await sending;

        Assert.Equal("done", response.Message);
        Assert.Same(context, await UserDataOfReplyAsync(response.Identifier));
    }

    [Fact]
    public async Task ACommandSentWithNoContextStaysWithoutOneEvenWhenItsReplyIsLate()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => _client.SendCommandAsync("test.slow", TimeSpan.FromMilliseconds(200)));
        var identifier = await IdentifierOfAsync("test.slow");

        await _server.ReplyAsync(identifier);

        Assert.Null(await UserDataOfReplyAsync(identifier));
    }

    [Fact]
    public async Task AnUnsolicitedFrameStillHasNoContext()
    {
        // An identifier this client never issued: what the game sends for its own console lines.
        await _server.ReplyAsync(987_654, "Saving complete");

        Assert.Null(await UserDataOfReplyAsync(987_654));
    }

    [Fact]
    public async Task ACancelledCommandsLateReplyIsRecognisedToo()
    {
        var context = new Marker("background poll");
        using var cts = new CancellationTokenSource();

        var sending = _client.SendCommandAsync("test.cancelled", TimeSpan.FromSeconds(30), cts.Token, context);
        var identifier = await IdentifierOfAsync("test.cancelled");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

        await _server.ReplyAsync(identifier);

        Assert.Same(context, await UserDataOfReplyAsync(identifier));
    }

    /// <summary>A server that never answers must not make the client remember every command it ever timed out.</summary>
    [Fact]
    public async Task TheMemoryOfTimedOutCommandsIsBounded()
    {
        const int Cap = 256;
        var context = new Marker("background poll");

        for (var i = 0; i < Cap + 20; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                _client.SendCommandAsync("test.never", TimeSpan.FromMilliseconds(1), userData: context));
        }

        var first = await IdentifierOfAsync("test.never", skip: 0);
        var beyondTheCap = await IdentifierOfAsync("test.never", skip: Cap + 10);
        await _server.ReplyAsync(first);
        await _server.ReplyAsync(beyondTheCap);

        Assert.Same(context, await UserDataOfReplyAsync(first));
        Assert.Null(await UserDataOfReplyAsync(beyondTheCap));
    }
}
