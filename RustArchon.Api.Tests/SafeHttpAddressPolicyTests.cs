// Copyright ©2026 Scott Blomfield

using System.Net;
using RustArchon.Api.Infrastructure.ThemeUpdates;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for the SSRF address blocklist behind <see cref="ThemeUpdateCheckClient"/> - see that class's
/// remarks for the threat model this exists to close.
/// </summary>
public class SafeHttpAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")] // loopback
    [InlineData("127.55.0.1")] // loopback, whole /8
    [InlineData("10.0.0.1")] // RFC 1918 private
    [InlineData("172.16.0.1")] // RFC 1918 private
    [InlineData("172.31.255.255")] // RFC 1918 private, top of the range
    [InlineData("192.168.1.1")] // RFC 1918 private
    [InlineData("169.254.169.254")] // cloud metadata endpoint
    [InlineData("169.254.0.1")] // link-local, whole /16
    [InlineData("100.64.0.1")] // carrier-grade NAT
    [InlineData("0.0.0.0")] // "this network"
    [InlineData("192.0.0.1")] // IETF protocol assignments
    [InlineData("198.18.0.1")] // benchmarking
    [InlineData("224.0.0.1")] // multicast
    [InlineData("255.255.255.255")] // broadcast
    [InlineData("::1")] // IPv6 loopback
    [InlineData("fe80::1")] // IPv6 link-local
    [InlineData("fc00::1")] // IPv6 unique local
    [InlineData("fd12:3456:789a::1")] // IPv6 unique local, other half of fc00::/7
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped IPv6 loopback
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped IPv6 metadata endpoint
    public void DisallowsEveryNonPublicAddress(string address) =>
        Assert.True(SafeHttpAddressPolicy.IsDisallowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")] // a real public IPv6 address (Cloudflare)
    public void AllowsGenuinelyPublicAddresses(string address) =>
        Assert.False(SafeHttpAddressPolicy.IsDisallowed(IPAddress.Parse(address)));
}
