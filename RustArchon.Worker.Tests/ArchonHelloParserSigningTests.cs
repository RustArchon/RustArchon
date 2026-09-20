// Copyright ©2026 Scott Blomfield

using RustArchon.Worker.Connections;

namespace RustArchon.Worker.Tests;

/// <summary>
/// The optional <c>signing</c> block of the plugin's <c>archon.hello</c> reply - the plugin's own check of its file
/// against the signature a Panel put on it. Lenient by design: it never fails the whole handshake, and what it
/// carries is normalized because it comes from a plugin running on someone else's server.
/// </summary>
public class ArchonHelloParserSigningTests
{
    private static string Reply(string signingJson) =>
        "{\"v\":1,\"ok\":true,\"data\":{\"plugin\":\"RustArchon\",\"version\":\"0.1.0\",\"protocolVersion\":1," +
        "\"capabilities\":[\"config\"]" + signingJson +
        ",\"settings\":{\"recording\":true,\"combat\":true,\"persisted\":true}}}";

    private static ArchonHello Parse(string message)
    {
        Assert.True(ArchonHelloParser.TryParse(message, out var hello, out var failure), failure);
        return hello!;
    }

    [Fact]
    public void ParsesAValidSigningBlock()
    {
        var hello = Parse(Reply(",\"signing\":{\"state\":\"valid\",\"keyFingerprint\":\"0123456789abcdef\"}"));

        Assert.Equal("valid", hello.SigningState);
        Assert.Equal("0123456789abcdef", hello.SigningKeyFingerprint);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("invalid")]
    [InlineData("unsigned")]
    [InlineData("unlocated")]
    [InlineData("error")]
    public void PassesEachKnownStateThrough(string state)
    {
        var hello = Parse(Reply($",\"signing\":{{\"state\":\"{state}\",\"keyFingerprint\":\"\"}}"));

        Assert.Equal(state, hello.SigningState);
    }

    [Fact]
    public void AnOlderPluginWithNoSigningBlockIsUnknownNotAFailure()
    {
        var hello = Parse(Reply(""));

        Assert.Equal("unknown", hello.SigningState);
        Assert.Equal("", hello.SigningKeyFingerprint);
    }

    [Theory]
    [InlineData("\"signing\":\"valid\"")] // not an object
    [InlineData("\"signing\":null")]
    [InlineData("\"signing\":[]")]
    [InlineData("\"signing\":{}")]
    [InlineData("\"signing\":{\"state\":7}")]
    [InlineData("\"signing\":{\"state\":\"\"}")]
    public void AMalformedSigningBlockReadsAsUnknownAndNeverFailsTheHandshake(string block)
    {
        Assert.Equal("unknown", Parse(Reply("," + block)).SigningState);
    }

    [Theory]
    [InlineData("VALID", "valid")]
    [InlineData("  Valid ", "valid")]
    [InlineData("trusted", "unknown")] // not one of ours
    [InlineData("valid; DROP TABLE", "unknown")]
    public void NormalizesTheStateToAKnownValue(string sent, string expected)
    {
        var hello = Parse(Reply($",\"signing\":{{\"state\":\"{sent}\"}}"));

        Assert.Equal(expected, hello.SigningState);
    }

    [Theory]
    [InlineData("0123456789ABCDEF", "0123456789abcdef")] // upper case is folded
    [InlineData("0123456789abcde", "")] // too short
    [InlineData("0123456789abcdef0", "")] // too long
    [InlineData("0123456789abcdeg", "")] // not hex
    [InlineData("<script>alert(1)</s>", "")]
    [InlineData("", "")]
    public void OnlyAWellFormedFingerprintIsKept(string sent, string expected)
    {
        var hello = Parse(Reply($",\"signing\":{{\"state\":\"valid\",\"keyFingerprint\":\"{sent}\"}}"));

        Assert.Equal(expected, hello.SigningKeyFingerprint);
    }

    [Fact]
    public void ANonStringFingerprintIsIgnored()
    {
        var hello = Parse(Reply(",\"signing\":{\"state\":\"valid\",\"keyFingerprint\":12345}"));

        Assert.Equal("valid", hello.SigningState);
        Assert.Equal("", hello.SigningKeyFingerprint);
    }
}
