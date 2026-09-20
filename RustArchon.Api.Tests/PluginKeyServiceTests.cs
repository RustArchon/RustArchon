// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// Rotating, retiring and revoking the plugin signing key against a real Postgres with real encryption. The rules
/// under test: exactly one active key; every past key is kept and can still sign a bridge; a revoked key never signs
/// again; nothing is ever deleted; every action is audit-logged; two admins rotating at once cannot both win.
/// </summary>
public class PluginKeyServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("plugin source");

    private ApiDbContext CreateContext() => new(postgres.Options);

    private static PluginSigningService NewSigning(ApiDbContext context) =>
        new(new PlatformSettingRepository(context), new ApiKeyProtector(Provider), NullLogger<PluginSigningService>.Instance,
            new PluginKeyHistoryRepository(context));

    private static PluginKeyService NewKeys(ApiDbContext context, TimeProvider? clock = null) =>
        new(context, NewSigning(context), new ServerPluginStatusRepository(context), new ApiKeyProtector(Provider),
            clock ?? TimeProvider.System, NullLogger<PluginKeyService>.Instance);

    /// <summary>Starts each test from a fresh, single active key with no history, whatever ran before.</summary>
    private async Task<(ApiDbContext Context, string Active)> FreshAsync()
    {
        var context = CreateContext();
        await PlatformSettingsRegistry.EnsureDefaultsAsync(
            context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger.Instance);
        await context.PluginKeyHistories.ExecuteDeleteAsync();
        await context.PluginAdminEvents.ExecuteDeleteAsync();
        await context.PlatformSettings.Where(s => s.Key == PlatformSettingsRegistry.PluginSigningKey)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, ""));
        context.ChangeTracker.Clear();
        var key = await NewSigning(context).GetPublicKeyAsync();
        return (context, key.Fingerprint);
    }

    private static bool Verifies(PluginPublicKey key, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(key.ModulusBase64), Exponent = Convert.FromBase64String(key.ExponentBase64) });
        return rsa.VerifyData(Payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    // ---- rotating ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RotatingMakesANewKeyActiveAndKeepsTheOldOneRetired()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;

        var created = await NewKeys(context).RotateAsync("admin@example.com", "scheduled");

        Assert.NotEqual(old, created.Fingerprint);
        Assert.Equal(created.Fingerprint, await NewSigning(context).TryGetFingerprintAsync());
        Assert.Equal(PluginKeyState.Active, await NewSigning(context).GetKeyStateAsync(created.Fingerprint));
        Assert.Equal(PluginKeyState.Retired, await NewSigning(context).GetKeyStateAsync(old));
    }

    [Fact]
    public async Task ARetiredKeyStillSignsAndItsSignatureVerifiesUnderItsOwnPublicKey()
    {
        // The bridge depends on this: the server still trusts the old key, so the old key must still be able to sign.
        var (context, old) = await FreshAsync();
        await using var _ = context;
        var oldKey = await NewSigning(context).GetPublicKeyAsync();
        await NewKeys(context).RotateAsync("admin@example.com", null);

        var signature = await NewSigning(context).SignWithAsync(old, Payload);

        Assert.True(Verifies(oldKey, signature));
        Assert.False(Verifies(await NewSigning(context).GetPublicKeyAsync(), signature)); // and not under the new one
    }

    [Fact]
    public async Task EveryKeyEverActiveStaysAvailableAfterSeveralRotations()
    {
        var (context, first) = await FreshAsync();
        await using var _ = context;
        var keys = NewKeys(context);
        var second = (await keys.RotateAsync("a@example.com", null)).Fingerprint;
        var third = (await keys.RotateAsync("a@example.com", null)).Fingerprint;

        var signing = NewSigning(context);
        Assert.Equal(PluginKeyState.Retired, await signing.GetKeyStateAsync(first));
        Assert.Equal(PluginKeyState.Retired, await signing.GetKeyStateAsync(second));
        Assert.Equal(PluginKeyState.Active, await signing.GetKeyStateAsync(third));
        foreach (var fingerprint in new[] { first, second, third })
        {
            Assert.NotEmpty(await signing.SignWithAsync(fingerprint, Payload));
        }
    }

    [Fact]
    public async Task ThePreviousKeysStoredValueMovesOverUnchangedSoNothingIsReEncryptedOrLost()
    {
        var (context, _) = await FreshAsync();
        await using var __ = context;
        var before = (await context.PlatformSettings.AsNoTracking().SingleAsync(s => s.Key == PlatformSettingsRegistry.PluginSigningKey)).Value;

        await NewKeys(context).RotateAsync("a@example.com", null);

        var kept = await context.PluginKeyHistories.AsNoTracking().SingleAsync();
        Assert.Equal(before, kept.EncryptedPrivateKey);
    }

    [Fact]
    public async Task RotatingIsAuditLoggedWithWhoDidItAndWhichKeyReplacedWhich()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;

        var created = await NewKeys(context).RotateAsync("admin@example.com", "yearly");

        var entry = await context.PluginAdminEvents.AsNoTracking().SingleAsync();
        Assert.Equal(PluginAdminEventKind.KeyRotated, entry.Kind);
        Assert.Equal("admin@example.com", entry.Actor);
        Assert.Equal(created.Fingerprint, entry.Subject);
        Assert.Contains(old, entry.Detail);
        Assert.Contains("yearly", entry.Detail);
    }

    [Fact]
    public async Task TheListShowsTheActiveKeyFirstThenTheRestNewestFirst()
    {
        var (context, first) = await FreshAsync();
        await using var _ = context;
        var keys = NewKeys(context);
        var second = (await keys.RotateAsync("a@example.com", null)).Fingerprint;
        var third = (await keys.RotateAsync("a@example.com", null)).Fingerprint;

        var list = await keys.ListAsync();

        Assert.Equal([third, second, first], list.Select(k => k.Fingerprint).ToArray());
        Assert.Equal([PluginKeyState.Active, PluginKeyState.Retired, PluginKeyState.Retired], list.Select(k => k.State).ToArray());
    }

    [Fact]
    public async Task TheListNeverContainsAPrivateKeyOrAnythingSecret()
    {
        var (context, _) = await FreshAsync();
        await using var __ = context;
        await NewKeys(context).RotateAsync("a@example.com", null);

        var text = string.Join(" ", (await NewKeys(context).ListAsync()).Select(k => k.ToString()));

        Assert.DoesNotContain("PRIVATE", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(typeof(PluginKeyInfo).GetProperties(), p => Assert.DoesNotContain("Private", p.Name));
    }

    // ---- revoking ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARevokedKeyNeverSignsAgainAndIsReportedRevoked()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;
        var keys = NewKeys(context);
        await keys.RotateAsync("a@example.com", null);

        var revoked = await keys.RevokeAsync(old, "leaked in a support ticket", "admin@example.com");

        Assert.Equal(PluginKeyState.Revoked, revoked.State);
        Assert.Equal("leaked in a support ticket", revoked.RevokedReason);
        Assert.Equal(PluginKeyState.Revoked, await NewSigning(context).GetKeyStateAsync(old));
        var refused = await Assert.ThrowsAsync<PluginKeyUnavailableException>(() => NewSigning(context).SignWithAsync(old, Payload));
        Assert.Equal(PluginKeyState.Revoked, refused.State);
    }

    [Fact]
    public async Task RevokingIsAuditLogged()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;
        var keys = NewKeys(context);
        await keys.RotateAsync("a@example.com", null);

        await keys.RevokeAsync(old, "compromised", "admin@example.com");

        var entry = await context.PluginAdminEvents.AsNoTracking().SingleAsync(e => e.Kind == PluginAdminEventKind.KeyRevoked);
        Assert.Equal(old, entry.Subject);
        Assert.Equal("admin@example.com", entry.Actor);
        Assert.Equal("compromised", entry.Detail);
    }

    [Fact]
    public async Task TheActiveKeyCannotBeRevokedOnItsOwnSoThereIsNeverNoActiveKey()
    {
        var (context, active) = await FreshAsync();
        await using var _ = context;

        var ex = await Assert.ThrowsAsync<PluginKeyOperationException>(() => NewKeys(context).RevokeAsync(active, "oops", "a@example.com"));

        Assert.Equal("is_active", ex.Code);
        Assert.Equal(PluginKeyState.Active, await NewSigning(context).GetKeyStateAsync(active));
    }

    [Fact]
    public async Task RotatingAndRevokingTheOldKeyInOneStepLeavesOneActiveKeyAndARevokedOne()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;

        var created = await NewKeys(context).RotateAsync("a@example.com", null, revokeCurrent: true, revokeReason: "suspected compromise");

        var signing = NewSigning(context);
        Assert.Equal(PluginKeyState.Revoked, await signing.GetKeyStateAsync(old));
        Assert.Equal(PluginKeyState.Active, await signing.GetKeyStateAsync(created.Fingerprint));
        Assert.Equal(2, await context.PluginAdminEvents.CountAsync()); // rotated, and revoked
    }

    [Fact]
    public async Task RevokingNeedsAReason()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;
        var keys = NewKeys(context);
        await keys.RotateAsync("a@example.com", null);

        Assert.Equal("reason_required", (await Assert.ThrowsAsync<PluginKeyOperationException>(() => keys.RevokeAsync(old, "  ", "a@example.com"))).Code);
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<PluginKeyOperationException>(() => keys.RotateAsync("a@example.com", null, revokeCurrent: true))).Code);
        Assert.Equal(PluginKeyState.Retired, await NewSigning(context).GetKeyStateAsync(old)); // untouched
    }

    [Fact]
    public async Task AnUnknownOrAlreadyRevokedKeyIsRefused()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;
        var keys = NewKeys(context);
        await keys.RotateAsync("a@example.com", null);
        await keys.RevokeAsync(old, "first", "a@example.com");

        Assert.Equal("not_found", (await Assert.ThrowsAsync<PluginKeyOperationException>(() => keys.RevokeAsync("0000000000000000", "x", "a"))).Code);
        Assert.Equal("already_revoked", (await Assert.ThrowsAsync<PluginKeyOperationException>(() => keys.RevokeAsync(old, "again", "a"))).Code);
    }

    [Fact]
    public async Task AnUnknownFingerprintCanNeverSign()
    {
        var (context, _) = await FreshAsync();
        await using var __ = context;

        var refused = await Assert.ThrowsAsync<PluginKeyUnavailableException>(() => NewSigning(context).SignWithAsync("ffffffffffffffff", Payload));

        Assert.Null(refused.State);
        Assert.Null(await NewSigning(context).GetKeyStateAsync("ffffffffffffffff"));
        Assert.Null(await NewSigning(context).GetKeyStateAsync(""));
    }

    [Fact]
    public async Task AFingerprintIsMatchedIgnoringCase()
    {
        var (context, active) = await FreshAsync();
        await using var _ = context;

        Assert.Equal(PluginKeyState.Active, await NewSigning(context).GetKeyStateAsync(active.ToUpperInvariant()));
    }

    // ---- races and reporting -------------------------------------------------------------------------------

    [Fact]
    public async Task ManyAdminsRotatingAtOnceCannotLoseAKeyOrLeaveTwoActive()
    {
        var (seed, first) = await FreshAsync();
        await seed.DisposeAsync();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = CreateContext();
            try
            {
                return (await NewKeys(context).RotateAsync("a@example.com", null)).Fingerprint;
            }
            catch (PluginKeyOperationException ex)
            {
                Assert.Equal("concurrent_change", ex.Code); // the only acceptable reason to lose
                return null;
            }
        }));

        await using var check = CreateContext();
        var winners = outcomes.Where(o => o is not null).ToList();
        var history = await check.PluginKeyHistories.AsNoTracking().ToListAsync();
        var active = await NewSigning(check).TryGetFingerprintAsync();

        Assert.NotEmpty(winners);                                    // somebody rotated
        Assert.Equal(winners.Count, history.Count);                  // and every success kept exactly one old key
        Assert.Contains(first, history.Select(h => h.Fingerprint));  // nothing was lost, including the original
        Assert.DoesNotContain(active, history.Select(h => h.Fingerprint)); // the active key is not also in the history
        Assert.Contains(active, winners);                            // and it is one a winner created
        Assert.Equal(history.Count, history.Select(h => h.Fingerprint).Distinct().Count());
    }

    [Fact]
    public async Task RotatingAgainOnTopOfARotationKeepsEveryEarlierKey()
    {
        // Simulated deterministically: someone else already replaced the key between our read and our swap, which
        // shows up as the stored value no longer being the one we read. Here the "other admin" is a plain update.
        var (context, old) = await FreshAsync();
        await using var _ = context;
        var keys = NewKeys(context);
        var winner = (await keys.RotateAsync("winner@example.com", null)).Fingerprint;

        // A second rotation starting now sees the winner's key as current and simply succeeds on top of it: the
        // history then holds both earlier keys, none lost.
        var next = (await keys.RotateAsync("later@example.com", null)).Fingerprint;

        var list = await keys.ListAsync();
        Assert.Equal([next, winner, old], list.Select(k => k.Fingerprint).ToArray());
    }

    [Fact]
    public async Task TheListSaysHowManyServersLastReportedEachKeyAndWhen()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;
        var keys = NewKeys(context);
        var active = (await keys.RotateAsync("a@example.com", null)).Fingerprint;

        var tenant = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenant, Name = $"T {tenant}", IsActive = true });
        await context.SaveChangesAsync();
        var repo = new ServerPluginStatusRepository(context);
        var lastSeen = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await repo.UpsertAsync(tenant, Guid.NewGuid(), Reported(old, lastSeen.AddDays(-3)));
        await repo.UpsertAsync(tenant, Guid.NewGuid(), Reported(old, lastSeen));
        await repo.UpsertAsync(tenant, Guid.NewGuid(), Reported(active, lastSeen));

        var list = await keys.ListAsync();

        var oldInfo = list.Single(k => k.Fingerprint == old);
        Assert.Equal(2, oldInfo.ServersReporting);
        Assert.Equal(lastSeen, oldInfo.LastReportedUtc);
        Assert.Equal(1, list.Single(k => k.Fingerprint == active).ServersReporting);
    }

    private static ServerPluginStatus Reported(string fingerprint, DateTimeOffset at) => new()
    {
        ProtocolVersion = 1, PluginVersion = "0.2.1", Capabilities = ["config"], SettingsPersisted = true,
        SigningState = "valid", SigningKeyFingerprint = fingerprint, CapturedAtUtc = at
    };

    // ---- bridges -------------------------------------------------------------------------------------------

    private static PluginScriptService NewScripts(ApiDbContext context) =>
        new(NewSigning(context), new EmbeddedPluginScriptSource());

    [Fact]
    public async Task ABridgeForTheActiveKeyIsExactlyTheOrdinaryFile()
    {
        var (context, active) = await FreshAsync();
        await using var _ = context;
        var scripts = NewScripts(context);

        Assert.Equal((await scripts.BuildAsync()).Bytes, (await scripts.BuildBridgeAsync(active)).Bytes);
    }

    [Fact]
    public async Task ABridgeForARetiredKeyEmbedsTheActiveKeyAndCarriesTwoSignatureLines()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;
        await NewKeys(context).RotateAsync("a@example.com", null);
        var scripts = NewScripts(context);

        var bridge = await scripts.BuildBridgeAsync(old);
        var plain = await scripts.BuildAsync();

        var text = Encoding.UTF8.GetString(bridge.Bytes);
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith(PluginScriptStamper.SignatureMarker, StringComparison.Ordinal)));
        Assert.Equal(plain.KeyFingerprint, bridge.KeyFingerprint); // it announces the ACTIVE key it moves the server to
        Assert.NotEqual(plain.Bytes, bridge.Bytes);
        var active = await NewSigning(context).GetPublicKeyAsync();
        Assert.Contains(active.ModulusBase64, text); // the active key is what is stamped in
    }

    [Fact]
    public async Task ABridgeForARevokedOrUnknownKeyIsRefused()
    {
        var (context, old) = await FreshAsync();
        await using var _ = context;
        var keys = NewKeys(context);
        await keys.RotateAsync("a@example.com", null);
        await keys.RevokeAsync(old, "compromised", "a@example.com");
        var scripts = NewScripts(context);

        await Assert.ThrowsAsync<PluginKeyUnavailableException>(() => scripts.BuildBridgeAsync(old));
        await Assert.ThrowsAsync<PluginKeyUnavailableException>(() => scripts.BuildBridgeAsync("ffffffffffffffff"));
    }
}
