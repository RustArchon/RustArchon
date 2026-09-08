// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JumpStart.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using JumpStart.Repositories;

namespace RustArchon.Api.IntegrationTests;

/// <summary>One endpoint, the permissions it demands, and a request that would reach it.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Url">A concrete URL - route parameters already filled in with throwaway values.</param>
/// <param name="Permissions">
/// Every permission claim the caller must hold. Usually one, but a controller carrying a named
/// policy <em>and</em> an entity or explicit requirement has to satisfy both.
/// </param>
/// <param name="Action">Controller and action name, for a failure message someone can act on.</param>
public record GuardedEndpoint(
    string Method, string Url, IReadOnlyList<string> Permissions, string Action)
{
    public override string ToString() =>
        $"{Method} /{Url} ({Action}) requires '{string.Join("' + '", Permissions)}'";
}

/// <summary>
/// Asks the running application which endpoints it has and what each one demands.
/// </summary>
/// <remarks>
/// <para>
/// Read out of ASP.NET Core's own action descriptors and policy provider rather than re-declared in
/// the tests. That is the difference between a list that tests the application and a list that tests
/// itself: an endpoint added next month appears here without anyone remembering to add it, which is
/// precisely the failure this project exists to prevent.
/// </para>
/// <para>
/// Two shapes of guard are recognised, because the application uses two.
/// <see cref="RequirePermissionAttribute"/> names its permission directly. The six named policies
/// (<c>ViewReports</c>, <c>ManageBilling</c> and so on) are registered as
/// <c>RequireClaim("Permission", ...)</c>, so the permission is read back off the policy's own
/// requirement - not from a copy of the list in Program.cs that could drift from it.
/// </para>
/// </remarks>
public static class EndpointCatalog
{
    private static readonly Regex RouteParameter =
        new(@"\{(?<name>[^}:?]+)(?<constraint>:[^}?]+)?\??\}", RegexOptions.Compiled);

    /// <summary>Every endpoint that demands a permission, with a URL that would reach it.</summary>
    public static async Task<IReadOnlyList<GuardedEndpoint>> DiscoverAsync(ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var actions = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>();

        var policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var found = new List<GuardedEndpoint>();

        foreach (var action in actions)
        {
            if (action.AttributeRouteInfo?.Template is not { } template)
            {
                continue;
            }

            // An endpoint that lets anyone in is not making a promise this project can check.
            if (action.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            {
                continue;
            }

            var permissions = await RequiredPermissionsAsync(action, policies);

            if (permissions.Count == 0)
            {
                continue;
            }

            found.Add(new GuardedEndpoint(
                MethodOf(action),
                Fill(template),
                permissions,
                $"{action.ControllerName}.{action.ActionName}"));
        }

        return found;
    }

    /// <summary>Every permission this action demands. Empty when it demands none.</summary>
    /// <remarks>
    /// The first two branches mirror <c>EntityPermissionHandler</c>'s own order exactly, including
    /// that a directly-named permission wins over an entity-derived one and that both look at the
    /// method before the controller. Mirrored rather than shared because the handler decides from a
    /// live <c>HttpContext</c>; if the two ever disagree, the admission half of the matrix fails,
    /// which is the right way to find out.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> RequiredPermissionsAsync(
        ControllerActionDescriptor action, IAuthorizationPolicyProvider policies)
    {
        var required = new List<string>();

        var named = Attribute.GetCustomAttribute(action.MethodInfo, typeof(RequirePermissionAttribute), inherit: true)
            as RequirePermissionAttribute
            ?? Attribute.GetCustomAttribute(action.ControllerTypeInfo, typeof(RequirePermissionAttribute), inherit: true)
            as RequirePermissionAttribute;

        if (named is not null)
        {
            required.Add(named.Permission);
        }
        else if (EntityPermissionOf(action) is { } entityPermission)
        {
            required.Add(entityPermission);
        }

        // Named policies are an additional requirement rather than an alternative - a controller
        // carrying one has to satisfy it as well as anything above.
        foreach (var data in action.EndpointMetadata.OfType<IAuthorizeData>())
        {
            if (string.IsNullOrWhiteSpace(data.Policy))
            {
                continue;
            }

            var policy = await policies.GetPolicyAsync(data.Policy);

            var claim = policy?.Requirements
                .OfType<ClaimsAuthorizationRequirement>()
                .FirstOrDefault(r => r.ClaimType == "Permission");

            if (claim?.AllowedValues?.FirstOrDefault() is { } value)
            {
                required.Add(value);
            }
        }

        return [.. required.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The <c>Entity.Action</c> permission an <c>[EntityAuthorize]</c> endpoint derives, per ADR-011.
    /// </summary>
    /// <remarks>
    /// <c>inherit: true</c> is load-bearing: <c>RustServersController</c>'s Update and Delete carry no
    /// attribute of their own and rely on the one on the generic base class's virtual method, which is
    /// exactly how the handler finds it too. Without inheritance those two endpoints would vanish from
    /// this catalog silently - the failure mode a coverage test must not have.
    /// </remarks>
    private static string? EntityPermissionOf(ControllerActionDescriptor action)
    {
        var attribute =
            Attribute.GetCustomAttribute(action.MethodInfo, typeof(EntityAuthorizeAttribute), inherit: true)
                as EntityAuthorizeAttribute
            ?? Attribute.GetCustomAttribute(action.ControllerTypeInfo, typeof(EntityAuthorizeAttribute), inherit: true)
                as EntityAuthorizeAttribute;

        if (attribute is null)
        {
            return null;
        }

        // Walk up to the first generic base and take TEntity, as EntityPermissionHandler does.
        for (var type = action.ControllerTypeInfo.AsType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            if (type.IsGenericType)
            {
                return $"{type.GetGenericArguments()[0].Name}.{attribute.Action}";
            }
        }

        return null;
    }

    /// <summary>The HTTP method this action answers on.</summary>
    public static string MethodOf(ActionDescriptor action) =>
        action.ActionConstraints?
            .OfType<HttpMethodActionConstraint>()
            .SelectMany(c => c.HttpMethods)
            .FirstOrDefault()
        ?? "GET";

    /// <summary>
    /// Turns a route template into a URL by filling every parameter with a throwaway value.
    /// </summary>
    /// <remarks>
    /// The values are meaningless on purpose. Authorization filters run before model binding, so a
    /// request refused for want of a permission is refused before anything looks at what these ids
    /// point to - which is what lets one substitution serve every endpoint.
    /// </remarks>
    public static string Fill(string template) =>
        RouteParameter.Replace(template, match =>
        {
            var constraint = match.Groups["constraint"].Value;

            if (constraint.Contains("guid", StringComparison.OrdinalIgnoreCase))
            {
                return Guid.NewGuid().ToString();
            }

            return constraint.Contains("int", StringComparison.OrdinalIgnoreCase) ? "1" : "test";
        });
}
