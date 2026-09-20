// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// The signing key rotation reminder against a real Postgres: it counts from when the active key became the active one (its creation, or the
/// rotation that replaced the one before), follows the Platform Setting (a year by default, zero for off), never fires without a known age, and
/// only ever says so: nothing rotates by itself.
/// </summary>
public class PluginKeyReminderTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();
    private static readonly DateTimeOffset Born = new(2026, 1, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly MutableClock _clock = new(Born);

    private ApiDbContext CreateContext() => new(postgres.Options);

    private PluginSigningService NewSigning(ApiDbContext context, bool audited = true) =>
        new(new PlatformSettingRepository(context), new ApiKeyProtector(Provider), NullLogger<PluginSigningService>.Instance,
            new PluginKeyHistoryRepository(context), audited ? new PluginAdminAudit(context, _clock) : null);

    private PluginKeyService NewKeys(ApiDbContext context, bool audited = true) =>
        new(context, NewSigning(context, audited), new ServerPluginStatusRepository(context), new ApiKeyProtector(Provider),
            _clock, NullLogger<PluginKeyService>.Instance);

    /// <summary>A Panel with no key yet and no history, the settings seeded.</summary>
    private async Task<ApiDbContext> EmptyAsync(string? reminderDays = null)
    {
        var context = CreateContext();
        await PlatformSettingsRegistry.EnsureDefaultsAsync(
            context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger.Instance);
        await context.PluginKeyHistories.ExecuteDeleteAsync();
        await context.PluginAdminEvents.ExecuteDeleteAsync();
        await context.PlatformSettings.Where(s => s.Key == PlatformSettingsRegistry.PluginSigningKey)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, ""));
        await context.PlatformSettings.Where(s => s.Key == PlatformSettingsRegistry.PluginKeyRotationReminderDays)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, reminderDays ?? PlatformSettingsRegistry.DefaultPluginKeyRotationReminderDays.ToString()));
        context.ChangeTracker.Clear();
        return context;
    }

    [Fact]
    public async Task WithNoKeyThereIsNothingToRemindAboutAndNothingIsCreated()
    {
        await using var context = await EmptyAsync();

        var reminder = await NewKeys(context).GetReminderAsync();

        Assert.False(reminder.Due);
        Assert.Null(reminder.Fingerprint);
        Assert.Null(reminder.ActiveSinceUtc);
        Assert.Null(await NewSigning(context).TryGetFingerprintAsync());
    }

    [Fact]
    public async Task ANewKeyCountsFromTheMomentItWasMadeAndIsNotDueYet()
    {
        await using var context = await EmptyAsync();
        var key = await NewSigning(context).GetPublicKeyAsync();
        _clock.Now = Born.AddDays(10);

        var reminder = await NewKeys(context).GetReminderAsync();

        Assert.False(reminder.Due);
        Assert.Equal(key.Fingerprint, reminder.Fingerprint);
        Assert.Equal(Born, reminder.ActiveSinceUtc);
        Assert.Equal(10, reminder.AgeDays);
        Assert.Equal(365, reminder.ReminderDays);
    }

    [Theory]
    [InlineData(364, false)]
    [InlineData(365, true)]
    [InlineData(700, true)]
    public async Task TheReminderIsDueOnceTheKeyHasBeenActiveForTheSettingsDays(int daysOld, bool due)
    {
        await using var context = await EmptyAsync();
        await NewSigning(context).GetPublicKeyAsync();
        _clock.Now = Born.AddDays(daysOld);

        Assert.Equal(due, (await NewKeys(context).GetReminderAsync()).Due);
    }

    [Fact]
    public async Task TheSettingChangesWhenTheReminderIsDue()
    {
        await using var context = await EmptyAsync("30");
        await NewSigning(context).GetPublicKeyAsync();
        _clock.Now = Born.AddDays(31);

        var reminder = await NewKeys(context).GetReminderAsync();

        Assert.True(reminder.Due);
        Assert.Equal(30, reminder.ReminderDays);
    }

    [Fact]
    public async Task ZeroTurnsTheReminderOffHoweverOldTheKeyIs()
    {
        await using var context = await EmptyAsync("0");
        await NewSigning(context).GetPublicKeyAsync();
        _clock.Now = Born.AddDays(5000);

        var reminder = await NewKeys(context).GetReminderAsync();

        Assert.False(reminder.Due);
        Assert.Equal(0, reminder.ReminderDays);
        Assert.Equal(5000, reminder.AgeDays);
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("")]
    [InlineData("-7")]
    public async Task ASettingThatIsNotAWholeNumberOfDaysMeansTheDefaultAYear(string junk)
    {
        await using var context = await EmptyAsync(junk);
        await NewSigning(context).GetPublicKeyAsync();
        _clock.Now = Born.AddDays(366);

        var reminder = await NewKeys(context).GetReminderAsync();

        Assert.Equal(365, reminder.ReminderDays);
        Assert.True(reminder.Due);
    }

    [Fact]
    public async Task RotatingStartsTheCountAgainFromTheRotation()
    {
        await using var context = await EmptyAsync();
        await NewSigning(context).GetPublicKeyAsync();
        _clock.Now = Born.AddDays(400);
        Assert.True((await NewKeys(context).GetReminderAsync()).Due);

        var created = await NewKeys(context).RotateAsync("admin@example.com", "yearly");

        var reminder = await NewKeys(context).GetReminderAsync();
        Assert.False(reminder.Due);
        Assert.Equal(created.Fingerprint, reminder.Fingerprint);
        Assert.Equal(Born.AddDays(400), reminder.ActiveSinceUtc);
        Assert.Equal(0, reminder.AgeDays);
    }

    [Fact]
    public async Task AKeyMadeBeforeTheAuditLineExistedCountsFromWhenItsSettingWasCreated()
    {
        await using var context = await EmptyAsync();
        await NewSigning(context, audited: false).GetPublicKeyAsync();       // made with no audit line, as every key was before this existed
        _clock.Now = DateTimeOffset.UtcNow.AddDays(400);

        var reminder = await NewKeys(context, audited: false).GetReminderAsync();

        Assert.NotNull(reminder.ActiveSinceUtc);
        Assert.True(reminder.Due);                                             // the setting row is a moment old, and the clock is 400 days on
    }

    [Fact]
    public async Task OnlyTheActiveKeyCarriesTheDateItBecameActive()
    {
        await using var context = await EmptyAsync();
        await NewSigning(context).GetPublicKeyAsync();
        await NewKeys(context).RotateAsync("admin@example.com", null);

        var keys = await NewKeys(context).ListAsync();

        Assert.NotNull(keys.Single(k => k.State == PluginKeyState.Active).ActiveSinceUtc);
        Assert.All(keys.Where(k => k.State != PluginKeyState.Active), k => Assert.Null(k.ActiveSinceUtc));
    }

    [Fact]
    public async Task TheFirstKeyBeingMadeIsInTheAuditLogAsGeneratedBySystem()
    {
        await using var context = await EmptyAsync();
        var key = await NewSigning(context).GetPublicKeyAsync();

        var line = await context.PluginAdminEvents.AsNoTracking().SingleAsync();

        Assert.Equal(PluginAdminEventKind.KeyGenerated, line.Kind);
        Assert.Equal(key.Fingerprint, line.Subject);
        Assert.Equal("system", line.Actor);
        Assert.Equal(Born, line.AtUtc);
    }
}
