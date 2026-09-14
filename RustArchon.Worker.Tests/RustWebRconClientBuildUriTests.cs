// Copyright ©2026 Scott Blomfield

using System;
using RustArchon.Rcon;

namespace RustArchon.Worker.Tests;

/// <summary>
/// Tests for <see cref="RustWebRconClient.BuildUri"/> - Rust authenticates WebRCON via the connection
/// URL's path segment, not a message-based handshake, so the password has to be percent-encoded or any
/// character with URI significance silently corrupts it before it ever reaches the wire. See that
/// method's remarks for the full incident this is a regression test for.
/// </summary>
public class RustWebRconClientBuildUriTests
{
    [Fact]
    public void AnOrdinaryAlphanumericPasswordRoundTripsUnchanged()
    {
        var uri = RustWebRconClient.BuildUri("192.168.0.143", 16012, "SimplePassword123");

        Assert.Equal("ws://192.168.0.143:16012/SimplePassword123", uri.ToString());
    }

    [Theory]
    [InlineData("abc#123")]
    [InlineData("abc?123")]
    [InlineData("abc/123")]
    [InlineData("abc%123")]
    [InlineData("abc 123")]
    public void SpecialCharactersSurviveIntactInsteadOfCorruptingThePath(string password)
    {
        var uri = RustWebRconClient.BuildUri("example.com", 16012, password);

        // The whole point: the server has to see exactly the configured password back, not a
        // truncated/split/re-interpreted version of it. Decoding the built URI's path segment must
        // reproduce the original password exactly.
        var decodedPassword = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        Assert.Equal(password, decodedPassword);

        // And nothing leaked into the query string or fragment - a raw "?"/"#" in the password used to
        // do exactly that.
        Assert.Equal(string.Empty, uri.Query);
        Assert.Equal(string.Empty, uri.Fragment);
    }

    [Fact]
    public void AHashCharacterNoLongerTruncatesThePassword()
    {
        // The clearest single repro of the original bug: unescaped, everything from "#" onward becomes
        // the URI fragment and is never transmitted at all - the server would see "abc", not "abc#123".
        var uri = RustWebRconClient.BuildUri("example.com", 16012, "abc#123");

        Assert.Equal("abc#123", Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')));
    }
}
