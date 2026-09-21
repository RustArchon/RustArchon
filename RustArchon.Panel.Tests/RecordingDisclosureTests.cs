// Copyright ©2026 Scott Blomfield

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using RustArchon.Panel.Components.Shared;

namespace RustArchon.Panel.Tests;

/// <summary>
/// What recording says about itself. It leads with what the customer gets, then states the plain facts a server owner needs - what is
/// recorded, who can see it, how long it is kept, how to turn it off - and gives them a line for their own rules. Nothing here may promise
/// what the product does not do, so each fact is pinned to the words that carry it.
/// </summary>
public class RecordingDisclosureTests : BunitContext
{
    public RecordingDisclosureTests() => Services.AddSingleton(ReportTestSupport.Localizer());

    [Fact]
    public void ItLeadsWithWhatTheCustomerGets()
    {
        var cut = Render<RecordingDisclosure>();

        var benefit = cut.Find("[data-testid=recording-disclosure-benefit]").TextContent;
        Assert.Contains("live map", benefit);
        Assert.Contains("combat log", benefit);
        Assert.Contains("evidence instead of guesses", benefit);
        Assert.True(cut.Markup.IndexOf("recording-disclosure-benefit") < cut.Markup.IndexOf("recording-disclosure-what"));
    }

    [Fact]
    public void ItSaysWhatIsRecordedWhoSeesItHowLongItIsKeptAndHowToStopIt()
    {
        var cut = Render<RecordingDisclosure>();

        Assert.Contains("player positions", cut.Find("[data-testid=recording-disclosure-what]").TextContent);
        Assert.Contains("who hit whom", cut.Find("[data-testid=recording-disclosure-what]").TextContent);
        Assert.Contains("tool cupboard", cut.Find("[data-testid=recording-disclosure-what]").TextContent);
        var who = cut.Find("[data-testid=recording-disclosure-who]").TextContent;
        Assert.Contains("only people in your organization", who);
        Assert.Contains("every look at them is logged", who);
        Assert.Contains("days your plan includes", cut.Find("[data-testid=recording-disclosure-how-long]").TextContent);
        var control = cut.Find("[data-testid=recording-disclosure-control]").TextContent;
        Assert.Contains("start on", control);
        Assert.Contains("turned off at any time", control);
    }

    [Fact]
    public void ItGivesTheOwnerALineForTheirServerRules()
    {
        var cut = Render<RecordingDisclosure>();

        Assert.Contains("most servers mention recording", cut.Find("[data-testid=recording-disclosure-rules]").TextContent);
        Assert.Contains("records player positions and combat", cut.Find("[data-testid=recording-disclosure-rules-text]").TextContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ItStartsOpenOrFoldedAsAsked(bool open)
    {
        var cut = Render<RecordingDisclosure>(p => p.Add(x => x.Open, open));

        Assert.Equal(open, cut.Find("[data-testid=recording-disclosure]").HasAttribute("open"));
    }
}
