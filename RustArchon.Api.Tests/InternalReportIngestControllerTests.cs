// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;

namespace RustArchon.Api.Tests;

/// <summary>
/// The Api end of the Panel's public report door: the secret is checked before a byte of the body is read, every refusal is the same
/// bare 404, and only then is the form filed.
/// </summary>
public class InternalReportIngestControllerTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private const string Token = "a-good-secret";

    private readonly Mock<IReportForwardingService> _forwarding = new();
    private readonly Mock<IReportIngestService> _ingest = new();
    private readonly Mock<IRustServerRepository> _servers = new();
    private readonly Mock<IReportIngestThrottle> _throttle = new();
    private readonly Mock<IPlatformSettingsCache> _settings = new();

    public InternalReportIngestControllerTests()
    {
        _forwarding.Setup(f => f.IsTokenValidAsync(ServerId, Token)).ReturnsAsync(true);
        _servers.Setup(s => s.GetByIdAcrossTenantsAsync(ServerId)).ReturnsAsync(new RustServer { Id = ServerId, TenantId = Guid.NewGuid() });
        _throttle.Setup(t => t.TryAcquire(ServerId, It.IsAny<int>())).Returns(true);
    }

    /// <summary>A stream that fails the test if anything reads it.</summary>
    private sealed class TripwireStream : Stream
    {
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { WasRead = true; return 0; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { WasRead = true; return ValueTask.FromResult(0); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private InternalReportIngestController Create(Stream body, string? contentType = "application/x-www-form-urlencoded")
    {
        var http = new DefaultHttpContext();
        http.Request.Body = body;
        http.Request.ContentType = contentType;
        return new InternalReportIngestController(_forwarding.Object, _ingest.Object, _servers.Object, _throttle.Object, _settings.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static MemoryStream Form(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task AGoodSecretFilesTheReportFromTheFormFields()
    {
        var controller = Create(Form("userid=76561198000000002&data=%7B%22Subject%22%3A%22hi%22%7D"));

        var result = await controller.Ingest(ServerId, Token);

        Assert.IsType<NoContentResult>(result);
        _ingest.Verify(i => i.IngestNativeAsync(
            It.Is<RustServer>(s => s.Id == ServerId), "76561198000000002", "{\"Subject\":\"hi\"}", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ARequestWithNoSecretIsANotFoundAndTheBodyIsNeverRead(string? token)
    {
        var body = new TripwireStream();

        var result = await Create(body).Ingest(ServerId, token);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(body.WasRead);
        _forwarding.Verify(f => f.IsTokenValidAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _ingest.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ASecretThatIsTooLongIsRefusedWithoutEvenBeingChecked()
    {
        var body = new TripwireStream();

        var result = await Create(body).Ingest(ServerId, new string('a', ReportForwardingService.MaxTokenLength + 1));

        Assert.IsType<NotFoundResult>(result);
        Assert.False(body.WasRead);
        _forwarding.Verify(f => f.IsTokenValidAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AWrongSecretIsANotFoundAndTheBodyIsNeverRead()
    {
        var body = new TripwireStream();

        var result = await Create(body).Ingest(ServerId, "the-wrong-secret");

        Assert.IsType<NotFoundResult>(result);
        Assert.False(body.WasRead);
        _ingest.VerifyNoOtherCalls();
        _throttle.Verify(t => t.TryAcquire(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never); // an unauthenticated caller cannot spend the budget
    }

    [Fact]
    public async Task ASecretForAServerThatHasGoneIsANotFound()
    {
        _servers.Setup(s => s.GetByIdAcrossTenantsAsync(ServerId)).ReturnsAsync((RustServer?)null);

        var result = await Create(Form("userid=1&data=%7B%7D")).Ingest(ServerId, Token);

        Assert.IsType<NotFoundResult>(result);
        _ingest.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AServerOverItsBudgetIsToldToSlowDownAndNothingIsFiled()
    {
        _throttle.Setup(t => t.TryAcquire(ServerId, It.IsAny<int>())).Returns(false);

        var result = await Create(Form("userid=1&data=%7B%7D")).Ingest(ServerId, Token);

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<StatusCodeResult>(result).StatusCode);
        _ingest.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheServersBudgetIsThePlatformSettingAndTheDefaultWhenItIsUnsetOrNonsense()
    {
        _settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.ReportsPerServerPerMinute)).ReturnsAsync("7");
        await Create(Form("userid=1&data=%7B%7D")).Ingest(ServerId, Token);
        _throttle.Verify(t => t.TryAcquire(ServerId, 7), Times.Once);

        foreach (var junk in new[] { null, "", "abc", "0", "-5" })
        {
            _throttle.Invocations.Clear();
            _settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.ReportsPerServerPerMinute)).ReturnsAsync(junk);
            await Create(Form("userid=1&data=%7B%7D")).Ingest(ServerId, Token);
            _throttle.Verify(t => t.TryAcquire(ServerId, PlatformSettingsRegistry.DefaultReportsPerServerPerMinute), Times.Once, $"for '{junk}'");
        }
    }

    [Fact]
    public async Task TheLimitsEndpointTellsThePanelThePerAddressSetting()
    {
        _settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.ReportsPerAddressPerMinute)).ReturnsAsync("45");

        var result = await Create(Form("")).Limits();

        Assert.Equal(45, result.Value!.PerAddressPerMinute);
    }

    [Fact]
    public async Task TheLimitsEndpointFallsBackToTheDefaultForANonsensicalSetting()
    {
        _settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.ReportsPerAddressPerMinute)).ReturnsAsync("0");

        var result = await Create(Form("")).Limits();

        Assert.Equal(PlatformSettingsRegistry.DefaultReportsPerAddressPerMinute, result.Value!.PerAddressPerMinute);
    }

    [Fact]
    public async Task ABodyThatIsNotAFormIsABadRequestEvenWithAGoodSecret()
    {
        var result = await Create(Form("{\"Subject\":\"hi\"}"), contentType: "application/json").Ingest(ServerId, Token);

        Assert.IsType<BadRequestResult>(result);
        _ingest.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingFormFieldsAreHandedOnEmptyAndLeftToTheIngestServiceToFlag()
    {
        var controller = Create(Form("unrelated=1"));

        var result = await controller.Ingest(ServerId, Token);

        Assert.IsType<NoContentResult>(result);
        _ingest.Verify(i => i.IngestNativeAsync(It.IsAny<RustServer>(), string.Empty, string.Empty, It.IsAny<CancellationToken>()), Times.Once);
    }
}
