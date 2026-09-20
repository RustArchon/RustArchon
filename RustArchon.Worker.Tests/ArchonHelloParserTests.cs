// Copyright ©2026 Scott Blomfield

using RustArchon.Messaging.Contracts;
using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// Tests for <see cref="ArchonHelloParser"/>: strict about the envelope (the panel must never enable a
/// feature on a reply it did not understand), lenient about extra fields (a newer plugin still parses).
/// The sample is the exact shape RustArchon.Plugin's <c>archon.hello</c> produces.
/// </summary>
public class ArchonHelloParserTests
{
    private const string ValidReply =
        "{\"v\":1,\"ok\":true,\"data\":{\"plugin\":\"RustArchon\",\"version\":\"0.1.0\",\"protocolVersion\":1," +
        "\"runtime\":\"4.0.30319.42000\",\"capabilities\":[\"config\"]," +
        "\"settings\":{\"recording\":true,\"combat\":false,\"persisted\":true},\"utc\":\"2026-09-19T20:00:00.0000000Z\"}}";

    [Fact]
    public void Parses_TheDocumentedReply()
    {
        var parsed = ArchonHelloParser.TryParse(ValidReply, out var hello, out var failure);

        Assert.True(parsed);
        Assert.Null(failure);
        Assert.NotNull(hello);
        Assert.Equal(1, hello!.ProtocolVersion);
        Assert.Equal("0.1.0", hello.PluginVersion);
        Assert.Equal(new[] { "config" }, hello.Capabilities);
        Assert.True(hello.RecordingEnabled);
        Assert.False(hello.CombatLogEnabled);
        Assert.True(hello.SettingsPersisted);
    }

    [Fact]
    public void IgnoresPropertiesItDoesNotKnowAbout()
    {
        var newer = ValidReply.Replace("\"runtime\"", "\"somethingNew\":{\"a\":1},\"runtime\"");

        Assert.True(ArchonHelloParser.TryParse(newer, out var hello, out _));
        Assert.Equal("0.1.0", hello!.PluginVersion);
    }

    [Fact]
    public void PersistedAbsentMeansNotPersisted()
    {
        // Absent is "unknown", which must not read as a positive.
        var noPersisted = ValidReply.Replace(",\"persisted\":true", "");

        Assert.True(ArchonHelloParser.TryParse(noPersisted, out var hello, out _));
        Assert.False(hello!.SettingsPersisted);
    }

    [Fact]
    public void SkipsEmptyAndNonStringCapabilities()
    {
        var odd = ValidReply.Replace("[\"config\"]", "[\"config\",\"\",7,null,\"combat\"]");

        Assert.True(ArchonHelloParser.TryParse(odd, out var hello, out _));
        Assert.Equal(new[] { "config", "combat" }, hello!.Capabilities);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyReplyFails(string? message)
    {
        Assert.False(ArchonHelloParser.TryParse(message, out var hello, out var failure));
        Assert.Null(hello);
        Assert.Equal("empty reply", failure);
    }

    [Theory]
    [InlineData("Unknown command: archon.hello")] // a server whose plugin is not actually loaded
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public void ANonEnvelopeReplyFailsInsteadOfThrowing(string message)
    {
        Assert.False(ArchonHelloParser.TryParse(message, out var hello, out var failure));
        Assert.Null(hello);
        Assert.False(string.IsNullOrEmpty(failure));
    }

    [Theory]
    [InlineData("{\"v\":2,\"ok\":true,\"data\":{}}")]
    [InlineData("{\"ok\":true,\"data\":{}}")]
    [InlineData("{\"v\":\"1\",\"ok\":true,\"data\":{}}")]
    public void AnUnsupportedOrMissingEnvelopeVersionFails(string message)
    {
        Assert.False(ArchonHelloParser.TryParse(message, out var hello, out var failure));
        Assert.Null(hello);
        Assert.Equal("unsupported or missing envelope version", failure);
    }

    [Fact]
    public void AnErrorEnvelopeFailsAndCarriesThePluginsReason()
    {
        var error = "{\"v\":1,\"ok\":false,\"err\":\"usage\",\"message\":\"x\"}";

        Assert.False(ArchonHelloParser.TryParse(error, out var hello, out var failure));
        Assert.Null(hello);
        Assert.Contains("usage", failure);
    }

    [Theory]
    [InlineData("\"protocolVersion\":1,", "")] // missing protocol
    [InlineData("\"protocolVersion\":1", "\"protocolVersion\":\"1\"")] // wrong type
    [InlineData("\"capabilities\":[\"config\"]", "\"capabilities\":\"config\"")] // not an array
    [InlineData("\"recording\":true", "\"recording\":\"yes\"")] // not a bool
    [InlineData("\"combat\":false", "\"combat\":null")]
    public void AMissingOrMistypedRequiredFieldFails(string find, string replace)
    {
        var broken = ValidReply.Replace(find, replace);

        Assert.False(ArchonHelloParser.TryParse(broken, out var hello, out var failure));
        Assert.Null(hello);
        Assert.Equal("a required field is missing or has the wrong type", failure);
    }

    [Fact]
    public void AnOutOfRangeProtocolNumberFailsInsteadOfThrowing()
    {
        var huge = ValidReply.Replace("\"protocolVersion\":1", "\"protocolVersion\":99999999999");

        Assert.False(ArchonHelloParser.TryParse(huge, out var hello, out var failure));
        Assert.Null(hello);
        Assert.Equal("a number was out of range", failure);
    }

    // ---- detecting the plugin in a plugin list -------------------------------------------------------------

    [Fact]
    public void IsPluginListed_IsTrueWhenRustArchonIsInTheList()
    {
        var plugins = new[] { new ServerPluginInfo("Kits", "k1lly0u", "4.4.9"), new ServerPluginInfo("RustArchon", "RustArchon", "0.1.0") };

        Assert.True(ArchonHelloParser.IsPluginListed(plugins));
    }

    [Fact]
    public void IsPluginListed_IgnoresCase()
    {
        Assert.True(ArchonHelloParser.IsPluginListed([new ServerPluginInfo("rustarchon", "x", "1")]));
    }

    [Fact]
    public void IsPluginListed_IsFalseForAListWithoutItOrAnEmptyOne()
    {
        Assert.False(ArchonHelloParser.IsPluginListed([]));
        Assert.False(ArchonHelloParser.IsPluginListed([new ServerPluginInfo("Kits", "k1lly0u", "4.4.9")]));
    }

    [Fact]
    public void IsPluginListed_DoesNotMatchLookalikeNames()
    {
        // Fail closed: only the exact plugin name counts, so someone else's "RustArchonHelper" is never probed.
        Assert.False(ArchonHelloParser.IsPluginListed([
            new ServerPluginInfo("RustArchonHelper", "x", "1"),
            new ServerPluginInfo("ArchonSpikeCore", "x", "1")]));
    }
}
