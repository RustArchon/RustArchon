// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The platform-admin plugin controller: locked to the platform-admin permission, attributes every action to the signed
/// in admin, turns a refused operation into a plain sentence (400) and an unknown target into 404, and never hands a
/// private key or source text to the browser.
/// </summary>
public class PluginAdminControllerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private readonly Mock<IPluginKeyService> _keys = new();
    private readonly Mock<IPluginReleaseService> _releases = new();

    private PluginAdminController Create(string? email = "admin@example.com", ApiDbContext? context = null)
    {
        var controller = new PluginAdminController(_keys.Object, _releases.Object, context ?? new ApiDbContext(postgres.Options));
        var identity = new ClaimsIdentity(email is null ? [] : [new Claim(ClaimTypes.Email, email)], "test");
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return controller;
    }

    private static PluginReleaseInfo Release(string version = "9.1.0", PluginReleaseKind kind = PluginReleaseKind.Main) =>
        new(Guid.NewGuid(), kind, version, PluginReleaseState.Draft, new string('a', 64), DateTimeOffset.UtcNow, "admin@example.com", null, null, null, null, null, null, false);

    // ---- who may call it -----------------------------------------------------------------------------------

    [Fact]
    public void EveryActionIsLockedToThePlatformAdminPermission()
    {
        var policy = typeof(PluginAdminController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal("ManagePlatformSettings", policy!.Policy);

        var actions = typeof(PluginAdminController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.All(actions, m => Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>()));
    }

    [Fact]
    public void NoActionExposesASecret()
    {
        var dtoTypes = new[] { typeof(PluginKeyDto), typeof(PluginReleaseDto), typeof(PluginReleasesDto), typeof(PluginServedDto), typeof(PluginAdminEventDto) };
        var names = dtoTypes.SelectMany(t => t.GetProperties()).Select(p => p.Name);

        Assert.DoesNotContain(names, n => n.Contains("Private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n is "Source" or "SourceText");
        Assert.DoesNotContain(names, n => n.Contains("Modulus", StringComparison.OrdinalIgnoreCase));
    }

    // ---- keys ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task RotatingIsAttributedToTheSignedInAdmin()
    {
        _keys.Setup(k => k.RotateAsync("admin@example.com", "yearly", false, null))
            .ReturnsAsync(new PluginKeyInfo("1111111111111111", PluginKeyState.Active, null, null, null, 0, null));

        var result = await Create().Rotate(new RotatePluginKeyRequestDto { Note = "yearly" });

        var dto = Assert.IsType<PluginKeyDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("active", dto.State);
        _keys.VerifyAll();
    }

    [Fact]
    public async Task RotatingWithARevokeAsksForItAndPassesTheReason()
    {
        _keys.Setup(k => k.RotateAsync("admin@example.com", null, true, "leaked"))
            .ReturnsAsync(new PluginKeyInfo("1111111111111111", PluginKeyState.Active, null, null, null, 0, null));

        await Create().Rotate(new RotatePluginKeyRequestDto { RevokeCurrent = true, RevokeReason = "leaked" });

        _keys.VerifyAll();
    }

    [Fact]
    public async Task AnUnidentifiedCallerIsRecordedAsUnknownNeverAsSomeoneElse()
    {
        _keys.Setup(k => k.RotateAsync("unknown", null, false, null))
            .ReturnsAsync(new PluginKeyInfo("1111111111111111", PluginKeyState.Active, null, null, null, 0, null));

        await Create(email: null).Rotate(new RotatePluginKeyRequestDto());

        _keys.VerifyAll();
    }

    [Fact]
    public async Task ARefusedKeyActionIsAPlainSentenceNot500()
    {
        _keys.Setup(k => k.RevokeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new PluginKeyOperationException("is_active", "The active key cannot be revoked."));

        var result = await Create().Revoke("1111111111111111", new RevokePluginKeyRequestDto { Reason = "x" });

        Assert.Equal("The active key cannot be revoked.", Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task RevokingAnUnknownKeyIs404()
    {
        _keys.Setup(k => k.RevokeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new PluginKeyOperationException("not_found", "No such key."));

        Assert.IsType<NotFoundResult>((await Create().Revoke("ffffffffffffffff", new RevokePluginKeyRequestDto { Reason = "x" })).Result);
    }

    [Fact]
    public async Task TheKeyListMapsStatesToLowerCaseNamesAndCarriesTheCounts()
    {
        var seen = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        _keys.Setup(k => k.ListAsync()).ReturnsAsync(
        [
            new PluginKeyInfo("1111111111111111", PluginKeyState.Active, null, null, null, 3, seen),
            new PluginKeyInfo("2222222222222222", PluginKeyState.Revoked, seen, seen, "leaked", 0, null)
        ]);

        var list = Assert.IsType<System.Collections.Generic.List<PluginKeyDto>>(Assert.IsType<OkObjectResult>((await Create().ListKeys()).Result).Value);

        Assert.Equal(["active", "revoked"], list.Select(k => k.State).ToArray());
        Assert.Equal(3, list[0].ServersReporting);
        Assert.Equal("leaked", list[1].RevokedReason);
    }

    // ---- releases ------------------------------------------------------------------------------------------

    private static IFormFile File(string name, string content) =>
        new FormFile(new System.IO.MemoryStream(Encoding.UTF8.GetBytes(content)), 0, Encoding.UTF8.GetByteCount(content), "file", name);

    [Theory]
    [InlineData("main", PluginReleaseKind.Main)]
    [InlineData("MAIN", PluginReleaseKind.Main)]
    [InlineData(" updater ", PluginReleaseKind.Updater)]
    public async Task AnUploadPassesTheKindTheFileTheNotesAndTheAdminToTheService(string kind, PluginReleaseKind expected)
    {
        _releases.Setup(r => r.UploadAsync(expected, It.Is<byte[]>(b => Encoding.UTF8.GetString(b) == "source"), "admin@example.com", "n"))
            .ReturnsAsync(Release(kind: expected));

        var result = await Create().Upload(kind, "n", File("RustArchon.cs", "source"));

        Assert.IsType<OkObjectResult>(result.Result);
        _releases.VerifyAll();
    }

    [Theory]
    [InlineData("")]
    [InlineData("both")]
    [InlineData(null)]
    public async Task AnUnknownKindIsRefusedBeforeAnythingIsRead(string? kind)
    {
        var result = await Create().Upload(kind!, null, File("x.cs", "source"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _releases.Verify(r => r.UploadAsync(It.IsAny<PluginReleaseKind>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AMissingOrEmptyFileIsRefused()
    {
        Assert.IsType<BadRequestObjectResult>((await Create().Upload("main", null, null!)).Result);
        Assert.IsType<BadRequestObjectResult>((await Create().Upload("main", null, File("x.cs", ""))).Result);
    }

    [Fact]
    public async Task AFileOverTheLimitIsRefusedWithoutBeingReadIntoMemory()
    {
        var big = new FormFile(new System.IO.MemoryStream(), 0, PluginReleaseService.MaxBytes + 1, "file", "big.cs");

        var result = await Create().Upload("main", null, big);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _releases.Verify(r => r.UploadAsync(It.IsAny<PluginReleaseKind>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AValidationRefusalFromTheServiceIsShownAsItsSentence()
    {
        _releases.Setup(r => r.UploadAsync(It.IsAny<PluginReleaseKind>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new PluginReleaseException("already_signed", "The file already carries a signature line."));

        var result = await Create().Upload("main", null, File("x.cs", "source"));

        Assert.Equal("The file already carries a signature line.", Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task PublishAndWithdrawMapNotFoundTo404AndOtherRefusalsTo400()
    {
        var id = Guid.NewGuid();
        _releases.Setup(r => r.PublishAsync(id, It.IsAny<string>())).ThrowsAsync(new PluginReleaseException("not_found", "No such release."));
        _releases.Setup(r => r.WithdrawAsync(id, It.IsAny<string>(), It.IsAny<string>())).ThrowsAsync(new PluginReleaseException("already_withdrawn", "Already withdrawn."));

        Assert.IsType<NotFoundResult>((await Create().Publish(id)).Result);
        Assert.Equal("Already withdrawn.", Assert.IsType<BadRequestObjectResult>((await Create().Withdraw(id, new WithdrawPluginReleaseRequestDto { Reason = "x" })).Result).Value);
    }

    [Fact]
    public async Task PublishingIsAttributedToTheSignedInAdmin()
    {
        var release = Release();
        _releases.Setup(r => r.PublishAsync(release.Id, "admin@example.com")).ReturnsAsync(release with { State = PluginReleaseState.Published, IsServed = true });

        var dto = Assert.IsType<PluginReleaseDto>(Assert.IsType<OkObjectResult>((await Create().Publish(release.Id)).Result).Value);

        Assert.Equal("published", dto.State);
        Assert.True(dto.IsServed);
    }

    [Fact]
    public async Task TheReleaseListShowsWhatIsServedAndWhereItComesFrom()
    {
        _releases.Setup(r => r.ResolveAsync(PluginReleaseKind.Main)).ReturnsAsync(new PluginServedSource("x", "9.1.0", true, Guid.NewGuid()));
        _releases.Setup(r => r.ResolveAsync(PluginReleaseKind.Updater)).ReturnsAsync(new PluginServedSource("x", "0.2.0", false, null));
        _releases.Setup(r => r.EmbeddedVersion(PluginReleaseKind.Main)).Returns("0.2.2");
        _releases.Setup(r => r.EmbeddedVersion(PluginReleaseKind.Updater)).Returns("0.2.0");
        _releases.Setup(r => r.ListAsync()).ReturnsAsync([Release()]);

        var dto = Assert.IsType<PluginReleasesDto>(Assert.IsType<OkObjectResult>((await Create().ListReleases()).Result).Value);

        Assert.Equal("9.1.0", dto.Main.ServedVersion);
        Assert.True(dto.Main.FromRelease);
        Assert.Equal("0.2.2", dto.Main.EmbeddedVersion);
        Assert.False(dto.Updater.FromRelease);
        Assert.Single(dto.Releases);
    }

    // ---- audit log -----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAuditLogIsNewestFirstAndCapped()
    {
        await using var context = new ApiDbContext(postgres.Options);
        await context.PluginAdminEvents.ExecuteDeleteAsync();
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
        {
            context.PluginAdminEvents.Add(new PluginAdminEvent { AtUtc = start.AddMinutes(i), Kind = PluginAdminEventKind.KeyRotated, Subject = $"k{i}", Actor = "a" });
        }
        await context.SaveChangesAsync();

        var three = Assert.IsType<System.Collections.Generic.List<PluginAdminEventDto>>(
            Assert.IsType<OkObjectResult>((await Create(context: context).Events(3)).Result).Value);
        var clamped = Assert.IsType<System.Collections.Generic.List<PluginAdminEventDto>>(
            Assert.IsType<OkObjectResult>((await Create(context: context).Events(-5)).Result).Value);

        Assert.Equal(["k4", "k3", "k2"], three.Select(e => e.Subject).ToArray());
        Assert.Single(clamped); // a nonsense limit is clamped to 1, not passed to the database
    }
}
