// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RustArchon.Api.Data;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// Checking an invitation code without spending it.
/// </summary>
/// <remarks>
/// <para>
/// The property this exists for: <strong>asking must not consume</strong>. Registration validates a
/// code before creating anything and redeems it only once the account and its organization exist, so
/// everything that can fail happens while the code is still untouched. If validation consumed, that
/// whole ordering would be pointless and every failed registration would cost somebody a code.
/// </para>
/// <para>
/// Redemption itself is not covered here: it is a single <c>ExecuteUpdate</c>, which the in-memory
/// provider this host uses cannot run. Testing it needs a relational provider.
/// </para>
/// </remarks>
public class InvitationCodeValidationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<string> SeedCodeAsync(
        string? boundEmail = null, bool active = true, bool redeemed = false)
    {
        var code = $"TEST-{Guid.NewGuid():N}"[..14].ToUpperInvariant();

        await factory.WithDatabaseAsync(async db =>
        {
            db.Set<InvitationCode>().Add(new InvitationCode
            {
                Code = code,
                BoundEmail = boundEmail,
                IsActive = active,
                RedeemedAtUtc = redeemed ? DateTimeOffset.UtcNow : null,
                RedeemedByEmail = redeemed ? "someone.else@example.com" : null
            });

            await db.SaveChangesAsync();
        });

        return code;
    }

    private async Task<RedeemInvitationCodeResult> ValidateAsync(string code, string email)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/invitations/validate", UriKind.Relative),
            new RedeemInvitationCodeRequest { Code = code, Email = email });

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<RedeemInvitationCodeResult>())!;
    }

    private Task<bool> IsSpentAsync(string code) =>
        factory.FromDatabaseAsync(db => db.Set<InvitationCode>()
            .AnyAsync(c => c.Code == code && c.RedeemedAtUtc != null));

    [Fact]
    public async Task AUsableCodeValidatesAndStaysUnspent()
    {
        var code = await SeedCodeAsync();

        var result = await ValidateAsync(code, "newcomer@example.com");

        Assert.True(result.Success);
        Assert.False(await IsSpentAsync(code), "Validating a code must not consume it.");
    }

    /// <summary>
    /// Asked repeatedly, it keeps answering - which is what makes it safe to call before every
    /// registration attempt, including the ones that go on to fail.
    /// </summary>
    [Fact]
    public async Task ValidatingRepeatedlyNeverConsumesIt()
    {
        var code = await SeedCodeAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.True((await ValidateAsync(code, "newcomer@example.com")).Success);
        }

        Assert.False(await IsSpentAsync(code));
    }

    [Fact]
    public async Task ACodeBoundToSomebodyElseIsRefused()
    {
        var code = await SeedCodeAsync(boundEmail: "invited@example.com");

        var result = await ValidateAsync(code, "someone.else@example.com");

        Assert.False(result.Success);

        // ...and refusing must not spend it either. This is the case that used to burn codes: a
        // failed attempt by the wrong person destroyed the invitation for the right one.
        Assert.False(await IsSpentAsync(code));
    }

    [Fact]
    public async Task ACodeBoundToThisPersonIsAccepted()
    {
        var code = await SeedCodeAsync(boundEmail: "invited@example.com");

        Assert.True((await ValidateAsync(code, "invited@example.com")).Success);
    }

    /// <summary>Case and dashes are how a person retypes a code, not a different code.</summary>
    [Fact]
    public async Task ItMatchesHoweverTheCodeWasRetyped()
    {
        var code = await SeedCodeAsync();

        Assert.True((await ValidateAsync(code.ToLowerInvariant(), "newcomer@example.com")).Success);
        Assert.True((await ValidateAsync(code.Replace("-", ""), "newcomer@example.com")).Success);
        Assert.True((await ValidateAsync($" {code} ", "newcomer@example.com")).Success);
    }

    [Fact]
    public async Task ADeactivatedCodeIsRefused()
    {
        var code = await SeedCodeAsync(active: false);

        Assert.False((await ValidateAsync(code, "newcomer@example.com")).Success);
    }

    [Fact]
    public async Task AnAlreadyRedeemedCodeIsRefused()
    {
        var code = await SeedCodeAsync(redeemed: true);

        Assert.False((await ValidateAsync(code, "newcomer@example.com")).Success);
    }

    [Fact]
    public async Task AnUnknownCodeIsRefused()
    {
        Assert.False((await ValidateAsync("NOT-A-REAL-CODE", "newcomer@example.com")).Success);
    }
}
