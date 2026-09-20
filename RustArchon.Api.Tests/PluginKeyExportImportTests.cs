// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using JumpStart.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.Security;
using RustArchon.Api.Repositories;

namespace RustArchon.Api.Tests;

/// <summary>
/// Exporting signing keys from one Panel and importing them into another, against a real Postgres with real encryption. Two
/// different data-protection key rings stand in for two Panels: what one Panel encrypted, the other cannot read, exactly as in life.
/// The rules under test: every key travels, encrypted under the passphrase and re-encrypted under the receiving Panel's own ring;
/// keys arrive as history (able to sign a bridge) unless the admin asks for the file's active key to become active; a revocation
/// travels and is never undone; nothing changes on a wrong passphrase, a damaged file, a dry run or a refusal; every real action
/// is audit-logged without a secret in it; and two imports at once cannot leave two active keys.
/// </summary>
public class PluginKeyExportImportTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Passphrase = "correct horse battery staple";
    private static readonly IDataProtectionProvider PanelA = new EphemeralDataProtectionProvider();
    private static readonly IDataProtectionProvider PanelB = new EphemeralDataProtectionProvider();
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("plugin source");

    private ApiDbContext NewContext() => new(postgres.Options);

    private static PluginSigningService NewSigning(ApiDbContext context, IDataProtectionProvider panel) =>
        new(new PlatformSettingRepository(context), new ApiKeyProtector(panel), NullLogger<PluginSigningService>.Instance,
            new PluginKeyHistoryRepository(context));

    private static PluginKeyService NewKeys(ApiDbContext context, IDataProtectionProvider panel) =>
        new(context, NewSigning(context, panel), new ServerPluginStatusRepository(context), new ApiKeyProtector(panel),
            TimeProvider.System, NullLogger<PluginKeyService>.Instance);

    /// <summary>Wipes every key and audit row, so this "Panel" starts with nothing, then (optionally) makes its first key.</summary>
    private async Task<ApiDbContext> ResetAsync(IDataProtectionProvider panel, bool createFirstKey = true)
    {
        var context = NewContext();
        await PlatformSettingsRegistry.EnsureDefaultsAsync(
            context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger.Instance);
        await context.PluginKeyHistories.ExecuteDeleteAsync();
        await context.PluginAdminEvents.ExecuteDeleteAsync();
        await context.PlatformSettings.Where(s => s.Key == PlatformSettingsRegistry.PluginSigningKey)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Value, ""));
        context.ChangeTracker.Clear();
        if (createFirstKey)
        {
            await NewSigning(context, panel).GetPublicKeyAsync();
        }

        return context;
    }

    private static bool Verifies(PluginPublicKey key, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(key.ModulusBase64), Exponent = Convert.FromBase64String(key.ExponentBase64) });
        return rsa.VerifyData(Payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>Panel A with a first key, rotated once: K2 active, K1 retired. Returns their fingerprints and K2's public half.</summary>
    private async Task<(string K1, string K2, PluginPublicKey K2Public)> PanelAWithTwoKeysAsync()
    {
        await using var context = await ResetAsync(PanelA);
        var k1 = (await NewSigning(context, PanelA).GetPublicKeyAsync()).Fingerprint;
        var k2 = (await NewKeys(context, PanelA).RotateAsync("admin@a", null)).Fingerprint;
        return (k1, k2, await NewSigning(context, PanelA).GetPublicKeyAsync());
    }

    private async Task<string> ExportFromPanelAAsync()
    {
        await using var context = NewContext();
        return (await NewKeys(context, PanelA).ExportAsync(Passphrase, "admin@a")).Json;
    }

    private static async Task<PluginKeyOperationException> RefusedAsync(Func<Task> action) =>
        await Assert.ThrowsAsync<PluginKeyOperationException>(action);

    private async Task<PluginKeyState?> StateAsync(ApiDbContext context, IDataProtectionProvider panel, string fingerprint) =>
        await NewSigning(context, panel).GetKeyStateAsync(fingerprint);

    // ---- exporting -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnExportHoldsTheActiveKeyAndEveryKeyInTheHistoryWithTheirStates()
    {
        var (k1, k2, _) = await PanelAWithTwoKeysAsync();

        var bundle = PluginKeyBundle.Open(await ExportFromPanelAAsync(), Passphrase);

        Assert.Equal([k2, k1], bundle.OrderByDescending(k => k.State == PluginKeyState.Active).Select(k => k.Fingerprint));
        Assert.Equal(PluginKeyState.Active, bundle.Single(k => k.Fingerprint == k2).State);
        Assert.Equal(PluginKeyState.Retired, bundle.Single(k => k.Fingerprint == k1).State);
    }

    [Fact]
    public async Task ARevokedKeysReasonIsExportedToo()
    {
        await using var context = await ResetAsync(PanelA);
        var keys = NewKeys(context, PanelA);
        var old = (await NewSigning(context, PanelA).GetPublicKeyAsync()).Fingerprint;
        await keys.RotateAsync("admin@a", null, revokeCurrent: true, revokeReason: "laptop stolen");

        var revoked = PluginKeyBundle.Open((await keys.ExportAsync(Passphrase, "admin@a")).Json, Passphrase).Single(k => k.State == PluginKeyState.Revoked);

        Assert.Equal((old, "laptop stolen"), (revoked.Fingerprint, revoked.RevokedReason));
    }

    [Fact]
    public async Task AnExportIsAuditLoggedWithWhoAndWhichKeysButNoSecret()
    {
        var (k1, k2, _) = await PanelAWithTwoKeysAsync();
        await using var context = NewContext();
        var export = await NewKeys(context, PanelA).ExportAsync(Passphrase, "boss@example.com");

        var audit = await context.PluginAdminEvents.AsNoTracking().SingleAsync(e => e.Kind == PluginAdminEventKind.KeysExported);

        Assert.Equal(("boss@example.com", k2), (audit.Actor, audit.Subject));
        Assert.Contains(k1, audit.Detail);
        Assert.DoesNotContain(Passphrase, audit.Detail);
        Assert.DoesNotContain("MII", audit.Detail);                                  // no PKCS#8 text
        Assert.All(export.Fingerprints, f => Assert.Contains(f, audit.Detail));
    }

    [Fact]
    public async Task AWeakPassphraseRefusesTheExportAndLeavesNoAuditRow()
    {
        await using var context = await ResetAsync(PanelA);

        var ex = await RefusedAsync(() => NewKeys(context, PanelA).ExportAsync("short", "admin@a"));

        Assert.Equal("passphrase_weak", ex.Code);
        Assert.False(await context.PluginAdminEvents.AnyAsync());
    }

    [Fact]
    public async Task AStoredKeyThatCannotBeReadStopsTheWholeExportRatherThanLeavingItOut()
    {
        await using var context = await ResetAsync(PanelA);
        context.PluginKeyHistories.Add(new PluginKeyHistory
        {
            Fingerprint = "deadbeefdeadbeef", ModulusBase64 = "AQAB", ExponentBase64 = "AQAB",
            EncryptedPrivateKey = "garbage that no ring can decrypt", State = PluginKeyState.Retired, RetiredAtUtc = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var ex = await RefusedAsync(() => NewKeys(context, PanelA).ExportAsync(Passphrase, "admin@a"));

        Assert.Equal("key_unreadable", ex.Code);
        Assert.False(await context.PluginAdminEvents.AnyAsync(e => e.Kind == PluginAdminEventKind.KeysExported));
    }

    [Fact]
    public async Task ABrandNewPanelWithNoKeyYetCreatesOneSoThereIsSomethingToBackUp()
    {
        await using var context = await ResetAsync(PanelA, createFirstKey: false);

        var export = await NewKeys(context, PanelA).ExportAsync(Passphrase, "admin@a");

        var only = Assert.Single(PluginKeyBundle.Open(export.Json, Passphrase));
        Assert.Equal(PluginKeyState.Active, only.State);
        Assert.Equal(only.Fingerprint, await NewSigning(context, PanelA).TryGetFingerprintAsync());
    }

    [Fact]
    public async Task TheExportedFileNameCarriesTheActiveKeysFingerprintAndIsJson()
    {
        var (_, k2, _) = await PanelAWithTwoKeysAsync();
        await using var context = NewContext();

        var export = await NewKeys(context, PanelA).ExportAsync(Passphrase, "admin@a");

        Assert.StartsWith($"rustarchon-signing-keys-{k2[..8]}-", export.FileName);
        Assert.EndsWith(".json", export.FileName);
    }

    // ---- importing into another Panel ----------------------------------------------------------------------

    [Fact]
    public async Task ImportingAddsTheFilesKeysToTheHistoryAndLeavesThisPanelsActiveKeyAlone()
    {
        var (k1, k2, k2Public) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        var bActive = await NewSigning(b, PanelB).TryGetFingerprintAsync();

        var result = await NewKeys(b, PanelB).ImportAsync(file, Passphrase, activateBundleKey: false, dryRun: false, "admin@b", null);

        Assert.Equal(bActive, await NewSigning(b, PanelB).TryGetFingerprintAsync());
        Assert.Equal(PluginKeyState.Retired, await StateAsync(b, PanelB, k1));
        Assert.Equal(PluginKeyState.Retired, await StateAsync(b, PanelB, k2));          // even the file's active key: history only
        Assert.All(result.Items, i => Assert.Equal("added", i.Action));
        Assert.False(result.ChangesActiveKey);

        // ...and B can now sign a bridge for a server that still trusts A's key.
        var signature = await NewSigning(b, PanelB).SignWithAsync(k2, Payload);
        Assert.True(Verifies(k2Public, signature));
    }

    [Fact]
    public async Task ImportingWithActivationMakesTheFilesActiveKeyActiveAndKeepsThePreviousOne()
    {
        var (k1, k2, k2Public) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        var bOld = (await NewSigning(b, PanelB).GetPublicKeyAsync()).Fingerprint;

        var result = await NewKeys(b, PanelB).ImportAsync(file, Passphrase, activateBundleKey: true, dryRun: false, "admin@b", "sync with A");

        Assert.Equal(k2, await NewSigning(b, PanelB).TryGetFingerprintAsync());
        Assert.Equal(PluginKeyState.Active, await StateAsync(b, PanelB, k2));
        Assert.Equal(PluginKeyState.Retired, await StateAsync(b, PanelB, bOld));        // kept, not lost
        Assert.Equal(PluginKeyState.Retired, await StateAsync(b, PanelB, k1));
        Assert.True(result.ChangesActiveKey);
        Assert.Equal((bOld, k2), (result.ActiveBefore, result.ActiveAfter));
        Assert.Contains(result.Items, i => i.Fingerprint == bOld && i.Action == "previous_active_retired" && i.BundleState is null);

        // What B signs now verifies under A's public key: the two Panels are interchangeable for this plugin.
        Assert.True(Verifies(k2Public, await NewSigning(b, PanelB).SignWithAsync(k2, Payload)));
        Assert.Equal(k2, (await NewSigning(b, PanelB).GetPublicKeyAsync()).Fingerprint);
    }

    [Fact]
    public async Task ImportedKeysAreEncryptedUnderTheReceivingPanelsOwnKeyRingNotTheSendersNorInTheClear()
    {
        var (k1, _, _) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        await NewKeys(b, PanelB).ImportAsync(file, Passphrase, false, false, "admin@b", null);

        var stored = (await b.PluginKeyHistories.AsNoTracking().SingleAsync(h => h.Fingerprint == k1)).EncryptedPrivateKey;

        Assert.NotEmpty(new ApiKeyProtector(PanelB).Unprotect(ApiKeyProtectorPurposes.PluginSigningKey, stored));
        Assert.ThrowsAny<Exception>(() => new ApiKeyProtector(PanelA).Unprotect(ApiKeyProtectorPurposes.PluginSigningKey, stored));
        Assert.DoesNotContain("MII", stored);                                            // not plain PKCS#8
    }

    [Fact]
    public async Task ImportingIntoAPanelWithNoKeyAtAllActivatesWithoutRetiringAnything()
    {
        var (k1, k2, _) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB, createFirstKey: false);

        var result = await NewKeys(b, PanelB).ImportAsync(file, Passphrase, true, false, "admin@b", null);

        Assert.Equal(k2, await NewSigning(b, PanelB).TryGetFingerprintAsync());
        Assert.Equal(PluginKeyState.Retired, await StateAsync(b, PanelB, k1));
        Assert.Null(result.ActiveBefore);
        Assert.DoesNotContain(result.Items, i => i.Action == "previous_active_retired");
    }

    [Fact]
    public async Task ImportingTheSameFileTwiceChangesNothingTheSecondTimeAndAddsNoAuditRow()
    {
        await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        var keys = NewKeys(b, PanelB);
        await keys.ImportAsync(file, Passphrase, true, false, "admin@b", null);
        var rows = await b.PluginKeyHistories.CountAsync();
        var audits = await b.PluginAdminEvents.CountAsync();

        var again = await keys.ImportAsync(file, Passphrase, true, false, "admin@b", null);

        Assert.False(again.ChangesAnything);
        Assert.All(again.Items, i => Assert.True(i.Action is "already_present" or "already_active"));
        Assert.Equal(rows, await b.PluginKeyHistories.CountAsync());
        Assert.Equal(audits, await b.PluginAdminEvents.CountAsync());
    }

    [Fact]
    public async Task ADryRunReportsThePlanButChangesNothingAndLeavesNoAuditRow()
    {
        await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        var before = await NewSigning(b, PanelB).TryGetFingerprintAsync();

        var plan = await NewKeys(b, PanelB).ImportAsync(file, Passphrase, true, dryRun: true, "admin@b", null);

        Assert.True(plan.DryRun);
        Assert.True(plan.ChangesActiveKey);
        Assert.Contains(plan.Items, i => i.Action == "activated");
        Assert.Equal(before, await NewSigning(b, PanelB).TryGetFingerprintAsync());
        Assert.Equal(0, await b.PluginKeyHistories.CountAsync());
        Assert.False(await b.PluginAdminEvents.AnyAsync());
    }

    [Fact]
    public async Task ARealImportIsAuditLoggedWithWhoWhatAndTheNoteButNoSecret()
    {
        var (_, k2, _) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);

        await NewKeys(b, PanelB).ImportAsync(file, Passphrase, true, false, "boss@example.com", "sync dev with the VS panel");

        var audit = await b.PluginAdminEvents.AsNoTracking().SingleAsync(e => e.Kind == PluginAdminEventKind.KeysImported);
        Assert.Equal(("boss@example.com", k2), (audit.Actor, audit.Subject));
        Assert.Contains("sync dev with the VS panel", audit.Detail);
        Assert.Contains("1 added", audit.Detail);                                  // K1; K2 is not added but activated
        Assert.Contains($"activated {k2}", audit.Detail);
        Assert.DoesNotContain(Passphrase, audit.Detail);
        Assert.DoesNotContain("MII", audit.Detail);
    }

    [Fact]
    public async Task AfterAnActivatingImportTheKeyListShowsTheNewActiveKeyAndTheOldOnesRetired()
    {
        var (k1, k2, _) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        var bOld = (await NewSigning(b, PanelB).GetPublicKeyAsync()).Fingerprint;
        await NewKeys(b, PanelB).ImportAsync(file, Passphrase, true, false, "admin@b", null);

        var list = await NewKeys(b, PanelB).ListAsync();

        Assert.Equal(k2, Assert.Single(list, k => k.State == PluginKeyState.Active).Fingerprint);
        Assert.Equal(new[] { bOld, k1 }.Order(), list.Where(k => k.State == PluginKeyState.Retired).Select(k => k.Fingerprint).Order());
    }

    // ---- revocations ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ARevocationInTheFileIsAppliedToAKeyThatWasOnlyRetiredHere()
    {
        var (k1, _, _) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        await NewKeys(b, PanelB).ImportAsync(file, Passphrase, false, false, "admin@b", null);
        Assert.Equal(PluginKeyState.Retired, await StateAsync(b, PanelB, k1));

        // A revokes K1 later and exports again: build that file directly (K1 revoked, with a reason).
        var opened = PluginKeyBundle.Open((await ExportOf(b, PanelB)), Passphrase);
        var revokedFile = PluginKeyBundle.Seal(opened.Select(k => k.Fingerprint == k1
            ? k with { State = PluginKeyState.Revoked, RevokedAtUtc = DateTimeOffset.UtcNow, RevokedReason = "leaked" } : k).ToList(), Passphrase, DateTimeOffset.UtcNow);

        var result = await NewKeys(b, PanelB).ImportAsync(revokedFile, Passphrase, false, false, "admin@b", null);

        Assert.Contains(result.Items, i => i.Fingerprint == k1 && i.Action == "revoked");
        Assert.Equal(PluginKeyState.Revoked, await StateAsync(b, PanelB, k1));
        var row = await b.PluginKeyHistories.AsNoTracking().SingleAsync(h => h.Fingerprint == k1);
        Assert.Equal("leaked", row.RevokedReason);
        await Assert.ThrowsAnyAsync<Exception>(() => NewSigning(b, PanelB).SignWithAsync(k1, Payload));   // a revoked key never signs
    }

    private static async Task<string> ExportOf(ApiDbContext context, IDataProtectionProvider panel) =>
        (await NewKeys(context, panel).ExportAsync(Passphrase, "admin")).Json;

    [Fact]
    public async Task AKeyRevokedHereIsNeverBroughtBackByAFileThatStillHasItRetired()
    {
        var (k1, _, _) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();                        // K1 retired in the file
        await using var b = await ResetAsync(PanelB);
        var keys = NewKeys(b, PanelB);
        await keys.ImportAsync(file, Passphrase, false, false, "admin@b", null);
        await keys.RevokeAsync(k1, "no longer trusted", "admin@b");

        var result = await keys.ImportAsync(file, Passphrase, false, false, "admin@b", null);

        Assert.Contains(result.Items, i => i.Fingerprint == k1 && i.Action == "already_present");
        Assert.Equal(PluginKeyState.Revoked, await StateAsync(b, PanelB, k1));
    }

    [Fact]
    public async Task AKeyRevokedInTheFileButActiveHereIsRefusedAndNothingChanges()
    {
        await using var context = await ResetAsync(PanelA);
        var keys = NewKeys(context, PanelA);
        var k1 = (await NewSigning(context, PanelA).GetPublicKeyAsync()).Fingerprint;
        var beforeRotation = (await keys.ExportAsync(Passphrase, "admin@a")).Json;                 // K1 active
        await keys.RotateAsync("admin@a", null, revokeCurrent: true, revokeReason: "compromised");
        var afterRotation = (await keys.ExportAsync(Passphrase, "admin@a")).Json;                  // K1 revoked, K2 active

        await using var b = await ResetAsync(PanelB, createFirstKey: false);
        var bKeys = NewKeys(b, PanelB);
        await bKeys.ImportAsync(beforeRotation, Passphrase, true, false, "admin@b", null);          // B now has K1 active
        Assert.Equal(k1, await NewSigning(b, PanelB).TryGetFingerprintAsync());
        var audits = await b.PluginAdminEvents.CountAsync();

        var ex = await RefusedAsync(() => bKeys.ImportAsync(afterRotation, Passphrase, true, false, "admin@b", null));

        Assert.Equal("revoked_in_bundle_active_here", ex.Code);
        Assert.Equal(k1, await NewSigning(b, PanelB).TryGetFingerprintAsync());
        Assert.Equal(audits, await b.PluginAdminEvents.CountAsync());
    }

    [Fact]
    public async Task ActivatingAKeyThatIsRevokedHereIsRefused()
    {
        var (_, k2, _) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        var keys = NewKeys(b, PanelB);
        await keys.ImportAsync(file, Passphrase, false, false, "admin@b", null);       // K2 arrives as history
        await keys.RevokeAsync(k2, "no longer trusted", "admin@b");

        var ex = await RefusedAsync(() => keys.ImportAsync(file, Passphrase, true, false, "admin@b", null));

        Assert.Equal("cannot_activate_revoked", ex.Code);
        Assert.Equal(PluginKeyState.Revoked, await StateAsync(b, PanelB, k2));
    }

    // ---- refusals leave everything as it was ---------------------------------------------------------------

    [Fact]
    public async Task ActivatingWhenTheFileHasNoActiveKeyIsRefused()
    {
        var (k1, _, _) = await PanelAWithTwoKeysAsync();
        var opened = PluginKeyBundle.Open(await ExportFromPanelAAsync(), Passphrase);
        var historyOnly = PluginKeyBundle.Seal(opened.Where(k => k.State != PluginKeyState.Active).ToList(), Passphrase, DateTimeOffset.UtcNow);
        await using var b = await ResetAsync(PanelB);

        var ex = await RefusedAsync(() => NewKeys(b, PanelB).ImportAsync(historyOnly, Passphrase, true, false, "admin@b", null));

        Assert.Equal("no_active_in_bundle", ex.Code);
        Assert.Equal(0, await b.PluginKeyHistories.CountAsync());
    }

    [Fact]
    public async Task AHistoryOnlyFileImportsFineWithoutActivation()
    {
        await PanelAWithTwoKeysAsync();
        var opened = PluginKeyBundle.Open(await ExportFromPanelAAsync(), Passphrase);
        var historyOnly = PluginKeyBundle.Seal(opened.Where(k => k.State != PluginKeyState.Active).ToList(), Passphrase, DateTimeOffset.UtcNow);
        await using var b = await ResetAsync(PanelB);

        var result = await NewKeys(b, PanelB).ImportAsync(historyOnly, Passphrase, false, false, "admin@b", null);

        Assert.Single(result.Items);
        Assert.Equal(1, await b.PluginKeyHistories.CountAsync());
    }

    [Fact]
    public async Task AWrongPassphraseChangesNothing()
    {
        await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        var before = await NewSigning(b, PanelB).TryGetFingerprintAsync();

        var ex = await RefusedAsync(() => NewKeys(b, PanelB).ImportAsync(file, "not the passphrase at all", true, false, "admin@b", null));

        Assert.Equal("bundle_unreadable", ex.Code);
        Assert.Equal(before, await NewSigning(b, PanelB).TryGetFingerprintAsync());
        Assert.Equal(0, await b.PluginKeyHistories.CountAsync());
        Assert.False(await b.PluginAdminEvents.AnyAsync());
    }

    [Fact]
    public async Task ADamagedFileChangesNothing()
    {
        await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        var damaged = file.Replace("\"ciphertext\": \"", "\"ciphertext\": \"AAAA");
        await using var b = await ResetAsync(PanelB);

        var ex = await RefusedAsync(() => NewKeys(b, PanelB).ImportAsync(damaged, Passphrase, true, false, "admin@b", null));

        Assert.Equal("bundle_unreadable", ex.Code);
        Assert.Equal(0, await b.PluginKeyHistories.CountAsync());
    }

    // ---- reactivation and races ----------------------------------------------------------------------------

    [Fact]
    public async Task ReactivatingAKeyThatIsRetiredHereMovesItOutOfTheHistoryWithoutDuplicatingAnything()
    {
        var (k1, k2, _) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        var keys = NewKeys(b, PanelB);
        await keys.ImportAsync(file, Passphrase, true, false, "admin@b", null);       // K2 active, K1 retired here
        var opened = PluginKeyBundle.Open(file, Passphrase);
        var olderFile = PluginKeyBundle.Seal(opened.Select(k => k with
        {
            State = k.Fingerprint == k1 ? PluginKeyState.Active : PluginKeyState.Retired,
            RetiredAtUtc = k.Fingerprint == k1 ? null : DateTimeOffset.UtcNow
        }).ToList(), Passphrase, DateTimeOffset.UtcNow);                              // a file where K1 is the active one

        var result = await keys.ImportAsync(olderFile, Passphrase, true, false, "admin@b", null);

        Assert.Equal(k1, await NewSigning(b, PanelB).TryGetFingerprintAsync());
        Assert.Equal(PluginKeyState.Retired, await StateAsync(b, PanelB, k2));
        Assert.Equal(1, await b.PluginKeyHistories.CountAsync(h => h.Fingerprint == k2));
        Assert.Equal(0, await b.PluginKeyHistories.CountAsync(h => h.Fingerprint == k1));   // active keys are not in the history
        Assert.Contains(result.Items, i => i.Fingerprint == k1 && i.Action == "activated");
    }

    [Fact]
    public async Task TwoImportsAtTheSameMomentNeverLeaveTwoActiveKeysOrDuplicateHistory()
    {
        var (_, k2, _) = await PanelAWithTwoKeysAsync();
        var file = await ExportFromPanelAAsync();
        await using var b = await ResetAsync(PanelB);
        await using var second = NewContext();

        var outcomes = await Task.WhenAll(
            Attempt(() => NewKeys(b, PanelB).ImportAsync(file, Passphrase, true, false, "admin@one", null)),
            Attempt(() => NewKeys(second, PanelB).ImportAsync(file, Passphrase, true, false, "admin@two", null)));

        Assert.All(outcomes, o => Assert.True(o is null or "concurrent_change", o));     // either wins, or loses cleanly
        await using var check = NewContext();
        Assert.Equal(k2, await NewSigning(check, PanelB).TryGetFingerprintAsync());
        var history = await check.PluginKeyHistories.AsNoTracking().Select(h => h.Fingerprint).ToListAsync();
        Assert.Equal(history.Distinct().Count(), history.Count);
        Assert.DoesNotContain(k2, history);                                                // the active key is never also in the history
    }

    private static async Task<string?> Attempt(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (PluginKeyOperationException ex)
        {
            return ex.Code;
        }
    }
}
