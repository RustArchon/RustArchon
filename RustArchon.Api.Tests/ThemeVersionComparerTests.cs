// Copyright ©2026 Scott Blomfield

using RustArchon.Api.Administration;

namespace RustArchon.Api.Tests;

public class ThemeVersionComparerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1", "2")]
    [InlineData("1.2", "1.10")] // numeric, not lexicographic, comparison
    [InlineData("2.0.0-beta.1", "2.0.0")] // a final release outranks a prerelease of the same number
    public void ReportsTheRemoteAsNewer(string local, string remote) =>
        Assert.True(ThemeVersionComparer.IsNewer(local, remote));

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.0.0", "1.0.0-beta.1")] // a prerelease never outranks the final release it precedes
    [InlineData("2.0.0-beta.1", "2.0.0-beta.2")] // prerelease identifiers themselves aren't compared
    [InlineData("2.0.0-alpha", "2.0.0-beta")]
    public void DoesNotReportTheRemoteAsNewer(string local, string remote) =>
        Assert.False(ThemeVersionComparer.IsNewer(local, remote));

    [Theory]
    [InlineData("not-a-version", "1.0.0")]
    [InlineData("1.0.0", "also-not-a-version")]
    [InlineData("", "1.0.0")]
    public void TreatsAMalformedVersionOnEitherSideAsNothingToReport(string local, string remote) =>
        Assert.False(ThemeVersionComparer.IsNewer(local, remote));

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0")]
    [InlineData("1")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0+build.7")]
    public void RecognizesEveryReasonableVersionShape(string value) =>
        Assert.True(ThemeVersionComparer.LooksLikeVersion(value));

    [Theory]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("one point oh")]
    public void RejectsSomethingThatIsNotAVersionAtAll(string value) =>
        Assert.False(ThemeVersionComparer.LooksLikeVersion(value));
}
