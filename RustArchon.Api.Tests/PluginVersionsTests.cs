// Copyright ©2026 Scott Blomfield

using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// <see cref="PluginVersions"/>: numeric, part-by-part, and fail closed - anything that is not major.minor.patch is
/// "not newer", so an odd version can never cause an update to be offered or started.
/// </summary>
public class PluginVersionsTests
{
    [Theory]
    [InlineData("0.2.1", "0.2.0", true)]
    [InlineData("0.3.0", "0.2.9", true)]
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.10.0", "0.9.0", true)] // a string comparison gets this wrong
    [InlineData("0.2.10", "0.2.9", true)]
    [InlineData("0.2.0", "0.2.0", false)] // equal is not newer
    [InlineData("0.2.0", "0.2.1", false)]
    [InlineData("0.9.0", "0.10.0", false)]
    [InlineData("0.1.99", "0.2.0", false)]
    public void IsNewerComparesNumericallyPartByPart(string offered, string installed, bool expected)
    {
        Assert.Equal(expected, PluginVersions.IsNewer(offered, installed));
    }

    [Theory]
    [InlineData(null, "0.2.0")]
    [InlineData("0.2.1", null)]
    [InlineData(null, null)]
    [InlineData("", "0.2.0")]
    [InlineData("0.2.1", "")]
    [InlineData("1.2", "0.2.0")]
    [InlineData("0.2.1", "1.2")]
    [InlineData("1.2.3.4", "0.2.0")]
    [InlineData("v1.2.3", "0.2.0")]
    [InlineData("1.2.3-beta", "0.2.0")]
    [InlineData("a.b.c", "0.2.0")]
    [InlineData(" 1.2.3", "0.2.0")]
    [InlineData("99999999999.0.0", "0.2.0")] // too large for an int
    public void AnythingThatIsNotThreeNumericPartsIsNeverNewer(string? offered, string? installed)
    {
        Assert.False(PluginVersions.IsNewer(offered, installed));
    }

    [Theory]
    [InlineData("0.0.0", true)]
    [InlineData("1.22.333", true)]
    [InlineData("1.2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("1.2.3 ", false)]
    public void IsValidAcceptsOnlyThreeNumericParts(string? version, bool expected)
    {
        Assert.Equal(expected, PluginVersions.IsValid(version));
    }
}
