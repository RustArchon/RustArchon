// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// Every permission-gated endpoint, called both ways: by somebody who holds the permission and by
/// somebody who does not.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the rest of the authorization work was missing. Up to now the guards were checked
/// by reading the attributes (<c>AuthorizationCoverageTests</c> proves each endpoint <em>declares</em>
/// one) and by clicking through a browser. Neither answers the actual question - whether a request
/// from someone without the permission is turned away by the running pipeline - and the gap between
/// "declares a guard" and "enforces it" is exactly where an authorization bug lives.
/// </para>
/// <para>
/// <strong>Both halves are needed.</strong> The refusal test alone would pass just as happily if
/// every endpoint refused everybody, or if the URLs were wrong and all of it 404'd before reaching a
/// guard. The admission test is the control: with the permission the endpoint itself names, the same
/// request must get past authorization. Together they say the guard is present, effective, and keyed
/// to the permission it claims.
/// </para>
/// </remarks>
public class PermissionMatrixTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Long enough for a real answer; a hang means the request got past authorization.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task EveryGuardedEndpointRefusesACallerWithoutThePermission()
    {
        var endpoints = await EndpointCatalog.DiscoverAsync(factory);

        Assert.NotEmpty(endpoints);

        var admitted = new List<string>();

        foreach (var endpoint in endpoints)
        {
            using var client = factory.CreateClient();

            // A real, valid, signed-in caller inside a real Organization - holding nothing.
            client.Authenticate(Guid.NewGuid(), Guid.NewGuid());

            var status = await SendAsync(client, endpoint);

            if (status != HttpStatusCode.Forbidden)
            {
                admitted.Add($"{endpoint} -> {(status is null ? "reached the message bus" : status.ToString())}");
            }
        }

        Assert.True(
            admitted.Count == 0,
            "These endpoints did not refuse a caller holding no permissions:"
            + Environment.NewLine + string.Join(Environment.NewLine, admitted));
    }

    [Fact]
    public async Task EveryGuardedEndpointAdmitsACallerHoldingThePermission()
    {
        var endpoints = await EndpointCatalog.DiscoverAsync(factory);

        Assert.NotEmpty(endpoints);

        var refused = new List<string>();

        foreach (var endpoint in endpoints)
        {
            using var client = factory.CreateClient();

            client.Authenticate(Guid.NewGuid(), Guid.NewGuid(), [.. endpoint.Permissions]);

            var status = await SendAsync(client, endpoint);

            // Anything but a refusal. A 400 for a nonsense body or a 404 for an id that points at
            // nothing both mean the request reached the action, which is all this half asserts.
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                refused.Add($"{endpoint} -> {status}");
            }
        }

        Assert.True(
            refused.Count == 0,
            "These endpoints refused a caller holding the very permission they name:"
            + Environment.NewLine + string.Join(Environment.NewLine, refused));
    }

    /// <summary>
    /// Fires the request, or reports a timeout as "no status".
    /// </summary>
    /// <remarks>
    /// A couple of endpoints hand off to MassTransit, whose bus is not running in this host - see
    /// <see cref="ApiFactory"/>. Those wait for a reply that never comes. That is not a
    /// result worth failing on either way: reaching the bus at all means authorization let the
    /// request through, which is what the admission half is asking.
    /// </remarks>
    /// <summary>Actions that take a multipart form, not JSON (a file upload).</summary>
    private static readonly HashSet<string> FormActions = new(StringComparer.Ordinal) { "PluginAdmin.Upload", "PluginAdmin.SignFile" };

    private static async Task<HttpStatusCode?> SendAsync(HttpClient client, GuardedEndpoint endpoint)
    {
        client.Timeout = RequestTimeout;

        using var request = new HttpRequestMessage(
            new HttpMethod(endpoint.Method), new Uri(endpoint.Url, UriKind.Relative));

        if (endpoint.Method is "POST" or "PUT" or "PATCH")
        {
            // A form endpoint answers 415 to a JSON body before authorization is even consulted, which says nothing about its guard.
            request.Content = FormActions.Contains(endpoint.Action)
                ? new MultipartFormDataContent { { new StringContent("main"), "kind" } }
                : new StringContent("{}", Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
