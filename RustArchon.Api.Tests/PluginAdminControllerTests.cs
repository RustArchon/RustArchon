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
    private readonly Mock<IPluginScriptService> _scripts = new();
    private readonly Mock<IPluginAdminAudit> _audit = new();
    private readonly Mock<IPluginRollout> _rollout = new();

    private PluginAdminController Create(string? email = "admin@example.com", ApiDbContext? context = null)
    {
        var controller = new PluginAdminController(
            _keys.Object, _releases.Object, context ?? new ApiDbContext(postgres.Options), _scripts.Object, _audit.Object, _rollout.Object, TimeProvider.System);
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

    // ---- signing a file, and downloading a stored release signed ---------------------------------------------

    private static readonly byte[] SignedBytes = Encoding.UTF8.GetBytes("// the signed file\n");

    private void GivenAValidFile(string version = "9.1.0")
    {
        _releases.Setup(r => r.Validate(It.IsAny<PluginReleaseKind>(), It.IsAny<byte[]>()))
            .Returns(new PluginValidatedSource("normalized source\n", version, new string('c', 64)));
        _scripts.Setup(s => s.SignSourceAsync(It.IsAny<string>(), It.IsAny<PluginReleaseKind>()))
            .ReturnsAsync(new PluginScript(SignedBytes, "82b49184449c98f6", version));
    }

    [Fact]
    public async Task SigningAFileReturnsTheSignedFileAsANeverCachedDownloadAndRecordsWhoAskedWhatAndWhy()
    {
        GivenAValidFile();

        var controller = Create();
        var result = await controller.SignFile("main", "  a hand-built variant for the test server  ", File("RustArchon.cs", "source"));

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(SignedBytes, file.FileContents);
        Assert.Equal("RustArchon.cs", file.FileDownloadName);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        _scripts.Verify(s => s.SignSourceAsync("normalized source\n", PluginReleaseKind.Main), Times.Once);
        _audit.Verify(a => a.RecordAsync(
            PluginAdminEventKind.FileSigned, "Main 9.1.0", "admin@example.com",
            It.Is<string>(d => d.Contains("sha256 cccccccccccc") && d.Contains("key 82b49184449c98f6") && d.EndsWith("a hand-built variant for the test server"))), Times.Once);
    }

    [Fact]
    public async Task SigningTheUpdaterNamesTheDownloadAfterTheUpdater()
    {
        GivenAValidFile("0.3.0");

        var result = await Create().SignFile("updater", "testing the updater", File("RustArchonUpdater.cs", "source"));

        Assert.Equal("RustArchonUpdater.cs", Assert.IsType<FileContentResult>(result).FileDownloadName);
        _scripts.Verify(s => s.SignSourceAsync(It.IsAny<string>(), PluginReleaseKind.Updater), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ok")]
    public async Task SigningNeedsAReasonSoTheAuditLineIsWorthSomething(string? note)
    {
        GivenAValidFile();

        var result = await Create().SignFile("main", note, File("RustArchon.cs", "source"));

        Assert.IsType<BadRequestObjectResult>(result);
        _scripts.Verify(s => s.SignSourceAsync(It.IsAny<string>(), It.IsAny<PluginReleaseKind>()), Times.Never);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ARefusedFileIsAPlainSentenceNothingIsSignedAndNothingIsRecorded()
    {
        _releases.Setup(r => r.Validate(It.IsAny<PluginReleaseKind>(), It.IsAny<byte[]>()))
            .Throws(new PluginReleaseException("syntax_error", "The file cannot be compiled as C# 7.3."));

        var result = await Create().SignFile("main", "testing a change", File("RustArchon.cs", "source"));

        Assert.Equal("The file cannot be compiled as C# 7.3.", Assert.IsType<BadRequestObjectResult>(result).Value);
        _scripts.Verify(s => s.SignSourceAsync(It.IsAny<string>(), It.IsAny<PluginReleaseKind>()), Times.Never);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SigningRefusesABadKindAndAMissingOrOversizedFileBeforeReadingIt()
    {
        Assert.IsType<BadRequestObjectResult>(await Create().SignFile("both", "why not", File("x.cs", "source")));
        Assert.IsType<BadRequestObjectResult>(await Create().SignFile("main", "why not", null!));
        Assert.IsType<BadRequestObjectResult>(await Create().SignFile("main", "why not", File("x.cs", "")));
        var big = new FormFile(new System.IO.MemoryStream(), 0, PluginReleaseService.MaxBytes + 1, "file", "big.cs");
        Assert.IsType<BadRequestObjectResult>(await Create().SignFile("main", "why not", big));
        _releases.Verify(r => r.Validate(It.IsAny<PluginReleaseKind>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task ADraftReleaseCanBeDownloadedSignedForATestInstallAndTheDownloadIsRecorded()
    {
        var id = Guid.NewGuid();
        _releases.Setup(r => r.GetSourceAsync(id)).ReturnsAsync(new PluginStoredSource(id, PluginReleaseKind.Main, "9.2.0", PluginReleaseState.Draft, "stored\n"));
        _scripts.Setup(s => s.SignSourceAsync("stored\n", PluginReleaseKind.Main)).ReturnsAsync(new PluginScript(SignedBytes, "82b49184449c98f6", "9.2.0"));

        var controller = Create();
        var result = await controller.DownloadRelease(id);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(SignedBytes, file.FileContents);
        Assert.Equal("RustArchon.cs", file.FileDownloadName);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        _audit.Verify(a => a.RecordAsync(
            PluginAdminEventKind.ReleaseSigned, "Main 9.2.0", "admin@example.com",
            It.Is<string>(d => d.Contains("draft") && d.Contains("82b49184449c98f6"))), Times.Once);
    }

    [Fact]
    public async Task DownloadingAReleaseThatIsMissingIsA404AndAWithdrawnOneIsAPlainSentence()
    {
        var missing = Guid.NewGuid();
        var withdrawn = Guid.NewGuid();
        _releases.Setup(r => r.GetSourceAsync(missing)).ThrowsAsync(new PluginReleaseException("not_found", "No such release."));
        _releases.Setup(r => r.GetSourceAsync(withdrawn)).ThrowsAsync(new PluginReleaseException("withdrawn", "A withdrawn release is not signed."));

        Assert.IsType<NotFoundResult>(await Create().DownloadRelease(missing));
        Assert.Equal("A withdrawn release is not signed.", Assert.IsType<BadRequestObjectResult>(await Create().DownloadRelease(withdrawn)).Value);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public void TheSigningEndpointsHaveTheSizeLimitAndNeverServeAnythingAnonymously()
    {
        var sign = typeof(PluginAdminController).GetMethod(nameof(PluginAdminController.SignFile))!;

        Assert.NotNull(sign.GetCustomAttribute<RequestSizeLimitAttribute>());
        Assert.Null(sign.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(typeof(PluginAdminController).GetMethod(nameof(PluginAdminController.DownloadRelease))!.GetCustomAttribute<AllowAnonymousAttribute>());
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

    [Fact]
    public async Task TheServedFilesShowHowFarTheirStagedRolloutHasGotAndNothingBeforeItBegins()
    {
        var started = DateTimeOffset.UtcNow.AddHours(-3);
        _releases.Setup(r => r.ResolveAsync(PluginReleaseKind.Main)).ReturnsAsync(new PluginServedSource("x", "9.1.0", true, Guid.NewGuid()));
        _releases.Setup(r => r.ResolveAsync(PluginReleaseKind.Updater)).ReturnsAsync(new PluginServedSource("x", "0.3.0", false, null));
        _releases.Setup(r => r.ListAsync()).ReturnsAsync([]);
        _rollout.Setup(r => r.PeekAsync(PluginReleaseKind.Main, "9.1.0", It.IsAny<DateTimeOffset>())).ReturnsAsync(new PluginRolloutStatus(started, 10, 0.3));
        _rollout.Setup(r => r.PeekAsync(PluginReleaseKind.Updater, "0.3.0", It.IsAny<DateTimeOffset>())).ReturnsAsync((PluginRolloutStatus?)null);

        var dto = Assert.IsType<PluginReleasesDto>(Assert.IsType<OkObjectResult>((await Create().ListReleases()).Result).Value);

        Assert.Equal(started, dto.Main.RolloutStartedUtc);
        Assert.Equal(10, dto.Main.RolloutHours);
        Assert.Equal(30, dto.Main.RolloutPercent);
        Assert.Null(dto.Updater.RolloutStartedUtc);
        Assert.Null(dto.Updater.RolloutPercent);
    }

    [Fact]
    public async Task ListingTheReleasesNeverBeginsARollout()
    {
        _releases.Setup(r => r.ResolveAsync(It.IsAny<PluginReleaseKind>())).ReturnsAsync(new PluginServedSource("x", "9.1.0", true, Guid.NewGuid()));
        _releases.Setup(r => r.ListAsync()).ReturnsAsync([]);

        await Create().ListReleases();

        _rollout.Verify(r => r.BeginAsync(It.IsAny<PluginReleaseKind>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    // ---- the rotation reminder ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheReminderEndpointReportsWhetherItIsDueWithoutChangingAnything()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-400);
        _keys.Setup(k => k.GetReminderAsync()).ReturnsAsync(new PluginKeyReminder("82b49184449c98f6", since, 400, 365, true));

        var dto = Assert.IsType<PluginKeyReminderDto>(Assert.IsType<OkObjectResult>((await Create().KeyReminder()).Result).Value);

        Assert.True(dto.Due);
        Assert.Equal("82b49184449c98f6", dto.Fingerprint);
        Assert.Equal(since, dto.ActiveSinceUtc);
        Assert.Equal(400, dto.AgeDays);
        Assert.Equal(365, dto.ReminderDays);
        _keys.Verify(k => k.RotateAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task TheKeyListCarriesWhenTheActiveKeyBecameActive()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-20);
        _keys.Setup(k => k.ListAsync()).ReturnsAsync(
        [
            new PluginKeyInfo("1111111111111111", PluginKeyState.Active, null, null, null, 3, null, since),
            new PluginKeyInfo("2222222222222222", PluginKeyState.Retired, since, null, null, 0, null)
        ]);

        var dtos = Assert.IsType<System.Collections.Generic.List<PluginKeyDto>>(Assert.IsType<OkObjectResult>((await Create().ListKeys()).Result).Value);

        Assert.Equal(since, dtos.Single(k => k.State == "active").ActiveSinceUtc);
        Assert.Null(dtos.Single(k => k.State == "retired").ActiveSinceUtc);
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

    // ---- exporting and importing keys ----------------------------------------------------------------------

    [Fact]
    public async Task ExportingReturnsTheBundleAsANeverCachedJsonFileAttributedToTheAdmin()
    {
        _keys.Setup(k => k.ExportAsync("a long enough passphrase", "admin@example.com"))
            .ReturnsAsync(new PluginKeyExport("rustarchon-signing-keys-abc.json", "{\"format\":\"x\"}", ["1111111111111111"]));
        var controller = Create();

        var result = await controller.ExportKeys(new ExportPluginKeysRequestDto { Passphrase = "a long enough passphrase" });

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", file.ContentType);
        Assert.Equal("rustarchon-signing-keys-abc.json", file.FileDownloadName);
        Assert.Equal("{\"format\":\"x\"}", Encoding.UTF8.GetString(file.FileContents));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task ARefusedExportIsAPlainSentenceNotAFile()
    {
        _keys.Setup(k => k.ExportAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new PluginKeyOperationException("passphrase_weak", "The passphrase must be 12 to 256 characters."));

        var result = await Create().ExportKeys(new ExportPluginKeysRequestDto { Passphrase = "short" });

        Assert.Equal("The passphrase must be 12 to 256 characters.", Assert.IsType<BadRequestObjectResult>(result).Value);
    }

    [Fact]
    public async Task ImportingPassesTheBundleThePassphraseTheFlagsAndTheAdminAndMapsThePlan()
    {
        _keys.Setup(k => k.ImportAsync("{bundle}", "a long enough passphrase", true, true, "admin@example.com", "sync"))
            .ReturnsAsync(new PluginKeyImportResult(true,
                [new PluginKeyImportItem("2222222222222222", PluginKeyState.Active, "activated"), new PluginKeyImportItem("1111111111111111", null, "previous_active_retired")],
                "1111111111111111", "2222222222222222"));

        var result = await Create().ImportKeys(new ImportPluginKeysRequestDto
        {
            Bundle = "{bundle}", Passphrase = "a long enough passphrase", ActivateBundleKey = true, DryRun = true, Note = "sync"
        });

        var dto = Assert.IsType<PluginKeyImportResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(dto.DryRun);
        Assert.True(dto.ChangesActiveKey);
        Assert.True(dto.ChangesAnything);
        Assert.Equal(("1111111111111111", "2222222222222222"), (dto.ActiveBefore, dto.ActiveAfter));
        Assert.Equal(("2222222222222222", "active", "activated"), (dto.Items[0].Fingerprint, dto.Items[0].BundleState, dto.Items[0].Action));
        Assert.Equal(string.Empty, dto.Items[1].BundleState);                       // a key here that the file does not mention
    }

    [Theory]
    [InlineData("bundle_unreadable")]
    [InlineData("revoked_in_bundle_active_here")]
    [InlineData("concurrent_change")]
    public async Task ARefusedImportIsAPlainSentence(string code)
    {
        _keys.Setup(k => k.ImportAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new PluginKeyOperationException(code, "a sentence for " + code));

        var result = await Create().ImportKeys(new ImportPluginKeysRequestDto { Bundle = "{}", Passphrase = "a long enough passphrase" });

        Assert.Equal("a sentence for " + code, Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public void ImportingHasARequestSizeLimitSoABogusFileCannotBeAnyBiggerThanABundle()
    {
        var limit = typeof(PluginAdminController).GetMethod(nameof(PluginAdminController.ImportKeys))!.GetCustomAttribute<RequestSizeLimitAttribute>();

        Assert.NotNull(limit);
        Assert.True(PluginKeyBundle.MaxBundleBytes + 8192 < 1024 * 1024);           // the ceiling it is set from is far below a megabyte
    }

    [Fact]
    public void TheRequestsThatCarryASecretAreNeverPartOfAnyResponseDto()
    {
        var responses = new[] { typeof(PluginKeyDto), typeof(PluginKeyImportResultDto), typeof(PluginKeyImportItemDto) };
        var names = responses.SelectMany(t => t.GetProperties()).Select(p => p.Name);

        Assert.DoesNotContain(names, n => n.Contains("Passphrase", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Bundle", StringComparison.OrdinalIgnoreCase) && n != "BundleState");
    }
}
