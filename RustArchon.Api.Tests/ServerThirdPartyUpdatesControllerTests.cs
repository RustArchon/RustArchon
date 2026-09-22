// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Moq;
using RustArchon.Api.Controllers;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Repositories;
using RustArchon.Api.Services;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// The endpoints behind the Plugins tab's "Update now": what is offered for each outdated plugin, and applying one by hand. Reading takes the permission
/// to see the server; applying, which changes files on someone's game server, takes the permission to update it. What is checked before anything is
/// sent is <see cref="ThirdPartyPluginUpdateService"/>'s and is tested there.
/// </summary>
public class ServerThirdPartyUpdatesControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ServerId = Guid.NewGuid();
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly Mock<IRustServerRepository> _servers = new();
    private readonly Mock<IThirdPartyPluginUpdateService> _updates = new();
    private readonly Mock<IPluginUpdateNoticeRepository> _notices = new();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public ServerThirdPartyUpdatesControllerTests()
    {
        _servers.Setup(s => s.GetByIdAsync(ServerId, null)).ReturnsAsync(new RustServer { Id = ServerId, TenantId = Guid.NewGuid(), IsEnabled = true });
        _updates.Setup(u => u.GetOffersAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>())).ReturnsAsync(new Dictionary<string, ThirdPartyUpdateOffer>());
        _notices.Setup(n => n.GetForServerAsync(ServerId)).ReturnsAsync([]);
    }

    private ServerThirdPartyUpdatesController Controller() => new(_servers.Object, _updates.Object, _notices.Object, new FixedClock(Now));

    // ---- reading -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownServerIsNotFound()
    {
        _servers.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((RustServer?)null);

        Assert.IsType<NotFoundResult>((await Controller().Get(Guid.NewGuid())).Result);
        Assert.IsType<NotFoundResult>((await Controller().Apply(Guid.NewGuid(), new ApplyThirdPartyPluginUpdateDto { PluginName = "x", FileSha256 = Sha })).Result);
    }

    [Fact]
    public async Task NothingToOfferIsAnEmptyListAndUpdatesAreStillSettled()
    {
        var result = Assert.IsType<OkObjectResult>((await Controller().Get(ServerId)).Result);

        Assert.Empty(Assert.IsType<List<ThirdPartyPluginUpdateOfferDto>>(result.Value));
        _updates.Verify(u => u.ReconcileAsync(It.IsAny<RustServer>(), Now), Times.Once());
    }

    [Fact]
    public async Task UpdatesUnderWayAreSettledBeforeTheOffersAreWorkedOut()
    {
        var order = new List<string>();
        _updates.Setup(u => u.ReconcileAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>())).Callback(() => order.Add("reconcile")).ReturnsAsync(1);
        _updates.Setup(u => u.GetOffersAsync(It.IsAny<RustServer>(), It.IsAny<DateTimeOffset>())).Callback(() => order.Add("offers"))
            .ReturnsAsync(new Dictionary<string, ThirdPartyUpdateOffer>());

        await Controller().Get(ServerId);

        Assert.Equal(["reconcile", "offers"], order);
    }

    [Fact]
    public async Task EachOfferCarriesThePluginsOwnCapitalizationAndIsListedByName()
    {
        _notices.Setup(n => n.GetForServerAsync(ServerId)).ReturnsAsync(
        [
            new PluginUpdateNotice { RustServerId = ServerId, Name = "Kits", NormalizedName = "kits" },
            new PluginUpdateNotice { RustServerId = ServerId, Name = "Better Chat", NormalizedName = "better chat" }
        ]);
        var held = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        _updates.Setup(u => u.GetOffersAsync(It.IsAny<RustServer>(), Now)).ReturnsAsync(new Dictionary<string, ThirdPartyUpdateOffer>
        {
            ["kits"] = new(ThirdPartyUpdateOfferStates.Ready, string.Empty, Sha, true, held),
            ["better chat"] = new(ThirdPartyUpdateOfferStates.RolledBack, "did not load", Sha, false, null)
        });

        var result = Assert.IsType<OkObjectResult>((await Controller().Get(ServerId)).Result);

        var offers = Assert.IsType<List<ThirdPartyPluginUpdateOfferDto>>(result.Value);
        Assert.Equal(["Better Chat", "Kits"], offers.Select(o => o.PluginName));
        var kits = offers.Single(o => o.PluginName == "Kits");
        Assert.Equal((ThirdPartyUpdateOfferStates.Ready, Sha, true, held), (kits.State, kits.FileSha256, kits.CanApply, kits.AutomaticUpdatesPausedUntilUtc));
        Assert.Equal("did not load", offers.Single(o => o.PluginName == "Better Chat").Detail);
    }

    // ---- applying --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyingIsAPersonsDecisionAndCarriesTheHashTheyWereShown()
    {
        _updates.Setup(u => u.StartAsync(It.IsAny<RustServer>(), "Kits", PluginUpdateTriggers.Manual, Sha))
            .ReturnsAsync(new PluginUpdateResultDto { Started = true, Code = "started" });

        var result = Assert.IsType<OkObjectResult>((await Controller().Apply(ServerId, new ApplyThirdPartyPluginUpdateDto { PluginName = "Kits", FileSha256 = Sha })).Result);

        Assert.True(Assert.IsType<PluginUpdateResultDto>(result.Value).Started);
        _updates.Verify(u => u.StartAsync(It.IsAny<RustServer>(), "Kits", PluginUpdateTriggers.Manual, Sha), Times.Once());
    }

    [Fact]
    public async Task TheFolderRulesAndTheChoiceToSaveThemAreHandedToTheService()
    {
        var rules = new List<RustArchon.Shared.PluginZips.ZipMappingRule> { new() { IsFolder = true, Source = "en/plugins/", Role = "plugins" } };
        _updates.Setup(u => u.StartAsync(It.IsAny<RustServer>(), "Kits", PluginUpdateTriggers.Manual, Sha, rules, true))
            .ReturnsAsync(new PluginUpdateResultDto { Started = true, Code = "started" });

        var result = Assert.IsType<OkObjectResult>((await Controller().Apply(ServerId, new ApplyThirdPartyPluginUpdateDto { PluginName = "Kits", FileSha256 = Sha, Rules = rules, SaveMapping = true })).Result);

        Assert.True(Assert.IsType<PluginUpdateResultDto>(result.Value).Started);
    }

    [Fact]
    public async Task AZipOfferCarriesItsFilesTheSavedRulesAndWhatTheyMiss()
    {
        _notices.Setup(n => n.GetForServerAsync(ServerId)).ReturnsAsync([new PluginUpdateNotice { RustServerId = ServerId, Name = "Kits", NormalizedName = "kits" }]);
        var files = new List<RustArchon.Shared.PluginZips.ZipEntryInfo> { new("en/plugins/kits.cs", 10), new("de/kits.cs", 11) };
        var saved = new List<RustArchon.Shared.PluginZips.ZipMappingRule> { new() { IsFolder = true, Source = "en/plugins/", Role = "plugins" } };
        _updates.Setup(u => u.GetOffersAsync(It.IsAny<RustServer>(), Now)).ReturnsAsync(new Dictionary<string, ThirdPartyUpdateOffer>
        {
            ["kits"] = new(ThirdPartyUpdateOfferStates.NeedsInstructions, string.Empty, Sha, true, null, "zip", files, saved, ["de/kits.cs"], MappingTrusted: true)
        });

        var offer = Assert.Single(Assert.IsType<List<ThirdPartyPluginUpdateOfferDto>>(Assert.IsType<OkObjectResult>((await Controller().Get(ServerId)).Result).Value));

        Assert.Equal(("zip", true), (offer.Kind, offer.MappingTrusted));
        Assert.Equal(["en/plugins/kits.cs", "de/kits.cs"], offer.Files.Select(f => f.Path));
        Assert.Equal(["en/plugins/"], offer.SavedRules.Select(r => r.Source));
        Assert.Equal(["de/kits.cs"], offer.UncoveredPaths);
    }

    [Fact]
    public void MoreRulesThanCanBeSentAreRefusedByTheRequestItself()
    {
        var request = new ApplyThirdPartyPluginUpdateDto
        {
            PluginName = "Kits", FileSha256 = Sha,
            Rules = [.. Enumerable.Range(0, RustArchon.Shared.PluginZips.ZipMapping.MaxRules + 1).Select(i => new RustArchon.Shared.PluginZips.ZipMappingRule { Source = $"f{i}/", IsFolder = true, Role = "data" })]
        };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, validateAllProperties: true));
    }

    [Fact]
    public async Task ARefusalComesBackAsAnOrdinaryResultSoThePageCanExplainIt()
    {
        _updates.Setup(u => u.StartAsync(It.IsAny<RustServer>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new PluginUpdateResultDto { Started = false, Code = "file_changed", Message = "The file has changed." });

        var result = Assert.IsType<OkObjectResult>((await Controller().Apply(ServerId, new ApplyThirdPartyPluginUpdateDto { PluginName = "Kits", FileSha256 = Sha })).Result);

        Assert.Equal("file_changed", Assert.IsType<PluginUpdateResultDto>(result.Value).Code);
    }

    [Fact]
    public async Task AnInvalidRequestIsBadAndNothingIsStarted()
    {
        var controller = Controller();
        controller.ModelState.AddModelError(nameof(ApplyThirdPartyPluginUpdateDto.FileSha256), "required");

        Assert.IsType<BadRequestObjectResult>((await controller.Apply(ServerId, new ApplyThirdPartyPluginUpdateDto { PluginName = "Kits" })).Result);
        _updates.Verify(u => u.StartAsync(It.IsAny<RustServer>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never());
    }

    [Theory]
    [InlineData(null, Sha)]
    [InlineData("", Sha)]
    [InlineData("Kits", null)]
    [InlineData("Kits", "")]
    [InlineData("Kits", "abc")]
    [InlineData("Kits", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]      // 65
    public void TheRequestNeedsBothAPluginAndTheHashItsFileWasShownWith(string? plugin, string? sha)
    {
        var request = new ApplyThirdPartyPluginUpdateDto { PluginName = plugin, FileSha256 = sha };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, validateAllProperties: true));
    }

    // ---- rechecking the file --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RecheckingHandsThePluginNameToTheServiceAndReturnsWhetherThereWasAnythingToCheck()
    {
        _updates.Setup(u => u.RecheckFileAsync(It.IsAny<RustServer>(), "Kits", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = Assert.IsType<OkObjectResult>((await Controller().Recheck(ServerId, new RecheckThirdPartyPluginFileDto { PluginName = "Kits" })).Result);

        Assert.True(Assert.IsType<bool>(result.Value));
        _updates.Verify(u => u.RecheckFileAsync(It.IsAny<RustServer>(), "Kits", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RecheckingSomethingWithNothingToCheckIsNotAnError()
    {
        _updates.Setup(u => u.RecheckFileAsync(It.IsAny<RustServer>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = Assert.IsType<OkObjectResult>((await Controller().Recheck(ServerId, new RecheckThirdPartyPluginFileDto { PluginName = "Kits" })).Result);

        Assert.False(Assert.IsType<bool>(result.Value));
    }

    [Fact]
    public async Task RecheckingAnUnknownServerIsNotFound()
    {
        _servers.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((RustServer?)null);

        Assert.IsType<NotFoundResult>((await Controller().Recheck(Guid.NewGuid(), new RecheckThirdPartyPluginFileDto { PluginName = "Kits" })).Result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RecheckingNeedsAPluginName(string? plugin)
    {
        var request = new RecheckThirdPartyPluginFileDto { PluginName = plugin };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, validateAllProperties: true));
    }

    [Fact]
    public async Task AnInvalidRecheckRequestIsBadAndNothingIsChecked()
    {
        var controller = Controller();
        controller.ModelState.AddModelError(nameof(RecheckThirdPartyPluginFileDto.PluginName), "required");

        Assert.IsType<BadRequestObjectResult>((await controller.Recheck(ServerId, new RecheckThirdPartyPluginFileDto())).Result);
        _updates.Verify(u => u.RecheckFileAsync(It.IsAny<RustServer>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // ---- excluding a plugin ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExcludingHandsThePluginNameAndNoteToTheServiceAsOfTheClocksNow()
    {
        var result = await Controller().Exclude(ServerId, new ExcludeThirdPartyPluginUpdateDto { PluginName = "Kits", Note = "the demo build" });

        Assert.IsType<OkResult>(result);
        _updates.Verify(u => u.ExcludeAsync(It.IsAny<RustServer>(), "Kits", "the demo build", Now), Times.Once());
    }

    [Fact]
    public async Task ExcludingAnUnknownServerIsNotFound()
    {
        _servers.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((RustServer?)null);

        Assert.IsType<NotFoundResult>(await Controller().Exclude(Guid.NewGuid(), new ExcludeThirdPartyPluginUpdateDto { PluginName = "Kits" }));
    }

    [Fact]
    public async Task AnInvalidExcludeRequestIsBadAndNothingIsExcluded()
    {
        var controller = Controller();
        controller.ModelState.AddModelError(nameof(ExcludeThirdPartyPluginUpdateDto.PluginName), "required");

        Assert.IsType<BadRequestObjectResult>(await controller.Exclude(ServerId, new ExcludeThirdPartyPluginUpdateDto()));
        _updates.Verify(u => u.ExcludeAsync(It.IsAny<RustServer>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>()), Times.Never());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ExcludingNeedsAPluginName(string? plugin)
    {
        var request = new ExcludeThirdPartyPluginUpdateDto { PluginName = plugin };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, validateAllProperties: true));
    }

    // ---- lifting an exclusion --------------------------------------------------------------------------------------------

    [Fact]
    public async Task IncludingHandsThePluginNameToTheService()
    {
        var result = await Controller().Include(ServerId, new RecheckThirdPartyPluginFileDto { PluginName = "Kits" });

        Assert.IsType<OkResult>(result);
        _updates.Verify(u => u.IncludeAsync(It.IsAny<RustServer>(), "Kits"), Times.Once());
    }

    [Fact]
    public async Task IncludingAnUnknownServerIsNotFound()
    {
        _servers.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((RustServer?)null);

        Assert.IsType<NotFoundResult>(await Controller().Include(Guid.NewGuid(), new RecheckThirdPartyPluginFileDto { PluginName = "Kits" }));
    }

    [Fact]
    public async Task AnInvalidIncludeRequestIsBadAndNothingIsIncluded()
    {
        var controller = Controller();
        controller.ModelState.AddModelError(nameof(RecheckThirdPartyPluginFileDto.PluginName), "required");

        Assert.IsType<BadRequestObjectResult>(await controller.Include(ServerId, new RecheckThirdPartyPluginFileDto()));
        _updates.Verify(u => u.IncludeAsync(It.IsAny<RustServer>(), It.IsAny<string>()), Times.Never());
    }

    // ---- permissions ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ReadingNeedsToSeeTheServerAndEveryOtherActionNeedsToUpdateIt()
    {
        string PermissionOn(string action) => typeof(ServerThirdPartyUpdatesController).GetMethod(action)!
            .GetCustomAttributes(typeof(JumpStart.Authorization.RequirePermissionAttribute), inherit: true)
            .Cast<JumpStart.Authorization.RequirePermissionAttribute>().Single().Permission;

        Assert.Equal(PermissionCatalog.ServerGet, PermissionOn(nameof(ServerThirdPartyUpdatesController.Get)));
        Assert.Equal(PermissionCatalog.ServerUpdate, PermissionOn(nameof(ServerThirdPartyUpdatesController.Apply)));
        Assert.Equal(PermissionCatalog.ServerUpdate, PermissionOn(nameof(ServerThirdPartyUpdatesController.Recheck)));
        Assert.Equal(PermissionCatalog.ServerUpdate, PermissionOn(nameof(ServerThirdPartyUpdatesController.Exclude)));
        Assert.Equal(PermissionCatalog.ServerUpdate, PermissionOn(nameof(ServerThirdPartyUpdatesController.Include)));
    }
}
