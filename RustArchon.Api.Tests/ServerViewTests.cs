// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.DiagnosticViews;
using RustArchon.Api.DiagnosticViews.Views;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using static RustArchon.Api.Tests.DiagnosticViewKit;

namespace RustArchon.Api.Tests;

/// <summary>The <c>server</c> diagnostic view: where a server stands (connection, owner worker, heartbeat, update switches, plan flags), and none of its secrets.</summary>
public class ServerViewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ServerView Create()
    {
        var context = new ApiDbContext(postgres.Options);
        return new ServerView(context, new ThirdPartyPluginUpdateGate(context), new FixedClock());
    }

    [Fact]
    public async Task AServerIsDescribedWithItsConnectionItsSwitchesAndWhatItsPlanOffers()
    {
        var seeded = await SeedServerAsync(postgres, optedIn: true);

        var (status, json) = await RunAsync(Create(), ServerView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.Equal(200, status);
        var server = json.GetProperty("data").GetProperty("server");
        Assert.Equal(seeded.ServerId, server.GetProperty("id").GetGuid());
        Assert.Equal("192.0.2.90", server.GetProperty("host").GetString());
        Assert.Equal("Connected", server.GetProperty("connectionStatus").GetString());
        Assert.True(server.GetProperty("thirdPartyPluginUpdatesEnabled").GetBoolean());
        Assert.True(server.GetProperty("planOffersThirdPartyPluginUpdates").GetBoolean());
        Assert.Equal("Open", server.GetProperty("thirdPartyGate").GetString());
        Assert.StartsWith("Connected", server.GetProperty("connectionExplanation").GetString());
    }

    [Fact]
    public async Task AServerThatIsNotConnectedSaysSoWithTheStatusTheDetailAndTheHeartbeatAge()
    {
        var seeded = await SeedServerAsync(postgres, status: RconConnectionStatus.Reconnecting, heartbeat: Now.AddMinutes(-7));

        var (_, json) = await RunAsync(Create(), ServerView.Definition, ("serverId", seeded.ServerId.ToString()));

        var explanation = json.GetProperty("data").GetProperty("server").GetProperty("connectionExplanation").GetString()!;
        Assert.Contains("Not connected", explanation);
        Assert.Contains("Reconnecting", explanation);
        Assert.Contains("detail text", explanation);
        Assert.Contains("7 minutes", explanation);
    }

    [Fact]
    public async Task AServerRecordedAsConnectedWhoseHeartbeatIsOldSaysThatRecordIsProbablyStale()
    {
        var seeded = await SeedServerAsync(postgres, status: RconConnectionStatus.Connected, heartbeat: Now.AddHours(-3));

        var (_, json) = await RunAsync(Create(), ServerView.Definition, ("serverId", seeded.ServerId.ToString()));

        var explanation = json.GetProperty("data").GetProperty("server").GetProperty("connectionExplanation").GetString()!;
        Assert.Contains("Recorded as connected", explanation);
        Assert.Contains("probably stale", explanation);
        Assert.Contains("3 hours", explanation);
    }

    [Fact]
    public async Task AServerWhoseHeartbeatHasNeverBeenSeenSaysThat()
    {
        var seeded = await SeedServerAsync(postgres, status: RconConnectionStatus.Reconnecting);
        await using (var context = new ApiDbContext(postgres.Options))
        {
            var row = await context.RustServers.AcrossAllTenants().SingleAsync(s => s.Id == seeded.ServerId);
            row.LastHeartbeatUtc = null;
            await context.SaveChangesAsync();
        }

        var (_, json) = await RunAsync(Create(), ServerView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.Contains("no heartbeat", json.GetProperty("data").GetProperty("server").GetProperty("connectionExplanation").GetString());
    }

    [Fact]
    public async Task ThePlanNotOfferingUpdatesIsSaid()
    {
        var seeded = await SeedServerAsync(postgres, planOffers: false);

        var (_, json) = await RunAsync(Create(), ServerView.Definition, ("serverId", seeded.ServerId.ToString()));

        var server = json.GetProperty("data").GetProperty("server");
        Assert.False(server.GetProperty("planOffersThirdPartyPluginUpdates").GetBoolean());
        Assert.Equal("PlanDoesNotOffer", server.GetProperty("thirdPartyGate").GetString());
    }

    [Fact]
    public async Task NoSecretOfTheServerAppearsAnywhereInTheAnswer()
    {
        var seeded = await SeedServerAsync(postgres);

        var (_, json) = await RunAsync(Create(), ServerView.Definition, ("serverId", seeded.ServerId.ToString()));

        foreach (var secret in ServerSecrets)
        {
            Assert.DoesNotContain(secret, Text(json));
        }
    }

    [Fact]
    public async Task AnUnknownServerIsNotFound()
    {
        var (status, json) = await RunAsync(Create(), ServerView.Definition, ("serverId", Guid.NewGuid().ToString()));

        Assert.Equal(404, status);
        Assert.Equal("not_found", json.GetProperty("error").GetProperty("code").GetString());
    }
}
