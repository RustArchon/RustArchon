// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RustArchon.Api.Data;

namespace RustArchon.Api.IntegrationTests;

/// <summary>
/// Boots the real API in memory, with the smallest possible set of substitutions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The point is that the pipeline is not rebuilt here.</strong> These tests exist to answer
/// "does this endpoint actually refuse that caller?", and an answer is only worth having if the
/// authentication scheme, the policy provider, the authorization handler and the filter order are the
/// ones that ship. So the host is <c>Program</c> itself, and only two things are replaced: the
/// database provider, and the hosted services.
/// </para>
/// <para>
/// <strong>Hosted services go</strong> because all three of them reach outside the process -
/// MassTransit's bus wants RabbitMQ, and the claim sweep and subscription scheduler would both run
/// their loops against the test database while assertions are being made about it. Removing
/// <see cref="IHostedService"/> as a category rather than by name means a service added later cannot
/// silently start doing that again.
/// </para>
/// <para>
/// <strong>The database is in-memory</strong> and named per factory, so one test class cannot see
/// another's rows. Startup's own seeders still run against it - the built-in Owner role, the plan
/// catalog, platform settings - which is exactly the state these tests want to start from.
/// </para>
/// </remarks>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"integration-{Guid.NewGuid()}";

    /// <summary>The signing key the test host validates against, and tests mint tokens with.</summary>
    public const string JwtSecret = "integration-tests-signing-key-not-a-real-secret-0123456789";

    public const string JwtIssuer = "RustArchon.Tests";
    public const string JwtAudience = "RustArchon.Tests";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        // Startup reads these before anything is built and throws without them. The connection string
        // is required but never dialled - the provider is replaced below.
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=unused");
        builder.UseSetting("RUSTARCHON_JWT_SECRET_KEY", JwtSecret);
        builder.UseSetting("JwtSettings:Issuer", JwtIssuer);
        builder.UseSetting("JwtSettings:Audience", JwtAudience);
        builder.UseSetting("JwtSettings:ExpiryMinutes", "60");
        builder.UseSetting("RUSTARCHON_INTERNAL_API_KEY", "integration-tests-internal-key");
        builder.UseSetting("DataProtection:KeyPath", Path.Combine(Path.GetTempPath(), "rustarchon-tests-keys"));

        builder.ConfigureTestServices(services =>
        {
            ReplaceDatabaseWithInMemory(services);

            // See the class remarks - by category, not by name.
            services.RemoveAll<IHostedService>();
        });
    }

    /// <summary>
    /// Points <see cref="ApiDbContext"/> at an in-memory store instead of PostgreSQL.
    /// </summary>
    /// <remarks>
    /// Every options-shaped registration has to go, not just <c>DbContextOptions&lt;ApiDbContext&gt;</c>:
    /// <c>AddDbContext</c> also leaves behind the per-context options configuration EF resolves
    /// through, and a leftover one re-applies <c>UseNpgsql</c> over the top of this - which surfaces
    /// much later as a connection attempt in a test that never mentioned a database.
    /// </remarks>
    private void ReplaceDatabaseWithInMemory(IServiceCollection services)
    {
        var doomed = services
            .Where(d => d.ServiceType.FullName is { } name
                && name.Contains("DbContextOptions", StringComparison.Ordinal)
                && name.Contains(nameof(ApiDbContext), StringComparison.Ordinal))
            .ToList();

        foreach (var descriptor in doomed)
        {
            services.Remove(descriptor);
        }

        services.RemoveAll<ApiDbContext>();

        services.AddDbContext<ApiDbContext>(options => options.UseInMemoryDatabase(_databaseName));
    }

    /// <summary>Runs <paramref name="work"/> against the test host's database.</summary>
    public async Task WithDatabaseAsync(Func<ApiDbContext, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        using var scope = Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<ApiDbContext>());
    }

    /// <summary>Reads something out of the test host's database.</summary>
    public async Task<T> FromDatabaseAsync<T>(Func<ApiDbContext, Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        using var scope = Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ApiDbContext>());
    }
}
