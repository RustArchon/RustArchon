// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JumpStart.Authorization;
using JumpStart.Authorization.Repositories;
using JumpStart.Data;
using JumpStart.MultiTenant.Services;
using JumpStart.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="OrganizationInvitationService"/> - RustArchon's half of inviting somebody.
/// </summary>
/// <remarks>
/// JumpStart's tests cover the lifecycle: tokens, expiry, revocation, single use, email binding.
/// What is tested here is what RustArchon added, and the piece that carries real weight is the role
/// check at invite time. Attaching a role to an invitation is a way of granting it, so if it were
/// not checked it would be a way <em>around</em> the rule that you cannot hand out a permission you
/// do not hold - the assignment happens later, unattended, with nobody left to measure.
/// </remarks>
public class OrganizationInvitationServiceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();

    private ApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    private sealed class FixedUserContext(Guid userId) : IUserContext
    {
        public Task<Guid?> GetCurrentUserIdAsync() => Task.FromResult<Guid?>(userId);
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Task<Guid?> GetCurrentTenantIdAsync() => Task.FromResult<Guid?>(tenantId);
    }

    /// <summary>A clock the test controls, so expiry is a decision rather than a wait.</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private OrganizationInvitationService CreateService(
        ApiDbContext context, Guid actingAs, Mock<ICommunicationPublisher>? communicationPublisher = null)
    {
        var resolver = new PermissionResolver(context);

        var evaluator = new DatabasePermissionEvaluator(
            resolver, new FixedUserContext(actingAs), new FixedTenantContext(_tenantId));

        var invitations = new TenantInvitationService(
            context,
            new TestClock(DateTimeOffset.UtcNow),
            NullLogger<TenantInvitationService>.Instance);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CorsSettings:BlazorServerUrl"] = "https://panel.example.com"
            })
            .Build();

        return new OrganizationInvitationService(
            context,
            invitations,
            resolver,
            evaluator,
            (communicationPublisher ?? new Mock<ICommunicationPublisher>()).Object,
            configuration,
            NullLogger<OrganizationInvitationService>.Instance);
    }

    private async Task SeedTenantAsync(ApiDbContext context)
    {
        context.Set<Tenant>().Add(new Tenant { Id = _tenantId, Name = "Acme", IsActive = true });
        await context.SaveChangesAsync();
    }

    /// <summary>Creates a role and returns its id, optionally granting it to <paramref name="holder"/>.</summary>
    private async Task<Guid> AddRoleAsync(
        ApiDbContext context, string name, Guid? tenantId, Guid? holder, params string[] permissions)
    {
        var role = new Role { Name = name, TenantId = tenantId };
        context.Set<Role>().Add(role);
        await context.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            context.Set<RolePermission>().Add(
                new RolePermission { RoleId = role.Id, Permission = permission });
        }

        if (holder is { } userId)
        {
            context.Set<UserRole>().Add(
                new UserRole { UserId = userId, RoleId = role.Id, TenantId = _tenantId });
        }

        await context.SaveChangesAsync();
        return role.Id;
    }

    /// <summary>
    /// The property this class exists for: an invitation cannot be used to award a permission the
    /// inviter does not hold.
    /// </summary>
    [Fact]
    public async Task RefusesToAttachARoleTheInviterDoesNotFullyHold()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();

        // The inviter can manage members - and nothing else.
        await AddRoleAsync(
            context, "Coordinator", _tenantId, inviter, PermissionCatalog.OrganizationManageMembers);

        // The role they are trying to hand out is worth considerably more.
        var powerful = await AddRoleAsync(
            context, "Operator", _tenantId, null,
            PermissionCatalog.OrganizationManageMembers, PermissionCatalog.SubscriptionManage);

        var refused = await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, inviter)
                .InviteAsync(_tenantId, inviter, "newcomer@example.com", powerful));

        // The message names what they were missing, so it is actionable rather than just a refusal.
        Assert.Contains(PermissionCatalog.SubscriptionManage, refused.Message, StringComparison.Ordinal);

        Assert.Empty(await context.Set<TenantInvitation>().AcrossAllTenants().ToListAsync());
    }

    [Fact]
    public async Task AllowsARoleTheInviterDoesHold()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();

        var roleId = await AddRoleAsync(
            context, "Moderator", _tenantId, inviter,
            PermissionCatalog.OrganizationManageMembers, PermissionCatalog.ServerSendCommand);

        var invitation = await CreateService(context, inviter)
            .InviteAsync(_tenantId, inviter, "newcomer@example.com", roleId);

        Assert.Equal("newcomer@example.com", invitation.Email);
        Assert.Equal(roleId, invitation.RoleId);
        Assert.Equal("Moderator", invitation.RoleName);
    }

    [Fact]
    public async Task RefusesARoleBelongingToAnotherOrganization()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();
        await AddRoleAsync(context, "Owner-ish", _tenantId, inviter, PermissionCatalog.OrganizationManageMembers);

        var theirs = await AddRoleAsync(context, "Moderator", _otherTenantId, null);

        await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, inviter)
                .InviteAsync(_tenantId, inviter, "newcomer@example.com", theirs));
    }

    [Fact]
    public async Task InvitingWithNoRoleNeedsNoPermissionsBeyondTheEndpointItself()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();

        var invitation = await CreateService(context, inviter)
            .InviteAsync(_tenantId, inviter, "newcomer@example.com", roleId: null);

        Assert.Null(invitation.RoleId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("two words@example.com")]
    public async Task RefusesAnAddressThatCannotBeOne(string email)
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();

        await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, inviter).InviteAsync(_tenantId, inviter, email, null));
    }

    /// <summary>
    /// The email is the whole point - an invitation nobody is told about is not an invitation.
    /// </summary>
    [Fact]
    public async Task SendsTheInvitationEmailWithALinkCarryingTheToken()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();
        var communicationPublisher = new Mock<ICommunicationPublisher>();

        string? templateCode = null;
        IReadOnlyDictionary<string, string>? tokens = null;
        string? toAddress = null;
        Guid? tenantIdSent = null;

        communicationPublisher
            .Setup(p => p.QueueTemplatedAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyDictionary<string, string>, string, Guid?, Guid?, string?, CancellationToken>(
                (code, sentTokens, to, _, tenantId, _, _) =>
                {
                    templateCode = code;
                    tokens = sentTokens;
                    toAddress = to;
                    tenantIdSent = tenantId;
                })
            .ReturnsAsync(Guid.NewGuid());

        await CreateService(context, inviter, communicationPublisher)
            .InviteAsync(_tenantId, inviter, "newcomer@example.com", null);

        Assert.Equal("newcomer@example.com", toAddress);
        Assert.Equal(_tenantId, tenantIdSent);
        Assert.Equal(EmailTemplateRegistry.Codes.OrganizationInvitation, templateCode);

        var token = await context.Set<TenantInvitation>()
            .AcrossAllTenants()
            .Select(i => i.Token)
            .SingleAsync();

        Assert.NotNull(tokens);
        Assert.Equal("Acme", tokens["OrganizationName"]);
        Assert.Contains("https://panel.example.com/Organization/Invitations/Accept", tokens["InviteLink"], StringComparison.Ordinal);
        Assert.Contains(token, tokens["InviteLink"], StringComparison.Ordinal);
    }

    /// <summary>
    /// Each refusal gets its own sentence. Collapsing them into "invalid link" is what makes somebody
    /// give up rather than sign in with the address the invitation was actually sent to.
    /// </summary>
    [Fact]
    public async Task AcceptingWithTheWrongAccountSaysSoSpecifically()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();
        var service = CreateService(context, inviter);

        await service.InviteAsync(_tenantId, inviter, "invited@example.com", null);

        var token = await context.Set<TenantInvitation>()
            .AcrossAllTenants().Select(i => i.Token).SingleAsync();

        var result = await service.AcceptAsync(token, Guid.NewGuid(), "someone.else@example.com");

        Assert.False(result.Joined);
        Assert.Null(result.TenantId);
        Assert.Contains("different email address", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptingAsTheInvitedPersonJoinsThem()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();
        var newcomer = Guid.NewGuid();
        var service = CreateService(context, inviter);

        await service.InviteAsync(_tenantId, inviter, "invited@example.com", null);

        var token = await context.Set<TenantInvitation>()
            .AcrossAllTenants().Select(i => i.Token).SingleAsync();

        var result = await service.AcceptAsync(token, newcomer, "invited@example.com");

        Assert.True(result.Joined);
        Assert.Equal(_tenantId, result.TenantId);
        Assert.Contains("Acme", result.Message, StringComparison.Ordinal);

        Assert.True(await context.Set<UserTenant>()
            .AcrossAllTenants()
            .AnyAsync(ut => ut.UserId == newcomer && ut.TenantId == _tenantId));
    }

    [Fact]
    public async Task AnUnknownTokenPeeksAsNothing()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        Assert.Null(await CreateService(context, Guid.NewGuid()).PeekAsync("nonsense"));
    }

    [Fact]
    public async Task ListingShowsOutstandingInvitationsWithTheirRoleNames()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();

        var roleId = await AddRoleAsync(
            context, "Moderator", _tenantId, inviter, PermissionCatalog.ServerSendCommand);

        var service = CreateService(context, inviter);

        await service.InviteAsync(_tenantId, inviter, "with-role@example.com", roleId);
        await service.InviteAsync(_tenantId, inviter, "without-role@example.com", null);

        var listed = await service.ListAsync(_tenantId);

        Assert.Equal(2, listed.Count);
        Assert.Equal("Moderator", listed.Single(i => i.Email == "with-role@example.com").RoleName);
        Assert.Null(listed.Single(i => i.Email == "without-role@example.com").RoleName);
    }

    [Fact]
    public async Task RevokingAnInvitationThatIsNotOutstandingIsRefused()
    {
        await using var context = CreateContext();
        await SeedTenantAsync(context);

        var inviter = Guid.NewGuid();

        await Assert.ThrowsAsync<MemberManagementException>(
            () => CreateService(context, inviter).RevokeAsync(_tenantId, Guid.NewGuid(), inviter));
    }
}
