// Copyright ©2026 Scott Blomfield

using System;
using System.Net.Http;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Components.Shared;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The rotation reminder every site administrator sees on every page: shown only when the Api says it is due, silent for everyone else (who get a
/// 403) and on any failure, dismissible for the visit, and a link to where the key is rotated.
/// </summary>
public class PluginKeyReminderBannerTests : BunitContext
{
    private readonly Mock<IPluginAdminApiClient> _client = new();

    public PluginKeyReminderBannerTests()
    {
        Services.AddSingleton(_client.Object);
        Services.AddSingleton(ReportTestSupport.Localizer());
    }

    private static PluginKeyReminderDto Reminder(bool due, int days = 400) =>
        new() { Due = due, Fingerprint = "82b49184449c98f6", AgeDays = days, ReminderDays = 365, ActiveSinceUtc = DateTimeOffset.UtcNow.AddDays(-days) };

    [Fact]
    public void ADueReminderIsShownWithHowOldTheKeyIsAndALinkToRotateIt()
    {
        _client.Setup(c => c.GetKeyReminderAsync()).ReturnsAsync(Reminder(due: true, days: 412));

        var cut = Render<PluginKeyReminderBanner>();

        var banner = cut.Find("[data-testid=key-reminder-banner]");
        Assert.Contains("412 days", banner.TextContent);
        Assert.Equal("Admin/Plugin", banner.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void AReminderThatIsNotDueShowsNothing()
    {
        _client.Setup(c => c.GetKeyReminderAsync()).ReturnsAsync(Reminder(due: false, days: 12));

        var cut = Render<PluginKeyReminderBanner>();

        Assert.Empty(cut.FindAll("[data-testid=key-reminder-banner]"));
    }

    [Fact]
    public void EveryoneWhoIsNotASiteAdminGetsA403AndSeesNothingNotEvenAnError()
    {
        _client.Setup(c => c.GetKeyReminderAsync()).ThrowsAsync(ReportTestSupport.Forbidden);

        var cut = Render<PluginKeyReminderBanner>();

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void AnyOtherFailureIsAlsoSilentBecauseAMissedNudgeIsNeverWorseThanABrokenLayout()
    {
        _client.Setup(c => c.GetKeyReminderAsync()).ThrowsAsync(new HttpRequestException("boom"));

        var cut = Render<PluginKeyReminderBanner>();

        Assert.Empty(cut.FindAll("[data-testid=key-reminder-banner]"));
    }

    [Fact]
    public void DismissingHidesItForThisVisit()
    {
        _client.Setup(c => c.GetKeyReminderAsync()).ReturnsAsync(Reminder(due: true));
        var cut = Render<PluginKeyReminderBanner>();

        cut.Find("[data-testid=key-reminder-dismiss]").Click();

        Assert.Empty(cut.FindAll("[data-testid=key-reminder-banner]"));
    }
}
