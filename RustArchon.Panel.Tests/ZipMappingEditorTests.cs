// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using RustArchon.Panel.Components.Pages.Servers;
using RustArchon.Panel.Localization;
using RustArchon.Shared.PluginZips;

namespace RustArchon.Panel.Tests;

/// <summary>
/// The page where a person says where a plugin's zip archive goes: the archive as its folders, a destination (or skip) for each folder and file, and a live
/// preview from the same rules the Api and the server's plugin decide with. Apply is offered only for a set of instructions that would work.
/// </summary>
public class ZipMappingEditorTests : BunitContext
{
    private static readonly IReadOnlyList<ZipEntryInfo> Archive =
    [
        new("en/plugins/test.cs", 100), new("en/configs/test.json", 20), new("en/images/test/one.jpg", 1000), new("en/images/test/two.jpg", 2000),
        new("ru/plugins/test.cs", 110), new("ru/configs/test.json", 21), new("ru/images/test/one.jpg", 1001), new("ru/images/test/two.jpg", 2001)
    ];

    private (List<ZipMappingRule> Rules, bool Save)? _applied;
    private int _cancelled;

    public ZipMappingEditorTests()
    {
        var localizer = new Mock<IStringLocalizer<SharedResource>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string key, object[] args) => new LocalizedString(key, string.Format(key, args)));
        Services.AddSingleton(localizer.Object);
    }

    private IRenderedComponent<ZipMappingEditor> Render(
        IReadOnlyList<ZipEntryInfo>? files = null, IReadOnlyList<ZipMappingRule>? initial = null, IReadOnlyList<string>? uncovered = null, bool busy = false) =>
        Render<ZipMappingEditor>(p => p
            .Add(c => c.Files, files ?? Archive)
            .Add(c => c.InitialRules, initial ?? [])
            .Add(c => c.UncoveredPaths, uncovered ?? [])
            .Add(c => c.Busy, busy)
            .Add(c => c.OnApply, (args) => { _applied = args; })
            .Add(c => c.OnCancel, () => { _cancelled++; }));

    private static IElement Row(IRenderedComponent<ZipMappingEditor> cut, string path) => cut.Find($"[data-testid=zip-row][data-path='{path}']");

    private static void SetRole(IRenderedComponent<ZipMappingEditor> cut, string path, string role) =>
        Row(cut, path).QuerySelector("[data-testid=zip-role]")!.Change(role);

    private static string Result(IRenderedComponent<ZipMappingEditor> cut, string path) => Row(cut, path).QuerySelector("[data-testid=zip-result]")!.TextContent.Trim();

    private static void MapEnglishAndSkipRussian(IRenderedComponent<ZipMappingEditor> cut)
    {
        SetRole(cut, "en/plugins/", "plugins");
        SetRole(cut, "en/configs/", "config");
        SetRole(cut, "en/images/", "data");
        SetRole(cut, "ru/", "skip");
    }

    private static bool ApplyDisabled(IRenderedComponent<ZipMappingEditor> cut) => cut.Find("[data-testid=zip-apply]").HasAttribute("disabled");

    // ---- the tree ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheArchiveIsShownAsItsFoldersAndFiles()
    {
        var cut = Render();

        var topLevel = cut.FindAll("[data-testid=zip-row][data-folder=yes]").Select(r => r.GetAttribute("data-path")).Where(p => p!.Count(c => c == '/') == 1).ToList();

        Assert.Equal(["en/", "ru/"], topLevel);
    }

    [Fact]
    public void SmallFoldersStartOpenSoTheFilesCanBeSeen()
    {
        var cut = Render();

        var paths = cut.FindAll("[data-testid=zip-row]").Select(r => r.GetAttribute("data-path")).ToList();

        Assert.Contains("en/plugins/", paths);
        Assert.Contains("en/plugins/test.cs", paths);
        Assert.Contains("en/images/test/one.jpg", paths);
    }

    [Fact]
    public void FoldersComeBeforeFilesAndEachFolderSaysHowManyFilesAreInIt()
    {
        var cut = Render([new("b.txt", 1), new("a/x.txt", 1), new("a/y.txt", 1)]);

        var paths = cut.FindAll("[data-testid=zip-row]").Select(r => r.GetAttribute("data-path")).ToList();

        Assert.Equal(["a/", "a/x.txt", "a/y.txt", "b.txt"], paths);
        Assert.Contains("(2)", Row(cut, "a/").TextContent);
    }

    [Fact]
    public void ABigFolderStartsFoldedAwayAndCanBeOpened()
    {
        var files = Enumerable.Range(0, 20).Select(i => new ZipEntryInfo($"big/f{i}.txt", 1)).ToList();
        var cut = Render(files);
        Assert.Single(cut.FindAll("[data-testid=zip-row]"));

        Row(cut, "big/").QuerySelector("[data-testid=zip-toggle]")!.Click();

        Assert.Equal(21, cut.FindAll("[data-testid=zip-row]").Count);
    }

    [Fact]
    public void FoldingAFolderHidesWhatIsInIt()
    {
        var cut = Render();

        Row(cut, "ru/").QuerySelector("[data-testid=zip-toggle]")!.Click();

        Assert.DoesNotContain(cut.FindAll("[data-testid=zip-row]"), r => r.GetAttribute("data-path")!.StartsWith("ru/") && r.GetAttribute("data-path") != "ru/");
    }

    // ---- the example: map English, skip Russian -----------------------------------------------------------------------------------

    [Fact]
    public void NothingIsAssignedAtFirstSoNothingCanBeApplied()
    {
        var cut = Render();

        Assert.True(ApplyDisabled(cut));
        Assert.Equal("no instruction", Result(cut, "en/plugins/test.cs"));
        Assert.NotEmpty(cut.FindAll("[data-testid=zip-problems] li"));
    }

    [Fact]
    public void MappingEnglishAndSkippingRussianShowsWhereEveryFileLandsAndAllowsApply()
    {
        var cut = Render();

        MapEnglishAndSkipRussian(cut);

        Assert.Equal("→ plugins/test.cs", Result(cut, "en/plugins/test.cs"));
        Assert.Equal("→ config/test.json", Result(cut, "en/configs/test.json"));
        Assert.Equal("→ data/test/one.jpg", Result(cut, "en/images/test/one.jpg"));      // the structure below the mapped folder is kept
        Assert.Equal("→ data/test/two.jpg", Result(cut, "en/images/test/two.jpg"));
        Assert.Equal("skipped", Result(cut, "ru/plugins/test.cs"));
        Assert.Equal("skipped", Result(cut, "ru/images/test/one.jpg"));
        Assert.Empty(cut.FindAll("[data-testid=zip-problems]"));
        Assert.False(ApplyDisabled(cut));
    }

    [Fact]
    public void AFolderRowSummarisesWhatBecomesOfItsFiles()
    {
        var cut = Render();
        SetRole(cut, "en/", "data");

        Assert.Equal("4 installed, 0 skipped, 0 without an instruction", Result(cut, "en/"));
        Assert.Equal("0 installed, 0 skipped, 4 without an instruction", Result(cut, "ru/"));
    }

    [Fact]
    public void ApplyHandsOverExactlyTheFolderRulesThatWereGiven()
    {
        var cut = Render();
        MapEnglishAndSkipRussian(cut);

        cut.Find("[data-testid=zip-apply]").Click();

        Assert.NotNull(_applied);
        Assert.Equal(
            [("en/configs/", "config"), ("en/images/", "data"), ("en/plugins/", "plugins"), ("ru/", "skip")],
            _applied.Value.Rules.Select(r => (r.Source, r.Role)).Order());
        Assert.All(_applied.Value.Rules, r => Assert.True(r.IsFolder));
        Assert.False(_applied.Value.Save);
    }

    [Fact]
    public void TheChoiceToSaveTheInstructionsIsPassedOn()
    {
        var cut = Render();
        MapEnglishAndSkipRussian(cut);

        cut.Find("[data-testid=zip-save]").Change(true);
        cut.Find("[data-testid=zip-apply]").Click();

        Assert.True(_applied!.Value.Save);
    }

    [Fact]
    public void TheSaveChoiceIsWordedAsTheOwnerRequiredItsConditionStated()
    {
        var cut = Render();

        Assert.Contains("provided that future updates contain only the same files in the same structure", cut.Find("label[for=zip-save]").TextContent);
    }

    [Fact]
    public void ApplyDoesNothingWhileTheInstructionsAreIncomplete()
    {
        var cut = Render();
        SetRole(cut, "en/", "data");

        cut.Find("[data-testid=zip-apply]").Click();

        Assert.Null(_applied);
    }

    [Fact]
    public void ApplyIsOffWhileARequestIsUnderWay()
    {
        var cut = Render(busy: true);
        MapEnglishAndSkipRussian(cut);

        Assert.True(ApplyDisabled(cut));
    }

    [Fact]
    public void CancelSaysSo()
    {
        var cut = Render();

        cut.Find("[data-testid=zip-cancel]").Click();

        Assert.Equal(1, _cancelled);
        Assert.Null(_applied);
    }

    // ---- file-level exceptions ------------------------------------------------------------------------------------------------------

    [Fact]
    public void OneFileCanBeSkippedInsideAFolderThatIsOtherwiseMapped()
    {
        var cut = Render();
        MapEnglishAndSkipRussian(cut);

        SetRole(cut, "en/images/test/two.jpg", "skip");

        Assert.Equal("skipped", Result(cut, "en/images/test/two.jpg"));
        Assert.Equal("→ data/test/one.jpg", Result(cut, "en/images/test/one.jpg"));
        Assert.False(ApplyDisabled(cut));
        cut.Find("[data-testid=zip-apply]").Click();
        var fileRule = Assert.Single(_applied!.Value.Rules, r => !r.IsFolder);
        Assert.Equal(("en/images/test/two.jpg", "skip"), (fileRule.Source, fileRule.Role));
    }

    [Fact]
    public void OneFileCanBeSentSomewhereElse()
    {
        var cut = Render();
        MapEnglishAndSkipRussian(cut);

        SetRole(cut, "en/configs/test.json", "data");

        Assert.Equal("→ data/test.json", Result(cut, "en/configs/test.json"));
    }

    [Fact]
    public void ClearingAFilesChoiceReturnsItToTheFolderItIsIn()
    {
        var cut = Render();
        MapEnglishAndSkipRussian(cut);
        SetRole(cut, "en/images/test/two.jpg", "skip");

        SetRole(cut, "en/images/test/two.jpg", "");

        Assert.Equal("→ data/test/two.jpg", Result(cut, "en/images/test/two.jpg"));
    }

    // ---- destination folder and keeping what is there -----------------------------------------------------------------------------

    [Fact]
    public void AFolderInsideTheDestinationChangesWhereFilesLand()
    {
        var cut = Render();
        MapEnglishAndSkipRussian(cut);

        Row(cut, "en/images/").QuerySelector("[data-testid=zip-sub]")!.Change("pictures");

        Assert.Equal("→ data/pictures/test/one.jpg", Result(cut, "en/images/test/one.jpg"));
    }

    [Fact]
    public void ASkippedFolderOffersNoDestinationFolderOrKeepChoice()
    {
        var cut = Render();
        SetRole(cut, "ru/", "skip");

        Assert.Empty(Row(cut, "ru/").QuerySelectorAll("[data-testid=zip-sub]"));
        Assert.Empty(Row(cut, "ru/").QuerySelectorAll("[data-testid=zip-keep]"));
    }

    [Fact]
    public void KeepingTheFileAlreadyThereIsPassedOnPerRule()
    {
        var cut = Render();
        MapEnglishAndSkipRussian(cut);

        Row(cut, "en/configs/").QuerySelector("[data-testid=zip-keep]")!.Change(true);
        cut.Find("[data-testid=zip-apply]").Click();

        Assert.Equal(["en/configs/"], _applied!.Value.Rules.Where(r => r.KeepExisting).Select(r => r.Source));
    }

    [Fact]
    public void AFilesInstalledByDefaultReplaceWhatIsThere()
    {
        var cut = Render();
        MapEnglishAndSkipRussian(cut);

        cut.Find("[data-testid=zip-apply]").Click();

        Assert.All(_applied!.Value.Rules, r => Assert.False(r.KeepExisting));
    }

    [Fact]
    public void TurningAFolderToSkipDropsItsFolderAndKeepChoices()
    {
        var cut = Render();
        SetRole(cut, "en/configs/", "config");
        Row(cut, "en/configs/").QuerySelector("[data-testid=zip-sub]")!.Change("x");
        Row(cut, "en/configs/").QuerySelector("[data-testid=zip-keep]")!.Change(true);

        SetRole(cut, "en/configs/", "skip");
        cut.Find("[data-testid=zip-role]"); // still rendered
        SetRole(cut, "en/plugins/", "plugins"); SetRole(cut, "en/images/", "data"); SetRole(cut, "ru/", "skip");
        cut.Find("[data-testid=zip-apply]").Click();

        var rule = _applied!.Value.Rules.Single(r => r.Source == "en/configs/");
        Assert.Equal(("skip", "", false), (rule.Role, rule.SubFolder, rule.KeepExisting));
    }

    // ---- what stops an update, said on the page --------------------------------------------------------------------------------------

    [Fact]
    public void TwoLanguagesInstalledOnTopOfEachOtherIsCaughtBeforeAnythingIsSent()
    {
        var cut = Render();
        SetRole(cut, "en/", "plugins");
        SetRole(cut, "ru/", "plugins");

        Assert.True(ApplyDisabled(cut));
        Assert.Contains(cut.FindAll("[data-testid=zip-problems] li"), li => li.TextContent.Contains("same path as another file"));
    }

    [Fact]
    public void AProgramFileThatWouldBeInstalledIsCaughtAndCanBeSkipped()
    {
        var files = new List<ZipEntryInfo>(Archive) { new("extension/Oxide.Ext.Discord.dll", 500) };
        var cut = Render(files);
        MapEnglishAndSkipRussian(cut);
        SetRole(cut, "extension/", "data");

        Assert.True(ApplyDisabled(cut));
        Assert.Contains(cut.FindAll("[data-testid=zip-problems] li"), li => li.TextContent.Contains("program file"));

        SetRole(cut, "extension/", "skip");

        Assert.False(ApplyDisabled(cut));
    }

    [Fact]
    public void APluginSourceFileSentAnywhereButThePluginsFolderIsCaught()
    {
        var cut = Render();
        SetRole(cut, "en/plugins/", "data");

        Assert.Contains(cut.FindAll("[data-testid=zip-problems] li"), li => li.TextContent.Contains("only go to the plugins folder"));
    }

    [Fact]
    public void InstructionsThatInstallNothingAreSaidToInstallNothing()
    {
        var cut = Render();
        SetRole(cut, "en/", "skip");
        SetRole(cut, "ru/", "skip");

        Assert.Contains("install no files", cut.Find("[data-testid=zip-problems]").TextContent);
        Assert.True(ApplyDisabled(cut));
    }

    [Fact]
    public void ManyProblemsAreCutToTheFirstFewWithACount()
    {
        var files = Enumerable.Range(0, 20).Select(i => new ZipEntryInfo($"a{i}.txt", 1)).ToList();
        var cut = Render(files);

        var problems = cut.FindAll("[data-testid=zip-problems] li");

        Assert.Equal(9, problems.Count);
        Assert.Contains("and 12 more", problems[^1].TextContent);
    }

    // ---- starting from what was saved ----------------------------------------------------------------------------------------------------

    [Fact]
    public void SavedInstructionsAreThereToStartFrom()
    {
        var saved = new List<ZipMappingRule>
        {
            new() { IsFolder = true, Source = "en/plugins/", Role = "plugins" }, new() { IsFolder = true, Source = "en/configs/", Role = "config", KeepExisting = true },
            new() { IsFolder = true, Source = "en/images/", Role = "data", SubFolder = "pictures" }, new() { IsFolder = true, Source = "ru/", Role = "skip" }
        };

        var cut = Render(initial: saved);

        Assert.False(ApplyDisabled(cut));
        Assert.Equal("→ data/pictures/test/one.jpg", Result(cut, "en/images/test/one.jpg"));
        Assert.True(Row(cut, "en/configs/").QuerySelector("[data-testid=zip-keep]")!.HasAttribute("checked"));
        Assert.Equal("data", ((IHtmlSelectElementLike)Row(cut, "en/images/").QuerySelector("[data-testid=zip-role]")!.ToSelectLike()).Value);
    }

    [Fact]
    public void SavedRulesAboutThingsNoLongerInTheArchiveAreDropped()
    {
        var saved = new List<ZipMappingRule>
        {
            new() { IsFolder = true, Source = "en/", Role = "plugins" }, new() { IsFolder = true, Source = "gone/", Role = "data" },
            new() { IsFolder = false, Source = "en/gone.txt", Role = "data" }, new() { IsFolder = true, Source = "ru/", Role = "skip" }
        };
        var cut = Render(initial: saved);

        cut.Find("[data-testid=zip-apply]").Click();

        Assert.Equal(["en/", "ru/"], _applied!.Value.Rules.Select(r => r.Source).Order());
    }

    [Fact]
    public void TheFilesSavedInstructionsMissedAreListedAsTheReasonForAsking()
    {
        var cut = Render(uncovered: ["de/plugins/test.cs", "readme.txt"]);

        var text = cut.Find("[data-testid=zip-uncovered]").TextContent;

        Assert.Contains("de/plugins/test.cs", text);
        Assert.Contains("readme.txt", text);
    }

    [Fact]
    public void NoWarningIsShownWhenNothingWasMissed()
    {
        Assert.Empty(Render().FindAll("[data-testid=zip-uncovered]"));
    }

    [Fact]
    public void FileNamesFromTheArchiveAreShownAsTextNeverAsMarkup()
    {
        var cut = Render([new("<img src=x onerror=alert(1)>.txt", 1)]);

        Assert.Empty(cut.FindAll("[data-testid=zip-tree] img"));
        Assert.Contains("<img src=x onerror=alert(1)>.txt", cut.Find("[data-testid=zip-tree]").TextContent);
    }
}

internal interface IHtmlSelectElementLike { string Value { get; } }

internal static class SelectExtensions
{
    // The select's chosen value, read from its markup (the option marked selected, or the value attribute bUnit keeps).
    public static IHtmlSelectElementLike ToSelectLike(this IElement select) => new SelectLike(select);

    private sealed class SelectLike(IElement select) : IHtmlSelectElementLike
    {
        public string Value => select is AngleSharp.Html.Dom.IHtmlSelectElement s ? s.Value : select.GetAttribute("value") ?? string.Empty;
    }
}
