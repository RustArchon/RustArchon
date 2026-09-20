// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// Plugin releases against a real Postgres: what an upload must satisfy before it is even stored, that a draft is never
/// served, that only an explicit publish makes something eligible, that the highest published version wins but never
/// rolls servers back below what is built in, and that everything is audit-logged and nothing is deleted.
/// </summary>
public class PluginReleaseServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly EmbeddedPluginScriptSource Embedded = new();

    private ApiDbContext CreateContext() => new(postgres.Options);

    private static PluginReleaseService NewService(ApiDbContext context) =>
        new(context, Embedded, TimeProvider.System, NullLogger<PluginReleaseService>.Instance);

    private async Task<ApiDbContext> FreshAsync()
    {
        var context = CreateContext();
        await context.PluginReleases.ExecuteDeleteAsync();
        await context.PluginAdminEvents.ExecuteDeleteAsync();
        return context;
    }

    /// <summary>The real embedded plugin source with its version changed - a realistic upload.</summary>
    private static byte[] MainSource(string version, Func<string, string>? tweak = null)
    {
        var text = Regex.Replace(Embedded.ReadSource(), @"(\[Info\(""RustArchon""\s*,\s*""[^""]*""\s*,\s*"")[^""]+(""\)\])", "${1}" + version + "${2}");
        return Encoding.UTF8.GetBytes(tweak?.Invoke(text) ?? text);
    }

    private static byte[] UpdaterSource(string version)
    {
        var text = Regex.Replace(Embedded.ReadUpdaterSource(), @"(\[Info\(""RustArchonUpdater""\s*,\s*""[^""]*""\s*,\s*"")[^""]+(""\)\])", "${1}" + version + "${2}");
        return Encoding.UTF8.GetBytes(text);
    }

    private static async Task<PluginReleaseException> RefusedAsync(Task<PluginReleaseInfo> attempt) =>
        await Assert.ThrowsAsync<PluginReleaseException>(() => attempt);

    // ---- uploading -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AValidUploadIsStoredAsADraftAndNothingIsServedFromIt()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);

        var release = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "admin@example.com", "fixes X");

        Assert.Equal(PluginReleaseState.Draft, release.State);
        Assert.False(release.IsServed);
        Assert.Equal("9.1.0", release.Version);
        Assert.Matches("^[0-9a-f]{64}$", release.Sha256);
        Assert.Equal("fixes X", release.Notes);
        Assert.False((await service.ResolveAsync(PluginReleaseKind.Main)).FromRelease); // still the embedded build
    }

    [Fact]
    public async Task TheStoredSourceIsTheNormalizedSourceTheStamperWillSign()
    {
        await using var context = await FreshAsync();
        // CRLF whatever line endings this checkout gave the embedded source (a Windows checkout already has them).
        var original = Encoding.UTF8.GetString(MainSource("9.1.0")).Replace("\r\n", "\n").Replace("\n", "\r\n");
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(original)).ToArray();

        await NewService(context).UploadAsync(PluginReleaseKind.Main, withBom, "a@example.com", null);

        var stored = (await context.PluginReleases.AsNoTracking().SingleAsync()).SourceText;
        Assert.DoesNotContain('\r', stored);
        Assert.False(stored.StartsWith('﻿'));
        Assert.EndsWith("\n", stored);
    }

    [Fact]
    public async Task AnUploadIsAuditLoggedWithWhoUploadedWhat()
    {
        await using var context = await FreshAsync();

        await NewService(context).UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "admin@example.com", null);

        var entry = await context.PluginAdminEvents.AsNoTracking().SingleAsync();
        Assert.Equal(PluginAdminEventKind.ReleaseUploaded, entry.Kind);
        Assert.Equal("Main 9.1.0", entry.Subject);
        Assert.Equal("admin@example.com", entry.Actor);
    }

    [Fact]
    public async Task AnEmptyOrOversizedOrNonTextFileIsRefused()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);

        Assert.Equal("empty", (await RefusedAsync(service.UploadAsync(PluginReleaseKind.Main, [], "a", null))).Code);
        Assert.Equal("too_large", (await RefusedAsync(service.UploadAsync(PluginReleaseKind.Main, new byte[PluginReleaseService.MaxBytes + 1], "a", null))).Code);
        Assert.Equal("not_text", (await RefusedAsync(service.UploadAsync(PluginReleaseKind.Main, [0xFF, 0xFE, 0xFD, 0x00, 0xC3], "a", null))).Code);
        Assert.Equal(0, await context.PluginReleases.CountAsync());
    }

    [Fact]
    public async Task AFileForTheWrongPluginIsRefused()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);

        Assert.Equal("wrong_plugin", (await RefusedAsync(service.UploadAsync(PluginReleaseKind.Main, UpdaterSource("9.1.0"), "a", null))).Code);
        Assert.Equal("wrong_plugin", (await RefusedAsync(service.UploadAsync(PluginReleaseKind.Updater, MainSource("9.1.0"), "a", null))).Code);
        Assert.Equal("wrong_plugin", (await RefusedAsync(service.UploadAsync(PluginReleaseKind.Main, "class NotAPlugin { }\n"u8.ToArray(), "a", null))).Code);
    }

    [Fact]
    public async Task AFileWithNoUsableVersionIsRefused()
    {
        await using var context = await FreshAsync();
        var noVersion = MainSource("x", t => t.Replace("\"x\")]", "\"not-a-version\")]"));

        Assert.Equal("no_version", (await RefusedAsync(NewService(context).UploadAsync(PluginReleaseKind.Main, noVersion, "a", null))).Code);
    }

    [Fact]
    public async Task AFileWhoseKeyPlaceholdersAreMissingOrDuplicatedIsRefused()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var missing = MainSource("9.1.0", t => t.Replace(PluginScriptStamper.ModulusPlaceholder, "already-stamped"));
        var doubled = MainSource("9.1.0", t => t + "\n// " + PluginScriptStamper.ExponentPlaceholder + "\n");

        Assert.Equal("bad_placeholders", (await RefusedAsync(service.UploadAsync(PluginReleaseKind.Main, missing, "a", null))).Code);
        Assert.Equal("bad_placeholders", (await RefusedAsync(service.UploadAsync(PluginReleaseKind.Main, doubled, "a", null))).Code);
    }

    [Fact]
    public async Task AFileDownloadedFromAPanelAlreadySignedIsRefusedSoWhatWasReviewedIsWhatGetsSigned()
    {
        await using var context = await FreshAsync();
        // A signed file has its key stamped in (no placeholders), so it fails on placeholders - and one that somehow
        // kept them but carries a signature line is refused explicitly.
        var signed = MainSource("9.1.0", t => t + PluginScriptStamper.SignatureMarker + "AAAA\n");

        Assert.Equal("already_signed", (await RefusedAsync(NewService(context).UploadAsync(PluginReleaseKind.Main, signed, "a", null))).Code);
    }

    [Fact]
    public async Task AFileThatCannotBeReadAsCSharp73IsRefusedWithWhereItGoesWrong()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        // The real source with a statement broken on a line of its own.
        var broken = MainSource("9.1.0", t => t + "\nclass Broken { void A() { int x = ; } }\n");

        var refusal = await RefusedAsync(service.UploadAsync(PluginReleaseKind.Main, broken, "a", null));

        Assert.Equal("syntax_error", refusal.Code);
        Assert.Contains("C# 7.3", refusal.Message);
        Assert.Matches(@"line \d+:", refusal.Message);
        Assert.Equal(0, await context.PluginReleases.CountAsync());
    }

    [Fact]
    public async Task ModernSyntaxTheGameServerCannotCompileIsRefusedAtUploadNotDiscoveredOnEveryServer()
    {
        await using var context = await FreshAsync();
        var modern = MainSource("9.1.0", t => t + "\nclass Modern { string A(int x) { return x switch { 1 => \"a\", _ => \"b\" }; } }\n");

        var refusal = await RefusedAsync(NewService(context).UploadAsync(PluginReleaseKind.Main, modern, "a", null));

        Assert.Equal("syntax_error", refusal.Code);
    }

    [Fact]
    public async Task ManyProblemsAreListedFewAndCounted()
    {
        await using var context = await FreshAsync();
        var many = MainSource("9.1.0", t => t + string.Concat(Enumerable.Range(0, 12).Select(i => $"\nclass Bad{i} {{ void A() {{ int x = ; }} }}\n")));

        var refusal = await RefusedAsync(NewService(context).UploadAsync(PluginReleaseKind.Main, many, "a", null));

        Assert.Equal("syntax_error", refusal.Code);
        Assert.Contains("more)", refusal.Message);
    }

    [Fact]
    public async Task ValidatingChecksEverythingAnUploadDoesButStoresNothingAndAllowsAVersionThatWasAlreadyUploaded()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);

        var validated = service.Validate(PluginReleaseKind.Main, MainSource("9.1.0"));    // same version again: fine for signing, not for uploading

        Assert.Equal("9.1.0", validated.Version);
        Assert.Matches("^[0-9a-f]{64}$", validated.Sha256);
        Assert.DoesNotContain('\r', validated.Text);
        Assert.Equal(1, await context.PluginReleases.CountAsync());
        Assert.Equal(1, await context.PluginAdminEvents.CountAsync());                   // the upload's own line; validating wrote none
        Assert.Equal("wrong_plugin", Assert.Throws<PluginReleaseException>(() => service.Validate(PluginReleaseKind.Updater, MainSource("9.1.0"))).Code);
        Assert.Equal("syntax_error", Assert.Throws<PluginReleaseException>(() => service.Validate(PluginReleaseKind.Main, MainSource("9.1.0", t => t + "\nclass B { int x = ; }\n"))).Code);
        Assert.Equal("empty", Assert.Throws<PluginReleaseException>(() => service.Validate(PluginReleaseKind.Main, [])).Code);
    }

    [Fact]
    public async Task ADraftAndAPublishedReleaseCanBeFetchedToBeSignedButAWithdrawnOneAndAMissingOneCannot()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var draft = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);
        var toPublish = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.2.0"), "a", null);
        await service.PublishAsync(toPublish.Id, "a");
        var toWithdraw = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.3.0"), "a", null);
        await service.WithdrawAsync(toWithdraw.Id, "bad build", "a");

        var fetchedDraft = await service.GetSourceAsync(draft.Id);
        var fetchedPublished = await service.GetSourceAsync(toPublish.Id);

        Assert.Equal(PluginReleaseState.Draft, fetchedDraft.State);
        Assert.Equal("9.1.0", fetchedDraft.Version);
        Assert.Equal(PluginReleaseKind.Main, fetchedDraft.Kind);
        Assert.Equal(PluginReleaseState.Published, fetchedPublished.State);
        Assert.Contains("[Info(\"RustArchon\"", fetchedDraft.Text);
        Assert.Equal("withdrawn", (await Assert.ThrowsAsync<PluginReleaseException>(() => service.GetSourceAsync(toWithdraw.Id))).Code);
        Assert.Equal("not_found", (await Assert.ThrowsAsync<PluginReleaseException>(() => service.GetSourceAsync(Guid.NewGuid()))).Code);
    }

    [Fact]
    public async Task AVersionCanOnlyBeUploadedOnceSoAReleaseNeverChangesUnderneathServers()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);

        Assert.Equal("version_exists", (await RefusedAsync(service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0", t => t + "// changed\n"), "a", null))).Code);
        await service.UploadAsync(PluginReleaseKind.Updater, UpdaterSource("9.1.0"), "a", null); // same number, other file: fine
    }

    // ---- publishing and serving ----------------------------------------------------------------------------

    [Fact]
    public async Task PublishingANewerReleaseMakesItTheOneServed()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var draft = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);

        var published = await service.PublishAsync(draft.Id, "pub@example.com");

        Assert.Equal(PluginReleaseState.Published, published.State);
        Assert.True(published.IsServed);
        Assert.Equal("pub@example.com", published.PublishedBy);
        var served = await service.ResolveAsync(PluginReleaseKind.Main);
        Assert.True(served.FromRelease);
        Assert.Equal("9.1.0", served.Version);
        Assert.Equal(draft.Id, served.ReleaseId);
    }

    [Fact]
    public async Task TheHighestPublishedVersionWins()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var low = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);
        var high = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.10.0"), "a", null); // 9.10 > 9.9: numeric
        var mid = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.9.0"), "a", null);
        foreach (var r in new[] { low, high, mid }) { await service.PublishAsync(r.Id, "a"); }

        Assert.Equal("9.10.0", (await service.ResolveAsync(PluginReleaseKind.Main)).Version);
    }

    [Fact]
    public async Task AReleaseNoNewerThanTheEmbeddedBuildIsNeverServed()
    {
        // An old release must not quietly roll every server back to it.
        await using var context = await FreshAsync();
        var service = NewService(context);
        var embedded = service.EmbeddedVersion(PluginReleaseKind.Main)!;
        var old = await service.UploadAsync(PluginReleaseKind.Main, MainSource("0.0.1"), "a", null);

        var published = await service.PublishAsync(old.Id, "a");

        Assert.False(published.IsServed);
        var served = await service.ResolveAsync(PluginReleaseKind.Main);
        Assert.False(served.FromRelease);
        Assert.Equal(embedded, served.Version);
    }

    [Fact]
    public async Task AReleaseEqualToTheEmbeddedVersionIsTheSameCodeSoTheEmbeddedBuildIsServed()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var release = await service.UploadAsync(PluginReleaseKind.Main, MainSource(service.EmbeddedVersion(PluginReleaseKind.Main)!), "a", null);
        await service.PublishAsync(release.Id, "a");

        Assert.False((await service.ResolveAsync(PluginReleaseKind.Main)).FromRelease);
    }

    [Fact]
    public async Task OnlyADraftCanBePublished()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var release = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);
        await service.PublishAsync(release.Id, "a");

        Assert.Equal("not_a_draft", (await RefusedAsync(service.PublishAsync(release.Id, "a"))).Code);
        await service.WithdrawAsync(release.Id, "bad", "a");
        Assert.Equal("not_a_draft", (await RefusedAsync(service.PublishAsync(release.Id, "a"))).Code);
        Assert.Equal("not_found", (await RefusedAsync(service.PublishAsync(Guid.NewGuid(), "a"))).Code);
    }

    [Fact]
    public async Task UpdaterAndMainReleasesAreServedIndependently()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var updater = await service.UploadAsync(PluginReleaseKind.Updater, UpdaterSource("9.2.0"), "a", null);
        await service.PublishAsync(updater.Id, "a");

        Assert.Equal("9.2.0", (await service.ResolveAsync(PluginReleaseKind.Updater)).Version);
        Assert.False((await service.ResolveAsync(PluginReleaseKind.Main)).FromRelease);
    }

    // ---- withdrawing ---------------------------------------------------------------------------------------

    [Fact]
    public async Task WithdrawingTheServedReleaseFallsBackToTheNextOneAndFinallyToTheEmbeddedBuild()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var embedded = service.EmbeddedVersion(PluginReleaseKind.Main);
        var a = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);
        var b = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.2.0"), "a", null);
        await service.PublishAsync(a.Id, "a");
        await service.PublishAsync(b.Id, "a");

        await service.WithdrawAsync(b.Id, "regression", "a");
        Assert.Equal("9.1.0", (await service.ResolveAsync(PluginReleaseKind.Main)).Version);

        await service.WithdrawAsync(a.Id, "also bad", "a");
        var served = await service.ResolveAsync(PluginReleaseKind.Main);
        Assert.False(served.FromRelease);
        Assert.Equal(embedded, served.Version);
    }

    [Fact]
    public async Task WithdrawingKeepsTheRowAndRecordsWhoWhenAndWhy()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var release = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);
        await service.PublishAsync(release.Id, "a");

        var withdrawn = await service.WithdrawAsync(release.Id, "crashes on load", "admin@example.com");

        Assert.Equal(PluginReleaseState.Withdrawn, withdrawn.State);
        Assert.Equal("crashes on load", withdrawn.WithdrawnReason);
        Assert.Equal("admin@example.com", withdrawn.WithdrawnBy);
        Assert.Equal(1, await context.PluginReleases.CountAsync()); // never deleted
        Assert.Equal(3, await context.PluginAdminEvents.CountAsync()); // uploaded, published, withdrawn
    }

    [Fact]
    public async Task WithdrawingNeedsAReasonAndAnExistingReleaseNotAlreadyWithdrawn()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var release = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);

        Assert.Equal("reason_required", (await RefusedAsync(service.WithdrawAsync(release.Id, "  ", "a"))).Code);
        Assert.Equal("not_found", (await RefusedAsync(service.WithdrawAsync(Guid.NewGuid(), "x", "a"))).Code);
        await service.WithdrawAsync(release.Id, "discard", "a"); // a draft can be discarded
        Assert.Equal("already_withdrawn", (await RefusedAsync(service.WithdrawAsync(release.Id, "again", "a"))).Code);
    }

    [Fact]
    public async Task TheListShowsWhichReleaseIsServedAndNeverTheSourceText()
    {
        await using var context = await FreshAsync();
        var service = NewService(context);
        var a = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.1.0"), "a", null);
        var b = await service.UploadAsync(PluginReleaseKind.Main, MainSource("9.2.0"), "a", null);
        await service.PublishAsync(a.Id, "a");
        await service.PublishAsync(b.Id, "a");

        var list = await service.ListAsync();

        Assert.Equal(["9.2.0"], list.Where(r => r.IsServed).Select(r => r.Version).ToArray());
        Assert.DoesNotContain(typeof(PluginReleaseInfo).GetProperties(), p => p.Name.Contains("Source", StringComparison.OrdinalIgnoreCase));
    }

    // ---- end to end through signing ------------------------------------------------------------------------

    [Fact]
    public async Task APublishedReleaseIsWhatGetsStampedSignedAndServed()
    {
        await using var context = await FreshAsync();
        await PlatformSettingsRegistry.EnsureDefaultsAsync(
            context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger.Instance);
        var releases = NewService(context);
        var release = await releases.UploadAsync(PluginReleaseKind.Main, MainSource("9.3.0"), "a", null);
        await releases.PublishAsync(release.Id, "a");
        var signing = new PluginSigningService(
            new PlatformSettingRepository(context), new ApiKeyProtector(new EphemeralDataProtectionProvider()),
            NullLogger<PluginSigningService>.Instance, new PluginKeyHistoryRepository(context));
        var scripts = new PluginScriptService(signing, new PublishedPluginScriptSource(releases));

        var built = await scripts.BuildAsync();

        Assert.Equal("9.3.0", built.PluginVersion);
        Assert.Equal("9.3.0", await scripts.GetLatestVersionAsync());
        Assert.DoesNotContain("@@RUSTARCHON", Encoding.UTF8.GetString(built.Bytes)); // stamped
        Assert.Contains(PluginScriptStamper.SignatureMarker, Encoding.UTF8.GetString(built.Bytes)); // signed
    }
}
