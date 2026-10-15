// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Api.Repositories;
using RustArchon.Diagnostics;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// Reading a game server's RustArchon plugin logs on demand through the Worker's RCON connection (ADR-0006): the drain loop, the cursor and gap maths,
/// and above all that every way the plugin can be unreachable is named instead of looking like an empty log.
/// </summary>
public class PluginLogSourceTests
{
    private static readonly Guid ServerId = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IRustServerRepository> _servers = new();
    private readonly Mock<IServerPluginStatusRepository> _statuses = new();
    private readonly Mock<IRequestClient<SendRconCommand>> _client = new();
    private readonly List<SendRconCommand> _sent = [];
    private readonly Queue<RconCommandResult> _replies = new();

    private PluginLogSource Source() => new(ServerId, _servers.Object, _statuses.Object, _client.Object);

    private void GivenServer(RconConnectionStatus status = RconConnectionStatus.Connected) =>
        _servers.Setup(r => r.GetByIdAcrossTenantsAsync(ServerId)).ReturnsAsync(new RustServer
        {
            Id = ServerId, TenantId = TenantId, ConnectionStatus = status,
        });

    private void GivenStatus(string version = "1.4.0", params string[] capabilities) =>
        _statuses.Setup(r => r.GetForServerAcrossTenantsAsync(TenantId, ServerId)).ReturnsAsync(new ServerPluginStatus
        {
            TenantId = TenantId, RustServerId = ServerId, PluginVersion = version,
            Capabilities = capabilities.Length == 0 ? ["config", "logs"] : capabilities,
        });

    private void GivenReady()
    {
        GivenServer();
        GivenStatus();
    }

    private void WillReply(params string[] messages)
    {
        foreach (var message in messages)
        {
            _replies.Enqueue(new RconCommandResult(true, message, null, null, null));
        }

        _client
            .Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .Callback<SendRconCommand, CancellationToken, RequestTimeout>((request, _, _) => _sent.Add(request))
            .ReturnsAsync(() => Mock.Of<Response<RconCommandResult>>(r => r.Message == _replies.Dequeue()));
    }

    private void WillFail(string error) =>
        _client
            .Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .ReturnsAsync(Mock.Of<Response<RconCommandResult>>(r => r.Message == new RconCommandResult(false, null, null, null, error)));

    /// <summary>A plugin drain reply: items are (seq, level 0-5, message).</summary>
    private static string Drain(long bootId, long head, long oldest, long cursor, bool reset, params (long Seq, int Level, string Message)[] items)
    {
        var sb = new StringBuilder();
        sb.Append("{\"v\":1,\"ok\":true,\"data\":{\"format\":1,\"bootId\":").Append(bootId)
            .Append(",\"head\":").Append(head).Append(",\"oldest\":").Append(oldest).Append(",\"cursor\":").Append(cursor)
            .Append(",\"lost\":false,\"reset\":").Append(reset ? "true" : "false").Append(",\"logs\":[");
        sb.Append(string.Join(',', items.Select(i =>
            "{\"s\":" + i.Seq + ",\"t\":\"2026-10-14T12:00:" + (i.Seq % 60).ToString("D2") + ".000Z\",\"l\":" + i.Level
            + ",\"c\":\"update\",\"m\":\"" + i.Message + "\",\"p\":{\"Plugin\":\"Foo\"}}")));
        sb.Append("]}}");
        return sb.ToString();
    }

    private static Task<LogSourceResult> Read(PluginLogSource source, SourceCursor? after = null, int max = 100, Func<LogEntry, bool>? filter = null) =>
        source.ReadAsync(after, max, filter ?? (_ => true), CancellationToken.None).AsTask();

    // ---- every way it can be unavailable is named -------------------------------------------------------

