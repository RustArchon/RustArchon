// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// <see cref="PluginUpdateTokenRepository"/> against a real Postgres: a token works exactly once, only for its own
/// server, only before it expires, and the database never holds the token itself.
/// </summary>
public class PluginUpdateTokenRepositoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private ApiDbContext CreateContext() => new(postgres.Options);

    private static async Task<Guid> SeedTenantAsync(ApiDbContext context)
    {
        var tenantId = Guid.NewGuid();
        context.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Test tenant {tenantId}", IsActive = true });
        await context.SaveChangesAsync();
        return tenantId;
    }

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const string Fingerprint = "0123456789abcdef";

    // ---- what a token looks like ---------------------------------------------------------------------------

    [Fact]
    public async Task ATokenIsLongRandomAndUrlSafe()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));

        var a = await repository.MintAsync(tenant, Guid.NewGuid(), Fingerprint, Lifetime);
        var b = await repository.MintAsync(tenant, Guid.NewGuid(), Fingerprint, Lifetime);

        Assert.Equal(43, a.Length); // 256 bits, base64url, no padding
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task TheDatabaseHoldsOnlyAHashNeverTheToken()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));

        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);

        var row = await context.Set<PluginUpdateToken>().AcrossAllTenants().SingleAsync(t => t.RustServerId == serverId);
        Assert.NotEqual(token, row.TokenHash);
        Assert.DoesNotContain(token, row.TokenHash);
        Assert.Equal(PluginUpdateTokenRepository.HashToken(token), row.TokenHash);
        Assert.Matches("^[0-9a-f]{64}$", row.TokenHash);
        Assert.Equal(Start.Add(Lifetime), row.ExpiresAtUtc);
        Assert.Null(row.RedeemedAtUtc);
    }

    [Fact]
    public void TheHashIsSha256HexOfTheToken()
    {
        // Pinned to a known value so the stored hash cannot silently change meaning.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", PluginUpdateTokenRepository.HashToken("abc"));
    }

    // ---- redeeming -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshTokenRedeemsForItsServer()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);

        Assert.NotNull(await repository.RedeemAsync(serverId, token));
    }

    [Fact]
    public async Task ATokenWorksExactlyOnce()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);

        Assert.NotNull(await repository.RedeemAsync(serverId, token));
        Assert.Null(await repository.RedeemAsync(serverId, token));
        Assert.Null(await repository.RedeemAsync(serverId, token));
    }

    [Fact]
    public async Task ATokenForOneServerIsRefusedForAnother()
    {
        // Required by the design: server A's token must never open server B.
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverA = Guid.NewGuid();
        var serverB = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var tokenForA = await repository.MintAsync(tenant, serverA, Fingerprint, Lifetime);

        Assert.Null(await repository.RedeemAsync(serverB, tokenForA));
        Assert.NotNull(await repository.RedeemAsync(serverA, tokenForA)); // and the wrong-server try did not burn it
    }

    [Fact]
    public async Task AnExpiredTokenIsRefused()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var clock = new TestClock(Start);
        var repository = new PluginUpdateTokenRepository(context, clock);
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);

        clock.Now = Start.Add(Lifetime).AddSeconds(1);

        Assert.Null(await repository.RedeemAsync(serverId, token));
    }

    [Fact]
    public async Task ATokenIsStillGoodJustBeforeItExpires()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var clock = new TestClock(Start);
        var repository = new PluginUpdateTokenRepository(context, clock);
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);

        clock.Now = Start.Add(Lifetime).AddSeconds(-1);

        Assert.NotNull(await repository.RedeemAsync(serverId, token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task AnUnknownOrEmptyTokenIsRefused(string token)
    {
        await using var context = CreateContext();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));

        Assert.Null(await repository.RedeemAsync(Guid.NewGuid(), token));
    }

    [Fact]
    public async Task RedeemingWorksWithNoAmbientTenantWhichIsHowTheAnonymousDoorCallsIt()
    {
        await using var minter = CreateContext();
        var tenant = await SeedTenantAsync(minter);
        var serverId = Guid.NewGuid();
        var token = await new PluginUpdateTokenRepository(minter, new TestClock(Start)).MintAsync(tenant, serverId, Fingerprint, Lifetime);

        await using var anonymous = CreateContext(); // a fresh context, no tenant
        Assert.NotNull(await new PluginUpdateTokenRepository(anonymous, new TestClock(Start)).RedeemAsync(serverId, token));
    }

    [Fact]
    public async Task EightSimultaneousRedemptionsProduceExactlyOneSuccess()
    {
        await using var seed = CreateContext();
        var tenant = await SeedTenantAsync(seed);
        var serverId = Guid.NewGuid();
        var token = await new PluginUpdateTokenRepository(seed, new TestClock(Start)).MintAsync(tenant, serverId, Fingerprint, Lifetime);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var context = CreateContext();
            return await new PluginUpdateTokenRepository(context, new TestClock(Start)).RedeemAsync(serverId, token);
        }));

        Assert.Equal(1, results.Count(r => r is not null));
    }

    // ---- revoking and housekeeping -------------------------------------------------------------------------

    [Fact]
    public async Task ARevokedTokenCanNoLongerBeRedeemed()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);

        await repository.RevokeAsync(token);

        Assert.Null(await repository.RedeemAsync(serverId, token));
    }

    [Fact]
    public async Task RevokingAnUnknownTokenIsHarmless()
    {
        await using var context = CreateContext();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));

        var ex = await Record.ExceptionAsync(() => repository.RevokeAsync("never-existed"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task MintingPurgesTokensThatExpiredMoreThanADayAgo()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var oldServer = Guid.NewGuid();
        var clock = new TestClock(Start);
        var repository = new PluginUpdateTokenRepository(context, clock);
        await repository.MintAsync(tenant, oldServer, Fingerprint, Lifetime);

        clock.Now = Start.AddDays(2); // the first token expired a day and a half ago
        var freshServer = Guid.NewGuid();
        await repository.MintAsync(tenant, freshServer, Fingerprint, Lifetime);

        var remaining = await context.Set<PluginUpdateToken>().AcrossAllTenants()
            .Where(t => t.RustServerId == oldServer || t.RustServerId == freshServer)
            .Select(t => t.RustServerId).ToListAsync();
        Assert.DoesNotContain(oldServer, remaining);
        Assert.Contains(freshServer, remaining);
    }

    [Fact]
    public async Task MintingKeepsRecentlyExpiredTokensForNow()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var server = Guid.NewGuid();
        var clock = new TestClock(Start);
        var repository = new PluginUpdateTokenRepository(context, clock);
        await repository.MintAsync(tenant, server, Fingerprint, Lifetime);

        clock.Now = Start.AddHours(2); // expired, but well within the day
        await repository.MintAsync(tenant, Guid.NewGuid(), Fingerprint, Lifetime);

        Assert.True(await context.Set<PluginUpdateToken>().AcrossAllTenants().AnyAsync(t => t.RustServerId == server));
    }

    [Fact]
    public void ANewServerAllowsUpdatesAndUpdatesAutomaticallyUntilTheOwnerTurnsThemOff()
    {
        Assert.True(new RustServer().PluginUpdatesEnabled);
        Assert.True(new RustServer().PluginAutoUpdateEnabled);
    }
    [Fact]
    public async Task RedeemingReturnsTheKeyFingerprintTheTokenWasMintedFor()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, serverId, "aaaaaaaaaaaaaaaa", Lifetime);

        var redemption = await repository.RedeemAsync(serverId, token);

        Assert.Equal("aaaaaaaaaaaaaaaa", redemption!.SigningKeyFingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task ATokenCannotBeMintedWithoutAKeyFingerprint(string? fingerprint)
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => repository.MintAsync(tenant, Guid.NewGuid(), fingerprint!, Lifetime));
    }

    [Fact]
    public async Task ATokenFromBeforeFingerprintsExistedCannotBeRedeemedForADownload()
    {
        // Rows minted by the earlier version have an empty fingerprint: they must not turn into a download signed with
        // some guessed key. They expire within ten minutes anyway.
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);
        await context.Set<PluginUpdateToken>().AcrossAllTenants().Where(t => t.RustServerId == serverId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SigningKeyFingerprint, ""));

        Assert.Null(await repository.RedeemAsync(serverId, token));
    }

    // ---- redeeming without naming the server (the header form) ---------------------------------------------

    [Fact]
    public async Task ATokenRedeemedByItselfSaysWhichServerAndKeyItWasMintedFor()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);

        var redemption = await repository.RedeemAsync(token);

        Assert.NotNull(redemption);
        Assert.Equal(serverId, redemption!.RustServerId);
        Assert.Equal(Fingerprint, redemption.SigningKeyFingerprint);
    }

    [Fact]
    public async Task ATokenRedeemedByItselfWorksExactlyOnceAndNotAgainByServer()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);

        Assert.NotNull(await repository.RedeemAsync(token));
        Assert.Null(await repository.RedeemAsync(token));
        Assert.Null(await repository.RedeemAsync(serverId, token));      // one use, whichever way it is presented
    }

    [Fact]
    public async Task ATokenUsedWithTheServerNamedCannotBeUsedAgainByItself()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);

        Assert.NotNull(await repository.RedeemAsync(serverId, token));
        Assert.Null(await repository.RedeemAsync(token));
    }

    [Fact]
    public async Task AnExpiredTokenRedeemedByItselfIsRefused()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var clock = new TestClock(Start);
        var repository = new PluginUpdateTokenRepository(context, clock);
        var token = await repository.MintAsync(tenant, Guid.NewGuid(), Fingerprint, Lifetime);
        clock.Now = Start + Lifetime + TimeSpan.FromSeconds(1);

        Assert.Null(await repository.RedeemAsync(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token-we-ever-minted")]
    public async Task AnUnknownOrEmptyTokenRedeemedByItselfIsRefused(string token)
    {
        await using var context = CreateContext();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));

        Assert.Null(await repository.RedeemAsync(token));
    }

    [Fact]
    public async Task ARevokedTokenRedeemedByItselfIsRefused()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, Guid.NewGuid(), Fingerprint, Lifetime);

        await repository.RevokeAsync(token);

        Assert.Null(await repository.RedeemAsync(token));
    }

    // ---- what a token may download -------------------------------------------------------------------------

    [Fact]
    public async Task ATokenIsForTheMainPluginUnlessMintedForTheUpdater()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var main = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime);
        var updater = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime, PluginUpdateTokenPurposes.Updater);

        Assert.Equal(PluginUpdateTokenPurposes.Main, (await repository.RedeemAsync(serverId, main))!.Purpose);
        Assert.Equal(PluginUpdateTokenPurposes.Updater, (await repository.RedeemAsync(serverId, updater))!.Purpose);
    }

    [Fact]
    public async Task TheUpdaterPurposeSurvivesRedeemingWithoutNamingTheServer()
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var serverId = Guid.NewGuid();
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));
        var token = await repository.MintAsync(tenant, serverId, Fingerprint, Lifetime, PluginUpdateTokenPurposes.Updater);

        var redemption = await repository.RedeemAsync(token);

        Assert.Equal((serverId, PluginUpdateTokenPurposes.Updater), (redemption!.RustServerId, redemption.Purpose));
    }

    [Theory]
    [InlineData("")]
    [InlineData("other")]
    [InlineData("MAIN")]
    public async Task AnUnknownPurposeCannotBeMinted(string purpose)
    {
        await using var context = CreateContext();
        var tenant = await SeedTenantAsync(context);
        var repository = new PluginUpdateTokenRepository(context, new TestClock(Start));

        await Assert.ThrowsAsync<ArgumentException>(() => repository.MintAsync(tenant, Guid.NewGuid(), Fingerprint, Lifetime, purpose));
    }
}
