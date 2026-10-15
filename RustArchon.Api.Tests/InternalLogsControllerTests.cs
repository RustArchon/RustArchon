// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Diagnostics;
using RustArchon.Messaging.Contracts;

namespace RustArchon.Api.Tests;

/// <summary>
/// The Api end of the diagnostic log endpoint (ADR-0006): token handling (off / refused / allowed), query handling, the sources it merges, and what it
/// records about being used.
/// </summary>
public class InternalLogsControllerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class CapturingLogger : ILogger<InternalLogsController>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Fixture
    {
        public Mock<IDiagnosticLogTokenService> Tokens { get; } = new();

        public LogRing ApiRing { get; } = new("api", "api-boot", 1000);

        public WorkerLogStore Workers { get; } = new();

        public Mock<IPluginLogSourceFactory> Plugins { get; } = new();

        public Mock<IPlatformSettingsCache> Settings { get; } = new();

        public CapturingLogger Logger { get; } = new();

        public InternalLogsController Controller(string? query = null, IDictionary<string, string>? headers = null)
        {
            var controller = new InternalLogsController(Tokens.Object, ApiRing, Workers, Plugins.Object, Settings.Object, Logger, TimeProvider.System);
            var http = new DefaultHttpContext();
            http.Response.Body = new MemoryStream();
            if (query is not null)
            {
                http.Request.QueryString = new QueryString(query);
            }

            controller.ControllerContext = new ControllerContext { HttpContext = http };
            return controller;
        }

        public void TokenIs(DiagnosticLogTokenState state, string? id = "tok12345") =>
            Tokens.Setup(t => t.CheckAsync(It.IsAny<string?>())).ReturnsAsync(new DiagnosticLogTokenCheck(state, state == DiagnosticLogTokenState.Valid ? id : null));
    }

    private static async Task<(IActionResult Result, string Body)> Call(InternalLogsController controller, string? token = "tok12345.secret")
    {
        var result = await controller.Get(token, CancellationToken.None);
        controller.Response.Body.Position = 0;
        return (result, await new StreamReader(controller.Response.Body, Encoding.UTF8).ReadToEndAsync());
    }

    private static string[] Lines(string body) => body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task WithNoTokenCreatedTheEndpointDoesNotExist()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Off);
        f.ApiRing.Append(T0, LogLevel.Error, "A", "should never be seen", null, null);

        var (result, body) = await Call(f.Controller());

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong.token")]
    public async Task AnyRefusedTokenGetsTheSameBareUnauthorizedAndNoLogData(string? token)
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Invalid);
        f.ApiRing.Append(T0, LogLevel.Error, "A", "should never be seen", null, null);

        var (result, body) = await Call(f.Controller(), token);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(body);
    }

    [Fact]
    public async Task AValidTokenGetsApiAndWorkerEntriesEachTaggedWithItsSourceAndAFinalMetaLine()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        f.ApiRing.Append(T0, LogLevel.Information, "Api.Cat", "from the api", null, null);
        f.Workers.Ingest(new WorkerLogBatch(
            "aaaaaaaa1111",
            [new WorkerLogEntry(1, T0.AddSeconds(1), (int)LogLevel.Information, "Worker.Cat", "from the worker", null, new Dictionary<string, string>())]));

        var (result, body) = await Call(f.Controller("?sources=api,worker"));

        Assert.IsType<EmptyResult>(result);
        var lines = Lines(body);
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("api", first.RootElement.GetProperty("source").GetString());
        Assert.Equal("worker:aaaaaaaa", second.RootElement.GetProperty("source").GetString());
        using var meta = JsonDocument.Parse(lines[2]);
        Assert.Equal(2, meta.RootElement.GetProperty("_meta").GetProperty("sources").GetArrayLength());
    }

    [Fact]
    public async Task OneWorkerInstanceCanBeAskedForByItsSourceName()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        f.Workers.Ingest(new WorkerLogBatch("aaaaaaaa1111", [new WorkerLogEntry(1, T0, (int)LogLevel.Information, "W", "from a", null, new Dictionary<string, string>())]));
        f.Workers.Ingest(new WorkerLogBatch("bbbbbbbb2222", [new WorkerLogEntry(1, T0, (int)LogLevel.Information, "W", "from b", null, new Dictionary<string, string>())]));

        var (_, body) = await Call(f.Controller("?sources=worker:bbbbbbbb"));

        var lines = Lines(body);
        Assert.Equal("from b", JsonDocument.Parse(lines[0]).RootElement.GetProperty("message").GetString());
        using var meta = JsonDocument.Parse(lines[^1]);
        Assert.Equal(new[] { "worker:bbbbbbbb" }, meta.RootElement.GetProperty("_meta").GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task AWorkerInstanceThatIsNotHeldIsNamedUnavailableNotSilentlyOmitted()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);

        var (_, body) = await Call(f.Controller("?sources=worker:cccccccc"));

        using var meta = JsonDocument.Parse(Lines(body)[^1]);
        var source = meta.RootElement.GetProperty("_meta").GetProperty("sources")[0];
        Assert.Equal("worker:cccccccc", source.GetProperty("name").GetString());
        Assert.False(source.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task TheResponseIsNdjsonAndNotCached()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        var controller = f.Controller("?sources=api");

        await Call(controller);

        Assert.Equal("application/x-ndjson", controller.Response.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task WorkerIsReportedUnavailableWithAReasonWhenNoWorkerHasBeenHeardFrom()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        f.ApiRing.Append(T0, LogLevel.Information, "A", "api entry", null, null);

        var (_, body) = await Call(f.Controller("?sources=api,worker"));

        using var meta = JsonDocument.Parse(Lines(body)[^1]);
        var worker = meta.RootElement.GetProperty("_meta").GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "worker");
        Assert.False(worker.GetProperty("available").GetBoolean());
        Assert.Contains("RabbitMQ", worker.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AskingForAPluginSourceUsesTheFactoryForThatServer()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        var serverId = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        var plugin = new Mock<ILogSource>();
        plugin.SetupGet(p => p.Name).Returns("plugin:" + serverId);
        plugin.Setup(p => p.ReadAsync(It.IsAny<SourceCursor?>(), It.IsAny<int>(), It.IsAny<Func<LogEntry, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(LogSourceResult.Unavailable("plugin:" + serverId, "the plugin is not installed"));
        f.Plugins.Setup(p => p.Create(serverId)).Returns(plugin.Object);

        var (_, body) = await Call(f.Controller("?sources=plugin:" + serverId));

        f.Plugins.Verify(p => p.Create(serverId), Times.Once);
        using var meta = JsonDocument.Parse(Lines(body)[^1]);
        var source = meta.RootElement.GetProperty("_meta").GetProperty("sources")[0];
        Assert.Equal("the plugin is not installed", source.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ThePanelSourceIsLeftToThePanel()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        f.ApiRing.Append(T0, LogLevel.Information, "A", "api entry", null, null);

        var (_, body) = await Call(f.Controller("?sources=api,panel"));

        using var meta = JsonDocument.Parse(Lines(body)[^1]);
        Assert.Equal(new[] { "api" }, meta.RootElement.GetProperty("_meta").GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task FiltersAndCursorsAreHonoured()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        f.ApiRing.Append(T0, LogLevel.Debug, "A", "debug one", null, null);
        f.ApiRing.Append(T0, LogLevel.Information, "A", "info two", null, null);
        f.ApiRing.Append(T0, LogLevel.Information, "A", "info three", null, null);

        var (_, body) = await Call(f.Controller("?sources=api&level=information&after=api:api-boot:2"));

        var entries = Lines(body).SkipLast(1).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetString()).ToArray();
        Assert.Equal(new[] { "info three" }, entries);
    }

    [Fact]
    public async Task ABadParameterIsA400NamingItOnlyAfterTheTokenWasAccepted()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);

        var (result, _) = await Call(f.Controller("?levl=debug"));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("levl", bad.Value!.ToString());
    }

    [Fact]
    public async Task ABadParameterWithABadTokenStillLooksLikeAnyOtherRefusal()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Invalid);

        var (result, _) = await Call(f.Controller("?levl=debug"), "wrong");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task SummaryModeReturnsCountsAndNoEntries()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);
        f.ApiRing.Append(T0, LogLevel.Information, "A", "one", null, null);

        var (_, body) = await Call(f.Controller("?sources=api&mode=summary"));

        var lines = Lines(body);
        Assert.Equal(2, lines.Length);
        Assert.Contains("_summary", lines[0]);
    }

    [Fact]
    public async Task UseIsLoggedWithTheTokenIdAndFiltersButNotTheResponse()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid, "tok12345");
        f.ApiRing.Append(T0, LogLevel.Information, "A", "secret-looking content that must not be echoed", null, null);

        await Call(f.Controller("?sources=api&level=debug&text=needle"));

        var entry = Assert.Single(f.Logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("tok12345", entry.Message);
        Assert.Contains("needle", entry.Message);
        Assert.DoesNotContain("secret-looking", entry.Message);
    }

    [Fact]
    public async Task ARejectionIsLoggedAsAWarningWithoutTheTokenValue()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Invalid);

        await Call(f.Controller(), "presented.SECRETVALUE");

        var entry = Assert.Single(f.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain("SECRETVALUE", entry.Message);
    }

    [Fact]
    public async Task LimitsReportsTheConfiguredFailureLimitWithoutNeedingAToken()
    {
        var f = new Fixture();
        f.Settings
            .Setup(s => s.GetStringAsync(PlatformSettingsRegistry.LogReadFailuresPerAddressPerMinute))
            .ReturnsAsync("25");

        var result = await f.Controller().Limits();

        Assert.Equal(25, result.Value!.FailuresPerAddressPerMinute);
        f.Tokens.Verify(t => t.CheckAsync(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task HelpNeedsAValidTokenToo()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Invalid);

        var result = await f.Controller().Help("wrong");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task HelpDescribesTheParametersFieldsAndPaging()
    {
        var f = new Fixture();
        f.TokenIs(DiagnosticLogTokenState.Valid);

        var result = await f.Controller().Help("tok12345.secret");

        var content = Assert.IsType<ContentResult>(result);
        Assert.StartsWith("text/plain", content.ContentType);
        foreach (var word in new[] { "sources", "level", "since", "after", "returnedThrough", "lost", "mode=summary", "X-RustArchon-Log-Token" })
        {
            Assert.Contains(word, content.Content);
        }
    }
}
