// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Api.DiagnosticViews;

namespace RustArchon.Api.Tests;

/// <summary>
/// The Api end of the diagnostic views door (ADR-0007): the same token rules as the log endpoint (off, refused, allowed), the catalog, a view by name, and
/// what is recorded about each use - the token, the view and its parameters, never the answer.
/// </summary>
public class InternalDiagnosticViewsControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 4, 30, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public sealed class EchoView : IDiagnosticView
    {
        public static List<string> Ran { get; } = [];

        public static Func<ViewArguments, ViewAnswer>? Behaviour { get; set; }

        public static ViewDefinition Definition { get; } = new(
            "echo", 1, "Echoes a name.",
            [new ViewParameter("name", "string", Required: true, "What to echo.")],
            [new ViewPartDefinition("result", ViewPartKind.Object, "The echo.", null, [new ViewField("name", "string", false)])]);

        public Task<ViewAnswer> ExecuteAsync(ViewArguments arguments, CancellationToken cancellationToken)
        {
            Ran.Add(arguments.GetString("name")!);
            return Task.FromResult(Behaviour?.Invoke(arguments)
                ?? ViewAnswer.Ok(new Dictionary<string, object?> { ["result"] = new Dictionary<string, object?> { ["name"] = arguments.GetString("name") } }));
        }
    }

    private sealed class Fixture
    {
        public Mock<IDiagnosticLogTokenService> Tokens { get; } = new();

        public CapturingLogger<InternalDiagnosticViewsController> Logger { get; } = new();

        public InternalDiagnosticViewsController Controller(string? query = null)
        {
            var services = new ServiceCollection();
            services.AddDiagnosticView<EchoView>();
            var provider = services.BuildServiceProvider();
            var http = new DefaultHttpContext { RequestServices = provider };
            if (query is not null)
            {
                http.Request.QueryString = new QueryString(query);
            }

            return new InternalDiagnosticViewsController(Tokens.Object, provider.GetRequiredService<ViewCatalog>(), provider, new ViewRunner(new FixedClock()), Logger)
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
        }

        public void TokenIs(DiagnosticLogTokenState state, string? id = "tok12345") =>
            Tokens.Setup(t => t.CheckAsync(It.IsAny<string?>())).ReturnsAsync(new DiagnosticLogTokenCheck(state, state == DiagnosticLogTokenState.Valid ? id : null));
    }

    private static JsonElement Json(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<ObjectResult>(result).Value);

    [Fact]
    public async Task WithNoTokenCreatedNeitherTheCatalogNorAViewExists()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Off);
        var controller = f.Controller("?name=x");

        Assert.IsType<NotFoundResult>(await controller.Catalog("tok12345.secret"));
        Assert.IsType<NotFoundResult>(await controller.View("echo", "tok12345.secret", CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong.token")]
    public async Task AnyRefusedTokenGetsTheSameBareUnauthorizedWhateverIsAskedFor(string? token)
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Invalid);
        EchoView.Ran.Clear();
        var controller = f.Controller("?name=x&bad=query");

        Assert.IsType<UnauthorizedResult>(await controller.Catalog(token));
        Assert.IsType<UnauthorizedResult>(await controller.View("echo", token, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.View("no-such-view", token, CancellationToken.None));
        Assert.Empty(EchoView.Ran);
    }

    [Fact]
    public async Task ARefusedTokenIsNeverEchoedIntoTheLog()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Invalid);

        await f.Controller().Catalog("tok12345.SUPERSECRETVALUE");

        Assert.DoesNotContain(f.Logger.Entries, e => e.Message.Contains("SUPERSECRETVALUE"));
    }

    [Fact]
    public async Task AValidTokenGetsTheCatalogWithEveryViewDescribed()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);

        var json = Json(await f.Controller().Catalog("tok12345.secret"));

        var view = json.GetProperty("views")[0];
        Assert.Equal("echo", view.GetProperty("name").GetString());
        Assert.Equal("name", view.GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.Equal("name", view.GetProperty("parts")[0].GetProperty("fields")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AValidTokenRunsTheNamedViewWithTheParametersAndGetsTheEnvelope()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        EchoView.Ran.Clear();

        var result = await f.Controller("?name=hello").View("echo", "tok12345.secret", CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, response.StatusCode);
        var json = Json(result);
        Assert.Equal("echo", json.GetProperty("view").GetString());
        Assert.Equal("hello", json.GetProperty("data").GetProperty("result").GetProperty("name").GetString());
        Assert.Equal(Now, json.GetProperty("readAtUtc").GetDateTimeOffset());
        Assert.Equal(["hello"], EchoView.Ran);
    }

    [Fact]
    public async Task AnUnknownViewIsANotFoundErrorWithTheErrorShapeNotABareNotFound()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);

        var result = await f.Controller().View("no-such-view", "tok12345.secret", CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("not_found", Json(result).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ABadParameterIsA400WithTheErrorShape()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);

        var result = await f.Controller("?surprise=1").View("echo", "tok12345.secret", CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("bad_request", Json(result).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AViewThatBreaksItsOwnDefinitionIsAGeneric500AndNothingItReturnedIsSent()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        EchoView.Behaviour = _ => ViewAnswer.Ok(new Dictionary<string, object?> { ["result"] = new Dictionary<string, object?> { ["name"] = "n", ["password"] = "hunter2" } });
        try
        {
            var result = await f.Controller("?name=x").View("echo", "tok12345.secret", CancellationToken.None);

            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(Json(result)));
            Assert.Contains(f.Logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Message.Contains("echo"));
        }
        finally
        {
            EchoView.Behaviour = null;
        }
    }

    [Fact]
    public async Task EveryUseIsRecordedWithTheTokenTheViewAndTheParametersAndNeverTheAnswer()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid, "tok12345");
        EchoView.Behaviour = _ => ViewAnswer.Ok(new Dictionary<string, object?> { ["result"] = new Dictionary<string, object?> { ["name"] = "THEANSWER" } });
        try
        {
            await f.Controller("?name=asked").View("echo", "tok12345.secret", CancellationToken.None);
        }
        finally
        {
            EchoView.Behaviour = null;
        }

        var entry = Assert.Single(f.Logger.Entries, e => e.Message.Contains("tok12345"));
        Assert.Contains("echo", entry.Message);
        Assert.Contains("asked", entry.Message);
        Assert.DoesNotContain("THEANSWER", entry.Message);
        Assert.DoesNotContain("secret", entry.Message);
    }

    [Fact]
    public async Task AnyResponseIsNeverCached()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        var controller = f.Controller("?name=x");

        await controller.View("echo", "tok12345.secret", CancellationToken.None);

        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void OnlyTheInternalServiceKeyOpensTheDoorAndNoActionAllowsAnonymousAccess()
    {
        var authorize = typeof(InternalDiagnosticViewsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal("InternalApiKey", authorize!.AuthenticationSchemes);
        Assert.All(
            typeof(InternalDiagnosticViewsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            m => Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>()));
    }
}
