// Copyright ©2026 Scott Blomfield

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Services;

namespace RustArchon.Api.Tests;

/// <summary>
/// Choosing a direct download from the marketplace index's answer, and asking for it. The answers here are the shapes the live index
/// really returned for the plugins on a real server (uMod ones with a direct .cs file, Codefling ones with none, a GitHub listing that
/// knows no version), because the risk is in how a search that returns "everything vaguely like it" is trusted, not in parsing.
/// </summary>
public class PluginDownloadMatcherTests
{
    private const string BlueprintShare =
        """[{"id":"98a1","name":"BlueprintShare","author":"NomadWarrior","marketplace":"uMod","url":"https://umod.org/plugins/blueprint-share","downloadUrl":"https://umod.org/plugins/BlueprintShare.cs","latestVersion":"1.4.7"}]""";

    private const string MonumentAddons =
        """[{"name":"MonumentAddons","author":"WhiteThunder","marketplace":"uMod","url":"https://umod.org/plugins/monument-addons","downloadUrl":"https://umod.org/plugins/MonumentAddons.cs","latestVersion":"0.21.4"},{"name":"MonumentAddons","author":"WheteThunger","marketplace":"Github","url":"https://github.com/WheteThunger/MonumentAddons","downloadUrl":"https://github.com/WheteThunger/MonumentAddons/releases/latest","latestVersion":"0"}]""";

    private const string HarborEvent =
        """[{"name":"Harbor Event","author":"KpucTaJl","marketplace":"Codefling","url":"https://codefling.com/plugins/harbor-event","downloadUrl":null,"latestVersion":"2.4.6"}]""";

    private static PluginDownloadRequest Request(
        string name = "BlueprintShare", string marketplace = "uMod", string version = "1.4.7", string page = "https://umod.org/plugins/blueprint-share") =>
        new(name, marketplace, version, page);

    private static string Listing(
        string name = "Thing", string marketplace = "uMod", string version = "1.0.0", string? download = "https://umod.org/plugins/Thing.cs", string page = "https://umod.org/plugins/thing") =>
        "{\"name\":\"" + name + "\",\"marketplace\":\"" + marketplace + "\",\"url\":\"" + page + "\",\"downloadUrl\":"
        + (download is null ? "null" : "\"" + download + "\"") + ",\"latestVersion\":\"" + version + "\"}";

    private static PluginDownloadMatch Choose(PluginDownloadRequest request, params string[] listings) =>
        PluginDownloadMatcher.Choose("[" + string.Join(",", listings) + "]", request);

    // ---- what counts as a match ----------------------------------------------------------------------------------

    [Fact]
    public void AUModPluginAtTheUpdatedVersionGetsItsDirectFile()
    {
        var match = PluginDownloadMatcher.Choose(BlueprintShare, Request());

        Assert.Equal(PluginDownloadOutcome.Found, match.Outcome);
        Assert.Equal("https://umod.org/plugins/BlueprintShare.cs", match.DownloadUrl);
        Assert.Equal("BlueprintShare", match.MatchedName);
        Assert.Equal("https://umod.org/plugins/blueprint-share", match.MatchedPageUrl);
        Assert.Equal("1.4.7", match.MatchedVersion);
    }

    [Fact]
    public void TheListingOnTheMarketplaceUpdateCheckerNamedIsChosenNotADifferentMarketplacesSamePlugin()
    {
        var match = PluginDownloadMatcher.Choose(MonumentAddons, Request("MonumentAddons", "uMod", "0.21.4", "https://umod.org/plugins/monument-addons"));

        Assert.Equal("https://umod.org/plugins/MonumentAddons.cs", match.DownloadUrl);
    }

    [Fact]
    public void AListingThatKnowsNoVersionIsNotAnAnswerForThisUpdate()
    {
        // The GitHub entry above says "0": it names a page of releases, not the file for 0.21.4.
        var match = PluginDownloadMatcher.Choose(MonumentAddons, Request("MonumentAddons", "GitHub", "0.21.4", "https://github.com/WheteThunger/MonumentAddons"));

        Assert.Equal(PluginDownloadOutcome.NotFound, match.Outcome);
        Assert.Null(match.DownloadUrl);
        Assert.Contains("older", match.Reason);
    }

    [Fact]
    public void APaidOrLoginOnlyPluginWithNoDirectDownloadIsNotFoundAndSaysWhy()
    {
        var match = PluginDownloadMatcher.Choose(HarborEvent, Request("HarborEvent", "Codefling", "2.4.6", "https://codefling.com/plugins/harbor-event"));

        Assert.Equal(PluginDownloadOutcome.NotFound, match.Outcome);
        Assert.Contains("no direct download", match.Reason);   // and it was found by name despite "Harbor Event" having a space
    }

