// Copyright ©2026 Scott Blomfield

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using RustArchon.Api.Administration;
using RustArchon.Api.Data;
using RustArchon.Api.Infrastructure;
using RustArchon.Api.Infrastructure.ObjectStorage;
using RustArchon.Api.Infrastructure.ThemeUpdates;
using RustArchon.Api.Repositories;
using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="ThemeService"/> - the upload/activate/delete orchestration around
/// <see cref="ThemePackageValidator"/> (tested separately) and <see cref="IObjectStorage"/>.
/// </summary>
public class ThemeServiceTests
{
    private static MemoryStream BuildValidPackageZip()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var entryStream = archive.CreateEntry(name).Open();
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                entryStream.Write(bytes);
            }

            Add("manifest.json", """{"name":"My Theme","version":"1.0.0"}""");
            Add("theme.css", "body { color: red; }");
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>A distinct package fixture for the install tests, deliberately not sharing
    /// <see cref="BuildValidPackageZip"/>'s "My Theme"/1.0.0 identity - a downloaded package's own
    /// manifest is what becomes the installed row's <see cref="Theme.Version"/>, independent of whatever
    /// version the small update-check JSON reported, and these tests should demonstrate that rather than
    /// coincidentally line up.</summary>
    private static byte[] BuildUpdatePackageBytes(string version)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var entryStream = archive.CreateEntry(name).Open();
                var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                entryStream.Write(bytes);
            }

            Add("manifest.json", $$"""{"name":"Updated Theme","version":"{{version}}"}""");
            Add("theme.css", "body { color: blue; }");
        }

        return stream.ToArray();
    }

    private static ThemeService CreateService(
        out Mock<IThemeRepository> repository, out Mock<IObjectStorage> objectStorage,
        out Mock<IAppGenerationCache> appGeneration) =>
        CreateService(out repository, out objectStorage, out appGeneration, out _, out _);

    private static ThemeService CreateService(
        out Mock<IThemeRepository> repository, out Mock<IObjectStorage> objectStorage,
        out Mock<IAppGenerationCache> appGeneration, out Mock<IActiveThemeCache> activeThemeCache) =>
        CreateService(out repository, out objectStorage, out appGeneration, out activeThemeCache, out _);

    private static ThemeService CreateService(
        out Mock<IThemeRepository> repository, out Mock<IObjectStorage> objectStorage,
        out Mock<IAppGenerationCache> appGeneration, out Mock<IActiveThemeCache> activeThemeCache,
        out Mock<IThemeUpdateCheckClient> updateCheckClient)
    {
        repository = new Mock<IThemeRepository>();
        objectStorage = new Mock<IObjectStorage>();
        appGeneration = new Mock<IAppGenerationCache>();
        activeThemeCache = new Mock<IActiveThemeCache>();
        updateCheckClient = new Mock<IThemeUpdateCheckClient>();

        // AddAsync/UpdateAsync just hand back whatever was passed in - the real repository does audit
        // stamping and persistence, neither of which this orchestration logic depends on.
        repository.Setup(r => r.AddAsync(It.IsAny<Theme>())).ReturnsAsync((Theme t) => t);
        repository.Setup(r => r.UpdateAsync(It.IsAny<Theme>())).ReturnsAsync((Theme t) => t);

        return new ThemeService(
            repository.Object, objectStorage.Object, appGeneration.Object, activeThemeCache.Object,
            updateCheckClient.Object, TimeProvider.System);
    }

    [Fact]
    public async Task UploadWritesEveryValidatedEntryUnderTheThemesOwnPrefixAndSavesTheCatalogRow()
    {
        var service = CreateService(out var repository, out var objectStorage, out _);

        using var zip = BuildValidPackageZip();
        var result = await service.UploadAsync(zip);

        Assert.True(result.Success);
        Assert.NotNull(result.Theme);
        Assert.Equal("My Theme", result.Theme!.Name);
        Assert.Equal("1.0.0", result.Theme.Version);
        Assert.False(result.Theme.IsActive);
        Assert.Equal(ThemeSource.Uploaded, result.Theme.Source); // the default - see UploadAsync's own remarks
        Assert.Contains("manifest.json", result.Theme.AssetPaths);
        Assert.Contains("theme.css", result.Theme.AssetPaths);

        var expectedPrefix = $"themes/{result.Theme.Id:D}/";
        objectStorage.Verify(
            o => o.PutAsync(expectedPrefix + "manifest.json", It.IsAny<byte[]>(), "application/json", It.IsAny<CancellationToken>()),
            Times.Once);
        objectStorage.Verify(
            o => o.PutAsync(expectedPrefix + "theme.css", It.IsAny<byte[]>(), "text/css", It.IsAny<CancellationToken>()),
            Times.Once);
        repository.Verify(r => r.AddAsync(It.IsAny<Theme>()), Times.Once);
    }

    [Fact]
    public async Task UploadWritesNothingWhenValidationFails()
    {
        var service = CreateService(out var repository, out var objectStorage, out _);

        using var emptyZipStream = new MemoryStream();
        using (var archive = new ZipArchive(emptyZipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Deliberately empty - ThemePackageValidator rejects this outright.
        }
        emptyZipStream.Position = 0;

        var result = await service.UploadAsync(emptyZipStream);

        Assert.False(result.Success);
        Assert.Null(result.Theme);
        Assert.NotEmpty(result.Errors);
        objectStorage.Verify(o => o.PutAsync(
            It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.AddAsync(It.IsAny<Theme>()), Times.Never);
    }

    [Fact]
    public async Task ActivatingClearsWhicheverThemeWasActiveBeforeInASeparateSave()
    {
        var service = CreateService(out var repository, out _, out var appGeneration, out var activeThemeCache);

        var previouslyActive = new Theme { Id = Guid.NewGuid(), Name = "Old", IsActive = true };
        var toActivate = new Theme { Id = Guid.NewGuid(), Name = "New", IsActive = false };

        repository.Setup(r => r.GetByIdAsync(toActivate.Id, null)).ReturnsAsync(toActivate);
        repository.Setup(r => r.GetActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(previouslyActive);

        var activated = await service.ActivateAsync(toActivate.Id);

        Assert.NotNull(activated);
        Assert.True(activated!.IsActive);
        Assert.False(previouslyActive.IsActive);

        repository.Verify(r => r.UpdateAsync(previouslyActive), Times.Once);
        repository.Verify(r => r.UpdateAsync(toActivate), Times.Once);
        appGeneration.Verify(a => a.BumpAsync(), Times.Once);
        activeThemeCache.Verify(a => a.SetActiveAsync(toActivate.Id), Times.Once);
    }

    [Fact]
    public async Task ActivatingWhenNothingElseWasActiveJustSetsTheOneRow()
    {
        var service = CreateService(out var repository, out _, out var appGeneration, out var activeThemeCache);

        var toActivate = new Theme { Id = Guid.NewGuid(), Name = "Only", IsActive = false };
        repository.Setup(r => r.GetByIdAsync(toActivate.Id, null)).ReturnsAsync(toActivate);
        repository.Setup(r => r.GetActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Theme?)null);

        var activated = await service.ActivateAsync(toActivate.Id);

        Assert.True(activated!.IsActive);
        repository.Verify(r => r.UpdateAsync(It.IsAny<Theme>()), Times.Once);
        appGeneration.Verify(a => a.BumpAsync(), Times.Once);
        activeThemeCache.Verify(a => a.SetActiveAsync(toActivate.Id), Times.Once);
    }

    [Fact]
    public async Task ActivatingAnUnknownIdReturnsNullAndBumpsNothing()
    {
        var service = CreateService(out var repository, out _, out var appGeneration);
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((Theme?)null);

        var activated = await service.ActivateAsync(Guid.NewGuid());

        Assert.Null(activated);
        appGeneration.Verify(a => a.BumpAsync(), Times.Never);
    }

    [Fact]
    public async Task DeletingTheActiveThemeThrowsAndTouchesNothing()
    {
        var service = CreateService(out var repository, out var objectStorage, out _);
        var active = new Theme { Id = Guid.NewGuid(), Name = "Active", IsActive = true };
        repository.Setup(r => r.GetByIdAsync(active.Id, null)).ReturnsAsync(active);

        await Assert.ThrowsAsync<ThemeInUseException>(() => service.DeleteAsync(active.Id));

        objectStorage.Verify(o => o.DeleteByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeletingAnInactiveThemeRemovesItsObjectsAndTheCatalogRow()
    {
        var service = CreateService(out var repository, out var objectStorage, out _);
        var theme = new Theme { Id = Guid.NewGuid(), Name = "Old", IsActive = false };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);
        repository.Setup(r => r.DeleteAsync(theme.Id)).ReturnsAsync(true);

        var deleted = await service.DeleteAsync(theme.Id);

        Assert.True(deleted);
        objectStorage.Verify(o => o.DeleteByPrefixAsync($"themes/{theme.Id:D}/", It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.DeleteAsync(theme.Id), Times.Once);
    }

    [Fact]
    public async Task DeletingAnUnknownIdReturnsFalse()
    {
        var service = CreateService(out var repository, out var objectStorage, out _);
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((Theme?)null);

        var deleted = await service.DeleteAsync(Guid.NewGuid());

        Assert.False(deleted);
        objectStorage.Verify(o => o.DeleteByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadingWithAnExplicitSourceRecordsItOnTheRow()
    {
        var service = CreateService(out _, out _, out _);

        using var zip = BuildValidPackageZip();
        // ThemesController.Build is the only real caller that ever passes this - see UploadAsync's own
        // remarks on why every other caller (a manual .zip upload, DefaultThemeSeeder, a downloaded
        // update) leaves it at the Uploaded default instead.
        var result = await service.UploadAsync(zip, CancellationToken.None, ThemeSource.Built);

        Assert.True(result.Success);
        Assert.Equal(ThemeSource.Built, result.Theme!.Source);
    }

    [Fact]
    public async Task GetAssetReturnsNullForAPathThatIsNotOneOfTheThemesOwnAssets()
    {
        var service = CreateService(out var repository, out var objectStorage, out _);
        var theme = new Theme { Id = Guid.NewGuid(), Name = "Theme", AssetPaths = ["theme.css"] };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);

        // Not one of AssetPaths - must never reach IObjectStorage at all, regardless of whether an
        // object happens to exist under that key (defense in depth against a caller-supplied path that
        // was never actually part of this theme's own package).
        var asset = await service.GetAssetAsync(theme.Id, "images/not-mine.png");

        Assert.Null(asset);
        objectStorage.Verify(
            o => o.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAssetReturnsNullForAnUnknownThemeId()
    {
        var service = CreateService(out var repository, out _, out _);
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((Theme?)null);

        var asset = await service.GetAssetAsync(Guid.NewGuid(), "theme.css");

        Assert.Null(asset);
    }

    [Fact]
    public async Task GetAssetReadsTheExactObjectStorageKeyForAKnownAssetPath()
    {
        var service = CreateService(out var repository, out var objectStorage, out _);
        var theme = new Theme { Id = Guid.NewGuid(), Name = "Theme", AssetPaths = ["images/hero.png"] };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);
        var expected = new ObjectContent([1, 2, 3], "image/png");
        objectStorage
            .Setup(o => o.GetAsync($"themes/{theme.Id:D}/images/hero.png", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var asset = await service.GetAssetAsync(theme.Id, "images/hero.png");

        Assert.Same(expected, asset);
    }

    [Fact]
    public async Task ReplacingAnUnknownIdReturnsNull()
    {
        var service = CreateService(out var repository, out _, out _);
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((Theme?)null);

        using var zip = BuildValidPackageZip();
        var result = await service.ReplaceAsync(Guid.NewGuid(), zip);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReplacingWithAnInvalidPackageLeavesTheExistingThemeUntouched()
    {
        var service = CreateService(out var repository, out var objectStorage, out _);
        var theme = new Theme { Id = Guid.NewGuid(), Name = "Old", Version = "1.0.0" };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);

        using var emptyZipStream = new MemoryStream();
        using (var archive = new ZipArchive(emptyZipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Deliberately empty - ThemePackageValidator rejects this outright.
        }
        emptyZipStream.Position = 0;

        var result = await service.ReplaceAsync(theme.Id, emptyZipStream);

        Assert.False(result!.Success);
        Assert.Equal("Old", theme.Name); // untouched
        objectStorage.Verify(o => o.PutAsync(
            It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.UpdateAsync(It.IsAny<Theme>()), Times.Never);
    }

    [Fact]
    public async Task ReplacingWritesNewContentAndRemovesOnlyTheAssetsNoLongerPresent()
    {
        var service = CreateService(out var repository, out var objectStorage, out _);
        var theme = new Theme
        {
            Id = Guid.NewGuid(), Name = "Old", Version = "1.0.0",
            AssetPaths = ["manifest.json", "theme.css", "images/old-logo.png"]
        };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);

        // The replacement package - built via BuildUpdatePackageBytes - has no images/old-logo.png, so
        // that one file should be cleaned up; manifest.json/theme.css get overwritten in place instead
        // of removed-then-recreated.
        using var newPackage = new MemoryStream(BuildUpdatePackageBytes("1.1.0"));

        var result = await service.ReplaceAsync(theme.Id, newPackage);

        Assert.True(result!.Success);
        Assert.Same(theme, result.Theme);
        Assert.Equal("Updated Theme", theme.Name);
        Assert.Equal("1.1.0", theme.Version);
        Assert.Equal(["manifest.json", "theme.css"], theme.AssetPaths);

        var prefix = $"themes/{theme.Id:D}/";
        objectStorage.Verify(o => o.PutAsync(prefix + "manifest.json", It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        objectStorage.Verify(o => o.PutAsync(prefix + "theme.css", It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        objectStorage.Verify(o => o.DeleteAsync(prefix + "images/old-logo.png", It.IsAny<CancellationToken>()), Times.Once);
        objectStorage.Verify(o => o.DeleteByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.AddAsync(It.IsAny<Theme>()), Times.Never);
    }

    [Fact]
    public async Task ReplacingOnlyBumpsAppGenerationWhenTheThemeIsActive()
    {
        var service = CreateService(out var repository, out _, out var appGeneration);
        var inactive = new Theme { Id = Guid.NewGuid(), Name = "Old", Version = "1.0.0", IsActive = false };
        repository.Setup(r => r.GetByIdAsync(inactive.Id, null)).ReturnsAsync(inactive);

        using (var zip = new MemoryStream(BuildUpdatePackageBytes("1.1.0")))
        {
            await service.ReplaceAsync(inactive.Id, zip);
        }

        appGeneration.Verify(a => a.BumpAsync(), Times.Never);

        var active = new Theme { Id = Guid.NewGuid(), Name = "Old", Version = "1.0.0", IsActive = true };
        repository.Setup(r => r.GetByIdAsync(active.Id, null)).ReturnsAsync(active);

        using (var zip = new MemoryStream(BuildUpdatePackageBytes("1.1.0")))
        {
            await service.ReplaceAsync(active.Id, zip);
        }

        appGeneration.Verify(a => a.BumpAsync(), Times.Once);
    }

    [Fact]
    public async Task ReplacingWithAnExplicitSourceOverridesItWhileOmittingItLeavesTheExistingValue()
    {
        var service = CreateService(out var repository, out _, out _);
        var theme = new Theme { Id = Guid.NewGuid(), Name = "Old", Version = "1.0.0", Source = ThemeSource.Uploaded };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);

        using (var zip = new MemoryStream(BuildUpdatePackageBytes("1.1.0")))
        {
            await service.ReplaceAsync(theme.Id, zip, CancellationToken.None, ThemeSource.Built);
        }

        Assert.Equal(ThemeSource.Built, theme.Source);

        using (var zip = new MemoryStream(BuildUpdatePackageBytes("1.2.0")))
        {
            // No explicit source this time (InstallUpdateAsync's own usage) - stays whatever it already
            // was, not reset back to the Uploaded default.
            await service.ReplaceAsync(theme.Id, zip);
        }

        Assert.Equal(ThemeSource.Built, theme.Source);
    }

    [Fact]
    public async Task CheckingForUpdateOnAThemeWithNoUpdateUrlIsANoOp()
    {
        var service = CreateService(out var repository, out _, out _, out _, out var updateCheckClient);
        var theme = new Theme { Id = Guid.NewGuid(), Name = "No Feed", Version = "1.0.0", UpdateUrl = null };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);

        var result = await service.CheckForUpdateAsync(theme.Id);

        Assert.Same(theme, result);
        updateCheckClient.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(r => r.UpdateAsync(It.IsAny<Theme>()), Times.Never);
    }

    [Fact]
    public async Task CheckingForUpdateOnAnUnknownIdReturnsNull()
    {
        var service = CreateService(out var repository, out _, out _, out _, out _);
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((Theme?)null);

        var result = await service.CheckForUpdateAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task ASuccessfulCheckRecordsTheReportedVersionAndClearsAnyPriorError()
    {
        var service = CreateService(out var repository, out _, out _, out _, out var updateCheckClient);
        var theme = new Theme
        {
            Id = Guid.NewGuid(), Name = "Fed", Version = "1.0.0",
            UpdateUrl = "https://example.com/update.json", LastUpdateCheckError = "stale error"
        };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);
        updateCheckClient.Setup(c => c.CheckAsync(theme.UpdateUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemeUpdateCheckOutcome.Succeeded("1.1.0", "https://example.com/theme-1.1.0.zip"));

        var result = await service.CheckForUpdateAsync(theme.Id);

        Assert.Equal("1.1.0", result!.LatestKnownVersion);
        Assert.Equal("https://example.com/theme-1.1.0.zip", result.LatestDownloadPackageUrl);
        Assert.Null(result.LastUpdateCheckError);
        Assert.NotNull(result.LastUpdateCheckOn);
        repository.Verify(r => r.UpdateAsync(theme), Times.Once);
    }

    [Fact]
    public async Task AFailedCheckRecordsTheErrorButKeepsWhateverAnEarlierSuccessfulCheckFound()
    {
        var service = CreateService(out var repository, out _, out _, out _, out var updateCheckClient);
        var theme = new Theme
        {
            Id = Guid.NewGuid(), Name = "Fed", Version = "1.0.0", UpdateUrl = "https://example.com/update.json",
            LatestKnownVersion = "1.1.0", LatestDownloadPackageUrl = "https://example.com/theme-1.1.0.zip"
        };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);
        updateCheckClient.Setup(c => c.CheckAsync(theme.UpdateUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemeUpdateCheckOutcome.Failed("Could not reach the update URL."));

        var result = await service.CheckForUpdateAsync(theme.Id);

        Assert.Equal("Could not reach the update URL.", result!.LastUpdateCheckError);
        // Unchanged - a transient failure doesn't erase an already-known update.
        Assert.Equal("1.1.0", result.LatestKnownVersion);
        Assert.Equal("https://example.com/theme-1.1.0.zip", result.LatestDownloadPackageUrl);
    }

    [Fact]
    public async Task InstallingAnUpdateOnAnUnknownIdReturnsNull()
    {
        var service = CreateService(out var repository, out _, out _, out _, out _);
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), null)).ReturnsAsync((Theme?)null);

        var result = await service.InstallUpdateAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task InstallingAnUpdateOnAThemeWithNoUpdateUrlFails()
    {
        var service = CreateService(out var repository, out _, out _, out _, out var updateCheckClient);
        var theme = new Theme { Id = Guid.NewGuid(), Name = "No Feed", Version = "1.0.0", UpdateUrl = null };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);

        var result = await service.InstallUpdateAsync(theme.Id);

        Assert.False(result!.Success);
        updateCheckClient.Verify(c => c.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallingWhenTheFreshCheckFindsNothingNewerFails()
    {
        var service = CreateService(out var repository, out _, out _, out _, out var updateCheckClient);
        var theme = new Theme
        {
            Id = Guid.NewGuid(), Name = "Up To Date", Version = "1.0.0", UpdateUrl = "https://example.com/update.json"
        };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);
        updateCheckClient.Setup(c => c.CheckAsync(theme.UpdateUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemeUpdateCheckOutcome.Succeeded("1.0.0", "https://example.com/theme-1.0.0.zip"));

        var result = await service.InstallUpdateAsync(theme.Id);

        Assert.False(result!.Success);
        updateCheckClient.Verify(
            c => c.DownloadPackageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallingWhenTheFeedNamesNoPackageUrlFails()
    {
        var service = CreateService(out var repository, out _, out _, out _, out var updateCheckClient);
        var theme = new Theme
        {
            Id = Guid.NewGuid(), Name = "Version Only Feed", Version = "1.0.0",
            UpdateUrl = "https://example.com/update.json"
        };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);
        updateCheckClient.Setup(c => c.CheckAsync(theme.UpdateUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemeUpdateCheckOutcome.Succeeded("1.1.0", packageUrl: null));

        var result = await service.InstallUpdateAsync(theme.Id);

        Assert.False(result!.Success);
    }

    [Fact]
    public async Task InstallingSuccessfullyReplacesTheThemesContentInPlace()
    {
        var service = CreateService(out var repository, out var objectStorage, out _, out _, out var updateCheckClient);
        var current = new Theme
        {
            Id = Guid.NewGuid(), Name = "Old", Version = "1.0.0", UpdateUrl = "https://example.com/update.json",
            AssetPaths = ["manifest.json", "theme.css"], IsActive = false
        };
        repository.Setup(r => r.GetByIdAsync(current.Id, null)).ReturnsAsync(current);
        updateCheckClient.Setup(c => c.CheckAsync(current.UpdateUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemeUpdateCheckOutcome.Succeeded("1.1.0", "https://example.com/theme-1.1.0.zip"));
        updateCheckClient
            .Setup(c => c.DownloadPackageAsync("https://example.com/theme-1.1.0.zip", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemePackageDownloadOutcome.Succeeded(BuildUpdatePackageBytes("1.1.0")));

        var result = await service.InstallUpdateAsync(current.Id);

        Assert.True(result!.Success);
        Assert.Same(current, result.Theme); // the exact same row - no new theme created
        Assert.Equal("Updated Theme", result.Theme!.Name);
        Assert.Equal("1.1.0", result.Theme.Version); // from the downloaded package's own manifest
        Assert.False(result.Theme.IsActive); // untouched - wasn't active before, still isn't
        repository.Verify(r => r.AddAsync(It.IsAny<Theme>()), Times.Never);
        repository.Verify(r => r.UpdateAsync(current), Times.AtLeastOnce);
    }

    [Fact]
    public async Task InstallingOnAnActiveThemeBumpsAppGenerationSinceItsChromeJustChanged()
    {
        var service = CreateService(out var repository, out _, out var appGeneration, out _, out var updateCheckClient);
        var current = new Theme
        {
            Id = Guid.NewGuid(), Name = "Old", Version = "1.0.0", UpdateUrl = "https://example.com/update.json",
            AssetPaths = ["manifest.json", "theme.css"], IsActive = true
        };
        repository.Setup(r => r.GetByIdAsync(current.Id, null)).ReturnsAsync(current);
        updateCheckClient.Setup(c => c.CheckAsync(current.UpdateUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemeUpdateCheckOutcome.Succeeded("1.1.0", "https://example.com/theme-1.1.0.zip"));
        updateCheckClient
            .Setup(c => c.DownloadPackageAsync("https://example.com/theme-1.1.0.zip", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemePackageDownloadOutcome.Succeeded(BuildUpdatePackageBytes("1.1.0")));

        var result = await service.InstallUpdateAsync(current.Id);

        Assert.True(result!.Success);
        Assert.Same(current, result.Theme);
        Assert.True(result.Theme!.IsActive); // untouched - was active before, still is - same row throughout
        appGeneration.Verify(a => a.BumpAsync(), Times.Once);
    }

    [Fact]
    public async Task AFailedDownloadDuringInstallFailsWithoutUploadingAnything()
    {
        var service = CreateService(out var repository, out _, out _, out _, out var updateCheckClient);
        var theme = new Theme
        {
            Id = Guid.NewGuid(), Name = "Old", Version = "1.0.0", UpdateUrl = "https://example.com/update.json"
        };
        repository.Setup(r => r.GetByIdAsync(theme.Id, null)).ReturnsAsync(theme);
        updateCheckClient.Setup(c => c.CheckAsync(theme.UpdateUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemeUpdateCheckOutcome.Succeeded("1.1.0", "https://example.com/theme-1.1.0.zip"));
        updateCheckClient
            .Setup(c => c.DownloadPackageAsync("https://example.com/theme-1.1.0.zip", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThemePackageDownloadOutcome.Failed("The package URL points to an address that isn't allowed."));

        var result = await service.InstallUpdateAsync(theme.Id);

        Assert.False(result!.Success);
        Assert.Contains("isn't allowed", result.Errors.Single());
        repository.Verify(r => r.AddAsync(It.IsAny<Theme>()), Times.Never);
    }
}
