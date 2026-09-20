// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Hubs;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Turning what a game server sends into a report, and merging the two delivery routes into one row (ADR-0003). The fixtures are
/// built from the payload Facepunch documents (https://wiki.facepunch.com/rust/receiving-reports); they prove our parsing, storage
/// and merging, not what a real Rust server sends - that cannot be tested without filing a real report with Facepunch.
/// </summary>
public class ReportIngestServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStorage : IObjectStorage
    {
        public readonly Dictionary<string, (byte[] Content, string ContentType)> Objects = [];
        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
        {
            Objects[key] = (content, contentType);
            return Task.CompletedTask;
        }

        public Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<ObjectContent?>(null);
        public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Kit
    {
        public readonly MutableClock Clock = new(Start);
        public readonly MemoryStorage Storage = new();
        public readonly Mock<IClientProxy> Group = new();
        public readonly Mock<IHubContext<RconHub>> Hub = new();
        public ReportIngestService Service = null!;
    }

    private Kit NewKit()
    {
        var kit = new Kit();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(kit.Group.Object);
        kit.Hub.Setup(h => h.Clients).Returns(clients.Object);
        kit.Service = new ReportIngestService(
            new ServerReportRepository(new ApiDbContext(postgres.Options)), kit.Storage, kit.Hub.Object, kit.Clock,
            NullLogger<ReportIngestService>.Instance);
        return kit;
    }

    private async Task<RustServer> SeedServerAsync()
    {
        await using var context = new ApiDbContext(postgres.Options);
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"Ingest tenant {Guid.NewGuid()}", IsActive = true };
        context.Set<Tenant>().Add(tenant);
        var server = new RustServer
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Name = $"Ingest server {Guid.NewGuid()}",
            Host = "192.0.2.10", RconPassword = "unused"
        };
        context.Set<RustServer>().Add(server);
        await context.SaveChangesAsync();
        return server;
    }

    private async Task<List<ServerReport>> RowsAsync(Guid serverId)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.ServerReports.AcrossAllTenants().Where(r => r.RustServerId == serverId).ToListAsync();
    }

    private const string ReporterId = "76561198000000002";
    private const string TargetId = "76561198000000001";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];

    private static string Payload(
        object? type = null, string subject = "Cheating", string message = "aimbot",
        string? targetId = TargetId, string? targetName = "Cheater", string? image = null, string? userId = ReporterId) =>
        JsonSerializer.Serialize(new
        {
            Subject = subject,
            Message = message,
            Type = type ?? 2,
            TargetReportType = (string?)null,
            TargetId = targetId,
            TargetName = targetName,
            AppInfo = new
            {
                Version = 2500,
                Name = "PC",
                UserId = userId,
                UserName = "Reporter",
                ServerAddress = "192.0.2.10:28015",
                LevelPos = "(10.5, 20, 30)",
                MinutesPlayed = 42,
                Image = image
            }
        });

    private static PluginReportInput PluginReport(
        string? reporter = ReporterId, string? target = null, string? targetName = null, string subject = "Cheating",
        ServerReportType type = ServerReportType.Cheat, string? detail = "{\"nearby\":3}") =>
        new(reporter, "Reporter", target, targetName, type, subject, "aimbot", detail, "{\"raw\":\"plugin\"}");

    // ---- the native route ----

    [Fact]
    public async Task ANativeReportIsStoredWithEveryFieldTheGameSent()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        var result = await kit.Service.IngestNativeAsync(server, ReporterId, Payload());

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.Equal(result.ReportId, row.Id);
        Assert.False(result.Merged);
        Assert.Equal(server.TenantId, row.TenantId);
        Assert.Equal(ServerReportSource.Native, row.Source);
        Assert.Equal(ServerReportType.Cheat, row.Type);
        Assert.Equal(ServerReportStatus.New, row.Status);
        Assert.Equal(ReporterId, row.ReporterSteamId);
        Assert.Equal("Reporter", row.ReporterName);
        Assert.Equal(TargetId, row.TargetSteamId);
        Assert.Equal("Cheater", row.TargetName);
        Assert.Equal("Cheating", row.Subject);
        Assert.Equal("aimbot", row.Message);
        Assert.Equal("(10.5, 20, 30)", row.Position);
        Assert.Equal(42, row.MinutesPlayed);
        Assert.Equal(Start, row.ReceivedAtUtc);
        Assert.False(row.ParseFailed);
        Assert.NotNull(row.NativePayload);
        Assert.Null(row.PluginPayload);
    }

    [Fact]
    public async Task TheUseridFieldIsTheReporterAndTheReportsOwnCopyIsTheFallback()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(server, "76561198111111111", Payload());
        kit.Clock.Now = Start.AddMinutes(10);
        await kit.Service.IngestNativeAsync(server, null, Payload(userId: "76561198222222222"));

        var reporters = (await RowsAsync(server.Id)).Select(r => r.ReporterSteamId).OrderBy(x => x).ToList();
        Assert.Equal(["76561198111111111", "76561198222222222"], reporters);
    }

    [Fact]
    public async Task AJpegScreenshotIsStoredOnItsOwnAndKeptOutOfTheRawPayload()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();
        var encoded = Convert.ToBase64String(Jpeg.Concat(new byte[5000]).ToArray());

        var result = await kit.Service.IngestNativeAsync(server, ReporterId, Payload(image: encoded));

        var row = Assert.Single(await RowsAsync(server.Id));
        var key = $"reports/{server.Id}/{result.ReportId}.jpg";
        Assert.Equal(key, row.ScreenshotObjectKey);
        Assert.Equal(5010, row.ScreenshotBytes);
        Assert.Equal("image/jpeg", kit.Storage.Objects[key].ContentType);
        Assert.Equal(5010, kit.Storage.Objects[key].Content.Length);
        Assert.DoesNotContain(encoded, row.NativePayload);
        Assert.DoesNotContain("\"Image\"", row.NativePayload);
    }

    [Fact]
    public async Task APngScreenshotIsStoredAsAPng()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        var result = await kit.Service.IngestNativeAsync(server, ReporterId, Payload(image: Convert.ToBase64String(Png)));

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.Equal($"reports/{server.Id}/{result.ReportId}.png", row.ScreenshotObjectKey);
        Assert.Equal("image/png", kit.Storage.Objects[row.ScreenshotObjectKey!].ContentType);
    }

    [Fact]
    public async Task ADataUriScreenshotIsUnwrapped()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(server, ReporterId, Payload(image: "data:image/jpeg;base64," + Convert.ToBase64String(Jpeg)));

        Assert.NotNull(Assert.Single(await RowsAsync(server.Id)).ScreenshotObjectKey);
    }

    /// <summary>The picture is identified by its own bytes, not by what the payload says it is.</summary>
    [Theory]
    [InlineData("aGVsbG8gd29ybGQ=")] // "hello world"
    [InlineData("!!! not base64 !!!")]
    public async Task SomethingThatIsNotAPictureIsNotStoredButTheReportStillIs(string image)
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(server, ReporterId, Payload(image: image));

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.Null(row.ScreenshotObjectKey);
        Assert.Empty(kit.Storage.Objects);
        Assert.False(row.ParseFailed);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("")]
    [InlineData(null)]
    public async Task APayloadThatCannotBeUnderstoodIsStoredAndFlaggedNotDropped(string? data)
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(server, ReporterId, data);

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.True(row.ParseFailed);
        Assert.Equal(ReporterId, row.ReporterSteamId);
        Assert.Equal(ServerReportType.General, row.Type);
        Assert.Equal(string.IsNullOrWhiteSpace(data) ? null : data, row.NativePayload);
    }

    [Fact]
    public async Task AnAbsurdlyDeepPayloadIsFlaggedNotFollowed()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 500)) + "1" + new string('}', 500);

        await kit.Service.IngestNativeAsync(server, ReporterId, deep);

        Assert.True(Assert.Single(await RowsAsync(server.Id)).ParseFailed);
    }

    [Theory]
    [InlineData(0, ServerReportType.General)]
    [InlineData(1, ServerReportType.Bug)]
    [InlineData(2, ServerReportType.Cheat)]
    [InlineData(3, ServerReportType.Abuse)]
    [InlineData(4, ServerReportType.Idea)]
    [InlineData("Abuse", ServerReportType.Abuse)]
    [InlineData("cheat", ServerReportType.Cheat)]
    [InlineData("3", ServerReportType.Abuse)]
    [InlineData(99, ServerReportType.General)] // a value Rust adds one day is still a report
    [InlineData(-1, ServerReportType.General)]
    [InlineData("nonsense", ServerReportType.General)]
    public async Task TheReportTypeAcceptsANumberOrANameAndFallsBackToGeneral(object type, ServerReportType expected)
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(server, ReporterId, Payload(type: type));

        Assert.Equal(expected, Assert.Single(await RowsAsync(server.Id)).Type);
    }

    [Fact]
    public async Task TextFromTheGameIsBoundedBeforeItIsStored()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(
            server, ReporterId,
            Payload(subject: new string('s', 500), message: new string('m', 10_000), targetName: new string('n', 300)));

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.Equal(ServerReport.MaxSubjectLength, row.Subject.Length);
        Assert.Equal(ServerReport.MaxMessageLength, row.Message.Length);
        Assert.Equal(ServerReport.MaxNameLength, row.TargetName!.Length);
    }

    [Theory]
    [InlineData("76561198000000001", "76561198000000001")]
    [InlineData("  76561198000000001  ", "76561198000000001")]
    [InlineData("not-a-steam-id", null)]
    [InlineData("76561198000000001; DROP TABLE", null)]
    [InlineData("765611980000000019999", null)] // longer than a SteamID64 can be
    [InlineData("", null)]
    public async Task ASteamIdThatIsNotJustDigitsIsDiscarded(string sent, string? expected)
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(server, ReporterId, Payload(targetId: sent));

        Assert.Equal(expected, Assert.Single(await RowsAsync(server.Id)).TargetSteamId);
    }

    [Fact]
    public async Task ReportsWithoutAnyOfTheOptionalFieldsAreStillStored()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(server, ReporterId, "{\"Subject\":\"hi\"}");

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.Equal("hi", row.Subject);
        Assert.Equal(string.Empty, row.Message);
        Assert.Null(row.TargetSteamId);
        Assert.Null(row.Position);
        Assert.Null(row.MinutesPlayed);
        Assert.False(row.ParseFailed);
    }

    // ---- telling the Panel ----

    [Fact]
    public async Task WatchersOfTheServerAreToldSomethingChangedButNeverGivenTheReport()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(server, ReporterId, Payload());

        kit.Group.Verify(
            g => g.SendCoreAsync("ReceiveServerReportsChanged", It.Is<object?[]>(args => args.Length == 0), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AFailureToTellWatchersDoesNotLoseTheReport()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();
        kit.Group.Setup(g => g.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        await kit.Service.IngestNativeAsync(server, ReporterId, Payload());

        Assert.Single(await RowsAsync(server.Id));
    }

    // ---- merging the two routes (ADR-0003) ----

    [Fact]
    public async Task ThePluginsCopyOfANativeReportMergesIntoOneRowKeepingBothRawPayloads()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();
        var native = await kit.Service.IngestNativeAsync(server, ReporterId, Payload(targetId: null, targetName: null));

        kit.Clock.Now = Start.AddSeconds(30);
        var plugin = await kit.Service.IngestPluginAsync(server, PluginReport(target: TargetId, targetName: "Cheater"));

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.True(plugin.Merged);
        Assert.Equal(native.ReportId, plugin.ReportId);
        Assert.Equal(ServerReportSource.Native | ServerReportSource.Plugin, row.Source);
        Assert.Equal(TargetId, row.TargetSteamId); // what the native copy lacked came from the plugin
        Assert.Equal("Cheater", row.TargetName);
        Assert.Equal("(10.5, 20, 30)", row.Position); // ...and what the plugin lacked stays from native
        Assert.Equal(42, row.MinutesPlayed);
        Assert.NotNull(row.NativePayload);
        Assert.Equal("{\"raw\":\"plugin\"}", row.PluginPayload);
        Assert.Equal("{\"nearby\":3}", row.PluginDetailJson);
        Assert.Equal(Start, row.ReceivedAtUtc); // it is still the earlier arrival
    }

    [Fact]
    public async Task TheOrderTheRoutesArriveInDoesNotMatter()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();
        await kit.Service.IngestPluginAsync(server, PluginReport(target: TargetId));

        kit.Clock.Now = Start.AddSeconds(5);
        var native = await kit.Service.IngestNativeAsync(
            server, ReporterId, Payload(image: Convert.ToBase64String(Jpeg)));

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.True(native.Merged);
        Assert.Equal(ServerReportSource.Native | ServerReportSource.Plugin, row.Source);
        Assert.NotNull(row.ScreenshotObjectKey); // the screenshot only the native route carries
        Assert.Equal("{\"nearby\":3}", row.PluginDetailJson);
    }

    [Fact]
    public async Task AMergeNeverOverwritesWhatTheFirstArrivalAlreadyHad()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();
        await kit.Service.IngestNativeAsync(server, ReporterId, Payload(targetId: TargetId, targetName: "Original"));

        await kit.Service.IngestPluginAsync(server, PluginReport(target: "76561198999999999", targetName: "Different"));

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.Equal(TargetId, row.TargetSteamId);
        Assert.Equal("Original", row.TargetName);
    }

    [Fact]
    public async Task TwoCopiesFromTheSameRouteAreTwoReportsNotAMerge()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(server, ReporterId, Payload());
        await kit.Service.IngestNativeAsync(server, ReporterId, Payload());

        Assert.Equal(2, (await RowsAsync(server.Id)).Count);
    }

    [Theory]
    [InlineData("other-reporter")]
    [InlineData("other-type")]
    [InlineData("other-subject")]
    [InlineData("too-late")]
    [InlineData("no-reporter")]
    public async Task ReportsThatAreNotTheSameEventStaySeparate(string difference)
    {
        var kit = NewKit();
        var server = await SeedServerAsync();
        var noReporter = difference == "no-reporter";
        await kit.Service.IngestNativeAsync(server, noReporter ? null : ReporterId, Payload(userId: null));

        if (difference == "too-late")
        {
            kit.Clock.Now = Start + ReportIngestService.MergeWindow + TimeSpan.FromSeconds(1);
        }

        await kit.Service.IngestPluginAsync(server, difference switch
        {
            "other-reporter" => PluginReport(reporter: "76561198777777777"),
            "other-type" => PluginReport(type: ServerReportType.Abuse),
            "other-subject" => PluginReport(subject: "Something else"),
            "no-reporter" => PluginReport(reporter: null),
            _ => PluginReport()
        });

        Assert.Equal(2, (await RowsAsync(server.Id)).Count);
    }

    [Fact]
    public async Task ReportsOnDifferentServersNeverMerge()
    {
        var kit = NewKit();
        var first = await SeedServerAsync();
        var second = await SeedServerAsync();

        await kit.Service.IngestNativeAsync(first, ReporterId, Payload());
        await kit.Service.IngestPluginAsync(second, PluginReport());

        Assert.Single(await RowsAsync(first.Id));
        Assert.Single(await RowsAsync(second.Id));
    }

    [Fact]
    public async Task APluginReportAloneIsAReportOfItsOwn()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        var result = await kit.Service.IngestPluginAsync(server, PluginReport(target: TargetId, targetName: "Cheater"));

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.False(result.Merged);
        Assert.Equal(ServerReportSource.Plugin, row.Source);
        Assert.Null(row.NativePayload);
        Assert.Equal(TargetId, row.TargetSteamId);
    }

    [Fact]
    public async Task PluginTextIsBoundedAndItsSteamIdsCheckedLikeTheNativeRoutes()
    {
        var kit = NewKit();
        var server = await SeedServerAsync();

        await kit.Service.IngestPluginAsync(server, new PluginReportInput(
            "not-an-id", new string('r', 500), "also-not", new string('t', 500), (ServerReportType)99,
            new string('s', 500), new string('m', 10_000), new string('d', 500_000), new string('p', 500_000)));

        var row = Assert.Single(await RowsAsync(server.Id));
        Assert.Null(row.ReporterSteamId);
        Assert.Null(row.TargetSteamId);
        Assert.Equal(ServerReportType.General, row.Type);
        Assert.Equal(ServerReport.MaxNameLength, row.ReporterName!.Length);
        Assert.Equal(ServerReport.MaxSubjectLength, row.Subject.Length);
        Assert.Equal(ServerReport.MaxMessageLength, row.Message.Length);
        Assert.Equal(ServerReport.MaxPayloadLength, row.PluginDetailJson!.Length);
        Assert.Equal(ServerReport.MaxPayloadLength, row.PluginPayload!.Length);
    }
}
