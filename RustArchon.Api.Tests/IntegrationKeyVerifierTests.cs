// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Checking an admin-typed key with its provider. The bodies here are what each provider's documentation says a successful call
/// returns; nobody has run this against the live providers, which is why the emphasis is on the failing-closed paths: only a
/// positive signal is ever "valid".
/// </summary>
public class IntegrationKeyVerifierTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // A real handler stops for a cancelled caller; a stub that ignored the token would make that path untestable.
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static (IntegrationKeyVerifier Verifier, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new IntegrationKeyVerifier(new HttpClient(handler), NullLogger<IntegrationKeyVerifier>.Instance), handler);
    }

    private static Task<IntegrationKeyVerdict> Verify(
        IntegrationKeyVerifier verifier, IntegrationKeyKind kind, GeolocationProviderKind provider = GeolocationProviderKind.None) =>
        verifier.VerifyAsync(kind, provider, "the-key", CancellationToken.None);

    private const string SteamGood = "{\"players\":[{\"SteamId\":\"76561197960287930\",\"NumberOfVACBans\":0}]}";
    private const string IpHubGood = "{\"ip\":\"8.8.8.8\",\"countryCode\":\"US\",\"block\":0}";
    private const string IpInfoGood = "{\"ip\":\"8.8.8.8\",\"country_code\":\"US\",\"as_name\":\"Google LLC\"}";

    // ---- Steam ----

    [Fact]
    public async Task SteamAcceptingTheKeyWithARealAnswerIsValidAndTheKeyGoesOnlyToSteam()
    {
        var (verifier, handler) = Create(_ => Json(HttpStatusCode.OK, SteamGood));

        Assert.Equal(IntegrationKeyVerdict.Valid, await Verify(verifier, IntegrationKeyKind.SteamWebApi));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("api.steampowered.com", request.RequestUri!.Host);
        Assert.Contains("key=the-key", request.RequestUri.Query);
    }

    [Fact]
    public async Task ASteamKeyIsUrlEncodedSoItCannotInjectExtraParameters()
    {
        var (verifier, handler) = Create(_ => Json(HttpStatusCode.OK, SteamGood));

        await verifier.VerifyAsync(IntegrationKeyKind.SteamWebApi, GeolocationProviderKind.None, "k&steamids=1", CancellationToken.None);

        Assert.Contains("key=k%26steamids%3D1", Assert.Single(handler.Requests).RequestUri!.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, IntegrationKeyVerdict.InvalidKey)]
    [InlineData(HttpStatusCode.Unauthorized, IntegrationKeyVerdict.InvalidKey)]
    [InlineData(HttpStatusCode.TooManyRequests, IntegrationKeyVerdict.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, IntegrationKeyVerdict.Unreachable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, IntegrationKeyVerdict.Unreachable)]
    [InlineData(HttpStatusCode.NotFound, IntegrationKeyVerdict.Unknown)]
    [InlineData(HttpStatusCode.BadRequest, IntegrationKeyVerdict.Unknown)]
    public async Task SteamRefusalsAreClassifiedAndNeverValid(HttpStatusCode status, IntegrationKeyVerdict expected)
    {
        var (verifier, _) = Create(_ => Json(status, "<html>nope</html>"));

        Assert.Equal(expected, await Verify(verifier, IntegrationKeyKind.SteamWebApi));
    }

    [Theory]
    [InlineData("{\"players\":[]}")]
    [InlineData("{}")]
    [InlineData("<html>hello</html>")]
    [InlineData("")]
    [InlineData("[]")]
    public async Task ASuccessfulAnswerWithoutTheExpectedShapeIsUnknownNotValid(string body)
    {
        var (verifier, _) = Create(_ => Json(HttpStatusCode.OK, body));

        Assert.Equal(IntegrationKeyVerdict.Unknown, await Verify(verifier, IntegrationKeyKind.SteamWebApi));
    }

    // ---- geolocation ----

    [Fact]
    public async Task IpHubSendsTheKeyInItsHeaderNotTheAddress()
    {
        var (verifier, handler) = Create(_ => Json(HttpStatusCode.OK, IpHubGood));

        Assert.Equal(IntegrationKeyVerdict.Valid, await Verify(verifier, IntegrationKeyKind.Geolocation, GeolocationProviderKind.IpHubInfo));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("v2.api.iphub.info", request.RequestUri!.Host);
        Assert.DoesNotContain("the-key", request.RequestUri.ToString());
        Assert.Equal("the-key", Assert.Single(request.Headers.GetValues("X-Key")));
    }

    [Fact]
    public async Task IpInfoSendsTheKeyAsABearerTokenNotInTheAddress()
    {
        var (verifier, handler) = Create(_ => Json(HttpStatusCode.OK, IpInfoGood));

        Assert.Equal(IntegrationKeyVerdict.Valid, await Verify(verifier, IntegrationKeyKind.Geolocation, GeolocationProviderKind.IpInfoIo));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("api.ipinfo.io", request.RequestUri!.Host);
        Assert.DoesNotContain("the-key", request.RequestUri.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("the-key", request.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData(GeolocationProviderKind.IpHubInfo)]
    [InlineData(GeolocationProviderKind.IpInfoIo)]
    public async Task AGeolocationProviderRejectingTheKeyIsInvalidAndLimitingUsIsRateLimited(GeolocationProviderKind provider)
    {
        var (rejects, _) = Create(_ => Json(HttpStatusCode.Forbidden, "{\"error\":\"bad key\"}"));
        var (limits, _) = Create(_ => Json(HttpStatusCode.TooManyRequests, "{}"));

        Assert.Equal(IntegrationKeyVerdict.InvalidKey, await Verify(rejects, IntegrationKeyKind.Geolocation, provider));
        Assert.Equal(IntegrationKeyVerdict.RateLimited, await Verify(limits, IntegrationKeyKind.Geolocation, provider));
    }

    [Theory]
    [InlineData(GeolocationProviderKind.IpHubInfo, "{\"error\":\"something\"}")]
    [InlineData(GeolocationProviderKind.IpInfoIo, "{\"status\":401}")]
    [InlineData(GeolocationProviderKind.IpHubInfo, "not json")]
    public async Task ASuccessStatusWithoutTheFieldsAnAcceptedKeyGetsIsNeverValid(GeolocationProviderKind provider, string body)
    {
        var (verifier, _) = Create(_ => Json(HttpStatusCode.OK, body));

        Assert.Equal(IntegrationKeyVerdict.Unknown, await Verify(verifier, IntegrationKeyKind.Geolocation, provider));
    }

    /// <summary>
    /// proxycheck.io answers a successful call identically for a good key and for one it does not recognise, so no answer can
    /// confirm one. It must never claim to have.
    /// </summary>
    [Fact]
    public async Task ProxyCheckCanNeverBeConfirmedOnlyRefused()
    {
        var (ok, _) = Create(_ => Json(HttpStatusCode.OK, "{\"status\":\"ok\",\"8.8.8.8\":{\"proxy\":\"no\"}}"));
        var (denied, _) = Create(_ => Json(HttpStatusCode.Forbidden, "{\"status\":\"denied\"}"));
        var (limited, _) = Create(_ => Json(HttpStatusCode.TooManyRequests, "{\"status\":\"denied\"}"));

        Assert.Equal(IntegrationKeyVerdict.Unknown, await Verify(ok, IntegrationKeyKind.Geolocation, GeolocationProviderKind.ProxyCheckIo));
        Assert.Equal(IntegrationKeyVerdict.InvalidKey, await Verify(denied, IntegrationKeyKind.Geolocation, GeolocationProviderKind.ProxyCheckIo));
        Assert.Equal(IntegrationKeyVerdict.RateLimited, await Verify(limited, IntegrationKeyKind.Geolocation, GeolocationProviderKind.ProxyCheckIo));
    }

    [Fact]
    public async Task NoProviderIsUnknownAndNothingIsCalled()
    {
        var (verifier, handler) = Create(_ => Json(HttpStatusCode.OK, IpHubGood));

        Assert.Equal(IntegrationKeyVerdict.Unknown, await Verify(verifier, IntegrationKeyKind.Geolocation, GeolocationProviderKind.None));
        Assert.Empty(handler.Requests);
    }

    // ---- transport failures ----

    [Fact]
    public async Task AnUnreachableProviderIsUnreachableNotValid()
    {
        var verifier = new IntegrationKeyVerifier(
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused"))),
            NullLogger<IntegrationKeyVerifier>.Instance);

        Assert.Equal(IntegrationKeyVerdict.Unreachable, await Verify(verifier, IntegrationKeyKind.SteamWebApi));
    }

    [Fact]
    public async Task AProviderThatTimesOutIsUnreachableButACallerWhoCancelsIsStillCancelled()
    {
        var timesOut = new IntegrationKeyVerifier(
            new HttpClient(new StubHandler(_ => throw new TaskCanceledException("timeout"))),
            NullLogger<IntegrationKeyVerifier>.Instance);
        Assert.Equal(IntegrationKeyVerdict.Unreachable, await Verify(timesOut, IntegrationKeyKind.SteamWebApi));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var (verifier, _) = Create(_ => Json(HttpStatusCode.OK, SteamGood));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verifier.VerifyAsync(IntegrationKeyKind.SteamWebApi, GeolocationProviderKind.None, "k", cancelled.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankKeyIsInvalidWithoutCallingAnyone(string key)
    {
        var (verifier, handler) = Create(_ => Json(HttpStatusCode.OK, SteamGood));

        Assert.Equal(IntegrationKeyVerdict.InvalidKey, await verifier.VerifyAsync(IntegrationKeyKind.SteamWebApi, GeolocationProviderKind.None, key, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    // ---- the endpoint ----

    private static async Task<ActionResult<VerifyIntegrationKeyResultDto>> Call(
        VerifyIntegrationKeyRequest request, IntegrationKeyVerdict verdict = IntegrationKeyVerdict.Valid)
    {
        var verifier = new Mock<IIntegrationKeyVerifier>();
        verifier.Setup(v => v.VerifyAsync(It.IsAny<IntegrationKeyKind>(), It.IsAny<GeolocationProviderKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(verdict);
        var controller = new IntegrationVerificationController(verifier.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };
        return await controller.Verify(request);
    }

    [Fact]
    public async Task TheEndpointReturnsTheVerdictAndIsNeverCached()
    {
        var result = await Call(new VerifyIntegrationKeyRequest { Kind = IntegrationKeyKind.SteamWebApi, Key = "abc" }, IntegrationKeyVerdict.InvalidKey);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(IntegrationKeyVerdict.InvalidKey, Assert.IsType<VerifyIntegrationKeyResultDto>(ok.Value).Verdict);
    }

    [Fact]
    public async Task TheEndpointRefusesRequestsThatAreNotWellFormed()
    {
        var tooLong = new string('k', IntegrationVerificationController.MaxKeyLength + 1);

        Assert.IsType<BadRequestResult>((await Call(new VerifyIntegrationKeyRequest { Kind = IntegrationKeyKind.SteamWebApi, Key = "" })).Result);
        Assert.IsType<BadRequestResult>((await Call(new VerifyIntegrationKeyRequest { Kind = IntegrationKeyKind.SteamWebApi, Key = tooLong })).Result);
        Assert.IsType<BadRequestResult>((await Call(new VerifyIntegrationKeyRequest { Kind = (IntegrationKeyKind)9, Key = "abc" })).Result);
        Assert.IsType<BadRequestResult>((await Call(new VerifyIntegrationKeyRequest { Kind = IntegrationKeyKind.Geolocation, Provider = GeolocationProviderKind.None, Key = "abc" })).Result);
        Assert.IsType<BadRequestResult>((await Call(new VerifyIntegrationKeyRequest { Kind = IntegrationKeyKind.Geolocation, Provider = (GeolocationProviderKind)9, Key = "abc" })).Result);
    }
}
