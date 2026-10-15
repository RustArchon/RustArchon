// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RustArchon.Api.Data;
using RustArchon.Api.DiagnosticViews;

namespace RustArchon.Api.Tests;

/// <summary>
/// The diagnostic views return a viewed entity's properties except the ones marked <see cref="DiagnosticsHiddenAttribute"/> - that attribute is the
/// definitive list of what is never sent (ADR-0007). These tests prove the marking is honoured, that the projection is flat and cheap, and (the backstop)
/// that a property which looks secret cannot sit unmarked on a viewed entity.
/// </summary>
public partial class EntityProjectorTests
{
    public enum Colour
    {
        Red,
        Blue
    }

    public sealed class Child
    {
        public string Inner { get; set; } = "inner";
    }

    public sealed class Sample
    {
        public Guid Id { get; set; } = Guid.Parse("01a09e82-6ba3-746c-a984-c61fba87ec8d");

        public string Name { get; set; } = "name";

        public string? Optional { get; set; }

        public int Count { get; set; } = 3;

        public DateTimeOffset At { get; set; } = new(2026, 10, 15, 4, 0, 0, TimeSpan.Zero);

        public Colour Colour { get; set; } = Colour.Blue;

        public string[] Tags { get; set; } = ["a", "b"];

        [DiagnosticsHidden("a credential")]
        public string Password { get; set; } = "hunter2";

        public Child Nested { get; set; } = new();

        public List<Child> Children { get; set; } = [new()];
    }

    [Fact]
    public void AMarkedPropertyIsNeverInTheOutputOrTheDescription()
    {
        var projected = EntityProjector.Project(new Sample());

        Assert.DoesNotContain("password", projected.Keys);
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(projected));
        Assert.DoesNotContain(EntityProjector.Describe(typeof(Sample)), f => f.Name == "password");
    }

    [Fact]
    public void ThePropertiesThatAreNotMarkedAreReturnedWithCamelCaseNamesAndStringEnums()
    {
        var projected = EntityProjector.Project(new Sample());

        Assert.Equal("name", projected["name"]);
        Assert.Equal(3, projected["count"]);
        Assert.Equal("Blue", projected["colour"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 4, 0, 0, TimeSpan.Zero), projected["at"]);
        Assert.Equal(["a", "b"], (string[])projected["tags"]!);
        Assert.Null(projected["optional"]);
        Assert.True(projected.ContainsKey("optional"));
    }

    [Fact]
    public void ANavigationOrACollectionOfEntitiesIsNotFollowed()
    {
        var projected = EntityProjector.Project(new Sample());

        Assert.DoesNotContain("nested", projected.Keys);
        Assert.DoesNotContain("children", projected.Keys);
    }

    [Fact]
    public void TheDescriptionNamesEachFieldWithItsTypeAndTheValuesOfAnEnum()
    {
        var fields = EntityProjector.Describe(typeof(Sample)).ToDictionary(f => f.Name);

        Assert.Equal("uuid", fields["id"].Type);
        Assert.Equal("string", fields["name"].Type);
        Assert.True(fields["optional"].Nullable);
        Assert.Equal("integer", fields["count"].Type);
        Assert.Equal("datetime", fields["at"].Type);
        Assert.Equal("string[]", fields["tags"].Type);
        Assert.Equal("string", fields["colour"].Type);
        Assert.Equal(["Red", "Blue"], fields["colour"].Values);
    }

    [Fact]
    public void TheDescriptionIsWorkedOutOnceNotPerCall()
    {
        Assert.Same(EntityProjector.Describe(typeof(Sample)), EntityProjector.Describe(typeof(Sample)));
    }

    [Fact]
    public void ThePropertyNamesInTheOutputAreExactlyThoseInTheDescription()
    {
        Assert.Equal(
            EntityProjector.Describe(typeof(Sample)).Select(f => f.Name).Order(),
            EntityProjector.Project(new Sample()).Keys.Order());
    }

    [Fact]
    public void AReasonIsRequiredToHideAProperty()
    {
        Assert.Throws<ArgumentException>(() => new DiagnosticsHiddenAttribute(" "));
    }

    [Fact]
    public void TheRealSecretsOfTheServerEntityNeverComeOutWhateverTheirValue()
    {
        var server = new RustServer
        {
            Host = "192.0.2.80", Port = 28016, RconPassword = "RCONPASS-1", SteamApiKey = "STEAMKEY-2", GeolocationApiKey = "GEOKEY-3", ReportsSecret = "REPORTSECRET-4"
        };

        var json = JsonSerializer.Serialize(EntityProjector.Project(server));

        foreach (var secret in new[] { "RCONPASS-1", "STEAMKEY-2", "GEOKEY-3", "REPORTSECRET-4" })
        {
            Assert.DoesNotContain(secret, json);
        }

        Assert.Contains("192.0.2.80", json);
    }

    [Fact]
    public void TheKeyHistoryNeverReturnsKeyMaterial()
    {
        var key = new PluginKeyHistory { Fingerprint = "82b49184449c98f6", ModulusBase64 = "MODULUS-1", ExponentBase64 = "EXPONENT-2", EncryptedPrivateKey = "PRIVATE-3" };

        var json = JsonSerializer.Serialize(EntityProjector.Project(key));

        Assert.Contains("82b49184449c98f6", json);
        foreach (var secret in new[] { "MODULUS-1", "EXPONENT-2", "PRIVATE-3" })
        {
            Assert.DoesNotContain(secret, json);
        }
    }

    // ---- the backstop -----------------------------------------------------------------------------------------------------------------

    // Properties that match the secret-looking names below and are known to be safe: a public fingerprint, a marketplace's name for itself, and a count
    // of how many times a downloaded file's checksum changed.
    private static readonly HashSet<string> KnownSafe =
    [
        "PluginKeyHistory.Fingerprint", "ServerPluginStatus.SigningKeyFingerprint", "PluginDownloadLookup.MarketplaceKey", "PluginDownloadLookup.FileHashChanges",
        "PlatformSetting.Key", "PlatformSetting.VisibleWhenKey"      // names of settings, not keys to anything
    ];

    [GeneratedRegex("password|passwd|secret|token|api[-_]?key|credential|private|hash|encrypted|modulus|exponent|signature|key", RegexOptions.IgnoreCase)]
    private static partial Regex SecretLookingName();

    [Fact]
    public void NoPropertyOfAViewedEntityThatLooksSecretIsLeftUnmarked()
    {
        var offenders = new List<string>();
        foreach (var entity in DiagnosticViewEntities.All)
        {
            foreach (var property in entity.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = $"{entity.Name}.{property.Name}";
                var looksSecret = SecretLookingName().IsMatch(property.Name) || property.PropertyType == typeof(byte[]);
                if (looksSecret && !EntityProjector.IsHidden(property) && !KnownSafe.Contains(name))
                {
                    offenders.Add(name);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These properties look secret and are not marked [DiagnosticsHidden(\"why\")]. Mark them, or (if they are safe) add them to KnownSafe with a reason: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheBackstopCatchesAnUnmarkedSecretLookingProperty()
    {
        var property = typeof(Unsafe).GetProperty(nameof(Unsafe.ApiToken))!;

        Assert.True(SecretLookingName().IsMatch(property.Name) && !EntityProjector.IsHidden(property));
    }

    private sealed class Unsafe
    {
        public string ApiToken { get; set; } = string.Empty;
    }
}
