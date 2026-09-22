// Copyright ©2026 Scott Blomfield

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RustArchon.Panel.Clients;
using RustArchon.Panel.Data;
using RustArchon.Panel.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Panel.Tests;

/// <summary>
/// Keeping the Api's copy of a person's language in step: written at sign-up and on every language switch (best effort - never failing either), and the
/// languages the identity database used to hold handed over in batches and deleted as the Api takes them.
/// </summary>
public class UserProfileWriterTests
{
    // ---- the writer ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLanguageIsSentToTheApiForThatPerson()
    {
        var client = new Mock<IInternalUserProfileApiClient>();
        var user = Guid.NewGuid();

        await new UserProfileWriter(client.Object, NullLogger<UserProfileWriter>.Instance).SetPreferredCultureAsync(user, "es-US");

        client.Verify(c => c.SetAsync(user, It.Is<UpdateUserProfileDto>(d => d.PreferredCulture == "es-US")), Times.Once());
    }

    [Fact]
    public async Task ClearingItIsSentAsNoLanguage()
    {
        var client = new Mock<IInternalUserProfileApiClient>();

        await new UserProfileWriter(client.Object, NullLogger<UserProfileWriter>.Instance).SetPreferredCultureAsync(Guid.NewGuid(), null);

        client.Verify(c => c.SetAsync(It.IsAny<Guid>(), It.Is<UpdateUserProfileDto>(d => d.PreferredCulture == null)), Times.Once());
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    public async Task AnApiThatCannotBeReachedNeverFailsSignUpOrTheLanguageSwitcher(Type failure)
    {
        var client = new Mock<IInternalUserProfileApiClient>();
        client.Setup(c => c.SetAsync(It.IsAny<Guid>(), It.IsAny<UpdateUserProfileDto>())).ThrowsAsync((Exception)Activator.CreateInstance(failure, "down")!);

        var exception = await Record.ExceptionAsync(() => new UserProfileWriter(client.Object, NullLogger<UserProfileWriter>.Instance).SetPreferredCultureAsync(Guid.NewGuid(), "en-US"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ACancelledRequestIsStillACancelledRequest()
    {
        var client = new Mock<IInternalUserProfileApiClient>();
        client.Setup(c => c.SetAsync(It.IsAny<Guid>(), It.IsAny<UpdateUserProfileDto>())).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new UserProfileWriter(client.Object, NullLogger<UserProfileWriter>.Instance).SetPreferredCultureAsync(Guid.NewGuid(), "en-US"));
    }

    // ---- handing over what identity holds ------------------------------------------------------------------------------------------------

    private static (UserProfileBackfillService Service, Mock<IInternalUserProfileApiClient> Client, ApplicationDbContext Db) Backfill()
    {
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(databaseName));
        var client = new Mock<IInternalUserProfileApiClient>();
        client.Setup(c => c.BackfillAsync(It.IsAny<BackfillUserProfilesDto>())).ReturnsAsync((BackfillUserProfilesDto b) => b.Items.Count);
        services.AddSingleton(client.Object);
        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ApplicationDbContext>();
        return (new UserProfileBackfillService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UserProfileBackfillService>.Instance), client, db);
    }

    private static LegacyUserCulture Waiting(string culture) => new() { UserId = Guid.NewGuid(), Culture = culture };

    [Fact]
    public async Task EveryLanguageWaitingIsHandedOver()
    {
        var (service, client, db) = Backfill();
        var spanish = Waiting("es-US");
        var english = Waiting("en-US");
        db.LegacyUserCultures.AddRange(spanish, english);
        await db.SaveChangesAsync();

        var created = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, created);
        client.Verify(c => c.BackfillAsync(It.Is<BackfillUserProfilesDto>(b =>
            b.Items.Count == 2
            && b.Items.Any(i => i.UserId == spanish.UserId && i.PreferredCulture == "es-US")
            && b.Items.Any(i => i.UserId == english.UserId && i.PreferredCulture == "en-US"))), Times.Once());
    }

    [Fact]
    public async Task ARowIsDeletedOnceTheApiHasIt()
    {
        var (service, _, db) = Backfill();
        db.LegacyUserCultures.AddRange(Waiting("es-US"), Waiting("en-US"));
        await db.SaveChangesAsync();

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Empty(await db.LegacyUserCultures.ToListAsync());
    }

    [Fact]
    public async Task WhenTheApiCannotBeReachedNothingIsDeletedAndTheNextStartTriesAgain()
    {
        var (service, client, db) = Backfill();
        db.LegacyUserCultures.AddRange(Waiting("es-US"), Waiting("en-US"));
        await db.SaveChangesAsync();
        client.Setup(c => c.BackfillAsync(It.IsAny<BackfillUserProfilesDto>())).ThrowsAsync(new HttpRequestException("down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RunOnceAsync(CancellationToken.None));

        Assert.Equal(2, await db.LegacyUserCultures.CountAsync());
        client.Setup(c => c.BackfillAsync(It.IsAny<BackfillUserProfilesDto>())).ReturnsAsync((BackfillUserProfilesDto b) => b.Items.Count);
        Assert.Equal(2, await service.RunOnceAsync(CancellationToken.None));
        Assert.Empty(await db.LegacyUserCultures.ToListAsync());
    }

    [Fact]
    public async Task APassThatFailsPartWayKeepsWhatWasAlreadyHandedOverOutOfTheNextOne()
    {
        var (service, client, db) = Backfill();
        var people = Enumerable.Range(0, 1200).Select(_ => Waiting("en-US")).ToList();
        db.LegacyUserCultures.AddRange(people);
        await db.SaveChangesAsync();
        var calls = 0;
        client.Setup(c => c.BackfillAsync(It.IsAny<BackfillUserProfilesDto>()))
            .ReturnsAsync((BackfillUserProfilesDto b) => ++calls == 2 ? throw new HttpRequestException("down") : b.Items.Count);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RunOnceAsync(CancellationToken.None));

        Assert.Equal(700, await db.LegacyUserCultures.CountAsync());                // the first 500 went, and were deleted; the rest wait
    }

    [Fact]
    public async Task ManyPeopleAreHandedOverInBatchesAndEveryoneIsSentExactlyOnce()
    {
        var (service, client, db) = Backfill();
        var people = Enumerable.Range(0, 1200).Select(_ => Waiting("en-US")).ToList();
        db.LegacyUserCultures.AddRange(people);
        await db.SaveChangesAsync();
        var sent = new List<Guid>();
        client.Setup(c => c.BackfillAsync(It.IsAny<BackfillUserProfilesDto>()))
            .Callback((BackfillUserProfilesDto b) => sent.AddRange(b.Items.Select(i => i.UserId)))
            .ReturnsAsync((BackfillUserProfilesDto b) => b.Items.Count);

        var created = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1200, created);
        Assert.Equal(people.Select(p => p.UserId).Order(), sent.Order());              // everyone, none twice
        client.Verify(c => c.BackfillAsync(It.Is<BackfillUserProfilesDto>(b => b.Items.Count <= 500)), Times.Exactly(3));
    }

    [Fact]
    public async Task NobodyWaitingMeansNoCallAtAll()
    {
        var (service, client, _) = Backfill();

        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None));

        client.Verify(c => c.BackfillAsync(It.IsAny<BackfillUserProfilesDto>()), Times.Never());
    }
}
