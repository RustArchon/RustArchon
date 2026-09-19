// Copyright ©2026 Scott Blomfield

using System.Linq;
using JumpStart.Authorization;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// Every endpoint carries an authorization requirement, and every permission it names is declared.
/// </summary>
/// <remarks>
/// <para>
/// The test that would have caught <c>SubscriptionController</c>: six endpoints that changed a plan
/// and raised invoices with legally sequential numbers sat behind a bare <c>[Authorize]</c>, and
/// nothing failed, because nothing was looking. "Comprehensively guarantee unauthorized access will
/// not be granted" has to mean something mechanical, not a habit of remembering.
/// </para>
/// <para>
/// It reads types rather than routes (see <see cref="AuthorizationCoverage"/>), so it needs no host.
/// The cost is that it cannot see minimal-API endpoints; RustArchon declares none today, and this
/// comment is the reminder for the day it does.
/// </para>
/// </remarks>
public class AuthorizationCoverageTests
{
    /// <summary>
    /// Endpoints that are deliberately reachable without authentication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each is a decision, listed so a reviewer sees it. Registration and invitation redemption both
    /// run before an account exists; the public plan list is what the marketing site renders; the
    /// public branding endpoint is the platform's own name/URL, which a nav bar (and the login page it
    /// renders on) needs before anyone is signed in.
    /// </para>
    /// <para>
    /// <c>PublicTicketingConfigController</c> is the same shape as <c>PublicBrandingController</c> - a
    /// narrow, non-secret config slice the marketing site's contact form needs before anyone is signed
    /// in. <c>TicketSubmissionController</c> is that form's actual submission - the caller has no
    /// account by definition, guarded instead by a honeypot, a per-IP rate limit
    /// (<c>"ticket-submission"</c> in <c>Program.cs</c>), and a captcha check. <c>GuestTicketController</c>
    /// is an anonymous submitter's own access to their one ticket afterward - gated by the unguessable
    /// <c>Ticket.GuestAccessToken</c> in the URL, not by anything this scan can see.
    /// </para>
    /// </remarks>
    private static readonly string[] DeliberatelyAnonymous =
    [
        "InvitationsController",
        "PublicPlansController",
        "PublicBrandingController",
        "PublicTicketingConfigController",
        "TicketSubmissionController",
        "GuestTicketController"
    ];

    /// <summary>
    /// Endpoints where "any signed-in user" is genuinely the right requirement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both entries are the same shape of problem: the caller holds nothing yet, and the endpoint is
    /// how they come to hold anything. Demanding a permission would be circular.
    /// </para>
    /// <para>
    /// <c>EnsureTenant</c> is the bootstrap case JumpStart's ADR-012 already records as an accepted
    /// gap - a newly registered user has no roles and no permissions, so the endpoint that gives them
    /// their first ones cannot itself demand one. It provisions only the caller's own tenant and is
    /// idempotent.
    /// </para>
    /// <para>
    /// <c>Accept</c> is the invitation case. Somebody accepting an invitation belongs to no
    /// Organization - that is what accepting is for - so there is no tenant to scope a permission to.
    /// What gates it instead is the unguessable token plus a signed-in account whose address matches
    /// the one the invitation was addressed to, neither of which this scan can see.
    /// </para>
    /// </remarks>
    private static readonly string[] AuthenticatedOnlyByDesign =
    [
        "AccountBootstrapController.EnsureTenant",

        // Same bootstrap circularity as EnsureTenant just above, for the same reason: this runs from
        // Register.razor's static form-post handler, before any Blazor circuit (and so before the
        // normal circuit-scoped JWT machinery) exists, off a bare identity-assertion token with no
        // tenant_id or Permission claim to check. What gates it instead is the unguessable
        // GuestAccessToken plus a matching verified email - see ClaimTicket's own remarks.
        "AccountBootstrapController.ClaimTicket",

        "InvitationAcceptanceController.Accept",

        // Creating an Organization of your own, and the two reads its screen needs. The same
        // circularity a third time: the Organization does not exist yet, so there is no tenant to
        // scope a permission to. Each acts only on the caller and can name nobody else.
        "OrganizationController.Create",
        "OrganizationController.PlanOptions",
        "OrganizationController.Founded"
    ];