    [Fact]
    public void NamesMatchOnLettersAndDigitsAlone()
    {
        Assert.Equal(PluginDownloadOutcome.Found, Choose(Request("NpcSpawn", "uMod", "1.0.0", ""), Listing("Npc Spawn")).Outcome);
        Assert.Equal(PluginDownloadOutcome.Found, Choose(Request("npc-spawn", "uMod", "1.0.0", ""), Listing("NPC Spawn")).Outcome);
    }

    [Fact]
    public void ASearchThatReturnsOnlyOtherPluginsFindsNothing()
    {
        var match = Choose(Request("RaidableBases", "uMod", "3.1.9", ""), Listing("AntiLadderandTwig"), Listing("RaidableBases XL Duo"), Listing("Raidable Bases Azuriom"));

        Assert.Equal(PluginDownloadOutcome.NotFound, match.Outcome);
        Assert.Contains("no listing", match.Reason);
    }

    [Fact]
    public void TheRightNameOnTheWrongMarketplaceIsNotAMatch()
    {
        var match = Choose(Request("Thing", "Codefling", "1.0.0", ""), Listing("Thing", marketplace: "uMod"));

        Assert.Equal(PluginDownloadOutcome.NotFound, match.Outcome);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]     // the same
    [InlineData("1.0.1", "1.0.0", true)]     // the index knows a newer one: still a download for this update
    [InlineData("1.0.0", "1.0.1", false)]    // a stale listing
    [InlineData("v1.0.0", "1.0.0", true)]    // a leading v is not a difference
    [InlineData("0.10.0", "0.9.0", true)]    // numbers, not text
    [InlineData("0.9.0", "0.10.0", false)]
    [InlineData("1.4.2f", "1.4.2f", true)]   // cannot be compared, so it must be written the same
    [InlineData("1.4.2g", "1.4.2f", false)]
    [InlineData("", "1.0.0", false)]
    public void AListingMustBeAtLeastTheVersionTheUpdateIsFor(string listed, string wanted, bool acceptable)
    {
        var match = Choose(Request("Thing", "uMod", wanted, ""), Listing("Thing", version: listed));

        Assert.Equal(acceptable ? PluginDownloadOutcome.Found : PluginDownloadOutcome.NotFound, match.Outcome);
    }

    [Fact]
    public void WhenTwoListingsQualifyTheOneOnTheNamedPageWins()
    {
        var match = Choose(
            Request("Thing", "uMod", "1.0.0", "https://umod.org/plugins/the-real-one/"),
            Listing("Thing", download: "https://umod.org/plugins/Decoy.cs", page: "https://umod.org/plugins/decoy"),
            Listing("Thing", download: "https://umod.org/plugins/Real.cs", page: "https://umod.org/plugins/The-Real-One"));

        Assert.Equal("https://umod.org/plugins/Real.cs", match.DownloadUrl);
    }

    [Fact]
    public void WithNoPageToTellThemApartTheIndexsOwnOrderDecides()
    {
        var match = Choose(
            Request("Thing", "uMod", "1.0.0", ""),
            Listing("Thing", download: "https://umod.org/plugins/First.cs"), Listing("Thing", download: "https://umod.org/plugins/Second.cs"));

        Assert.Equal("https://umod.org/plugins/First.cs", match.DownloadUrl);
    }

    [Fact]
    public void AListingWithoutAnAddressIsSkippedForALaterOneThatHasIt()
    {
        var match = Choose(Request("Thing", "uMod", "1.0.0", ""), Listing("Thing", download: null), Listing("Thing", download: "https://umod.org/plugins/Later.cs"));

        Assert.Equal("https://umod.org/plugins/Later.cs", match.DownloadUrl);
    }

    // ---- the address is the sensitive part -----------------------------------------------------------------------

    [Theory]
    [InlineData("https://umod.org/plugins/Thing.cs", "umod", true)]
    [InlineData("https://cdn.umod.org/plugins/Thing.cs", "umod", true)]                 // under the marketplace's own domain
    [InlineData("HTTPS://UMOD.ORG/plugins/Thing.cs", "umod", true)]                     // case is not a difference
    [InlineData("http://umod.org/plugins/Thing.cs", "umod", false)]                     // not encrypted
    [InlineData("javascript:alert(1)", "umod", false)]
    [InlineData("file:///etc/passwd", "umod", false)]
    [InlineData("//umod.org/plugins/Thing.cs", "umod", false)]                          // not absolute
    [InlineData("/plugins/Thing.cs", "umod", false)]
    [InlineData("https://user:pass@umod.org/plugins/Thing.cs", "umod", false)]          // credentials
    [InlineData("https://evil.example/plugins/Thing.cs", "umod", false)]                // some other host
    [InlineData("https://umod.org.evil.example/plugins/Thing.cs", "umod", false)]       // starts like it
    [InlineData("https://evilumod.org/plugins/Thing.cs", "umod", false)]                // ends like it
    [InlineData("https://umod.org@evil.example/plugins/Thing.cs", "umod", false)]       // the classic
    [InlineData("https://umod.org/plugins/Thing.cs", "codefling", false)]               // a real host, but not this marketplace's
    [InlineData("https://umod.org/plugins/Thing.cs", "someunknownmarketplace", false)]  // a marketplace nobody has vetted
    [InlineData("https://umod.org/plugins/Thing.cs", "", false)]
    [InlineData("", "umod", false)]
    [InlineData(null, "umod", false)]
    public void OnlyAnHttpsAddressOnTheMarketplacesOwnHostIsEverPassedOn(string? url, string marketplaceKey, bool passed)
    {
        var safe = PluginDownloadMatcher.SafeDownloadUrl(url, marketplaceKey);

        Assert.Equal(passed, safe is not null);
    }

