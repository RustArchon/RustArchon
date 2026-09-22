// Copyright ©2026 Scott Blomfield

using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// <see cref="ServerPollService"/>: asks the owning Worker to poll a server right now, and the per-server throttle in front of it - see
/// <see cref="PollServerNow"/>'s remarks for why this exists (a person's Refresh, or the Api itself right after an update settles, should not
/// have to wait out the background poll's own few-minute schedule).
/// </summary>
public class ServerPollServiceTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly string[] Plugins = [PollServerNowKinds.Plugins];

    private static (ServerPollService Service, Mock<IRequestClient<PollServerNow>> Client, ServerPollThrottle Throttle) Build(TimeProvider? clock = null)
    {
        var client = new Mock<IRequestClient<PollServerNow>>();
        var throttle = new ServerPollThrottle(clock ?? TimeProvider.System);
        return (new ServerPollService(client.Object, throttle, NullLogger<ServerPollService>.Instance), client, throttle);
    }

    private static void GivenResponse(Mock<IRequestClient<PollServerNow>> client, bool connected) =>
        client.Setup(c => c.GetResponse<PollServerNowResult>(It.IsAny<PollServerNow>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .ReturnsAsync(Mock.Of<Response<PollServerNowResult>>(r => r.Message == new PollServerNowResult(connected)));

    [Fact]
    public async Task AConnectedServerIsReportedAsPolled()
    {
        var (service, client, _) = Build();
        GivenResponse(client, connected: true);

        Assert.Equal(ServerPollOutcome.Polled, await service.PollNowAsync(ServerId, Plugins));

        client.Verify(c => c.GetResponse<PollServerNowResult>(
            It.Is<PollServerNow>(m => m.ServerId == ServerId && m.Polls == Plugins), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()), Times.Once());
    }

    [Fact]
    public async Task AServerThatIsNotConnectedIsReportedAsNotConnected()
    {
        var (service, client, _) = Build();
        GivenResponse(client, connected: false);

        Assert.Equal(ServerPollOutcome.NotConnected, await service.PollNowAsync(ServerId, Plugins));
    }

    [Fact]
    public async Task NobodyAnsweringMeansNoWorkerOwnsTheConnection()
    {
        var (service, client, _) = Build();
        client.Setup(c => c.GetResponse<PollServerNowResult>(It.IsAny<PollServerNow>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()))
            .ThrowsAsync(new RequestTimeoutException("nobody answered"));

        Assert.Equal(ServerPollOutcome.NoWorker, await service.PollNowAsync(ServerId, Plugins));
    }

    [Fact]
    public async Task AskedTwiceInQuickSuccessionTheSecondIsRateLimitedAndNeverReachesTheWorker()
    {
        var (service, client, _) = Build();
        GivenResponse(client, connected: true);

        await service.PollNowAsync(ServerId, Plugins);
        var second = await service.PollNowAsync(ServerId, Plugins);

        Assert.Equal(ServerPollOutcome.RateLimited, second);
        client.Verify(c => c.GetResponse<PollServerNowResult>(It.IsAny<PollServerNow>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()), Times.Once());
    }

    [Fact]
    public async Task TheThrottleIsPerServerNotGlobal()
    {
        var (service, client, _) = Build();
        GivenResponse(client, connected: true);
        await service.PollNowAsync(ServerId, Plugins);

        var otherServer = Guid.NewGuid();
        var outcome = await service.PollNowAsync(otherServer, Plugins);

        Assert.Equal(ServerPollOutcome.Polled, outcome);
    }

    [Fact]
    public async Task OnceTheMinimumIntervalHasPassedItCanBeAskedAgain()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var (service, client, _) = Build(clock);
        GivenResponse(client, connected: true);
        await service.PollNowAsync(ServerId, Plugins);

        clock.Now += ServerPollThrottle.MinInterval + TimeSpan.FromMilliseconds(1);
        var outcome = await service.PollNowAsync(ServerId, Plugins);

        Assert.Equal(ServerPollOutcome.Polled, outcome);
        client.Verify(c => c.GetResponse<PollServerNowResult>(It.IsAny<PollServerNow>(), It.IsAny<CancellationToken>(), It.IsAny<RequestTimeout>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ARequestThatWasThrottledNeverCountsAgainstTheNextWindow()
    {
        // Two rapid calls: the first goes through, the second is throttled and must not itself reset the cooldown clock.
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var (service, client, _) = Build(clock);
        GivenResponse(client, connected: true);
        await service.PollNowAsync(ServerId, Plugins);
        await service.PollNowAsync(ServerId, Plugins); // throttled

        clock.Now += ServerPollThrottle.MinInterval + TimeSpan.FromMilliseconds(1);
        var outcome = await service.PollNowAsync(ServerId, Plugins);

        Assert.Equal(ServerPollOutcome.Polled, outcome);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>The throttle alone, directly - <see cref="ServerPollServiceTests"/> covers it indirectly through the service; these pin its own contract.</summary>
public class ServerPollThrottleTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void TheFirstRequestForAServerAlwaysGoesThrough()
    {
        Assert.True(new ServerPollThrottle(new FixedClock(DateTimeOffset.UtcNow)).TryAcquire(Guid.NewGuid()));
    }

    [Fact]
    public void ASecondRequestInsideTheWindowIsRefused()
    {
        var serverId = Guid.NewGuid();
        var throttle = new ServerPollThrottle(new FixedClock(DateTimeOffset.UtcNow));
        Assert.True(throttle.TryAcquire(serverId));

        Assert.False(throttle.TryAcquire(serverId));
    }

    [Fact]
    public void ExactlyAtTheBoundaryIsAllowed()
    {
        var serverId = Guid.NewGuid();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var throttle = new ServerPollThrottle(clock);
        Assert.True(throttle.TryAcquire(serverId));

        clock.Now += ServerPollThrottle.MinInterval;

        Assert.True(throttle.TryAcquire(serverId));
    }

    [Fact]
    public void OneMillisecondBeforeTheBoundaryIsStillTooSoon()
    {
        var serverId = Guid.NewGuid();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var throttle = new ServerPollThrottle(clock);
        Assert.True(throttle.TryAcquire(serverId));

        clock.Now += ServerPollThrottle.MinInterval - TimeSpan.FromMilliseconds(1);

        Assert.False(throttle.TryAcquire(serverId));
    }

    [Fact]
    public void OneMillisecondPastTheWindowIsAllowed()
    {
        var serverId = Guid.NewGuid();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var throttle = new ServerPollThrottle(clock);
        Assert.True(throttle.TryAcquire(serverId));

        clock.Now += ServerPollThrottle.MinInterval + TimeSpan.FromMilliseconds(1);

        Assert.True(throttle.TryAcquire(serverId));
    }
}
