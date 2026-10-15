// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.DiagnosticViews.Views;
using RustArchon.Api.Infrastructure;
using RustArchon.Messaging.Contracts;
using static RustArchon.Api.Tests.DiagnosticViewKit;

namespace RustArchon.Api.Tests;

/// <summary>The <c>server-plugin</c> diagnostic view: what the RustArchon plugin on a server reports about itself, whose key signed it, and the plugin list.</summary>
public class ServerPluginViewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Fingerprint = "0123456789abcdef";

    private ServerPluginView Create(PluginKeyState? keyState = PluginKeyState.Active)
    {
        var script = new Mock<IPluginScriptService>();
        script.Setup(s => s.GetKeyStateAsync(Fingerprint)).ReturnsAsync(keyState);
        return new ServerPluginView(new ApiDbContext(postgres.Options), script.Object, new FixedClock());
    }

    private async Task AddStatusAsync(Seeded seeded, string signingState = "valid", string fingerprint = Fingerprint, TimeSpan? age = null, string[]? capabilities = null)
    {
        await using var context = new ApiDbContext(postgres.Options);
        context.ServerPluginStatuses.Add(new ServerPluginStatus
        {
            TenantId = seeded.TenantId, RustServerId = seeded.ServerId, ProtocolVersion = 1, PluginVersion = "0.11.0",
            Capabilities = capabilities ?? ["config", "thirdparty-update"], SigningState = signingState, SigningKeyFingerprint = fingerprint,
            CapturedAtUtc = Now - (age ?? TimeSpan.FromMinutes(1))
        });
        await context.SaveChangesAsync();
    }

    private async Task AddPluginAsync(Seeded seeded, string name, string version = "1.0.0")
    {
        await using var context = new ApiDbContext(postgres.Options);
        context.ServerPlugins.Add(new ServerPlugin
        {
            TenantId = seeded.TenantId, RustServerId = seeded.ServerId, Name = name, Author = "someone", Version = version, Framework = ServerModFramework.Carbon, CapturedAtUtc = Now
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task APluginThatReportedIsDescribedWithItsVersionCapabilitiesSigningAndWhoseKeyItIs()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddStatusAsync(seeded);
        await AddPluginAsync(seeded, "RustArchon");
        await AddPluginAsync(seeded, "Kits", "2.0.0");

        var (status, json) = await RunAsync(Create(), ServerPluginView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.Equal(200, status);
        var data = json.GetProperty("data");
        var reported = data.GetProperty("status");
        Assert.Equal("0.11.0", reported.GetProperty("pluginVersion").GetString());
        Assert.Equal("valid", reported.GetProperty("signingState").GetString());
        Assert.True(reported.GetProperty("signingKeyIsThisPanels").GetBoolean());
        Assert.Equal("Active", reported.GetProperty("signingKeyState").GetString());
        Assert.Equal(["config", "thirdparty-update"], reported.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.True(data.GetProperty("rustarchon").GetProperty("listed").GetBoolean());
        Assert.True(data.GetProperty("rustarchon").GetProperty("reportedRecently").GetBoolean());
        Assert.Equal(["Kits", "RustArchon"], data.GetProperty("plugins").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Order());
        Assert.Empty(UnavailableParts(json));
    }

    [Fact]
    public async Task AKeyThisPanelDoesNotKnowIsSaidToBeNotThisPanels()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddStatusAsync(seeded);

        var (_, json) = await RunAsync(Create(keyState: null), ServerPluginView.Definition, ("serverId", seeded.ServerId.ToString()));

        var reported = json.GetProperty("data").GetProperty("status");
        Assert.False(reported.GetProperty("signingKeyIsThisPanels").GetBoolean());
        Assert.Equal("Unknown", reported.GetProperty("signingKeyState").GetString());
    }

    [Fact]
    public async Task ARevokedKeyIsNamedAsRevoked()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddStatusAsync(seeded);

        var (_, json) = await RunAsync(Create(PluginKeyState.Revoked), ServerPluginView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.Equal("Revoked", json.GetProperty("data").GetProperty("status").GetProperty("signingKeyState").GetString());
    }

    [Fact]
    public async Task AServerWhosePluginHasNeverReportedNamesTheMissingPartAndWhy()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddPluginAsync(seeded, "Kits");

        var (status, json) = await RunAsync(Create(), ServerPluginView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.Equal(200, status);
        Assert.Equal(["status"], UnavailableParts(json));
        Assert.Contains("not reported in", json.GetProperty("unavailable")[0].GetProperty("reason").GetString());
        Assert.False(json.GetProperty("data").GetProperty("rustarchon").GetProperty("listed").GetBoolean());
        Assert.Contains("not installed", json.GetProperty("data").GetProperty("rustarchon").GetProperty("explanation").GetString());
    }

    [Fact]
    public async Task AReportThatIsOldSaysTheServerHasNotBeenHeardFromRecently()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddStatusAsync(seeded, age: TimeSpan.FromHours(2));
        await AddPluginAsync(seeded, "RustArchon");

        var (_, json) = await RunAsync(Create(), ServerPluginView.Definition, ("serverId", seeded.ServerId.ToString()));

        var summary = json.GetProperty("data").GetProperty("rustarchon");
        Assert.False(summary.GetProperty("reportedRecently").GetBoolean());
        Assert.Contains("not been heard from recently", summary.GetProperty("explanation").GetString());
        Assert.Equal(7200, summary.GetProperty("ageSeconds").GetInt32());
    }

    [Fact]
    public async Task ThePluginBeingListedButNeverHavingReportedIsSaidToBeNotAnsweringYet()
    {
        var seeded = await SeedServerAsync(postgres);
        await AddPluginAsync(seeded, "RustArchon");

        var (_, json) = await RunAsync(Create(), ServerPluginView.Definition, ("serverId", seeded.ServerId.ToString()));

        Assert.Contains("has not answered", json.GetProperty("data").GetProperty("rustarchon").GetProperty("explanation").GetString());
    }

    [Fact]
    public async Task AnUnknownServerIsNotFound()
    {
        var (status, _) = await RunAsync(Create(), ServerPluginView.Definition, ("serverId", Guid.NewGuid().ToString()));

        Assert.Equal(404, status);
    }
}