    [Fact]
    public void AnAddressLongerThanItsColumnIsNotPassedOn()
    {
        Assert.Null(PluginDownloadMatcher.SafeDownloadUrl("https://umod.org/" + new string('a', 600), "umod"));
    }

    [Fact]
    public void AListingWhoseOnlyAddressIsUnsafeIsNotFoundAndSaysSo()
    {
        var match = Choose(Request("Thing", "uMod", "1.0.0", ""), Listing("Thing", download: "https://evil.example/Thing.cs"));

        Assert.Equal(PluginDownloadOutcome.NotFound, match.Outcome);
        Assert.Contains("not one we pass on", match.Reason);
    }

    [Fact]
    public void AnAddressOnAnotherMarketplacesHostIsNotAcceptedEvenFromAMatchingListing()
    {
        var match = Choose(Request("Thing", "Codefling", "1.0.0", ""), Listing("Thing", marketplace: "Codefling", download: "https://umod.org/plugins/Thing.cs"));

        Assert.Equal(PluginDownloadOutcome.NotFound, match.Outcome);
    }

    // ---- an answer that is not what was expected -----------------------------------------------------------------

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("<html>maintenance</html>")]
    public void AnAnswerThatIsNotJsonIsAFailureNotACrash(string body)
    {
        var match = PluginDownloadMatcher.Choose(body, Request());

        Assert.Equal(PluginDownloadOutcome.Failed, match.Outcome);
    }

    [Theory]
    [InlineData("""{"error":"nope"}""")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void JsonThatIsNotAListIsAFailure(string body)
    {
        Assert.Equal(PluginDownloadOutcome.Failed, PluginDownloadMatcher.Choose(body, Request()).Outcome);
    }

    [Fact]
    public void AnEmptyListIsSimplyNotFound()
    {
        Assert.Equal(PluginDownloadOutcome.NotFound, PluginDownloadMatcher.Choose("[]", Request()).Outcome);
    }

    [Fact]
    public void OddEntriesAreSkippedAndDoNotSpoilTheGoodOnes()
    {
        var body = "[null, 7, \"x\", {\"name\": 5, \"marketplace\": [], \"downloadUrl\": {}}, " + Listing("Thing", download: "https://umod.org/plugins/Thing.cs") + "]";

        var match = PluginDownloadMatcher.Choose(body, Request("Thing", "uMod", "1.0.0", ""));

        Assert.Equal("https://umod.org/plugins/Thing.cs", match.DownloadUrl);
    }

    // ---- keys ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("uMod", "umod")]
    [InlineData("Lone.Design", "lonedesign")]
    [InlineData(" Codefling ", "codefling")]
    [InlineData(null, "")]
    public void AMarketplaceIsKeyedByItsLettersAndDigits(string? marketplace, string key) =>
        Assert.Equal(key, PluginDownloadMatcher.MarketplaceKey(marketplace));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData(" V1.2.3 ", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    public void AVersionIsKeyedWithoutItsLeadingV(string version, string key) => Assert.Equal(key, PluginDownloadMatcher.VersionKey(version));

    // ---- asking --------------------------------------------------------------------------------------------------

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public readonly System.Collections.Generic.List<HttpRequestMessage> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (PluginDownloadResolver Resolver, StubHandler Handler) Resolver(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(PluginDownloadResolver.BaseAddress) };
        return (new PluginDownloadResolver(http, NullLogger<PluginDownloadResolver>.Instance), handler);
    }

    [Fact]
    public async Task AskingSendsOnlyThePluginsNameAndMarketplaceToTheOneFixedAddress()
    {
        var (resolver, handler) = Resolver((_, _) => Task.FromResult(Json(BlueprintShare)));

        await resolver.AskAsync(Request("Blueprint Share&x=1", "Lone.Design", "1.4.7", "https://umod.org/plugins/blueprint-share"), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("serverarmour.com", sent.RequestUri!.Host);
        Assert.Equal("/api/v1/marketplace/search", sent.RequestUri.AbsolutePath);
        // Encoded, so a name cannot add parameters of its own; and nothing else - no author, no server, no version, no credentials.
        Assert.Equal("?plugin=Blueprint%20Share%26x%3D1&market=Lone.Design", sent.RequestUri.Query);
        Assert.Null(sent.Headers.Authorization);
    }

    [Fact]
    public async Task AGoodAnswerIsMatchedAndKeptWhole()
    {
        var (resolver, _) = Resolver((_, _) => Task.FromResult(Json(BlueprintShare)));

        var answer = await resolver.AskAsync(Request(), default);

        Assert.Equal(PluginDownloadOutcome.Found, answer.Match.Outcome);
        Assert.Equal(BlueprintShare, answer.ResponseJson);
        Assert.Equal(200, answer.HttpStatus);
        Assert.False(answer.ResponseTruncated);
        Assert.Null(answer.AskedToWait);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnErrorStatusIsAFailureNotAnAnswer(HttpStatusCode status)
    {
        var (resolver, _) = Resolver((_, _) => Task.FromResult(Json("oops", status)));

        var answer = await resolver.AskAsync(Request(), default);

        Assert.Equal(PluginDownloadOutcome.Failed, answer.Match.Outcome);
        Assert.Equal((int)status, answer.HttpStatus);
        Assert.Null(answer.AskedToWait);
    }

    [Fact]
    public async Task BeingToldToSlowDownAsksEveryoneToWaitAsLongAsTheIndexSays()
    {
        var (resolver, _) = Resolver((_, _) =>
        {
            var response = Json("slow down", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(7));
            return Task.FromResult(response);
        });

        var answer = await resolver.AskAsync(Request(), default);

        Assert.Equal(PluginDownloadOutcome.Failed, answer.Match.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(7), answer.AskedToWait);
    }

    [Fact]
    public async Task BeingToldToSlowDownWithoutSayingForHowLongUsesTheDefault()
    {
        var (resolver, _) = Resolver((_, _) => Task.FromResult(Json("", HttpStatusCode.TooManyRequests)));

        var answer = await resolver.AskAsync(Request(), default);

        Assert.Equal(PluginDownloadResolver.DefaultBackOff, answer.AskedToWait);
    }

    [Fact]
    public async Task AnAbsurdRetryAfterIsCappedSoOneAnswerCannotSwitchLookupsOffForDays()
    {
        var (resolver, _) = Resolver((_, _) =>
        {
            var response = Json("", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(30));
            return Task.FromResult(response);
        });

        var answer = await resolver.AskAsync(Request(), default);

        Assert.Equal(PluginDownloadResolver.MaxBackOff, answer.AskedToWait);
    }

    [Fact]
    public async Task ATimeoutIsAFailureThatIsNotMistakenForTheCallersOwnCancellation()
    {
        var (resolver, _) = Resolver((_, _) => throw new TaskCanceledException("the client's own timeout"));

        var answer = await resolver.AskAsync(Request(), default);

        Assert.Equal(PluginDownloadOutcome.Failed, answer.Match.Outcome);
        Assert.Contains("in time", answer.Match.Reason);
    }

    [Fact]
    public async Task AnUnreachableIndexIsAFailure()
    {
        var (resolver, _) = Resolver((_, _) => throw new HttpRequestException("no route"));

        var answer = await resolver.AskAsync(Request(), default);

        Assert.Equal(PluginDownloadOutcome.Failed, answer.Match.Outcome);
        Assert.Null(answer.HttpStatus);
    }

    [Fact]
    public async Task TheCallersOwnCancellationIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var (resolver, _) = Resolver((_, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Json("[]")); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.AskAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task AnAnswerOverTheLimitIsAFailureAndOnlyWhatFitsIsKept()
    {
        var huge = "[" + new string(' ', PluginDownloadLookup.MaxResponseLength + 5000) + "]";
        var (resolver, _) = Resolver((_, _) => Task.FromResult(Json(huge)));

        var answer = await resolver.AskAsync(Request(), default);

        Assert.Equal(PluginDownloadOutcome.Failed, answer.Match.Outcome);
        Assert.True(answer.ResponseTruncated);
        Assert.Equal(PluginDownloadLookup.MaxResponseLength, answer.ResponseJson!.Length);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAListIsKeptAsReceivedAndCountsAsAFailure()
    {
        var (resolver, _) = Resolver((_, _) => Task.FromResult(Json("<html>maintenance</html>")));

        var answer = await resolver.AskAsync(Request(), default);

        Assert.Equal(PluginDownloadOutcome.Failed, answer.Match.Outcome);
        Assert.Equal("<html>maintenance</html>", answer.ResponseJson);
    }
}
