// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// That the permission matrix is actually looking at the whole application.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PermissionMatrixTests"/> can only test endpoints <see cref="EndpointCatalog"/> finds,
/// so the matrix has one quiet failure mode: an endpoint that discovery misses is an endpoint nobody
/// is checking, and the suite stays green while coverage falls. That is worse than no test, because
/// it reads as reassurance.
/// </para>
/// <para>
/// So this comes at it from the other side, enumerating every action the application actually routes
/// and insisting each one is accounted for in exactly one of three ways: it demands a permission, it
/// is open to the world on purpose, or it is open to any signed-in user on purpose. The third
/// category is the interesting one, and it is checked rather than merely exempted - see
/// <see cref="EndpointsOpenToAnySignedInUserStillRequireSigningIn"/>.
/// </para>
/// </remarks>
public class EndpointCoverageTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>
    /// Endpoints reachable with no credentials at all.
    /// </summary>
    /// <remarks>
    /// A list rather than a rule, because "this one is open to the world" should be a decision
    /// somebody wrote down. Anything anonymous and not named here fails.
    /// </remarks>
    private static readonly HashSet<string> Anonymous = new(StringComparer.Ordinal)
    {
        // The Register page calls these before an account exists - see InvitationsController.
        "Invitations.GetStatus",
        "Invitations.Redeem",

        // Checks a code without consuming it, so registration can refuse a bad one before creating
        // anything and spend a good one only once the account exists. Anonymous for the same reason
        // as Redeem: the caller has no account yet, that being the point. It reveals only whether a
        // code the caller already holds would work.
        "Invitations.Validate",

        // The pricing page, for visitors who are not signed in.
        "PublicPlans.GetActive",

        // Reading an invitation someone was emailed. The person holding the link may not have an
        // account yet, and being told which Organization is asking for them is what makes them
        // willing to create one. The token is the credential: 256 unguessable bits, revealing only
        // an Organization's name to whoever already has the link.
        "InvitationAcceptance.Peek",

        // The platform's own name and public site URL - the nav bar (and the login page it renders
        // on) needs this before anyone is signed in. See PublicBrandingController.
        "PublicBranding.Get",

        // The marketing site's contact form has to know which captcha widget to load, and which
        // queues to offer, before the visitor has any identity at all. Exposes only the provider
        // name, the widget's public site key and each active queue's display fields - never the
        // secret key, and never the platform settings table. Same shape as PublicBranding.Get.
        "PublicTicketingConfig.Get",

        // Submitting a ticket without an account is what the contact form is for. Three defences,
        // cheapest first, all checked by TicketingPublicEndpointTests: a honeypot field, a per-IP
        // rate limit (the "ticket-submission" policy), and a captcha whenever the platform has a
        // provider configured. Creates one ticket and queues one email, and reads nothing back.
        "TicketSubmission.Submit",

        // A submitter looking at, and replying to, their own ticket. The token in the URL is the
        // credential: 256 random bits, and GetByGuestAccessTokenAsync also refuses one past its
        // expiry, so an unknown, expired or guessed token is a plain 404 that reveals nothing.
        // Never exposes staff notes, and replies go through the same TicketReplyPolicy as a
        // signed-in customer's.
        "GuestTicket.Get",
        "GuestTicket.AddMessage",
    };

    /// <summary>
    /// Endpoints any signed-in user may call, holding no permissions.
    /// </summary>
    /// <remarks>
    /// Each is here because requiring a permission would be circular - the caller cannot yet have
    /// one. A brand-new account belongs to no Organization and holds no claims, so the endpoint that
    /// gives it an Organization, the one that mints its first real token, and the one that tells it
    /// which Organizations it belongs to all have to work before any of that exists. Gating
    /// <c>Tenants.Mine</c> on a <c>Tenant.List</c> permission would additionally leak the existence
    /// of every Organization to anyone holding it (JumpStart ADR-015).
    /// </remarks>
    private static readonly HashSet<string> AnySignedInUser = new(StringComparer.Ordinal)
    {
        "AccountBootstrap.EnsureTenant",
        "Token.Exchange",
        "Tenants.Mine",

        // Accepting an invitation. The caller belongs to no Organization yet - that is the point of
        // accepting - so there is no tenant to scope a permission to and no permission they could
        // hold. What gates it instead is the token plus a signed-in account whose address matches
        // the one the invitation was sent to.
        "InvitationAcceptance.Accept",

        // Relinking a guest ticket to the account its submitter has just created. Runs from
        // Register.razor straight after sign-up, on the same short-lived identity-assertion token
        // EnsureTenant uses - so, like EnsureTenant, the caller cannot yet hold a permission. What
        // gates it instead is proof of ownership (the ticket's guest token) plus a verified email
        // that matches the address the ticket was submitted from.
        "AccountBootstrap.ClaimTicket",

        // Creating an Organization of your own, and the two reads the screen that does it needs.
        // Same shape again: the Organization does not exist yet, so there is nothing to hold a
        // permission in. All three are about the caller and name nobody else - they found it, they
        // get Owner in it, and Plan.OnePerOwner is the only limit that applies.
        "Organization.Create",
        "Organization.PlanOptions",
        "Organization.Founded",

        // A person's own settings (their language). They belong to the person, not to any organization, so there is no tenant to scope a permission to.
        // The route names nobody: the person is whoever the request is signed in as, so it can neither read nor change anyone else's.
        "UserProfile.Get",
        "UserProfile.Put",
    };

    /// <summary>Endpoints authenticated by the shared internal-service key rather than a user token.</summary>
    private const string InternalScheme = "InternalApiKey";

    [Fact]
    public async Task EveryRoutedEndpointIsAccountedFor()
    {
        var guarded = (await EndpointCatalog.DiscoverAsync(factory))
            .Select(e => e.Action)
            .ToHashSet(StringComparer.Ordinal);

        var unaccounted = RoutedActions()
            .Select(Name)
            .Where(name => !guarded.Contains(name)
                && !Anonymous.Contains(name)
                && !AnySignedInUser.Contains(name))
            .Where(name => !UsesInternalKey(name))
            .ToList();

        Assert.True(
            unaccounted.Count == 0,
            "These endpoints demand no permission and are on none of the deliberately-open lists. "
            + "Either guard them, or list them with a note saying why:"
            + Environment.NewLine + string.Join(Environment.NewLine, unaccounted));
    }

    /// <summary>
    /// The exemption list is a claim, so it gets tested: every endpoint on it must still refuse an
    /// anonymous caller.
    /// </summary>
    /// <remarks>
    /// Without this, adding a name to <see cref="AnySignedInUser"/> would be a way to silence the
    /// coverage test on an endpoint that turned out to need no credentials at all - which is the
    /// exact mistake the list exists to make visible.
    /// </remarks>
    [Fact]
    public async Task EndpointsOpenToAnySignedInUserStillRequireSigningIn()
    {
        var wideOpen = new List<string>();

        foreach (var action in RoutedActions().Where(a => AnySignedInUser.Contains(Name(a))))
        {
            using var client = factory.CreateClient();

            using var request = new HttpRequestMessage(
                new HttpMethod(EndpointCatalog.MethodOf(action)),
                new Uri(EndpointCatalog.Fill(action.AttributeRouteInfo!.Template!), UriKind.Relative));

            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Delete)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request);

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                wideOpen.Add($"{Name(action)} -> {response.StatusCode}");
            }
        }

        Assert.True(
            wideOpen.Count == 0,
            "These are listed as needing a signed-in user, but answered an anonymous request:"
            + Environment.NewLine + string.Join(Environment.NewLine, wideOpen));
    }

    /// <summary>
    /// A floor on how much the matrix covers.
    /// </summary>
    /// <remarks>
    /// The first test would still pass if discovery broke so completely that everything looked
    /// exempt - it compares two lists that could fail together. This compares against a number, which
    /// cannot fail in sympathy. Raise it when the surface genuinely grows; a drop means something
    /// stopped being checked.
    /// </remarks>
    [Fact]
    public async Task TheMatrixCoversTheWholeGuardedSurface()
    {
        var guarded = await EndpointCatalog.DiscoverAsync(factory);

        Assert.True(
            guarded.Count >= 120,
            $"Only {guarded.Count} guarded endpoints were discovered; the application has well over "
            + "a hundred. Discovery has probably stopped recognising a shape of guard.");
    }

    private static string Name(ControllerActionDescriptor action) =>
        $"{action.ControllerName}.{action.ActionName}";

    private List<ControllerActionDescriptor> RoutedActions()
    {
        using var scope = factory.Services.CreateScope();

        return [.. scope.ServiceProvider
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(a => a.AttributeRouteInfo?.Template is not null)];
    }

    private bool UsesInternalKey(string name) =>
        RoutedActions()
            .Where(a => Name(a) == name)
            .SelectMany(a => a.EndpointMetadata.OfType<IAuthorizeData>())
            .Any(d => d.AuthenticationSchemes?.Contains(InternalScheme, StringComparison.Ordinal) == true);
}
