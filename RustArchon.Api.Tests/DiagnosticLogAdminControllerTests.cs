// Copyright ©2026 Scott Blomfield

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.DiagnosticLogs;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

public class DiagnosticLogAdminControllerTests
{
    private static DiagnosticLogAdminController Create(Mock<IDiagnosticLogTokenService> tokens)
    {
        var controller = new DiagnosticLogAdminController(tokens.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "admin@example.com")], "test"))
                }
            }
        };
        return controller;
    }

    [Fact]
    public async Task GetSaysThereIsNoTokenWhenNoneWasEverCreated()
    {
        var tokens = new Mock<IDiagnosticLogTokenService>();
        tokens.Setup(t => t.GetStatusAsync()).ReturnsAsync((DiagnosticLogTokenStatus?)null);

        var result = await Create(tokens).Get();

        var dto = Assert.IsType<DiagnosticLogTokenStatusDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(dto.Exists);
    }

    [Fact]
    public async Task GetShowsTheIdAndWhoMadeItButCarriesNoSecretField()
    {
        var tokens = new Mock<IDiagnosticLogTokenService>();
        var created = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        tokens.Setup(t => t.GetStatusAsync()).ReturnsAsync(new DiagnosticLogTokenStatus("abcd1234", created, "admin@example.com", null));

        var result = await Create(tokens).Get();

        var dto = Assert.IsType<DiagnosticLogTokenStatusDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(dto.Exists);
        Assert.Equal("abcd1234", dto.TokenId);
        Assert.Equal(created, dto.CreatedAtUtc);
        Assert.DoesNotContain(typeof(DiagnosticLogTokenStatusDto).GetProperties(), p => p.Name.Contains("Secret") || p.Name == "Token");
    }

    [Fact]
    public async Task CreateReturnsTheTokenOnceUncachedAndRecordsTheActor()
    {
        var tokens = new Mock<IDiagnosticLogTokenService>();
        tokens.Setup(t => t.CreateAsync("admin@example.com"))
            .ReturnsAsync(new DiagnosticLogTokenCreated("abcd1234.secret", "abcd1234", DateTimeOffset.UnixEpoch));
        var controller = Create(tokens);

        var result = await controller.Create();

        var dto = Assert.IsType<DiagnosticLogTokenCreatedDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("abcd1234.secret", dto.Token);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        tokens.Verify(t => t.CreateAsync("admin@example.com"), Times.Once);
    }

    [Fact]
    public async Task RevokeIsNoContentWhenItRevokedAndNotFoundWhenThereWasNothing()
    {
        var tokens = new Mock<IDiagnosticLogTokenService>();
        tokens.SetupSequence(t => t.RevokeAsync("admin@example.com")).ReturnsAsync(true).ReturnsAsync(false);
        var controller = Create(tokens);

        Assert.IsType<NoContentResult>(await controller.Revoke());
        Assert.IsType<NotFoundResult>(await controller.Revoke());
    }
}
