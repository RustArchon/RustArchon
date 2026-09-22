// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Localization;
using Moq;
using RustArchon.Panel.Localization;
using RustArchon.Panel.Services;

namespace RustArchon.Panel.Tests;

/// <summary>Small fakes shared by the F7 report and add-server-wizard tests.</summary>
internal static class ReportTestSupport
{
    /// <summary>A localizer that returns the English key, formatting any arguments into it, like the real one does for a missing entry.</summary>
    public static IStringLocalizer<SharedResource> Localizer()
    {
        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns((string key, object[] args) => new LocalizedString(key, string.Format(key, args)));
        return localizer.Object;
    }

    /// <summary>
    /// A name lookup that knows the given people and, like the real one for an account that no longer exists, answers the start of the
    /// id for anyone else.
    /// </summary>
    public static IUserDisplayNames UserNames(params (Guid Id, string Name)[] known)
    {
        var lookup = known.ToDictionary(k => k.Id, k => k.Name);
        var names = new Mock<IUserDisplayNames>();
        names.Setup(n => n.ResolveAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync((IEnumerable<Guid> ids) =>
                (IReadOnlyDictionary<Guid, string>)ids.Distinct().ToDictionary(id => id, id => lookup.GetValueOrDefault(id, id.ToString()[..8])));
        return names.Object;
    }

    /// <summary>What Refit throws for an error status.</summary>
    public static Refit.ApiException Refused(HttpStatusCode status, string? body = null) =>
        Refit.ApiException.Create(
            new HttpRequestMessage(), HttpMethod.Get,
            new HttpResponseMessage(status) { Content = body is null ? null : new StringContent(body) },
            new Refit.RefitSettings()).GetAwaiter().GetResult();

    public static Refit.ApiException Forbidden => Refused(HttpStatusCode.Forbidden);
}
