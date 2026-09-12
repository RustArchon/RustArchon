// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="OrganizationLifecycleService"/>'s email notifications - the status-change
/// machinery itself (server stop/restore, the Cancelled guard) predates this and isn't retested here.
/// </summary>
public class OrganizationLifecycleServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task SeedAsync(ApiDbContext context, string? contactEmail)
    {
        context.Set<Tenant>().Add(new Tenant { Id = _tenantId, Name = "Acme", IsActive = true, ContactEmail = contactEmail });
        context.Set<Subscription>().Add(new Subscription
        {
            TenantId = _tenantId,
            PlanId = Guid.NewGuid(),
            StartDate = DateTimeOffset.UtcNow,
            Status = SubscriptionStatus.Active
        });
        await context.SaveChangesAsync();
    }

    private static OrganizationLifecycleService CreateService(
        ApiDbContext context, Mock<ICommunicationPublisher> communicationPublisher) =>
        new(
            context,
            Mock.Of<IPublishEndpoint>(),
            communicationPublisher.Object,
            new FixedClock(DateTimeOffset.UtcNow),
            NullLogger<OrganizationLifecycleService>.Instance);

    [Fact]
    public async Task SuspendingQueuesTheCutoffNoticeWhenAContactEmailIsOnFile()
    {
        await using var context = CreateContext();
        await SeedAsync(context, "owner@acme.example");

        var communicationPublisher = new Mock<ICommunicationPublisher>();
        string? templateCode = null;
        IReadOnlyDictionary<string, string>? tokens = null;

        communicationPublisher
            .Setup(p => p.QueueTemplatedAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, string>, string, Guid?, Guid?, string?, CancellationToken>(
                (code, sentTokens, _, _, _, _, _) => { templateCode = code; tokens = sentTokens; })
            .ReturnsAsync(Guid.NewGuid());

        await CreateService(context, communicationPublisher)
            .SetStatusAsync(_tenantId, SubscriptionStatus.Suspended, "Non-payment.");

        Assert.Equal(EmailTemplateRegistry.Codes.SubscriptionSuspended, templateCode);
        Assert.NotNull(tokens);
        Assert.Equal("Acme", tokens[EmailTemplateRegistry.Placeholders.OrganizationName]);
        Assert.Equal("Non-payment.", tokens[EmailTemplateRegistry.Placeholders.Reason]);
    }

    [Fact]
    public async Task SuspendingWithNoContactEmailQueuesNothingAndDoesNotThrow()
    {
        await using var context = CreateContext();
        await SeedAsync(context, contactEmail: null);

        var communicationPublisher = new Mock<ICommunicationPublisher>();

        await CreateService(context, communicationPublisher)
            .SetStatusAsync(_tenantId, SubscriptionStatus.Suspended, "Non-payment.");

        communicationPublisher.Verify(
            p => p.QueueTemplatedAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReinstatingFromSuspendedToActiveSendsTheReactivatedNotice()
    {
        await using var context = CreateContext();
        await SeedAsync(context, "owner@acme.example");
        var service = CreateService(context, new Mock<ICommunicationPublisher>());

        await service.SetStatusAsync(_tenantId, SubscriptionStatus.Suspended, "Non-payment.");

        var communicationPublisher = new Mock<ICommunicationPublisher>();
        string? templateCode = null;

        communicationPublisher
            .Setup(p => p.QueueTemplatedAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, string>, string, Guid?, Guid?, string?, CancellationToken>(
                (code, _, _, _, _, _, _) => templateCode = code)
            .ReturnsAsync(Guid.NewGuid());

        await CreateService(context, communicationPublisher)
            .SetStatusAsync(_tenantId, SubscriptionStatus.Active, reason: null);

        Assert.Equal(EmailTemplateRegistry.Codes.SubscriptionReactivated, templateCode);
    }

    [Fact]
    public async Task CancellingForATosViolationSendsTheViolationNotice()
    {
        await using var context = CreateContext();
        await SeedAsync(context, "owner@acme.example");

        var communicationPublisher = new Mock<ICommunicationPublisher>();
        string? templateCode = null;

        communicationPublisher
            .Setup(p => p.QueueTemplatedAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, string>, string, Guid?, Guid?, string?, CancellationToken>(
                (code, _, _, _, _, _, _) => templateCode = code)
            .ReturnsAsync(Guid.NewGuid());

        var cancelled = await CreateService(context, communicationPublisher)
            .CancelAsync(_tenantId, CancellationReasonCategory.TosViolation, "Cheating.");

        Assert.True(cancelled);
        Assert.Equal(EmailTemplateRegistry.Codes.TosViolationNotice, templateCode);
    }

    [Theory]
    [InlineData(CancellationReasonCategory.CustomerRequest)]
    [InlineData(CancellationReasonCategory.NonPayment)]
    [InlineData(CancellationReasonCategory.Other)]
    public async Task CancellingForAnyOtherReasonSendsTheOrdinaryCancellationNotice(
        CancellationReasonCategory category)
    {
        await using var context = CreateContext();
        await SeedAsync(context, "owner@acme.example");

        var communicationPublisher = new Mock<ICommunicationPublisher>();
        string? templateCode = null;

        communicationPublisher
            .Setup(p => p.QueueTemplatedAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, string>, string, Guid?, Guid?, string?, CancellationToken>(
                (code, _, _, _, _, _, _) => templateCode = code)
            .ReturnsAsync(Guid.NewGuid());

        var cancelled = await CreateService(context, communicationPublisher)
            .CancelAsync(_tenantId, category, "Reason given.");

        Assert.True(cancelled);
        Assert.Equal(EmailTemplateRegistry.Codes.SubscriptionCancelled, templateCode);
    }

    [Fact]
    public async Task CancellingWithNoContactEmailQueuesNothingAndDoesNotThrow()
    {
        await using var context = CreateContext();
        await SeedAsync(context, contactEmail: null);

        var cancelled = await CreateService(context, new Mock<ICommunicationPublisher>())
            .CancelAsync(_tenantId, CancellationReasonCategory.TosViolation, "Cheating.");

        Assert.True(cancelled);
    }
}
