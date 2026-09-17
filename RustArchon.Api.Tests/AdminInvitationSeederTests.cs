// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="AdminInvitationSeeder"/>: the one-time bootstrap invitation code seeded from
/// <c>RUSTARCHON_ADMIN_EMAIL</c>/<c>RUSTARCHON_ADMIN_CODE</c>.
/// </summary>
public class AdminInvitationSeederTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    [Fact]
    public async Task WithNoExistingCode_SeedsOneBoundToTheConfiguredEmail()
    {
        await using var context = CreateContext();

        await AdminInvitationSeeder.SeedAsync(context, "admin@example.com", "ABCD1234", NullLogger.Instance);

        var code = await context.InvitationCodes.SingleAsync();
        Assert.Equal("ABCD1234", code.Code);
        Assert.Equal("admin@example.com", code.BoundEmail);
        Assert.True(code.IsActive);
        Assert.Null(code.RedeemedAtUtc);
    }

    [Fact]
    public async Task RunningTwiceWithTheSameEmail_DoesNotDuplicateTheRow()
    {
        await using var context = CreateContext();

        await AdminInvitationSeeder.SeedAsync(context, "admin@example.com", "ABCD1234", NullLogger.Instance);
        await AdminInvitationSeeder.SeedAsync(context, "admin@example.com", "ABCD1234", NullLogger.Instance);

        Assert.Equal(1, await context.InvitationCodes.CountAsync());
    }

    /// <summary>
    /// The scenario that motivated this: an operator deploys with a typo'd
    /// <c>RUSTARCHON_ADMIN_EMAIL</c>, notices before ever registering, corrects it in <c>.env</c> and
    /// restarts. The still-unclaimed code should follow the correction rather than silently staying
    /// bound to the address nobody can log in as.
    /// </summary>
    [Fact]
    public async Task WhenTheConfiguredEmailChangesAndTheCodeIsStillUnclaimed_RebindsTheExistingCode()
    {
        await using var context = CreateContext();

        await AdminInvitationSeeder.SeedAsync(context, "typo@example.com", "ABCD1234", NullLogger.Instance);
        await AdminInvitationSeeder.SeedAsync(context, "correct@example.com", "ABCD1234", NullLogger.Instance);

        var code = await context.InvitationCodes.SingleAsync();
        Assert.Equal("ABCD1234", code.Code);
        Assert.Equal("correct@example.com", code.BoundEmail);
    }

    [Fact]
    public async Task WhenTheCodeHasAlreadyBeenRedeemed_TheEmailChangeIsIgnored()
    {
        await using var context = CreateContext();

        await AdminInvitationSeeder.SeedAsync(context, "original@example.com", "ABCD1234", NullLogger.Instance);
        var redeemed = await context.InvitationCodes.SingleAsync();
        redeemed.RedeemedAtUtc = DateTimeOffset.UtcNow;
        redeemed.RedeemedByEmail = "original@example.com";
        await context.SaveChangesAsync();

        await AdminInvitationSeeder.SeedAsync(context, "someone-else@example.com", "ABCD1234", NullLogger.Instance);

        var code = await context.InvitationCodes.SingleAsync();
        Assert.Equal("original@example.com", code.BoundEmail);
    }

    [Fact]
    public async Task ChangingTheCodeItselfSeedsASeparateRow_RatherThanReplacingTheFirst()
    {
        await using var context = CreateContext();

        await AdminInvitationSeeder.SeedAsync(context, "admin@example.com", "ABCD1234", NullLogger.Instance);
        await AdminInvitationSeeder.SeedAsync(context, "admin@example.com", "WXYZ5678", NullLogger.Instance);

        Assert.Equal(2, await context.InvitationCodes.CountAsync());
    }

    [Fact]
    public async Task WithNoCodeConfigured_DoesNothing()
    {
        await using var context = CreateContext();

        await AdminInvitationSeeder.SeedAsync(context, "admin@example.com", null, NullLogger.Instance);

        Assert.Empty(context.InvitationCodes);
    }

    [Fact]
    public async Task WithACodeButNoEmailConfigured_DoesNothing()
    {
        await using var context = CreateContext();

        await AdminInvitationSeeder.SeedAsync(context, null, "ABCD1234", NullLogger.Instance);

        Assert.Empty(context.InvitationCodes);
    }
}
