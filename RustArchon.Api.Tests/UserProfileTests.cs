// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using JumpStart.Repositories;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Messaging.Contracts;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// A person's own settings in the main database: the store (create, update, clear, never two rows for one person, hand-over of what the identity database
/// held without overwriting anything), the two ways to reach it (the person's own, and the Panel's shared-secret ones), and the point of it - an email to
/// a person is worded in their language when the caller did not say which.
/// </summary>
public class UserProfileTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private UserProfileStore Store() => new(new ApiDbContext(postgres.Options), new FixedClock(Now));

    private async Task<List<UserProfile>> RowsAsync(Guid userId)
    {
        await using var context = new ApiDbContext(postgres.Options);
        return await context.UserProfiles.AsNoTracking().Where(p => p.UserId == userId).ToListAsync();
    }

    // ---- the store --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewPersonHasNoProfileUntilSomethingIsSaved()
    {
        Assert.Null(await Store().FindAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task SavingTheLanguageCreatesTheProfileAndReadingItBackGivesTheSameValue()
    {
        var user = Guid.NewGuid();

        var saved = await Store().SetPreferredCultureAsync(user, "es-US");

        Assert.Equal("es-US", saved.PreferredCulture);
        Assert.Equal(Now, saved.UpdatedOn);
        Assert.Equal("es-US", (await Store().FindAsync(user))!.PreferredCulture);
    }

    [Fact]
    public async Task ChangingItUpdatesTheOneRowRatherThanAddingAnother()
    {
        var user = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(user, "en-US");

        await Store().SetPreferredCultureAsync(user, "es-US");

        var rows = await RowsAsync(user);
        Assert.Equal("es-US", Assert.Single(rows).PreferredCulture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankClearsTheLanguageButKeepsTheProfile(string? blank)
    {
        var user = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(user, "en-US");

        await Store().SetPreferredCultureAsync(user, blank);

        Assert.Null(Assert.Single(await RowsAsync(user)).PreferredCulture);
    }

    [Fact]
    public async Task TheCultureNameIsTrimmed()
    {
        var user = Guid.NewGuid();

        await Store().SetPreferredCultureAsync(user, "  en-GB ");

        Assert.Equal("en-GB", Assert.Single(await RowsAsync(user)).PreferredCulture);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("en_US")]
    [InlineData("e")]
    [InlineData("en-")]
    [InlineData("en-US; DROP TABLE")]
    [InlineData("<script>")]
    [InlineData("en-US-en-US-en-US-en-US")]
    public async Task ANameThatIsNotACultureNameIsRefusedAndNothingIsStored(string bad)
    {
        var user = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => Store().SetPreferredCultureAsync(user, bad));

        Assert.Empty(await RowsAsync(user));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("en-US")]
    [InlineData("zh-Hant-TW")]
    [InlineData("fil")]
    public void OrdinaryCultureNamesAreAccepted(string good)
    {
        Assert.Equal(good, UserProfileStore.Normalize(good));
    }

    [Fact]
    public async Task TwoCallsSavingTheSamePersonAtOnceLeaveOneRowAndNeitherFails()
    {
        var user = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Store().SetPreferredCultureAsync(user, i % 2 == 0 ? "en-US" : "es-US")));

        Assert.Single(await RowsAsync(user));
    }

    [Fact]
    public async Task SeveralPeopleCanBeReadAtOnceAndAPersonWithNoneIsAbsent()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var none = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(a, "en-US");
        await Store().SetPreferredCultureAsync(b, "es-US");

        var found = await Store().FindManyAsync([a, b, none, a]);

        Assert.Equal(2, found.Count);
        Assert.Equal("es-US", found[b].PreferredCulture);
        Assert.False(found.ContainsKey(none));
        Assert.Empty(await Store().FindManyAsync([]));
    }

    // ---- handing over what identity held ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHandOverCreatesProfilesOnlyForPeopleWhoHaveNone()
    {
        var has = Guid.NewGuid();
        var lacks = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(has, "es-US");

        var created = await Store().BackfillPreferredCulturesAsync([(has, "en-US"), (lacks, "en-GB")]);

        Assert.Equal(1, created);
        Assert.Equal("es-US", (await Store().FindAsync(has))!.PreferredCulture);         // what was there is newer, so it stands
        Assert.Equal("en-GB", (await Store().FindAsync(lacks))!.PreferredCulture);
    }

    [Fact]
    public async Task RepeatingTheHandOverChangesNothing()
    {
        var user = Guid.NewGuid();
        await Store().BackfillPreferredCulturesAsync([(user, "en-GB")]);

        var again = await Store().BackfillPreferredCulturesAsync([(user, "es-US")]);

        Assert.Equal(0, again);
        Assert.Equal("en-GB", Assert.Single(await RowsAsync(user)).PreferredCulture);
    }

    [Fact]
    public async Task ABadValueInABatchIsRecordedAsNoLanguageAndDoesNotSinkTheRest()
    {
        var bad = Guid.NewGuid();
        var good = Guid.NewGuid();

        var created = await Store().BackfillPreferredCulturesAsync([(bad, "not a culture"), (good, "es-US")]);

        Assert.Equal(2, created);
        Assert.Null((await Store().FindAsync(bad))!.PreferredCulture);
        Assert.Equal("es-US", (await Store().FindAsync(good))!.PreferredCulture);
    }

    [Fact]
    public async Task ThePeopleInABatchAreCountedOnceEachEvenIfListedTwice()
    {
        var user = Guid.NewGuid();

        var created = await Store().BackfillPreferredCulturesAsync([(user, "en-US"), (user, "es-US")]);

        Assert.Equal(1, created);
        Assert.Single(await RowsAsync(user));
    }

    [Fact]
    public async Task AnEmptyBatchIsFine()
    {
        Assert.Equal(0, await Store().BackfillPreferredCulturesAsync([]));
    }

    [Fact]
    public async Task TwoHandOversOfTheSamePeopleAtOnceDoNotConflict()
    {
        var people = Enumerable.Range(0, 10).Select(_ => (Guid.NewGuid(), (string?)"en-US")).ToList();

        var counts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Store().BackfillPreferredCulturesAsync(people)));

        Assert.Equal(10, counts.Sum());          // each created exactly once between them
        foreach (var (id, _) in people)
        {
            Assert.Single(await RowsAsync(id));
        }
    }

    // ---- a person's own settings ------------------------------------------------------------------------------------------------------

    private UserProfileController Me(Guid? userId)
    {
        var user = new Mock<IUserContext>();
        user.Setup(u => u.GetCurrentUserIdAsync()).ReturnsAsync(userId);
        return new UserProfileController(Store(), user.Object);
    }

    [Fact]
    public async Task ASignedInPersonSeesTheirOwnLanguageOrNoneYet()
    {
        var user = Guid.NewGuid();

        var before = Assert.IsType<UserProfileDto>(Assert.IsType<OkObjectResult>((await Me(user).Get(CancellationToken.None)).Result).Value);
        await Store().SetPreferredCultureAsync(user, "es-US");
        var after = Assert.IsType<UserProfileDto>(Assert.IsType<OkObjectResult>((await Me(user).Get(CancellationToken.None)).Result).Value);

        Assert.Null(before.PreferredCulture);
        Assert.Equal(user, before.UserId);
        Assert.Equal("es-US", after.PreferredCulture);
    }

    [Fact]
    public async Task ASignedInPersonCanChangeTheirLanguageAndOnlyTheirOwn()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(theirs, "en-US");

        var result = await Me(mine).Put(new UpdateUserProfileDto { PreferredCulture = "es-US" }, CancellationToken.None);

        Assert.Equal("es-US", Assert.IsType<UserProfileDto>(Assert.IsType<OkObjectResult>(result.Result).Value).PreferredCulture);
        Assert.Equal("es-US", (await Store().FindAsync(mine))!.PreferredCulture);
        Assert.Equal("en-US", (await Store().FindAsync(theirs))!.PreferredCulture);          // nobody else's changed
    }

    [Fact]
    public async Task SavingWithNothingSentClearsTheLanguage()
    {
        var user = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(user, "en-US");

        await Me(user).Put(new UpdateUserProfileDto(), CancellationToken.None);

        Assert.Null((await Store().FindAsync(user))!.PreferredCulture);
    }

    [Fact]
    public async Task SomeoneWhoIsNotSignedInGetsNothing()
    {
        Assert.IsType<UnauthorizedResult>((await Me(null).Get(CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await Me(null).Put(new UpdateUserProfileDto { PreferredCulture = "en-US" }, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task ABodyThatFailsValidationIsRefused()
    {
        var controller = Me(Guid.NewGuid());
        controller.ModelState.AddModelError(nameof(UpdateUserProfileDto.PreferredCulture), "That is not a culture name such as en-US.");

        Assert.IsType<BadRequestObjectResult>((await controller.Put(new UpdateUserProfileDto { PreferredCulture = "nonsense" }, CancellationToken.None)).Result);
    }

    [Theory]
    [InlineData("en-US", true)]
    [InlineData("es", true)]
    [InlineData(null, true)]
    [InlineData("english", false)]
    [InlineData("en-US; drop", false)]
    [InlineData("en-US-aaaaaaaaa", false)]
    public void TheBodyValidatesTheCultureName(string? culture, bool valid)
    {
        var dto = new UpdateUserProfileDto { PreferredCulture = culture };

        Assert.Equal(valid, Validator.TryValidateObject(dto, new ValidationContext(dto), null, validateAllProperties: true));
    }

    [Fact]
    public void ThePersonalEndpointsNeedASignInAndTheInternalOnesNeedTheSharedSecret()
    {
        var me = typeof(UserProfileController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        var internalKey = typeof(InternalUserProfilesController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();

        Assert.Null(me.AuthenticationSchemes);
        Assert.Equal("InternalApiKey", internalKey.AuthenticationSchemes);
        Assert.Empty(typeof(UserProfileController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true));
        Assert.Empty(typeof(InternalUserProfilesController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true));
    }

    // ---- what the Panel keeps in step -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePanelCanSetAndReadAPersonsLanguageWithoutThemBeingSignedIn()
    {
        var user = Guid.NewGuid();
        var controller = new InternalUserProfilesController(Store());

        var put = await controller.Put(user, new UpdateUserProfileDto { PreferredCulture = "es-US" }, CancellationToken.None);
        var get = await controller.Get(user, CancellationToken.None);

        Assert.Equal("es-US", Assert.IsType<UserProfileDto>(Assert.IsType<OkObjectResult>(put.Result).Value).PreferredCulture);
        Assert.Equal("es-US", Assert.IsType<UserProfileDto>(Assert.IsType<OkObjectResult>(get.Result).Value).PreferredCulture);
    }

    [Fact]
    public async Task ThePanelsHandOverReportsHowManyProfilesItCreated()
    {
        var existing = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(existing, "en-US");
        var controller = new InternalUserProfilesController(Store());

        var result = await controller.Backfill(new BackfillUserProfilesDto
        {
            Items = [new() { UserId = existing, PreferredCulture = "es-US" }, new() { UserId = Guid.NewGuid(), PreferredCulture = "es-US" }]
        }, CancellationToken.None);

        Assert.Equal(1, Assert.IsType<int>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal("en-US", (await Store().FindAsync(existing))!.PreferredCulture);
    }

    // ---- the point: email in the person's own language --------------------------------------------------------------------------------

    private sealed record Sent(Communication Communication);

    private (CommunicationPublisher Publisher, List<Communication> Saved) Publisher(string? defaultCulture = "en-US")
    {
        var template = new EmailTemplate { Code = "T", Name = "T" };
        template.Translations.Add(new EmailTemplateTranslation { Culture = "en-US", Subject = "Hello {{OrganizationName}}", HtmlBody = "<p>English</p>" });
        template.Translations.Add(new EmailTemplateTranslation { Culture = "es-US", Subject = "Hola {{OrganizationName}}", HtmlBody = "<p>Español</p>" });
        var templates = new Mock<IEmailTemplateRepository>();
        templates.Setup(t => t.GetByCodeAsync("T")).ReturnsAsync(template);

        var saved = new List<Communication>();
        var communications = new Mock<ICommunicationRepository>();
        communications.Setup(c => c.AddAsync(It.IsAny<Communication>())).Callback((Communication c) => saved.Add(c)).ReturnsAsync((Communication c) => c);

        var settings = new Mock<IPlatformSettingsCache>();
        settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.DefaultCulture)).ReturnsAsync(defaultCulture);
        settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.SiteName)).ReturnsAsync("Site");
        settings.Setup(s => s.GetStringAsync(PlatformSettingsRegistry.SiteUrl)).ReturnsAsync("https://site.example");

        var publisher = new CommunicationPublisher(
            communications.Object, templates.Object, new Mock<IPublishEndpoint>().Object, new ConfigurationBuilder().Build(), settings.Object, Store());
        return (publisher, saved);
    }

    private static readonly IReadOnlyDictionary<string, string> Tokens = new Dictionary<string, string> { ["OrganizationName"] = "Acme" };

    [Fact]
    public async Task AnEmailToAPersonWhoChoseALanguageIsWordedInItWhenTheCallerDidNotSay()
    {
        var user = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(user, "es-US");
        var (publisher, saved) = Publisher();

        await publisher.QueueTemplatedAsync("T", Tokens, "a@example.com", user, tenantId: null);

        Assert.Equal("Hola Acme", Assert.Single(saved).Subject);
    }

    [Fact]
    public async Task WhatTheCallerAsksForWinsOverThePersonsOwnSetting()
    {
        var user = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(user, "es-US");
        var (publisher, saved) = Publisher();

        await publisher.QueueTemplatedAsync("T", Tokens, "a@example.com", user, tenantId: null, culture: "en-US");

        Assert.Equal("Hello Acme", Assert.Single(saved).Subject);
    }

    [Fact]
    public async Task APersonWithNoSettingGetsThePlatformDefaultLanguage()
    {
        var (publisher, saved) = Publisher(defaultCulture: "es-US");

        await publisher.QueueTemplatedAsync("T", Tokens, "a@example.com", Guid.NewGuid(), tenantId: null);

        Assert.Equal("Hola Acme", Assert.Single(saved).Subject);
    }

    [Fact]
    public async Task AnEmailWithNoPersonAtAllIsUnchanged()
    {
        var (publisher, saved) = Publisher();

        await publisher.QueueTemplatedAsync("T", Tokens, "a@example.com", userId: null, tenantId: Guid.NewGuid());

        Assert.Equal("Hello Acme", Assert.Single(saved).Subject);
    }

    [Fact]
    public async Task ALanguageTheTemplateHasNoVersionOfFallsBackTheWayItAlwaysDid()
    {
        var user = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(user, "fr-FR");
        var (publisher, saved) = Publisher();

        await publisher.QueueTemplatedAsync("T", Tokens, "a@example.com", user, tenantId: null);

        Assert.Equal("Hello Acme", Assert.Single(saved).Subject);
    }

    [Fact]
    public async Task APersonWhoClearedTheirLanguageIsTreatedAsHavingNone()
    {
        var user = Guid.NewGuid();
        await Store().SetPreferredCultureAsync(user, "es-US");
        await Store().SetPreferredCultureAsync(user, null);
        var (publisher, saved) = Publisher();

        await publisher.QueueTemplatedAsync("T", Tokens, "a@example.com", user, tenantId: null);

        Assert.Equal("Hello Acme", Assert.Single(saved).Subject);
    }
}
