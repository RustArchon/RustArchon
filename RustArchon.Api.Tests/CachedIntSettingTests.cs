// Copyright ©2026 Scott Blomfield

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// The whole-number Platform Settings that a rate limiter reads without waiting: the default until the first refresh, the setting after,
/// a failed refresh changing nothing, and a limit typed wrongly meaning the default rather than "nothing" or "no limit".
/// </summary>
public class CachedIntSettingTests
{
    private const string Key = PlatformSettingsRegistry.IntegrationChecksPerUserPerMinute;
    private const int Default = PlatformSettingsRegistry.DefaultIntegrationChecksPerUserPerMinute;

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Mock<IPlatformSettingsCache> _cache = new();
    private readonly MutableClock _clock = new(DateTimeOffset.UnixEpoch.AddDays(1));

    private IntegrationCheckLimit Create()
    {
        var services = new ServiceCollection().AddSingleton(_cache.Object).BuildServiceProvider();
        return new IntegrationCheckLimit(services.GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<IntegrationCheckLimit>.Instance);
    }

    [Fact]
    public async Task ItAnswersTheDefaultUntilTheFirstRefreshAndTheSettingAfter()
    {
        _cache.Setup(c => c.GetStringAsync(Key)).ReturnsAsync("35");
        var limit = Create();

        await limit.RefreshAsync();

        Assert.Equal(35, limit.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("many")]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task AMissingOrNonsensicalSettingMeansTheDefault(string? stored)
    {
        _cache.Setup(c => c.GetStringAsync(Key)).ReturnsAsync(stored);
        var limit = Create();

        await limit.RefreshAsync();

        Assert.Equal(Default, limit.Value);
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheLastValue()
    {
        _cache.Setup(c => c.GetStringAsync(Key)).ReturnsAsync("35");
        var limit = Create();
        await limit.RefreshAsync();

        _cache.Setup(c => c.GetStringAsync(Key)).ThrowsAsync(new InvalidOperationException("the database is down"));
        await limit.RefreshAsync();

        Assert.Equal(35, limit.Value);
    }

    [Fact]
    public async Task AReadWithinTheMaxAgeDoesNotAskAgainButALaterOneDoes()
    {
        _cache.Setup(c => c.GetStringAsync(Key)).ReturnsAsync("35");
        var limit = Create();
        await limit.RefreshAsync();
        _cache.Invocations.Clear();

        _clock.Now += CachedIntSetting.MaxAge - TimeSpan.FromSeconds(1);
        _ = limit.Value;
        _cache.Verify(c => c.GetStringAsync(Key), Times.Never);

        _cache.Setup(c => c.GetStringAsync(Key)).ReturnsAsync("50");
        _clock.Now += TimeSpan.FromSeconds(2);
        _ = limit.Value;                        // this read starts the refresh; it never waits for it
        await WaitUntilAsync(() => limit.Value == 50);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
