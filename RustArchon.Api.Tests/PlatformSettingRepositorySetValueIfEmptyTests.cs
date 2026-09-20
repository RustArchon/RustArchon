// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="PlatformSettingRepository.SetValueIfEmptyAsync"/> against a real Postgres - the atomic
/// "write it only if nobody has yet" that keeps two instances from ending up with two different plugin signing keys.
/// </summary>
public class PlatformSettingRepositorySetValueIfEmptyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private ApiDbContext CreateContext() => new(postgres.Options);

    private static async Task<PlatformSettingRepository> SeededRepositoryAsync(ApiDbContext context)
    {
        await PlatformSettingsRegistry.EnsureDefaultsAsync(
            context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger.Instance);
        return new PlatformSettingRepository(context);
    }

    private static async Task ResetAsync(ApiDbContext context, string key, string value)
    {
        await context.Set<PlatformSetting>().Where(s => s.Key == key).ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, value));
    }

    [Fact]
    public async Task TheSigningKeySettingIsRegisteredEmptyAsASecret()
    {
        await using var context = CreateContext();
        var repository = await SeededRepositoryAsync(context);

        var setting = await repository.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey);

        Assert.NotNull(setting);
        Assert.Equal(PlatformSettingValueType.Secret, setting!.ValueType);
        Assert.Equal(PlatformSettingsRegistry.Categories.Plugin, setting.Category);
    }

    [Fact]
    public async Task SetsTheValueWhenItIsEmptyAndSaysSo()
    {
        await using var context = CreateContext();
        var repository = await SeededRepositoryAsync(context);
        await ResetAsync(context, PlatformSettingsRegistry.PluginSigningKey, "");

        var wrote = await repository.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, "first");

        Assert.True(wrote);
        await using var fresh = CreateContext();
        Assert.Equal("first", (await new PlatformSettingRepository(fresh).GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value);
    }

    [Fact]
    public async Task ASecondWriterLosesAndTheFirstValueSurvives()
    {
        await using var context = CreateContext();
        var repository = await SeededRepositoryAsync(context);
        await ResetAsync(context, PlatformSettingsRegistry.PluginSigningKey, "");

        var first = await repository.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, "winner");
        var second = await repository.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, "loser");

        Assert.True(first);
        Assert.False(second);
        await using var fresh = CreateContext();
        Assert.Equal("winner", (await new PlatformSettingRepository(fresh).GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value);
    }

    [Fact]
    public async Task RacingWritersProduceExactlyOneWinner()
    {
        await using var seedContext = CreateContext();
        await SeededRepositoryAsync(seedContext);
        await ResetAsync(seedContext, PlatformSettingsRegistry.PluginSigningKey, "");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var context = CreateContext(); // separate context per "instance"
            return await new PlatformSettingRepository(context).SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, $"writer-{i}");
        }));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task NeverOverwritesAValueThatIsAlreadyThere()
    {
        await using var context = CreateContext();
        var repository = await SeededRepositoryAsync(context);
        await ResetAsync(context, PlatformSettingsRegistry.PluginSigningKey, "already set");

        var wrote = await repository.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, "replacement");

        Assert.False(wrote);
        await using var fresh = CreateContext();
        Assert.Equal("already set", (await new PlatformSettingRepository(fresh).GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value);
    }

    [Fact]
    public async Task TheSameContextSeesTheValueItJustWroteNotAStaleTrackedCopy()
    {
        // Regression: the signing service reads the setting (so the row is tracked, empty), writes with this atomic
        // update, then reads again through the SAME context. ExecuteUpdate bypasses the change tracker, so without a
        // refresh that second read returned the old empty value and the first download reported "could not be stored".
        await using var context = CreateContext();
        var repository = await SeededRepositoryAsync(context);
        await ResetAsync(context, PlatformSettingsRegistry.PluginSigningKey, "");
        context.ChangeTracker.Clear(); // ResetAsync bypassed the tracker; drop the copy seeding left tracked
        Assert.Equal("", (await repository.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value); // now tracked

        var wrote = await repository.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, "written");

        Assert.True(wrote);
        Assert.Equal("written", (await repository.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value);
    }

    [Fact]
    public async Task ALosingWriterSeesTheWinnersValueOnItsNextRead()
    {
        await using var loser = CreateContext();
        var loserRepository = await SeededRepositoryAsync(loser);
        await ResetAsync(loser, PlatformSettingsRegistry.PluginSigningKey, "");
        loser.ChangeTracker.Clear();
        Assert.Equal("", (await loserRepository.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value); // tracked, empty

        await using (var winner = CreateContext())
        {
            Assert.True(await new PlatformSettingRepository(winner).SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, "winner"));
        }

        var wrote = await loserRepository.SetValueIfEmptyAsync(PlatformSettingsRegistry.PluginSigningKey, "loser");

        Assert.False(wrote);
        Assert.Equal("winner", (await loserRepository.GetByKeyAsync(PlatformSettingsRegistry.PluginSigningKey))!.Value);
    }

    [Fact]
    public async Task ReturnsFalseForASettingThatDoesNotExist()
    {
        await using var context = CreateContext();
        var repository = await SeededRepositoryAsync(context);

        Assert.False(await repository.SetValueIfEmptyAsync("NoSuchSetting", "x"));
    }
}
