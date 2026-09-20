// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Messaging;
using RustArchon.Api.Repositories;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="PluginSettingsSynchronizer"/> and the two consumers that drive it. Repositories and the
/// request client are mocked: the interesting behavior is when a command is (not) sent, that it is sent
/// non-interactively, and that "applied" is only recorded when the plugin actually answered ok.
/// </summary>
public class PluginSettingsSynchronizerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ServerId = Guid.NewGuid();
    private const string OkReply = "{\"v\":1,\"ok\":true,\"data\":{\"recording\":false,\"combat\":true,\"persisted\":true}}";
    private const string ErrReply = "{\"v\":1,\"ok\":false,\"err\":\"unknown_key\",\"message\":\"no\"}";

    private readonly Mock<IRustServerRepository> _servers = new();
    private readonly Mock<IServerPluginStatusRepository> _statuses = new();
    private readonly Mock<IRequestClient<SendRconCommand>> _client = new();
    private readonly List<SendRconCommand> _sent = [];

    private PluginSettingsSynchronizer Create() =>
        new(_servers.Object, _statuses.Object, _client.Object, NullLogger<PluginSettingsSynchronizer>.Instance);

    private void GivenServer(bool recording = true, bool combat = true, bool enabled = true, Guid? tenantId = null) =>
        _servers.Setup(r => r.GetByIdAcrossTenantsAsync(ServerId)).ReturnsAsync(new RustServer
        {
            Id = ServerId,
            TenantId = tenantId ?? TenantId,
            IsEnabled = enabled,
            PluginRecordingEnabled = recording,
            PluginCombatLogEnabled = combat
        });

    private void GivenStatus(bool recording = true, bool combat = true, params string[] capabilities) =>
        _statuses.Setup(r => r.GetForServerAcrossTenantsAsync(TenantId, ServerId)).ReturnsAsync(new ServerPluginStatus
        {
            TenantId = TenantId,
            RustServerId = ServerId,
            Capabilities = capabilities.Length == 0 ? ["config"] : capabilities,
            ReportedRecordingEnabled = recording,
            ReportedCombatLogEnabled = combat
        });

    private void GivenPluginReplies(string message, bool success = true) =>
        _client
            .Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .Callback<SendRconCommand, CancellationToken, RequestTimeout>((request, _, _) => _sent.Add(request))
            .ReturnsAsync(Mock.Of<Response<RconCommandResult>>(r =>
                r.Message == new RconCommandResult(success, message, null, null, success ? null : "NotConnected")));

    // ---- when nothing is sent -------------------------------------------------------------------------------

    [Fact]
    public async Task DoesNothingBeforeThePluginHasEverSaidHello()
    {
        GivenServer(recording: false);
        _statuses.Setup(r => r.GetForServerAcrossTenantsAsync(TenantId, ServerId)).ReturnsAsync((ServerPluginStatus?)null);

        await Create().SyncAsync(TenantId, ServerId);

        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DoesNothingWhenReportedAlreadyMatchesDesired()
    {
        GivenServer(recording: true, combat: false);
        GivenStatus(recording: true, combat: false);

        await Create().SyncAsync(TenantId, ServerId);

        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DoesNothingForADisabledServer()
    {
        GivenServer(recording: false, enabled: false);
        GivenStatus(recording: true);

        await Create().SyncAsync(TenantId, ServerId);

        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DoesNothingWhenTheServerBelongsToADifferentTenant()
    {
        // A message naming the wrong tenant must never reach another tenant's server.
        GivenServer(recording: false, tenantId: Guid.NewGuid());
        GivenStatus(recording: true);

        await Create().SyncAsync(TenantId, ServerId);

        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DoesNothingWhenTheServerIsUnknown()
    {
        _servers.Setup(r => r.GetByIdAcrossTenantsAsync(ServerId)).ReturnsAsync((RustServer?)null);

        await Create().SyncAsync(TenantId, ServerId);

        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendsNothingWhenThePluginDidNotReportTheConfigCapability()
    {
        GivenServer(recording: false);
        GivenStatus(recording: true, combat: true, "recording", "combat"); // no "config"

        await Create().SyncAsync(TenantId, ServerId);

        _client.VerifyNoOtherCalls();
    }

    // ---- sending -------------------------------------------------------------------------------------------

    [Fact]
    public async Task SendsTheDifferingSettingNonInteractivelyToTheRightServer()
    {
        GivenServer(recording: false, combat: true);
        GivenStatus(recording: true, combat: true);
        GivenPluginReplies(OkReply);

        await Create().SyncAsync(TenantId, ServerId);

        var command = Assert.Single(_sent);
        Assert.Equal(ServerId, command.ServerId);
        Assert.Equal("archon.config set recording false", command.Command);
        Assert.False(command.Interactive); // otherwise it would show in the Console tab as something a person typed
    }

    [Fact]
    public async Task RecordsTheNewStateOnlyAfterThePluginAnswersOk()
    {
        GivenServer(recording: false, combat: true);
        GivenStatus(recording: true, combat: true);
        GivenPluginReplies(OkReply);

        await Create().SyncAsync(TenantId, ServerId);

        _statuses.Verify(r => r.MarkSettingsAppliedAsync(TenantId, ServerId, false, true), Times.Once);
    }

    [Fact]
    public async Task DoesNotRecordAnythingWhenThePluginRefusesTheChange()
    {
        GivenServer(recording: false);
        GivenStatus(recording: true);
        GivenPluginReplies(ErrReply);

        await Create().SyncAsync(TenantId, ServerId);

        _statuses.Verify(r => r.MarkSettingsAppliedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task DoesNotRecordAnythingWhenTheServerIsNotConnected()
    {
        GivenServer(recording: false);
        GivenStatus(recording: true);
        GivenPluginReplies("", success: false);

        await Create().SyncAsync(TenantId, ServerId);

        _statuses.Verify(r => r.MarkSettingsAppliedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ATimeoutIsSwallowedSoTheNextHandshakeCanRetry()
    {
        GivenServer(recording: false);
        GivenStatus(recording: true);
        _client
            .Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .ThrowsAsync(new RequestTimeoutException());

        await Create().SyncAsync(TenantId, ServerId); // must not throw

        _statuses.Verify(r => r.MarkSettingsAppliedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task StopsAtTheFirstRefusalButKeepsWhatAlreadyWorked()
    {
        GivenServer(recording: false, combat: false);
        GivenStatus(recording: true, combat: true);
        var replies = new Queue<string>([OkReply, ErrReply]);
        _client
            .Setup(c => c.GetResponse<RconCommandResult>(It.IsAny<SendRconCommand>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .Callback<SendRconCommand, CancellationToken, RequestTimeout>((request, _, _) => _sent.Add(request))
            .ReturnsAsync(() => Mock.Of<Response<RconCommandResult>>(r =>
                r.Message == new RconCommandResult(true, replies.Dequeue(), null, null, null)));

        await Create().SyncAsync(TenantId, ServerId);

        Assert.Equal(2, _sent.Count);
        // recording was switched off; combat was refused, so it stays recorded as on.
        _statuses.Verify(r => r.MarkSettingsAppliedAsync(TenantId, ServerId, false, true), Times.Once);
    }

    // ---- consumers ------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandshakeConsumerStoresTheReportThenSyncs()
    {
        var synchronizer = new Mock<IPluginSettingsSynchronizer>();
        var statuses = new Mock<IServerPluginStatusRepository>();
        ServerPluginStatus? stored = null;
        statuses
            .Setup(r => r.UpsertAsync(TenantId, ServerId, It.IsAny<ServerPluginStatus>()))
            .Callback<Guid, Guid, ServerPluginStatus>((_, _, s) => stored = s)
            .Returns(Task.CompletedTask);
        var capturedAt = DateTimeOffset.UtcNow;
        var message = new ServerPluginHandshakeCaptured(
            ServerId, TenantId, 1, "0.1.0", ["config"], RecordingEnabled: false, CombatLogEnabled: true, SettingsPersisted: true, capturedAt);
        var context = Mock.Of<ConsumeContext<ServerPluginHandshakeCaptured>>(c => c.Message == message);

        await new ServerPluginHandshakeCapturedConsumer(statuses.Object, synchronizer.Object).Consume(context);

        Assert.NotNull(stored);
        Assert.Equal("0.1.0", stored!.PluginVersion);
        Assert.Equal(["config"], stored.Capabilities);
        Assert.False(stored.ReportedRecordingEnabled);
        Assert.True(stored.ReportedCombatLogEnabled);
        Assert.Equal(capturedAt, stored.CapturedAtUtc);
        synchronizer.Verify(s => s.SyncAsync(TenantId, ServerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SettingsChangedConsumerSyncsTheNamedServer()
    {
        var synchronizer = new Mock<IPluginSettingsSynchronizer>();
        var message = new ServerPluginSettingsChanged(ServerId, TenantId);
        var context = Mock.Of<ConsumeContext<ServerPluginSettingsChanged>>(c => c.Message == message);

        await new ServerPluginSettingsChangedConsumer(synchronizer.Object).Consume(context);

        synchronizer.Verify(s => s.SyncAsync(TenantId, ServerId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
