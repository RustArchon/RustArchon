// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using RustArchon.Api.Data;
using RustArchon.Api.DiagnosticViews.Views;
using RustArchon.Messaging.Contracts;
using static RustArchon.Api.Tests.DiagnosticViewKit;

namespace RustArchon.Api.Tests;

/// <summary>
/// The <c>server-rcon-recent</c> diagnostic view: the latest console frames for a server, including the ones the Api and the plugin sent themselves. Secrets in
/// the text are redacted; chat, names, Steam IDs and IP addresses are not (the owner's decision: they are not secret and the Console tab shows them).
/// </summary>
public class ServerRconRecentViewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ServerRconRecentView Create() => new(new ApiDbContext(postgres.Options));

    private async Task AddFrameAsync(Seeded seeded, string message, DateTimeOffset at, bool interactive = false, string type = "Generic", string? stacktrace = null)
    {
        await using var context = new ApiDbContext(postgres.Options);
        context.RconEvents.Add(new RconEvent
        {
            TenantId = seeded.TenantId, RustServerId = seeded.ServerId, CapturedAtUtc = at, Identifier = 7, Type = type, Message = message, Stacktrace = stacktrace,
            Interactive = interactive, Direction = RconEventDirection.Received
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task TheLatestFramesAreReturnedOldestFirstIncludingTheOnesNobodyTypedAndTheirFlags()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddFrameAsync(seeded, "{\"v\":1,\"ok\":true}", Now.AddMinutes(-3), interactive: false);
        await AddFrameAsync(seeded, "playerlist output", Now.AddMinutes(-2), interactive: true);
        await AddFrameAsync(seeded, "newest", Now.AddMinutes(-1), interactive: false);

        var (status, json) = await RunAsync(Create(), ServerRconRecentView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.Equal(200, status);
        var frames = json.GetProperty("data").GetProperty("frames").EnumerateArray().ToList();
        Assert.Equal(["{\"v\":1,\"ok\":true}", "playerlist output", "newest"], frames.Select(f => f.GetProperty("message").GetString()));
        Assert.Equal([false, true, false], frames.Select(f => f.GetProperty("interactive").GetBoolean()));
        Assert.Equal("Received", frames[0].GetProperty("direction").GetString());
        Assert.Equal("Generic", frames[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task OnlyTheLatestFramesUpToTheRequestedCountAreReturned()
    {
        var seeded = await SeedServerAsync(postgres);
        for (var i = 0; i < 10; i++)
        {
            await AddFrameAsync(seeded, $"frame {i}", Now.AddMinutes(-20 + i));
        }

        var (_, json) = await RunAsync(Create(), ServerRconRecentView.Definition, ("serverId", seeded.ServerId.ToString()), ("count", "3"));

        Assert.Equal(["frame 7", "frame 8", "frame 9"], json.GetProperty("data").GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task ACountOverTheLimitIsHeldToTheLimit()
    {
        var seeded = await SeedServerAsync(postgres);
        for (var i = 0; i < ServerRconRecentView.MaxFrames + 5; i++)
        {
            await AddFrameAsync(seeded, $"f{i}", Now.AddMinutes(-1000 + i));
        }

        var (_, json) = await RunAsync(Create(), ServerRconRecentView.Definition, ("serverId", seeded.ServerId.ToString()), ("count", "5000"));

        Assert.Equal(ServerRconRecentView.MaxFrames, json.GetProperty("data").GetProperty("frames").GetArrayLength());
    }

    [Fact]
    public async Task ASecretInAFrameIsRedactedButChatNamesSteamIdsAndIpAddressesAreNot()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddFrameAsync(seeded, "rcon login failed: password=hunter2 Authorization: Bearer abcdefghijklmnop.qrstuvwxyz", Now.AddMinutes(-2));
        await AddFrameAsync(seeded, "[CHAT] Scott (76561198000000000): meet me at 192.0.2.7 please", Now.AddMinutes(-1), type: "Chat", interactive: true);

        var (_, json) = await RunAsync(Create(), ServerRconRecentView.Definition, ("serverId", seeded.ServerId.ToString()));

        var text = Text(json);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abcdefghijklmnop", text);
        Assert.Contains("Scott", text);
        Assert.Contains("76561198000000000", text);
        Assert.Contains("192.0.2.7", text);
        Assert.Contains("meet me at", text);
    }

    [Fact]
    public async Task ASecretInAStackTraceIsRedactedToo()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddFrameAsync(seeded, "boom", Now.AddMinutes(-1), stacktrace: "at Login(password=hunter2)");

        var (_, json) = await RunAsync(Create(), ServerRconRecentView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.DoesNotContain("hunter2", Text(json));
    }

    [Fact]
    public async Task ALongMessageIsCutAndSaysSo()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddFrameAsync(seeded, new string('x', ServerRconRecentView.MaxMessageCharacters + 500), Now.AddMinutes(-1));
        await AddFrameAsync(seeded, "short", Now);

        var (_, json) = await RunAsync(Create(), ServerRconRecentView.Definition, ("serverId", seeded.ServerId.ToString()));

        var frames = json.GetProperty("data").GetProperty("frames").EnumerateArray().ToList();
        Assert.True(frames[0].GetProperty("truncated").GetBoolean());
        Assert.Equal(ServerRconRecentView.MaxMessageCharacters, frames[0].GetProperty("message").GetString()!.Length);
        Assert.False(frames[1].GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task AServerWithNoFramesYetSaysSoNotAnEmptyAnswer()
    {
        var seeded = await SeedServerAsync(postgres);

        var (status, json) = await RunAsync(Create(), ServerRconRecentView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.Equal(200, status);
        Assert.Equal(["frames"], UnavailableParts(json));
        Assert.Contains("No console frames", json.GetProperty("unavailable")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task OtherServersFramesAreNeverIncluded()
    {
        var mine = await SeedServerAsync(postgres);
        var other = await SeedServerAsync(postgres);
        await AddFrameAsync(mine, "mine", Now.AddMinutes(-1));
        await AddFrameAsync(other, "someone else's", Now.AddMinutes(-1));

        var (_, json) = await RunAsync(Create(), ServerRconRecentView.Definition, ("serverId", mine.ServerId.ToString()));

        Assert.DoesNotContain("someone else's", Text(json));
    }

    [Fact]
    public async Task AnUnknownServerIsNotFound()
    {
        var (status, _) = await RunAsync(Create(), ServerRconRecentView.Definition, ("serverId", Guid.NewGuid().ToString()));

        Assert.Equal(404, status);
    }
}
