// Copyright ©2026 Scott Blomfield

using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RustArchon.Api.Data;
using RustArchon.Api.DiagnosticViews;

namespace RustArchon.Api.Tests;

/// <summary>The views as the Api registers them: all of them, in one catalog built once, and nothing in that catalog that is marked hidden.</summary>
public class DiagnosticViewRegistrationTests
{
    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddDiagnosticViews();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void EveryInitialViewIsRegisteredAndInTheCatalog()
    {
        using var provider = Provider();

        var names = provider.GetRequiredService<ViewCatalog>().Describe().Select(d => d.Name);

        Assert.Equal(["platform-settings", "plugin-keys", "server", "server-plugin", "server-rcon-recent", "server-updates"], names);
    }

    [Fact]
    public void TheCatalogIsBuiltOnceAndEveryViewTypeIsResolvableByName()
    {
        using var provider = Provider();
        var catalog = provider.GetRequiredService<ViewCatalog>();

        Assert.Same(catalog, provider.GetRequiredService<ViewCatalog>());
        foreach (var registration in provider.GetServices<DiagnosticViewRegistration>())
        {
            Assert.NotNull(registration.Definition.Name);
            Assert.Same(registration.Definition, catalog.Find(registration.Definition.Name));
        }
    }

    [Fact]
    public void NoFieldInTheCatalogIsNamedAfterAPropertyMarkedHiddenOnTheEntityItCameFrom()
    {
        using var provider = Provider();
        foreach (var view in provider.GetServices<DiagnosticViewRegistration>().Select(r => r.Definition))
        {
            foreach (var part in view.Parts.Where(p => p.EntityType is not null))
            {
                var hidden = part.EntityType!.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(EntityProjector.IsHidden).Select(p => p.Name).ToList();
                var computed = part.ExtraFields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                var fields = ViewCatalog.FieldsOf(part).Select(f => f.Name).ToList();
                foreach (var name in hidden.Where(h => !computed.Contains(char.ToLowerInvariant(h[0]) + h[1..])))
                {
                    Assert.DoesNotContain(char.ToLowerInvariant(name[0]) + name[1..], fields);
                }
            }
        }
    }

    [Fact]
    public void EveryViewIsReadOnlyByItsShapeAndTakesOnlyTypedFlatParameters()
    {
        using var provider = Provider();
        foreach (var view in provider.GetRequiredService<ViewCatalog>().Describe())
        {
            Assert.All(view.Parameters, p => Assert.Contains(p.Type, new[] { "string", "integer", "uuid", "boolean" }));
            Assert.NotEmpty(view.Parts);
        }
    }

    [Fact]
    public void TheRealSecretsAreNotInAnyFieldOfAnyView()
    {
        using var provider = Provider();
        var fieldNames = provider.GetRequiredService<ViewCatalog>().Describe().SelectMany(v => v.Parts).SelectMany(p => p.Fields).Select(f => f.Name.ToLowerInvariant()).ToList();

        foreach (var secret in new[] { "rconpassword", "steamapikey", "geolocationapikey", "reportssecret", "encryptedprivatekey", "modulusbase64", "exponentbase64" })
        {
            Assert.DoesNotContain(secret, fieldNames);
        }

        Assert.DoesNotContain(typeof(RustServer).GetProperty(nameof(RustServer.RconPassword))!.Name.ToLowerInvariant(), fieldNames);
    }
}
