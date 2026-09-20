// Copyright ©2026 Scott Blomfield

using System.Reflection;
using System.Security.Claims;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using RustArchon.Api.Administration;
using RustArchon.Api.Controllers;
using RustArchon.Api.Infrastructure;

namespace RustArchon.Api.Tests;

/// <summary>
/// "A site admin can do everything": run through the real authorization service with JumpStart's own handler beside ours,
/// against real controllers, so the test proves the combination rather than the handler in isolation.
/// </summary>
public class SiteAdminAuthorizationHandlerTests
{
    private static IAuthorizationService Authorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddScoped<IAuthorizationHandler, EntityPermissionHandler>();
        services.AddScoped<IAuthorizationHandler, SiteAdminAuthorizationHandler>();
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal User(params string[] permissions) =>
        new(new ClaimsIdentity(permissions.Select(p => new Claim("Permission", p)), "test"));

    /// <summary>A request whose matched endpoint is <paramref name="action"/> of <paramref name="controller"/>.</summary>
    private static HttpContext Request(Type controller, string action)
    {
        var context = new DefaultHttpContext();
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = controller.GetTypeInfo(),
            MethodInfo = controller.GetMethod(action)!
        };
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(descriptor), "test"));
        return context;
    }

    private static async Task<bool> CanAsync(ClaimsPrincipal user, Type controller, string action) =>
        (await Authorization().AuthorizeAsync(user, Request(controller, action), new EntityPermissionRequirement())).Succeeded;

    [Fact]
    public async Task ASiteAdminPassesAnOwnerOnlyEndpointEvenAsAMemberWithALesserRole()
    {
        // What the dev-admin account looks like in an organization it was added to as "Server Administrator": its own
        // tenant grants, plus the global Platform.* ones - and none of the owner-only permission.
        var user = User(PermissionCatalog.ServerGet, PermissionCatalog.PlatformManageOrganizations);

        Assert.True(await CanAsync(user, typeof(ServerBasesController), nameof(ServerBasesController.Get)));
    }

    [Fact]
    public async Task AMemberWithALesserRoleAndNoPlatformPermissionStillCannot()
    {
        var user = User(PermissionCatalog.ServerGet);

        Assert.False(await CanAsync(user, typeof(ServerBasesController), nameof(ServerBasesController.Get)));
    }

    [Fact]
    public async Task ANamedPermissionStillWorksForItsHolderAsBefore()
    {
        var user = User(PermissionCatalog.ServerViewBases);

        Assert.True(await CanAsync(user, typeof(ServerBasesController), nameof(ServerBasesController.Get)));
    }

    [Fact]
    public async Task ADifferentPlatformPermissionDoesNotMakeSomeoneASiteAdmin()
    {
        // Only Platform.ManageOrganizations is the site-admin test (the one SiteAdminCrossTenantPolicy uses); holding some
        // other platform permission, say for reports, is not "can do everything".
        var user = User(PermissionCatalog.PlatformViewReports);

        Assert.False(await CanAsync(user, typeof(ServerBasesController), nameof(ServerBasesController.Get)));
    }

    [Fact]
    public async Task NobodyWithNoPermissionsPasses()
    {
        Assert.False(await CanAsync(User(), typeof(ServerBasesController), nameof(ServerBasesController.Get)));
    }

    [Fact]
    public async Task TheSameRuleAppliesToOtherPermissionGatedEndpoints()
    {
        var siteAdmin = User(PermissionCatalog.PlatformManageOrganizations);

        Assert.True(await CanAsync(siteAdmin, typeof(ServerCombatController), nameof(ServerCombatController.Get)));
    }
}
