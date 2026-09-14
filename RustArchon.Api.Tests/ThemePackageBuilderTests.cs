// Copyright ©2026 Scott Blomfield

using System.IO.Compression;
using System.Linq;
using Microsoft.AspNetCore.Http;
using RustArchon.Api.Administration;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="ThemePackageBuilder"/> - the theme-builder UI's "assemble the pieces into a
/// .zip" step. Deliberately light on manifest-content assertions of its own: see
/// <see cref="RoundTripsThroughThemePackageValidatorForAWellFormedRequest"/> and
/// <see cref="LeavesManifestContentValidationEntirelyToThemePackageValidator"/>, which both confirm the
/// actual design claim - that <see cref="ThemePackageValidator"/>, not this class, is the single source
/// of truth for whether a built theme's content is valid.
/// </summary>
public class ThemePackageBuilderTests
{
    private static IFormFile MakeFile(string fileName, string content = "content") =>
        new FormFile(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), 0, content.Length, "file", fileName);

    private static ZipArchive OpenArchive(MemoryStream package)
    {
        package.Position = 0;
        return new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
    }

    [Fact]
    public void BuildsAWellFormedPackageFromMinimalFields()
    {
        var request = new ThemeBuildRequest { Name = "My Theme", Version = "1.0.0", Css = "body { color: red; }" };

        var result = ThemePackageBuilder.Build(request);

        Assert.True(result.Success);
        using var archive = OpenArchive(result.Package!);
        Assert.NotNull(archive.GetEntry(ThemePackageValidator.ManifestFileName));
        Assert.NotNull(archive.GetEntry(ThemePackageValidator.StylesheetFileName));
    }

    [Fact]
    public void PlacesImagesAndFontsUnderTheirOwnFolders()
    {
        var request = new ThemeBuildRequest
        {
            Name = "My Theme", Version = "1.0.0", Css = "body { }",
            Images = [MakeFile("hero.png")],
            Fonts = [MakeFile("body.woff2")]
        };

        var result = ThemePackageBuilder.Build(request);

        Assert.True(result.Success);
        using var archive = OpenArchive(result.Package!);
        Assert.NotNull(archive.GetEntry("images/hero.png"));
        Assert.NotNull(archive.GetEntry("fonts/body.woff2"));
    }

    [Theory]
    [InlineData("../../evil.png", "evil.png")]
    [InlineData("sub/dir/name.png", "name.png")]
    [InlineData(@"C:\Windows\name.png", "name.png")]
    public void SanitizesADirectoryComponentOutOfAnUploadedFileName(string rawName, string expectedName)
    {
        var request = new ThemeBuildRequest
        {
            Name = "My Theme", Version = "1.0.0", Css = "body { }",
            Images = [MakeFile(rawName)]
        };

        var result = ThemePackageBuilder.Build(request);

        Assert.True(result.Success);
        using var archive = OpenArchive(result.Package!);
        Assert.NotNull(archive.GetEntry($"images/{expectedName}"));
        // The one thing that would actually be dangerous here - an entry path escaping images/ entirely -
        // never happens regardless of how hostile the original file name was.
        Assert.All(archive.Entries, e => Assert.StartsWith(
            e.FullName is ThemePackageValidator.ManifestFileName or ThemePackageValidator.StylesheetFileName ? "" : "images/",
            e.FullName));
    }

    [Fact]
    public void RejectsTwoImagesThatSanitizeToTheSameName()
    {
        var request = new ThemeBuildRequest
        {
            Name = "My Theme", Version = "1.0.0", Css = "body { }",
            Images = [MakeFile("logo.png"), MakeFile("branding/logo.png")]
        };

        var result = ThemePackageBuilder.Build(request);

        Assert.False(result.Success);
        Assert.Null(result.Package);
        Assert.Contains(result.Errors, e => e.Contains("logo.png"));
    }

    [Fact]
    public void RejectsAFileWithNoUsableName()
    {
        var request = new ThemeBuildRequest
        {
            Name = "My Theme", Version = "1.0.0", Css = "body { }",
            Fonts = [MakeFile("")]
        };

        var result = ThemePackageBuilder.Build(request);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("font"));
    }

    [Fact]
    public void RoundTripsThroughThemePackageValidatorForAWellFormedRequest()
    {
        var request = new ThemeBuildRequest
        {
            Name = "My Theme", Version = "1.2.3", Description = "A lovely theme.",
            AuthorName = "Scott", AuthorEmail = "scott@example.com", Website = "https://example.com",
            Css = "body { color: blue; }",
            Images = [MakeFile("hero.png")],
            Fonts = [MakeFile("body.woff2")]
        };

        var built = ThemePackageBuilder.Build(request);
        Assert.True(built.Success);

        using var archive = OpenArchive(built.Package!);
        var validated = ThemePackageValidator.Validate(archive);

        Assert.True(validated.Success);
        Assert.Equal("My Theme", validated.Manifest!.Name);
        Assert.Equal("1.2.3", validated.Manifest.Version);
        Assert.Equal("A lovely theme.", validated.Manifest.Description);
        Assert.Contains("images/hero.png", validated.Entries.Select(e => e.Path));
        Assert.Contains("fonts/body.woff2", validated.Entries.Select(e => e.Path));
    }

    [Fact]
    public void LeavesManifestContentValidationEntirelyToThemePackageValidator()
    {
        // Deliberately missing Version - ThemePackageBuilder itself has no opinion on this; it should
        // build a perfectly well-formed zip whose manifest.json just happens to be missing a required
        // field, exactly as if an admin had hand-authored one that way.
        var request = new ThemeBuildRequest { Name = "My Theme", Version = "", Css = "body { }" };

        var built = ThemePackageBuilder.Build(request);
        Assert.True(built.Success);

        using var archive = OpenArchive(built.Package!);
        var validated = ThemePackageValidator.Validate(archive);

        Assert.False(validated.Success);
        Assert.Contains(validated.Errors, e => e.Contains("version") && e.Contains("required"));
    }
}