    private static readonly System.Reflection.Assembly ApiAssembly =
        typeof(PermissionCatalog).Assembly;

    [Fact]
    public void EveryEndpointCarriesAnAuthorizationRequirement()
    {
        var unguarded = AuthorizationCoverage
            .Scan(ApiAssembly, DeliberatelyAnonymous)
            .Where(e => !e.IsGuarded)
            .Select(e => e.ToString())
            .ToList();

        Assert.True(
            unguarded.Count == 0,
            "These endpoints have no authorization requirement. Add one, or list them in "
            + $"DeliberatelyAnonymous with a reason:{System.Environment.NewLine}"
            + string.Join(System.Environment.NewLine, unguarded));
    }

    /// <summary>
    /// A bare <c>[Authorize]</c> means "any signed-in user", which for this application is almost
    /// never the intent - every controller here acts on an Organization's servers, money or members.
    /// </summary>
    /// <remarks>
    /// Kept separate from the test above because the two say different things: that one is about a
    /// missing guard, this one is about a guard that is weaker than it looks. This is what
    /// <c>SubscriptionController</c> failed - it <em>had</em> a guard.
    /// </remarks>
    [Fact]
    public void NoEndpointReliesOnlyOnBeingSignedIn()
    {
        var authenticatedOnly = AuthorizationCoverage
            .Scan(ApiAssembly, DeliberatelyAnonymous)
            .Where(e => e.Guard == "Authorize"
                && !AuthenticatedOnlyByDesign.Contains($"{e.Controller}.{e.Action}"))
            .Select(e => e.ToString())
            .ToList();

        Assert.True(
            authenticatedOnly.Count == 0,
            "These endpoints require only that the caller is signed in, not that they hold any "
            + $"permission:{System.Environment.NewLine}"
            + string.Join(System.Environment.NewLine, authenticatedOnly));
    }

    /// <summary>
    /// Every permission an endpoint names has to exist in the catalog, or the endpoint is
    /// unreachable by anyone - a typo produces a guard nobody can ever satisfy.
    /// </summary>
    [Fact]
    public void EveryNamedPermissionIsDeclared()
    {
        var declared = PermissionCatalog.All.Select(p => p.Name).ToHashSet();

        var undeclared = AuthorizationCoverage
            .Scan(ApiAssembly, DeliberatelyAnonymous)
            .Select(e => e.Guard)
            .Where(g => g is not null && g.StartsWith("RequirePermission(", System.StringComparison.Ordinal))
            .Select(g => g!["RequirePermission(".Length..^1])
            .Distinct()
            .Where(p => !declared.Contains(p))
            .ToList();

        Assert.Empty(undeclared);
    }

    /// <summary>
    /// The policies registered in <c>Program.cs</c> check permission claims by name, so those names
    /// have to be declared too - they are the other half of the vocabulary.
    /// </summary>
    [Fact]
    public void CatalogDeclaresEveryPermissionTheSiteAdminRoleIsSeededWith()
    {
        var declared = PermissionCatalog.All.Select(p => p.Name).ToHashSet();

        Assert.All(PermissionCatalog.PlatformPermissions, p => Assert.Contains(p, declared));
        Assert.All(PermissionCatalog.OwnerPermissions, p => Assert.Contains(p, declared));
    }

    /// <summary>
    /// Owner is a tenant role, so everything in it must be tenant-scoped - a platform permission in
    /// there would be refused at grant time and the failure would surface at sign-up.
    /// </summary>
    [Fact]
    public void OwnerHoldsOnlyTenantScopedPermissions()
    {
        var byName = PermissionCatalog.All.ToDictionary(p => p.Name);

        Assert.All(
            PermissionCatalog.OwnerPermissions,
            p => Assert.Equal(PermissionScope.Tenant, byName[p].Scope));
    }

    /// <summary>
    /// Spending money is not delegable: no role a customer builds may contain it. See
    /// <see cref="PermissionCatalog.SubscriptionManage"/>.
    /// </summary>
    [Fact]
    public void ChangingThePlanIsNotDelegableToACustomRole()
    {
        var manage = PermissionCatalog.All.Single(p => p.Name == PermissionCatalog.SubscriptionManage);

        Assert.False(manage.DelegableByTenantAdmin);
    }
}
