// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using RustArchon.Api.Data;
using RustArchon.Api.DiagnosticViews;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// What every diagnostic view test needs: an organization on a plan with a server in it (every secret column filled with a recognisable value, so a test can
/// prove none comes out), and a way to run a view through the real framework and read the envelope it answers with.
/// </summary>
internal static class DiagnosticViewKit
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 4, 30, 0, TimeSpan.Zero);

    public static readonly string[] ServerSecrets = ["RCONPASS-SECRET", "STEAMKEY-SECRET", "GEOKEY-SECRET", "REPORTSECRET-SECRET"];

    public sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    public sealed record Seeded(Guid TenantId, Guid ServerId);

    public static async Task<Seeded> SeedServerAsync(
        PostgresFixture postgres, bool planOffers = true, RconConnectionStatus status = RconConnectionStatus.Connected, bool optedIn = false, DateTimeOffset? heartbeat = null)
    {
        var tenantId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        await using var context = new ApiDbContext(postgres.Options, new FixedTenantContext(tenantId));
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"View tenant {tenantId}", IsActive = true });
        var plan = new Plan { Name = $"View plan {Guid.NewGuid()}", Active = true, OffersThirdPartyPluginUpdates = planOffers };
        plan.Prices.Add(new PlanPrice { TermMonths = 1, UnitAmount = 0m, IncludedUnits = 1, Currency = "USD" });
        context.Set<Plan>().Add(plan);
        context.Set<Subscription>().Add(new Subscription { TenantId = tenantId, PlanId = plan.Id, Plan = plan, StartDate = Now.AddYears(-1) });
        context.Set<RustServer>().Add(new RustServer
        {
            Id = serverId, TenantId = tenantId, Name = "View server " + serverId.ToString("N")[..6], Host = "192.0.2.90", Port = 28016,
            RconPassword = ServerSecrets[0], SteamApiKey = ServerSecrets[1], GeolocationApiKey = ServerSecrets[2], ReportsSecret = ServerSecrets[3],
            IsEnabled = true, ThirdPartyPluginUpdatesEnabled = optedIn, ConnectionStatus = status, ConnectionStatusDetail = "detail text",
            ConnectionStatusChangedAtUtc = Now.AddMinutes(-10), LastHeartbeatUtc = heartbeat ?? Now.AddSeconds(-20)
        });
        await context.SaveChangesAsync();
        return new Seeded(tenantId, serverId);
    }

    /// <summary>Runs a view the way the Api does and returns the answer's status and JSON.</summary>
    public static async Task<(int Status, JsonElement Json)> RunAsync(IDiagnosticView view, ViewDefinition definition, params (string Key, string Value)[] query)
    {
        var runner = new ViewRunner(new FixedClock());
        var outcome = await runner.RunAsync(definition, view, query.ToDictionary(q => q.Key, q => (string?)q.Value), CancellationToken.None);
        return (outcome.Status, JsonSerializer.SerializeToElement(outcome.Body));
    }

    /// <summary>The whole answer as text, for "this secret appears nowhere in it".</summary>
    public static string Text(JsonElement json) => json.GetRawText();

    public static IReadOnlyList<string> UnavailableParts(JsonElement json) =>
        [.. json.GetProperty("unavailable").EnumerateArray().Select(u => u.GetProperty("part").GetString()!)];
}
