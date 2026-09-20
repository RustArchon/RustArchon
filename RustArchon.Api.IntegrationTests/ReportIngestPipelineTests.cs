// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using JumpStart.Data;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// The Api's internal report door, exercised through the real pipeline (ADR-0001): the real authentication scheme, the real form limits,
/// the real ingestion service. The unit tests build a request by hand; only this can show that a large screenshot is not quietly
/// dropped by ASP.NET's default form limits, and that the door really refuses a caller without the service key.
/// </summary>
public class ReportIngestPipelineTests(ReportIngestPipelineTests.ReportApiFactory factory) : IClassFixture<ReportIngestPipelineTests.ReportApiFactory>
{
    private const string InternalKey = "integration-tests-internal-key";

    /// <summary>The real host, except that pictures go to memory instead of to Garage.</summary>
    public sealed class ReportApiFactory : ApiFactory
    {
        public MemoryStorage Storage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(Storage);
            });
        }
    }

    public sealed class MemoryStorage : IObjectStorage
    {
        public readonly Dictionary<string, byte[]> Objects = [];

        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
        {
            lock (Objects)
            {
                Objects[key] = content;
            }

            return Task.CompletedTask;
        }

        public Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<ObjectContent?>(null);
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A server with a secret already minted, in the host's own database.</summary>
    private async Task<(Guid ServerId, string Secret)> SeedServerAsync()
    {
        var secret = ReportForwardingService.NewSecret();
        var tenantId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        await factory.WithDatabaseAsync(async db =>
        {
            var protector = new ApiKeyProtector(factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"Report tenant {tenantId}", IsActive = true });
            db.Set<RustServer>().Add(new RustServer
            {
                Id = serverId, TenantId = tenantId, Name = $"Report server {serverId}", Host = "192.0.2.10", RconPassword = "unused",
                ReportsSecret = protector.Protect(ApiKeyProtectorPurposes.ReportsSecret, secret)
            });
            await db.SaveChangesAsync();
        });

        return (serverId, secret);
    }

    private static string Payload(string? image = null) => JsonSerializer.Serialize(new
    {
        Subject = "Cheating", Message = "aimbot", Type = 2, TargetId = "76561198000000001", TargetName = "Cheater",
        AppInfo = new { UserId = "76561198000000002", UserName = "Reporter", LevelPos = "(1, 2, 3)", MinutesPlayed = 42, Image = image }
    });

    private static HttpRequestMessage Post(Guid serverId, string? token, string data, bool withKey = true, string userId = "76561198000000002")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/reports/{serverId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["userid"] = userId, ["data"] = data })
        };
        if (withKey)
        {
            request.Headers.Add("X-Internal-Api-Key", InternalKey);
        }

        if (token is not null)
        {
            request.Headers.Add("X-RustArchon-Report-Token", token);
        }

        return request;
    }

    private Task<List<ServerReport>> ReportsAsync(Guid serverId) =>
        factory.FromDatabaseAsync(db => db.ServerReports.AcrossAllTenants().Where(r => r.RustServerId == serverId).ToListAsync());

    [Fact]
    public async Task AGoodSecretFilesTheReportThroughTheRealPipeline()
    {
        var (serverId, secret) = await SeedServerAsync();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(serverId, secret, Payload()));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var report = Assert.Single(await ReportsAsync(serverId));
        Assert.Equal("Cheating", report.Subject);
        Assert.Equal(ServerReportType.Cheat, report.Type);
        Assert.Equal("76561198000000002", report.ReporterSteamId);
    }

    /// <summary>
    /// ADR-0001 promises no cap on stored screenshots. ASP.NET's default form limit is 4 MB per value, so without the limits on the
    /// endpoint a 6 MB picture would be refused; this proves it is filed and stored whole.
    /// </summary>
    [Fact]
    public async Task ASixMegabyteScreenshotIsNotDroppedByTheDefaultFormLimits()
    {
        var (serverId, secret) = await SeedServerAsync();
        var jpeg = new byte[6 * 1024 * 1024];
        new Random(7).NextBytes(jpeg);
        jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF;
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(serverId, secret, Payload(Convert.ToBase64String(jpeg))));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var report = Assert.Single(await ReportsAsync(serverId));
        Assert.Equal(jpeg.Length, report.ScreenshotBytes);
        Assert.Equal(jpeg, factory.Storage.Objects[report.ScreenshotObjectKey!]);
        Assert.DoesNotContain("Image", report.NativePayload!); // the picture never goes into the raw payload
    }

    [Fact]
    public async Task ACallerWithoutTheInternalServiceKeyIsTurnedAwayEvenWithAGoodSecret()
    {
        var (serverId, secret) = await SeedServerAsync();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(serverId, secret, Payload(), withKey: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await ReportsAsync(serverId));
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AWrongOrMissingSecretIsANotFoundAndFilesNothing(string? token)
    {
        var (serverId, _) = await SeedServerAsync();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(serverId, token, Payload()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ReportsAsync(serverId));
    }

    [Fact]
    public async Task OneServersSecretDoesNothingAtAnother()
    {
        var (serverA, secretA) = await SeedServerAsync();
        var (serverB, _) = await SeedServerAsync();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(serverB, secretA, Payload()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ReportsAsync(serverB));
        Assert.Empty(await ReportsAsync(serverA));
    }

    [Fact]
    public async Task AServerThatHasNeverHadASecretMintedAcceptsNothing()
    {
        var serverId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        await factory.WithDatabaseAsync(async db =>
        {
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = $"No-secret tenant {tenantId}", IsActive = true });
            db.Set<RustServer>().Add(new RustServer { Id = serverId, TenantId = tenantId, Name = $"No-secret {serverId}", Host = "192.0.2.10", RconPassword = "unused" });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Post(serverId, ReportForwardingService.NewSecret(), Payload()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ReportsAsync(serverId));
    }

    [Fact]
    public async Task ARefusalIsIdenticalWhetherTheServerExistsOrNot()
    {
        var (serverId, _) = await SeedServerAsync();
        using var client = factory.CreateClient();

        using var knownServer = await client.SendAsync(Post(serverId, "wrong-secret", Payload()));
        using var unknownServer = await client.SendAsync(Post(Guid.NewGuid(), "wrong-secret", Payload()));

        Assert.Equal(knownServer.StatusCode, unknownServer.StatusCode);
        Assert.Equal(knownServer.Content.Headers.ContentType, unknownServer.Content.Headers.ContentType);

        // The bodies differ only by the per-request trace id ASP.NET adds to every problem response; nothing in either says which
        // of "no such server", "no secret" or "wrong secret" it was.
        static string WithoutTraceId(string body) => System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "\"traceId\":\"-\"");
        Assert.Equal(WithoutTraceId(await knownServer.Content.ReadAsStringAsync()), WithoutTraceId(await unknownServer.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task AJsonBodyIsRefusedEvenWithAGoodSecret()
    {
        var (serverId, secret) = await SeedServerAsync();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/reports/{serverId}")
        {
            Content = new StringContent(Payload(), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Internal-Api-Key", InternalKey);
        request.Headers.Add("X-RustArchon-Report-Token", secret);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ReportsAsync(serverId));
    }

    [Fact]
    public async Task ASecretRotatedAwayStopsWorkingAtOnce()
    {
        var (serverId, oldSecret) = await SeedServerAsync();
        var newSecret = ReportForwardingService.NewSecret();
        await factory.WithDatabaseAsync(async db =>
        {
            var protector = new ApiKeyProtector(factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
            var row = await db.Set<RustServer>().AcrossAllTenants().SingleAsync(s => s.Id == serverId);
            row.ReportsSecret = protector.Protect(ApiKeyProtectorPurposes.ReportsSecret, newSecret);
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        using var old = await client.SendAsync(Post(serverId, oldSecret, Payload()));
        using var current = await client.SendAsync(Post(serverId, newSecret, Payload()));

        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
    }
}
