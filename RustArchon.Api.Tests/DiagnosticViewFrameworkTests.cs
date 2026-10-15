// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RustArchon.Api.DiagnosticViews;

namespace RustArchon.Api.Tests;

/// <summary>
/// The framework every diagnostic view runs in (change 003, ADR-0007): a catalog built once from the views' definitions, parameters checked against it, and
/// an envelope whose content is checked against what the catalog says it will contain - so a view cannot return a field the catalog does not list.
/// </summary>
public class DiagnosticViewFrameworkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 4, 30, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public sealed class Widget
    {
        public Guid Id { get; set; } = Guid.Parse("01a09e82-6ba3-746c-a984-c61fba87ec8d");

        public string Name { get; set; } = "widget";

        [DiagnosticsHidden("a credential")]
        public string Password { get; set; } = "hunter2";
    }

    private static ViewDefinition WidgetDefinition(string name = "widget", int version = 1, string parameterType = "uuid") => new(
        name, version, "Shows a widget.",
        [new ViewParameter("widgetId", parameterType, Required: true, "Which widget.")],
        [
            new ViewPartDefinition("widget", ViewPartKind.Object, "The widget itself.", typeof(Widget), [new ViewField("computed", "string", false)]),
            new ViewPartDefinition("history", ViewPartKind.List, "Recent events.", null, [new ViewField("at", "datetime", false), new ViewField("what", "string", false)])
        ]);

    private sealed class StubView(Func<ViewArguments, ViewAnswer> run) : IDiagnosticView
    {
        public static ViewDefinition Definition => WidgetDefinition();

        public Task<ViewAnswer> ExecuteAsync(ViewArguments arguments, CancellationToken cancellationToken) => Task.FromResult(run(arguments));
    }

    private static ViewAnswer GoodResult() => ViewAnswer.Ok(new Dictionary<string, object?>
    {
        ["widget"] = new Dictionary<string, object?> { ["id"] = Guid.Empty, ["name"] = "w", ["computed"] = "c" },
        ["history"] = new List<Dictionary<string, object?>> { new() { ["at"] = Now, ["what"] = "happened" } }
    });

    private static ViewCatalog CatalogOf(params ViewDefinition[] definitions) => new(definitions);

    // ---- the catalog ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCatalogDescribesEachViewItsParametersAndEveryFieldItCanReturn()
    {
        var description = Assert.Single(CatalogOf(WidgetDefinition()).Describe());

        Assert.Equal(("widget", 1, "Shows a widget."), (description.Name, description.Version, description.Description));
        var parameter = Assert.Single(description.Parameters);
        Assert.Equal(("widgetId", "uuid", true), (parameter.Name, parameter.Type, parameter.Required));
        var widget = description.Parts.Single(p => p.Name == "widget");
        Assert.Equal("object", widget.Kind);
        Assert.Equal(["computed", "id", "name"], widget.Fields.Select(f => f.Name).Order());
        Assert.Equal("list", description.Parts.Single(p => p.Name == "history").Kind);
    }

    [Fact]
    public void AHiddenPropertyOfAnEntityIsAbsentFromTheCatalog()
    {
        var fields = CatalogOf(WidgetDefinition()).Describe().Single().Parts.Single(p => p.Name == "widget").Fields;

        Assert.DoesNotContain(fields, f => f.Name == "password");
    }

    [Fact]
    public void TheCatalogListsViewsByName()
    {
        var names = CatalogOf(WidgetDefinition("zebra"), WidgetDefinition("apple")).Describe().Select(d => d.Name);

        Assert.Equal(["apple", "zebra"], names);
    }

    [Fact]
    public void TwoViewsWithTheSameNameFailTheCatalogsConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => CatalogOf(WidgetDefinition(), WidgetDefinition()));
    }

    [Fact]
    public void AViewWithoutAVersionOrWithAParameterTypeTheCatalogCannotDescribeFailsConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => CatalogOf(WidgetDefinition(version: 0)));
        Assert.Throws<InvalidOperationException>(() => CatalogOf(WidgetDefinition(parameterType: "banana")));
        Assert.Throws<InvalidOperationException>(() => CatalogOf(WidgetDefinition("Not Kebab")));
    }

    [Fact]
    public void TheCatalogIsASingletonBuiltFromTheRegisteredViewsAndTheSameInstanceEveryTime()
    {
        var services = new ServiceCollection();
        services.AddDiagnosticView<StubView>();
        services.AddSingleton<TimeProvider>(new FixedClock());
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<ViewCatalog>();
        var second = provider.GetRequiredService<ViewCatalog>();

        Assert.Same(first, second);
        Assert.Equal("widget", Assert.Single(first.Describe()).Name);
        Assert.NotNull(first.Find("widget"));
        Assert.Null(first.Find("nothing"));
    }

    // ---- running a view ---------------------------------------------------------------------------------------------------------------

    private static Task<ViewRunOutcome> RunAsync(Func<ViewArguments, ViewAnswer> view, Dictionary<string, string?>? query = null, int maxBytes = ViewRunner.MaxResponseBytes)
    {
        var runner = new ViewRunner(new FixedClock(), maxBytes);
        return runner.RunAsync(WidgetDefinition(), new StubView(view), query ?? new() { ["widgetId"] = Guid.Empty.ToString() }, CancellationToken.None);
    }

    [Fact]
    public async Task ASuccessfulViewAnswersWithTheEnvelopeAndWhenItWasRead()
    {
        var outcome = await RunAsync(_ => GoodResult());

        Assert.Equal(200, outcome.Status);
        var json = JsonSerializer.SerializeToElement(outcome.Body);
        Assert.Equal("widget", json.GetProperty("view").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal(Now, json.GetProperty("readAtUtc").GetDateTimeOffset());
        Assert.Equal("w", json.GetProperty("data").GetProperty("widget").GetProperty("name").GetString());
        Assert.Equal(0, json.GetProperty("unavailable").GetArrayLength());
    }

    [Fact]
    public async Task APartThatCouldNotBeReadIsNamedWithItsReasonNotLeftEmpty()
    {
        var outcome = await RunAsync(_ => ViewAnswer.Ok(
            new Dictionary<string, object?> { ["widget"] = new Dictionary<string, object?> { ["id"] = Guid.Empty, ["name"] = "w", ["computed"] = "c" }, ["history"] = null },
            [new UnavailablePart("history", "The server is not connected.")]));

        var json = JsonSerializer.SerializeToElement(outcome.Body);
        Assert.Equal("history", json.GetProperty("unavailable")[0].GetProperty("part").GetString());
        Assert.Equal("The server is not connected.", json.GetProperty("unavailable")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AViewThatReturnsAFieldTheCatalogDoesNotListIsRejectedNotSent()
    {
        var leaking = new Dictionary<string, object?> { ["id"] = Guid.Empty, ["name"] = "w", ["computed"] = "c", ["password"] = "hunter2" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(_ => ViewAnswer.Ok(new Dictionary<string, object?> { ["widget"] = leaking, ["history"] = new List<Dictionary<string, object?>>() })));

        Assert.Contains("password", error.Message);
        Assert.DoesNotContain("hunter2", error.Message);
    }

    [Fact]
    public async Task AViewThatReturnsAPartTheCatalogDoesNotListIsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(_ => ViewAnswer.Ok(new Dictionary<string, object?> { ["surprise"] = new Dictionary<string, object?>() })));
    }

    [Fact]
    public async Task AMissingUnknownOrMalformedParameterIsRefusedBeforeTheViewRuns()
    {
        var ran = false;
        ViewAnswer Spy(ViewArguments _)
        {
            ran = true;
            return GoodResult();
        }

        Assert.Equal(400, (await RunAsync(Spy, new())).Status);
        Assert.Equal(400, (await RunAsync(Spy, new() { ["widgetId"] = "not-a-guid" })).Status);
        Assert.Equal(400, (await RunAsync(Spy, new() { ["widgetId"] = Guid.Empty.ToString(), ["surprise"] = "x" })).Status);
        Assert.False(ran);
    }

    [Fact]
    public async Task AnUnknownThingIsNotFoundWithTheSameShapeOfError()
    {
        var outcome = await RunAsync(_ => ViewAnswer.NotFound("No such widget."));

        Assert.Equal(404, outcome.Status);
        var json = JsonSerializer.SerializeToElement(outcome.Body);
        Assert.Equal("not_found", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("No such widget.", json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AResponseOverTheSizeBoundIsAnErrorNotASilentlyCutAnswer()
    {
        var big = new List<Dictionary<string, object?>>();
        for (var i = 0; i < 200; i++)
        {
            big.Add(new() { ["at"] = Now, ["what"] = new string('x', 100) });
        }

        var outcome = await RunAsync(
            _ => ViewAnswer.Ok(new Dictionary<string, object?> { ["widget"] = new Dictionary<string, object?> { ["id"] = Guid.Empty, ["name"] = "w", ["computed"] = "c" }, ["history"] = big }),
            maxBytes: 4096);

        Assert.Equal(413, outcome.Status);
        Assert.Equal("too_large", JsonSerializer.SerializeToElement(outcome.Body).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void TheArgumentsGiveTypedAccessToWhatWasBound()
    {
        var bound = ViewArguments.Bind(
            [new ViewParameter("serverId", "uuid", true, "x"), new ViewParameter("count", "integer", false, "x", "50"), new ViewParameter("verbose", "boolean", false, "x", "false")],
            new Dictionary<string, string?> { ["serverId"] = Guid.Empty.ToString(), ["verbose"] = "true" });

        Assert.True(bound.Ok);
        Assert.Equal(Guid.Empty, bound.Arguments!.GetGuid("serverId"));
        Assert.Equal(50, bound.Arguments.GetInt("count"));
        Assert.True(bound.Arguments.GetBool("verbose"));
    }
}
