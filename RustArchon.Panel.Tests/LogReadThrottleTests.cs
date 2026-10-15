// Copyright ©2026 Scott Blomfield

using RustArchon.Panel.Services;

namespace RustArchon.Panel.Tests;

public class LogReadThrottleTests
{
    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void AnAddressIsAllowedUntilItHasFailedTheLimitInTheLastMinute()
    {
        var throttle = new LogReadThrottle(new MutableTime(), () => 3);

        for (var i = 0; i < 2; i++)
        {
            throttle.RecordFailure("1.2.3.4");
        }

        Assert.False(throttle.IsBlocked("1.2.3.4"));
        throttle.RecordFailure("1.2.3.4");
        Assert.True(throttle.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public void AddressesAreCountedSeparately()
    {
        var throttle = new LogReadThrottle(new MutableTime(), () => 1);

        throttle.RecordFailure("1.2.3.4");

        Assert.True(throttle.IsBlocked("1.2.3.4"));
        Assert.False(throttle.IsBlocked("5.6.7.8"));
    }

    [Fact]
    public void FailuresAgeOutAfterAMinute()
    {
        var time = new MutableTime();
        var throttle = new LogReadThrottle(time, () => 2);
        throttle.RecordFailure("1.2.3.4");
        time.Now = time.Now.AddSeconds(30);
        throttle.RecordFailure("1.2.3.4");
        Assert.True(throttle.IsBlocked("1.2.3.4"));

        time.Now = time.Now.AddSeconds(31);

        Assert.False(throttle.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public void ABlockedAddressIsFreedOnceEnoughOfItsFailuresHaveAgedOut()
    {
        var time = new MutableTime();
        var throttle = new LogReadThrottle(time, () => 3);
        for (var i = 0; i < 3; i++)
        {
            throttle.RecordFailure("1.2.3.4");
            time.Now = time.Now.AddSeconds(10);
        }

        Assert.True(throttle.IsBlocked("1.2.3.4"));
        time.Now = time.Now.AddSeconds(31);

        Assert.False(throttle.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public void AChangedLimitTakesEffectImmediately()
    {
        var limit = 5;
        var throttle = new LogReadThrottle(new MutableTime(), () => limit);
        for (var i = 0; i < 3; i++)
        {
            throttle.RecordFailure("1.2.3.4");
        }

        Assert.False(throttle.IsBlocked("1.2.3.4"));
        limit = 3;

        Assert.True(throttle.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public void ManyDifferentAddressesCannotGrowTheTableWithoutBound()
    {
        var time = new MutableTime();
        var throttle = new LogReadThrottle(time, () => 10);

        for (var i = 0; i < LogReadThrottle.MaxAddresses * 3; i++)
        {
            throttle.RecordFailure("addr-" + i);
        }

        Assert.True(throttle.TrackedAddresses <= LogReadThrottle.MaxAddresses);
    }
}