    [Fact]
    public async Task AnUnknownServerIsNamedUnavailable()
    {
        _servers.Setup(r => r.GetByIdAcrossTenantsAsync(ServerId)).ReturnsAsync((RustServer?)null);

        var result = await Read(Source());

        Assert.False(result.Available);
        Assert.Contains("no server", result.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("plugin:" + ServerId, result.Name);
    }

    [Fact]
    public async Task AServerWhosePluginHasNeverReportedIsUnavailableWithThatReason()
    {
        GivenServer();
        _statuses.Setup(r => r.GetForServerAcrossTenantsAsync(TenantId, ServerId)).ReturnsAsync((ServerPluginStatus?)null);

        var result = await Read(Source());

        Assert.False(result.Available);
        Assert.Contains("never reported", result.UnavailableReason);
    }

    [Fact]
    public async Task APluginThatDoesNotReportTheLogsCapabilityIsTooOldAndSaysWhich()
    {
        GivenServer();
        GivenStatus("1.2.0", "config", "combat");

        var result = await Read(Source());

        Assert.False(result.Available);
        Assert.Contains("1.2.0", result.UnavailableReason);
        Assert.Contains("logs", result.UnavailableReason);
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData(RconConnectionStatus.Disconnected)]
    [InlineData(RconConnectionStatus.Connecting)]
    [InlineData(RconConnectionStatus.Reconnecting)]
    [InlineData(RconConnectionStatus.Error)]
    public async Task AServerWhoseRconConnectionIsNotUpIsUnavailableWithItsStatus(RconConnectionStatus status)
    {
        GivenServer(status);
        GivenStatus();

        var result = await Read(Source());

        Assert.False(result.Available);
        Assert.Contains(status.ToString(), result.UnavailableReason);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AFailedSendIsUnavailableWithTheWorkersError()
    {
        GivenReady();
        WillFail("NotConnected");

        var result = await Read(Source());

        Assert.False(result.Available);
        Assert.Contains("NotConnected", result.UnavailableReason);
    }

    [Fact]
    public async Task ATimeoutIsUnavailableAndPointsAtTheWorkerAndTheBroker()
    {
        GivenReady();
        _client
            .Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .ThrowsAsync(new RequestTimeoutException("timed out"));

        var result = await Read(Source());

        Assert.False(result.Available);
        Assert.Contains("RabbitMQ", result.UnavailableReason);
    }

    [Theory]
    [InlineData("Unknown command: archon.logs.drain")]
    [InlineData("not json at all")]
    [InlineData("{\"v\":1,\"ok\":false,\"err\":\"usage\",\"message\":\"bad\"}")]
    [InlineData("{\"v\":2,\"ok\":true,\"data\":{}}")]
    [InlineData("{\"v\":1,\"ok\":true,\"data\":{\"format\":1}}")]
    public async Task AReplyThatIsNotAValidLogDrainIsUnavailableNotEmpty(string reply)
    {
        GivenReady();
        WillReply(reply);

        var result = await Read(Source());

        Assert.False(result.Available);
        Assert.NotEmpty(result.UnavailableReason!);
    }

    // ---- reading ----------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutACursorItReadsFromTheStartAndReturnsTheMostRecentMatchesUpToTheHead()
    {
        GivenReady();
        WillReply(
            Drain(500, 6, 1, 3, false, (1, 2, "a"), (2, 2, "b"), (3, 2, "c")),
            Drain(500, 6, 1, 6, false, (4, 2, "d"), (5, 2, "e"), (6, 2, "f")));

        var result = await Read(Source(), max: 4);

        Assert.True(result.Available);
        Assert.Equal(new long[] { 3, 4, 5, 6 }, result.Page!.Entries.Select(e => e.Seq));
        Assert.Equal(6, result.Page.ReturnedThrough);
        Assert.Equal(6, result.Page.Head);
        Assert.Equal("500", result.Page.InstanceId);
        Assert.Equal("archon.logs.drain 0 0 " + PluginLogSource.PageSize, _sent[0].Command);
        Assert.Equal("archon.logs.drain 500 3 " + PluginLogSource.PageSize, _sent[1].Command);
        Assert.All(_sent, s => Assert.False(s.Interactive));
        Assert.All(_sent, s => Assert.Equal(ServerId, s.ServerId));
    }

    [Fact]
    public async Task ATailReadDoesNotCountEntriesThatScrolledOutBeforeItAsLost()
    {
        GivenReady();
        WillReply(Drain(500, 100, 91, 100, false, (91, 2, "x")));

        var result = await Read(Source());

        Assert.Equal(0, result.Page!.Lost);
        Assert.False(result.Page.Reset);
    }

    [Fact]
    public async Task WithACursorItAsksFromThereAndReportsWhereItStopped()
    {
        GivenReady();
        WillReply(Drain(500, 12, 1, 12, false, (11, 2, "k"), (12, 2, "l")));

        var result = await Read(Source(), new SourceCursor("500", 10));

        Assert.Equal("archon.logs.drain 500 10 " + PluginLogSource.PageSize, _sent[0].Command);
        Assert.Equal(new long[] { 11, 12 }, result.Page!.Entries.Select(e => e.Seq));
        Assert.Equal(12, result.Page.ReturnedThrough);
        Assert.Equal(0, result.Page.Lost);
    }

    [Fact]
    public async Task ACursorBehindWhatThePluginStillHoldsReportsTheEntriesLost()
    {
        GivenReady();
        WillReply(Drain(500, 2000, 1801, 2000, false, (1801, 2, "k")));

        var result = await Read(Source(), new SourceCursor("500", 1000));

        Assert.Equal(800, result.Page!.Lost);
    }

    [Fact]
    public async Task APluginThatRestartedIsAResetAndItsNewInstanceIsReported()
    {
        GivenReady();
        WillReply(Drain(777, 3, 1, 3, true, (1, 2, "a"), (2, 2, "b"), (3, 2, "c")));

        var result = await Read(Source(), new SourceCursor("500", 1000));

        Assert.True(result.Page!.Reset);
        Assert.Equal("777", result.Page.InstanceId);
        Assert.Equal(0, result.Page.Lost);
        Assert.Equal(3, result.Page.Entries.Count);
    }

    [Fact]
    public async Task ACursorWhoseInstanceIsNotANumberIsTreatedAsAFirstReadThatIsAReset()
    {
        GivenReady();
        WillReply(Drain(777, 1, 1, 1, true, (1, 2, "a")));

        var result = await Read(Source(), new SourceCursor("not-a-number", 5));

        Assert.StartsWith("archon.logs.drain 0 0 ", _sent[0].Command);
        Assert.True(result.Page!.Reset);
    }

    [Fact]
    public async Task FilteredOutEntriesAreSkippedButTheCursorStillAdvancesPastThem()
    {
        GivenReady();
        WillReply(Drain(500, 4, 1, 4, false, (1, 1, "debug"), (2, 5, "boom"), (3, 1, "debug"), (4, 1, "debug")));

        var result = await Read(Source(), new SourceCursor("500", 0), filter: e => e.Level >= LogLevel.Error);

        Assert.Equal(new long[] { 2 }, result.Page!.Entries.Select(e => e.Seq));
        Assert.Equal(4, result.Page.ReturnedThrough);
    }

    [Fact]
    public async Task WhenTheCountIsReachedWithACursorItStopsAtTheLastReturnedEntry()
    {
        GivenReady();
        WillReply(Drain(500, 9, 1, 5, false, (1, 2, "a"), (2, 2, "b"), (3, 2, "c"), (4, 2, "d"), (5, 2, "e")));

        var result = await Read(Source(), new SourceCursor("500", 0), max: 2);

        Assert.Equal(new long[] { 1, 2 }, result.Page!.Entries.Select(e => e.Seq));
        Assert.Equal(2, result.Page.ReturnedThrough);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task AnEndlessSourceIsStoppedAfterTheRoundLimitAndTheCallerContinuesFromWhereItGot()
    {
        GivenReady();
        for (var round = 0; round < PluginLogSource.MaxRounds + 5; round++)
        {
            WillReply(Drain(500, 1_000_000, 1, (round + 1) * 10L, false, (round * 10L + 1, 1, "x")));
        }

        var result = await Read(Source(), new SourceCursor("500", 0), max: 1000, filter: _ => false);

        Assert.True(result.Available);
        Assert.Equal(PluginLogSource.MaxRounds, _sent.Count);
        Assert.Equal(PluginLogSource.MaxRounds * 10L, result.Page!.ReturnedThrough);
    }

    [Fact]
    public async Task EntriesCarryTheirFieldsAndAreTaggedWithThePluginSource()
    {
        GivenReady();
        WillReply(Drain(500, 1, 1, 1, false, (1, 3, "update refused")));

        var entry = (await Read(Source())).Page!.Entries.Single();

        Assert.Equal("plugin:" + ServerId, entry.Source);
        Assert.Equal("500", entry.InstanceId);
        Assert.Equal(1, entry.Seq);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("update", entry.Category);
        Assert.Equal("update refused", entry.Message);
        Assert.Equal("Foo", entry.Properties["Plugin"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 12, 0, 1, TimeSpan.Zero), entry.TimestampUtc);
    }
}
