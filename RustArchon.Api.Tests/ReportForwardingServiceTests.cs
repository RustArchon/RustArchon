// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using JumpStart.Repositories;
using MassTransit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The report-forwarding address (ADR-0001): a secret per server that is minted on first ask, stored encrypted, checked in constant
/// time, and never good for any server but its own. Against a real Postgres so the tenant filter and the cross-tenant lookup are the
/// real ones.
/// </summary>
public class ReportForwardingServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly IApiKeyProtector Protector = new ApiKeyProtector(new EphemeralDataProtectionProvider());
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<(Guid TenantId, Guid ServerId)> SeedAsync(bool enabled = true)
    {
        await using var context = new ApiDbContext(postgres.Options);
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Reports tenant {Guid.NewGuid()}", IsActive = true };
        context.Set<Tenant>().Add(tenant);
        var server = new RustServer
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = $"Reports server {Guid.NewGuid()}",
            Host = "192.0.2.10",
            RconPassword = "unused",
            IsEnabled = enabled
        };
        context.Set<RustServer>().Add(server);
        await context.SaveChangesAsync();
        return (tenant.Id, server.Id);
    }

    private ReportForwardingService Create(
        Guid? tenantId, Mock<IRequestClient<SendRconCommand>>? rcon = null, string baseUrl = "https://panel.example/")
    {
        var context = tenantId is { } id
            ? new ApiDbContext(postgres.Options, new FixedTenantContext(id))
            : new ApiDbContext(postgres.Options);
        var settings = new Mock<IPlatformSettingsCache>();
        settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.PanelBaseUrl)).ReturnsAsync(baseUrl);

        return new ReportForwardingService(
            new RustServerRepository(context), new ServerReportRepository(context), Protector, settings.Object,
            (rcon ?? new Mock<IRequestClient<SendRconCommand>>()).Object, new FixedClock(Now),
            NullLogger<ReportForwardingService>.Instance);
    }

    private static string SecretOf(ReportForwardingDto dto) => dto.Url[(dto.Url.LastIndexOf('/') + 1)..];

    private async Task<RustServer> ReloadAsync(Guid serverId)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.Set<RustServer>().AcrossAllTenants().SingleAsync(s => s.Id == serverId);
    }

    // ---- minting ----

    [Fact]
    public async Task TheFirstAskMintsASecretAndLaterAsksReturnTheSameOne()
    {
        var (tenant, server) = await SeedAsync();

        var first = await Create(tenant).GetAsync(server);
        var second = await Create(tenant).GetAsync(server);

        Assert.NotNull(first);
        Assert.Equal(first!.Url, second!.Url);
        Assert.Equal(43, SecretOf(first).Length);
    }

    [Fact]
    public async Task AServerStartsWithNoSecretSoItAcceptsNothingUntilOneIsAskedFor()
    {
        var (_, server) = await SeedAsync();

        Assert.Null((await ReloadAsync(server)).ReportsSecret);
        Assert.False(await Create(null).IsTokenValidAsync(server, ReportForwardingService.NewSecret()));
    }

    [Fact]
    public async Task TheAddressCarriesThePanelBaseUrlTheServerIdAndTheSecretAndTheCommandQuotesIt()
    {
        var (tenant, server) = await SeedAsync();

        var dto = (await Create(tenant, baseUrl: "https://panel.example/").GetAsync(server))!;

        Assert.Equal($"https://panel.example/ingest/reports/{server}/{SecretOf(dto)}", dto.Url);
        Assert.Equal($"server.reportsServerEndpoint \"{dto.Url}\"", dto.Command);
    }

    [Fact]
    public async Task TheSecretIsStoredEncryptedNotAsPlainText()
    {
        var (tenant, server) = await SeedAsync();

        var secret = SecretOf((await Create(tenant).GetAsync(server))!);

        var stored = (await ReloadAsync(server)).ReportsSecret;
        Assert.NotNull(stored);
        Assert.DoesNotContain(secret, stored);
        Assert.Equal(secret, Protector.Unprotect(ApiKeyProtectorPurposes.ReportsSecret, stored));
    }

    [Fact]
    public async Task EveryServerGetsItsOwnIndependentSecret()
    {
        var (tenantA, serverA) = await SeedAsync();
        var (tenantB, serverB) = await SeedAsync();

        var a = SecretOf((await Create(tenantA).GetAsync(serverA))!);
        var b = SecretOf((await Create(tenantB).GetAsync(serverB))!);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task AnotherTenantsServerIsNotFound()
    {
        var (_, server) = await SeedAsync();
        var (otherTenant, _) = await SeedAsync();

        Assert.Null(await Create(otherTenant).GetAsync(server));
        Assert.Null(await Create(otherTenant).RotateAsync(server));
        Assert.Null(await Create(otherTenant).VerifyAsync(server));

        // ...and nothing was minted as a side effect of asking.
        Assert.Null((await ReloadAsync(server)).ReportsSecret);
    }

    // ---- rotation ----

    [Fact]
    public async Task RotatingReplacesTheSecretAndTheOldAddressStopsWorkingAtOnce()
    {
        var (tenant, server) = await SeedAsync();
        var old = SecretOf((await Create(tenant).GetAsync(server))!);

        var rotated = SecretOf((await Create(tenant).RotateAsync(server))!);

        Assert.NotEqual(old, rotated);
        Assert.False(await Create(null).IsTokenValidAsync(server, old));
        Assert.True(await Create(null).IsTokenValidAsync(server, rotated));
    }

    // ---- checking a presented secret ----

    [Fact]
    public async Task ItsOwnSecretIsGoodForItsOwnServer()
    {
        var (tenant, server) = await SeedAsync();
        var secret = SecretOf((await Create(tenant).GetAsync(server))!);

        Assert.True(await Create(null).IsTokenValidAsync(server, secret));
    }

    /// <summary>The requirement ADR-0001 names: a secret is bound to its server, so it is worthless at any other.</summary>
    [Fact]
    public async Task ServerAsSecretIsRejectedAtServerBAndTheOtherWayRound()
    {
        var (tenantA, serverA) = await SeedAsync();
        var (tenantB, serverB) = await SeedAsync();
        var secretA = SecretOf((await Create(tenantA).GetAsync(serverA))!);
        var secretB = SecretOf((await Create(tenantB).GetAsync(serverB))!);

        Assert.False(await Create(null).IsTokenValidAsync(serverB, secretA));
        Assert.False(await Create(null).IsTokenValidAsync(serverA, secretB));
        Assert.True(await Create(null).IsTokenValidAsync(serverA, secretA));
        Assert.True(await Create(null).IsTokenValidAsync(serverB, secretB));
    }

    [Fact]
    public async Task ASecretThatIsWrongOrEmptyOrTooLongOrForAServerThatDoesNotExistIsRejected()
    {
        var (tenant, server) = await SeedAsync();
        var secret = SecretOf((await Create(tenant).GetAsync(server))!);
        var service = Create(null);

        Assert.False(await service.IsTokenValidAsync(server, secret[..^1]));
        Assert.False(await service.IsTokenValidAsync(server, secret + "x"));
        Assert.False(await service.IsTokenValidAsync(server, string.Empty));
        Assert.False(await service.IsTokenValidAsync(server, new string('a', ReportForwardingService.MaxTokenLength + 1)));
        Assert.False(await service.IsTokenValidAsync(Guid.NewGuid(), secret));
    }

    [Fact]
    public async Task ADisabledServerAcceptsNoReportsEvenWithItsRealSecret()
    {
        var (tenant, server) = await SeedAsync();
        var secret = SecretOf((await Create(tenant).GetAsync(server))!);

        await using (var context = new ApiDbContext(postgres.Options))
        {
            var row = await context.Set<RustServer>().AcrossAllTenants().SingleAsync(s => s.Id == server);
            row.IsEnabled = false;
            await context.SaveChangesAsync();
        }

        Assert.False(await Create(null).IsTokenValidAsync(server, secret));
    }

    [Fact]
    public async Task ASecretThatNoLongerDecryptsIsAClosedDoorNotAnOpenOne()
    {
        var (_, server) = await SeedAsync();
        await using (var context = new ApiDbContext(postgres.Options))
        {
            var row = await context.Set<RustServer>().AcrossAllTenants().SingleAsync(s => s.Id == server);
            row.ReportsSecret = "not-a-protected-value";
            await context.SaveChangesAsync();
        }

        Assert.False(await Create(null).IsTokenValidAsync(server, "not-a-protected-value"));
        Assert.False(await Create(null).IsTokenValidAsync(server, ReportForwardingService.NewSecret()));
    }

    // ---- reading the convar back ----

    private static Mock<IRequestClient<SendRconCommand>> RconReplying(List<SendRconCommand> sent, Func<string> message, bool success = true)
    {
        var client = new Mock<IRequestClient<SendRconCommand>>();
        client.Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .Callback<SendRconCommand, CancellationToken, RequestTimeout>((request, _, _) => sent.Add(request))
            .ReturnsAsync(() => Mock.Of<Response<RconCommandResult>>(r =>
                r.Message == new RconCommandResult(success, message(), null, null, success ? null : "NotConnected")));
        return client;
    }

    [Fact]
    public async Task VerifyingReadsTheConvarNonInteractivelyAndAMatchIsRemembered()
    {
        var (tenant, server) = await SeedAsync();
        var address = (await Create(tenant).GetAsync(server))!;
        var sent = new List<SendRconCommand>();

        var result = await Create(tenant, RconReplying(sent, () => $"server.reportsServerEndpoint: \"{address.Url}\"")).VerifyAsync(server);

        Assert.Equal(ReportForwardingVerdict.Matches, result!.Verdict);
        var command = Assert.Single(sent);
        Assert.Equal(ReportForwardingService.EndpointConvar, command.Command);
        Assert.False(command.Interactive); // so it never shows on a tenant's Console tab
        Assert.Equal(Now, (await ReloadAsync(server)).ReportForwardingVerifiedAtUtc);
        Assert.True((await Create(tenant).GetAsync(server))!.Verified);
    }

    [Fact]
    public async Task ASecretThatDoesNotMatchIsNeverShownBackNorRemembered()
    {
        var (tenant, server) = await SeedAsync();
        await Create(tenant).GetAsync(server);
        var elsewhere = $"https://other.example/ingest/reports/{server}/{ReportForwardingService.NewSecret()}";

        var result = await Create(tenant, RconReplying([], () => $"server.reportsServerEndpoint: \"{elsewhere}\"")).VerifyAsync(server);

        Assert.Equal(ReportForwardingVerdict.Mismatch, result!.Verdict);
        Assert.EndsWith("/***", result.ObservedRedacted);
        Assert.Null((await ReloadAsync(server)).ReportForwardingVerifiedAtUtc);
    }

    [Fact]
    public async Task RotatingForgetsThatTheServerWasVerified()
    {
        var (tenant, server) = await SeedAsync();
        var address = (await Create(tenant).GetAsync(server))!;
        await Create(tenant, RconReplying([], () => $"\"{address.Url}\"")).VerifyAsync(server);
        Assert.NotNull((await ReloadAsync(server)).ReportForwardingVerifiedAtUtc);

        var rotated = await Create(tenant).RotateAsync(server);

        Assert.False(rotated!.Verified);
        Assert.Null((await ReloadAsync(server)).ReportForwardingVerifiedAtUtc);
    }

    [Fact]
    public async Task ACheckOnAServerNobodyHasAskedTheAddressOfIsNotSetAndMintsNothing()
    {
        var (tenant, server) = await SeedAsync();
        var sent = new List<SendRconCommand>();

        var result = await Create(tenant, RconReplying(sent, () => "")).VerifyAsync(server);

        Assert.Equal(ReportForwardingVerdict.NotSet, result!.Verdict);
        Assert.Empty(sent);
        Assert.Null((await ReloadAsync(server)).ReportsSecret);
    }

    [Fact]
    public async Task AConsoleThatCannotBeReachedIsUnavailableNotAFailedMatch()
    {
        var (tenant, server) = await SeedAsync();
        await Create(tenant).GetAsync(server);

        var notConnected = await Create(tenant, RconReplying([], () => "", success: false)).VerifyAsync(server);
        Assert.Equal(ReportForwardingVerdict.Unavailable, notConnected!.Verdict);

        var timesOut = new Mock<IRequestClient<SendRconCommand>>();
        timesOut.Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .ThrowsAsync(new RequestTimeoutException());
        var timedOut = await Create(tenant, timesOut).VerifyAsync(server);
        Assert.Equal(ReportForwardingVerdict.Unavailable, timedOut!.Verdict);
    }

    // ---- interpreting the reply (pure) ----

    private const string Expected = "https://panel.example/ingest/reports/11111111-1111-1111-1111-111111111111/SECRETSECRETSECRETSECRETSECRETSECRETSEC";

    [Theory]
    [InlineData("server.reportsServerEndpoint: \"" + Expected + "\"")]
    [InlineData("server.reportsServerEndpoint: " + Expected)]
    [InlineData("noise before \"" + Expected + "\" noise after")]
    [InlineData("'" + Expected + "'")]
    public void TheExactAddressAnywhereInTheReplyMatches(string reply) =>
        Assert.Equal(ReportForwardingVerdict.Matches, ReportForwardingService.Interpret(reply, Expected).Verdict);

    [Theory]
    [InlineData("server.reportsServerEndpoint: \"" + Expected + "x\"")] // a longer address that merely starts with ours
    [InlineData("server.reportsServerEndpoint: \"https://elsewhere.example/hook\"")]
    [InlineData("server.reportsServerEndpoint: \"http://panel.example/ingest/reports/11111111-1111-1111-1111-111111111111/SECRETSECRETSECRETSECRETSECRETSECRETSEC\"")] // scheme differs
    public void AnythingThatIsNotExactlyTheAddressIsAMismatch(string reply)
    {
        var result = ReportForwardingService.Interpret(reply, Expected);

        Assert.Equal(ReportForwardingVerdict.Mismatch, result.Verdict);
        Assert.NotNull(result.ObservedRedacted);
    }

    [Fact]
    public void AnEmptyConvarIsNotSet() =>
        Assert.Equal(ReportForwardingVerdict.NotSet, ReportForwardingService.Interpret("server.reportsServerEndpoint: \"\"", Expected).Verdict);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Unknown command: server.reportsServerEndpoint")]
    public void ANonAnswerIsUnreadableNeverAMatch(string? reply) =>
        Assert.Equal(ReportForwardingVerdict.Unreadable, ReportForwardingService.Interpret(reply, Expected).Verdict);

    [Fact]
    public void TheObservedAddressIsShownWithoutItsSecret()
    {
        var result = ReportForwardingService.Interpret($"\"{Expected}\"", Expected);

        Assert.DoesNotContain("SECRET", result.ObservedRedacted);
        Assert.Equal("https://panel.example/ingest/reports/11111111-1111-1111-1111-111111111111/***", result.ObservedRedacted);
    }

    [Fact]
    public void GeneratedSecretsAreLongRandomAndSafeInAnAddress()
    {
        var secrets = Enumerable.Range(0, 200).Select(_ => ReportForwardingService.NewSecret()).ToList();

        Assert.Equal(200, secrets.Distinct().Count());
        Assert.All(secrets, s =>
        {
            Assert.Equal(43, s.Length);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", s);
        });
    }
}
