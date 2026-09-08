// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// Mints the tokens these tests authenticate with.
/// </summary>
/// <remarks>
/// <para>
/// Real JWTs, signed with the key the test host validates against, rather than a stub authentication
/// handler. A stub would sail past the very thing under test: whether the claims a token actually
/// carries are enough to get through. Everything the API decides from - who you are
/// (<see cref="ClaimTypes.NameIdentifier"/>), which Organization you are acting in (<c>tenant_id</c>)
/// and what you may do (<c>Permission</c>, one claim each) - is set here explicitly, so a test can
/// hand over exactly one of them and watch the request be refused.
/// </para>
/// <para>
/// A token with no <c>tenant_id</c> is the shape that matters most: it is what
/// <c>TokenController.Exchange</c> issues before an Organization has been chosen, and it is a real
/// token belonging to a real user - which is why "authenticated" and "scoped to a tenant" have to be
/// two separate questions.
/// </para>
/// </remarks>
public static class TestTokens
{
    /// <summary>A token for <paramref name="userId"/>, optionally inside a tenant, with permissions.</summary>
    public static string Create(
        Guid userId, Guid? tenantId = null, params string[] permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (tenantId is { } tenant)
        {
            claims.Add(new Claim("tenant_id", tenant.ToString()));
        }

        claims.AddRange(permissions.Select(p => new Claim("Permission", p)));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.JwtSecret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: ApiFactory.JwtIssuer,
            audience: ApiFactory.JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Attaches a freshly minted token to <paramref name="client"/>.</summary>
    public static HttpClient Authenticate(
        this HttpClient client, Guid userId, Guid? tenantId = null, params string[] permissions)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Create(userId, tenantId, permissions));

        return client;
    }
}
