// Copyright ©2026 Scott Blomfield

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using RustArchon.Api.Administration;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="ThemePackageValidator"/> - in particular the zip-slip guard, since accepting a
/// malicious entry path here would mean this Api extracts an admin-uploaded zip's contents to wherever
/// the attacker's path says, not just under the intended theme's own object-storage prefix. See that
/// class's own remarks.
/// </summary>
public class ThemePackageValidatorTests
{
    /// <summary>Builds an in-memory zip from a set of entry name/content pairs - the only way to feed
    /// <see cref="ThemePackageValidator.Validate"/> a real <see cref="ZipArchive"/> without touching
    /// disk.</summary>
    private static ZipArchive BuildZip(params (string Name, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static readonly byte[] Bytes = "content"u8.ToArray();

    private static readonly byte[] ValidManifestBytes =
        """{"name":"Test Theme","version":"1.0.0"}"""u8.ToArray();

    private static ZipArchive ValidPackage(params (string Name, byte[] Content)[] extra) => BuildZip(
        [("manifest.json", ValidManifestBytes), ("theme.css", Bytes), .. extra]);

    [Fact]
    public void AcceptsAWellFormedPackage()
    {
        using var archive = ValidPackage(("images/hero.png", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Entries.Count);
        Assert.Contains(result.Entries, e => e.Path == "images/hero.png" && e.ContentType == "image/png");
        Assert.Contains(result.Entries, e => e.Path == "theme.css" && e.ContentType == "text/css");
        Assert.NotNull(result.Manifest);
        Assert.Equal("Test Theme", result.Manifest!.Name);
        Assert.Equal("1.0.0", result.Manifest.Version);
    }

    [Fact]
    public void RejectsAManifestThatIsNotValidJson()
    {
        using var archive = BuildZip(("manifest.json", Bytes), ("theme.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("manifest.json") && e.Contains("valid JSON"));
    }

    [Fact]
    public void RejectsAManifestMissingTheName()
    {
        var manifest = """{"version":"1.0.0"}"""u8.ToArray();
        using var archive = BuildZip(("manifest.json", manifest), ("theme.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("'name' is required"));
    }

    [Fact]
    public void RejectsAManifestMissingTheVersion()
    {
        var manifest = """{"name":"Test Theme"}"""u8.ToArray();
        using var archive = BuildZip(("manifest.json", manifest), ("theme.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("'version' is required"));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("one point oh")]
    public void RejectsAManifestWithAMalformedVersion(string version)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { name = "Test Theme", version });
        using var archive = BuildZip(("manifest.json", manifest), ("theme.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("doesn't look like a version number"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0+build.7")]
    public void AcceptsEveryReasonableVersionShape(string version)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { name = "Test Theme", version });
        using var archive = BuildZip(("manifest.json", manifest), ("theme.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.True(result.Success);
        Assert.Equal(version, result.Manifest!.Version);
    }

    [Fact]
    public void AcceptsAManifestWithFullOptionalMetadataAndPersistsAllOfIt()
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = "Test Theme",
            version = "2.1.0",
            description = "A test theme.",
            authorName = "Jane Doe",
            authorEmail = "jane@example.com",
            website = "https://example.com",
            updateUrl = "https://example.com/updates.json"
        });
        using var archive = BuildZip(("manifest.json", manifest), ("theme.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.True(result.Success);
        Assert.Equal("A test theme.", result.Manifest!.Description);
        Assert.Equal("Jane Doe", result.Manifest.AuthorName);
        Assert.Equal("jane@example.com", result.Manifest.AuthorEmail);
        Assert.Equal("https://example.com", result.Manifest.Website);
        Assert.Equal("https://example.com/updates.json", result.Manifest.UpdateUrl);
    }

    [Fact]
    public void RejectsAManifestWithAnInvalidAuthorEmail()
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new { name = "Test Theme", version = "1.0.0", authorEmail = "not-an-email" });
        using var archive = BuildZip(("manifest.json", manifest), ("theme.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("authorEmail") && e.Contains("valid email"));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    public void RejectsAManifestWithAnInvalidWebsiteUrl(string website)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { name = "Test Theme", version = "1.0.0", website });
        using var archive = BuildZip(("manifest.json", manifest), ("theme.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("website") && e.Contains("http"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("images/../../../secrets.json")]
    [InlineData("..\\..\\windows\\win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("images\\hero.png")]
    public void RejectsAZipSlipOrAbsoluteOrBackslashPath(string maliciousEntryName)
    {
        using var archive = BuildZip(
            ("manifest.json", Bytes), ("theme.css", Bytes), (maliciousEntryName, Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Errors, e => e.Contains(maliciousEntryName));
    }

    [Fact]
    public void AcceptsARootLevelImageAndFontWithNoSubfolderNeeded()
    {
        using var archive = ValidPackage(("hero.png", Bytes), ("brand.woff2", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.True(result.Success);
        Assert.Contains(result.Entries, e => e.Path == "hero.png");
        Assert.Contains(result.Entries, e => e.Path == "brand.woff2");
    }

    [Fact]
    public void RejectsAnImageNestedDeeperThanTheImagesFolder()
    {
        using var archive = ValidPackage(("images/sub/hero.png", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("images/sub/hero.png") && e.Contains("nested"));
    }

    [Fact]
    public void RejectsAFontPlacedInTheImagesFolder()
    {
        using var archive = ValidPackage(("images/font.woff", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("images/font.woff") && e.Contains("fonts"));
    }

    [Fact]
    public void RejectsAnImagePlacedInTheFontsFolder()
    {
        using var archive = ValidPackage(("fonts/hero.png", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("fonts/hero.png") && e.Contains("images"));
    }

    [Fact]
    public void RejectsAStrayCssFileOtherThanTheRequiredStylesheet()
    {
        using var archive = ValidPackage(("extra.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("extra.css") && e.Contains(ThemePackageValidator.StylesheetFileName));
    }

    [Fact]
    public void RejectsAStrayJsonFileOtherThanTheManifest()
    {
        using var archive = ValidPackage(("data.json", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("data.json") && e.Contains(ThemePackageValidator.ManifestFileName));
    }

    [Fact]
    public void RejectsADisallowedFileExtension()
    {
        using var archive = ValidPackage(("theme.js", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("theme.js") && e.Contains(".js"));
    }

    [Fact]
    public void RejectsAPackageMissingTheManifest()
    {
        using var archive = BuildZip(("theme.css", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("manifest.json"));
    }

    [Fact]
    public void RejectsAPackageMissingTheStylesheet()
    {
        using var archive = BuildZip(("manifest.json", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("theme.css"));
    }

    [Fact]
    public void RejectsAnEmptyPackage()
    {
        using var archive = BuildZip();

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("empty"));
    }

    [Fact]
    public void SkipsDirectoryEntriesRatherThanTreatingThemAsFiles()
    {
        using var archive = ValidPackage(("images/", []));

        var result = ThemePackageValidator.Validate(archive);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Entries, e => e.Path.EndsWith('/'));
    }

    [Fact]
    public void RejectsAFileOverThePerFileSizeLimit()
    {
        // One byte over the 8 MB per-file cap - large enough to trip CopyWithLimit's actual byte count
        // rather than trusting the zip's own declared entry length.
        var oversized = new byte[8 * 1024 * 1024 + 1];
        using var archive = ValidPackage(("images/huge.png", oversized));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("huge.png") && e.Contains("MB"));
    }

    [Theory]
    [InlineData("con.png")]
    [InlineData("nul.css")]
    [InlineData("images/lpt1.png")]
    public void RejectsAWindowsReservedNameSegment(string reservedPath)
    {
        using var archive = BuildZip(("manifest.json", Bytes), ("theme.css", Bytes), (reservedPath, Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("reserved"));
    }

    [Fact]
    public void RejectsAPackageWithTooManyFiles()
    {
        var many = Enumerable.Range(0, 201)
            .Select(i => ($"images/{i}.png", Bytes))
            .ToArray();
        using var archive = ValidPackage(many);

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("more than"));
    }

    [Fact]
    public void ReportsEveryBadEntryAtOnceRatherThanStoppingAtTheFirst()
    {
        using var archive = BuildZip(
            ("manifest.json", Bytes), ("theme.css", Bytes),
            ("theme.js", Bytes), ("../escape.png", Bytes));

        var result = ThemePackageValidator.Validate(archive);

        Assert.False(result.Success);
        Assert.Equal(2, result.Errors.Count);
    }
}
