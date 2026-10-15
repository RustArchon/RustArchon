// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using RustArchon.Api.Data;
using RustArchon.Api.DiagnosticViews.Views;
using RustArchon.Api.Infrastructure;
using static RustArchon.Api.Tests.DiagnosticViewKit;

namespace RustArchon.Api.Tests;

/// <summary>
/// The <c>platform-settings</c> and <c>plugin-keys</c> diagnostic views: settings by name (a secret one only as set or not set) and the signing keys' fingerprints
/// and states, never any key material.
/// </summary>
public class PlatformSettingsAndKeysViewTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<string> AddSettingAsync(PlatformSettingValueType type, string value, string? key = null)
    {
        key ??= "ViewTest" + Guid.NewGuid().ToString("N")[..10];
        await using var context = new ApiDbContext(postgres.Options);
        context.PlatformSettings.Add(new PlatformSetting { Key = key, DisplayName = "Display " + key, Category = "ViewTests", Order = 1, ValueType = type, Value = value });
        await context.SaveChangesAsync();
        return key;
    }

    private async Task<Dictionary<string, System.Text.Json.JsonElement>> SettingsAsync()
    {
        var (status, json) = await RunAsync(new PlatformSettingsView(new ApiDbContext(postgres.Options)), PlatformSettingsView.Definition);
        Assert.Equal(200, status);
        return json.GetProperty("data").GetProperty("settings").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!);
    }

    [Fact]
    public async Task AnOrdinarySettingShowsItsValue()
    {
        var key = await AddSettingAsync(PlatformSettingValueType.Integer, "42");

        var row = (await SettingsAsync())[key];

        Assert.Equal("42", row.GetProperty("value").GetString());
        Assert.True(row.GetProperty("isSet").GetBoolean());
        Assert.False(row.GetProperty("isSecret").GetBoolean());
        Assert.Equal("Integer", row.GetProperty("valueType").GetString());
    }

    [Fact]
    public async Task ASecretSettingIsOnlyEverSetOrNotSetNeverItsValue()
    {
        var set = await AddSettingAsync(PlatformSettingValueType.Secret, "SUPERSECRETVALUE-1");
        var unset = await AddSettingAsync(PlatformSettingValueType.Secret, string.Empty);

        var rows = await SettingsAsync();

        Assert.True(rows[set].GetProperty("isSet").GetBoolean());
        Assert.True(rows[set].GetProperty("isSecret").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows[set].GetProperty("value").ValueKind);
        Assert.False(rows[unset].GetProperty("isSet").GetBoolean());
        var (_, json) = await RunAsync(new PlatformSettingsView(new ApiDbContext(postgres.Options)), PlatformSettingsView.Definition);
        Assert.DoesNotContain("SUPERSECRETVALUE-1", Text(json));
    }

    [Theory]
    [InlineData("ThirdPartyWebhookSecret")]
    [InlineData("SomeApiKey")]
    [InlineData("AdminPassword")]
    [InlineData("AccessToken")]
    public async Task AStringSettingWhoseNameSaysItIsASecretIsTreatedAsOneWhateverItsDeclaredType(string name)
    {
        var key = await AddSettingAsync(PlatformSettingValueType.String, "LOOKS-HARMLESS-BUT-IS-NOT", name + Guid.NewGuid().ToString("N")[..6]);

        var row = (await SettingsAsync())[key];

        Assert.True(row.GetProperty("isSecret").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, row.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task TheViewCanBeCalledWithNoParametersAndTheSettingsCarryTheirDescriptionsAndCategories()
    {
        var key = await AddSettingAsync(PlatformSettingValueType.Boolean, "true");

        var row = (await SettingsAsync())[key];

        Assert.Equal("ViewTests", row.GetProperty("category").GetString());
        Assert.Equal("Display " + key, row.GetProperty("displayName").GetString());
    }

    // ---- plugin-keys ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheKeysAreListedByFingerprintWithTheirStateAndHowManyServersReportThemAndNoKeyMaterial()
    {
        var keys = new Mock<IPluginKeyService>();
        keys.Setup(k => k.ListAsync()).ReturnsAsync(
        [
            new PluginKeyInfo("1111111111111111", PluginKeyState.Active, null, null, null, 3, Now.AddMinutes(-5), Now.AddDays(-30)),
            new PluginKeyInfo("2222222222222222", PluginKeyState.Revoked, Now.AddDays(-60), Now.AddDays(-10), "leaked", 0, null)
        ]);

        var (status, json) = await RunAsync(new PluginKeysView(keys.Object), PluginKeysView.Definition);

        Assert.Equal(200, status);
        var rows = json.GetProperty("data").GetProperty("keys").EnumerateArray().ToDictionary(k => k.GetProperty("fingerprint").GetString()!);
        Assert.Equal("Active", rows["1111111111111111"].GetProperty("state").GetString());
        Assert.Equal(3, rows["1111111111111111"].GetProperty("serversReporting").GetInt32());
        Assert.Equal("Revoked", rows["2222222222222222"].GetProperty("state").GetString());
        Assert.Equal("leaked", rows["2222222222222222"].GetProperty("revokedReason").GetString());
        var text = Text(json).ToLowerInvariant();
        Assert.DoesNotContain("modulus", text);
        Assert.DoesNotContain("exponent", text);
        Assert.DoesNotContain("privatekey", text);
    }
}
