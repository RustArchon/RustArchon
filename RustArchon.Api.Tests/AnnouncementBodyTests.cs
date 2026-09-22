// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using RustArchon.Api.Administration;

namespace RustArchon.Api.Tests;

/// <summary>
/// What becomes of the announcement editor's content on its way into an email. The email HTML is built from the editor's structured content, never cleaned up
/// from HTML it was handed - so the tests that matter most are the hostile ones: whatever is in the content, the only markup that comes out is markup this class wrote.
/// </summary>
public class AnnouncementBodyTests
{
    private static string Delta(params string[] ops) => "{\"ops\":[" + string.Join(",", ops) + "]}";

    private static string Text(string text, string? attributes = null) =>
        "{\"insert\":" + System.Text.Json.JsonSerializer.Serialize(text) + (attributes is null ? "" : ",\"attributes\":{" + attributes + "}") + "}";

    // ---- the formatting the toolbar can make ------------------------------------------------------------------------------------------

    [Fact]
    public void PlainTextBecomesAParagraph()
    {
        Assert.Equal("<p>Hello there</p>", AnnouncementBody.ToHtml(Delta(Text("Hello there\n"))));
    }

    [Fact]
    public void EachLineIsItsOwnParagraphAndABlankLineIsKept()
    {
        Assert.Equal("<p>One</p><p><br></p><p>Two</p>", AnnouncementBody.ToHtml(Delta(Text("One\n\nTwo\n"))));
    }

    [Theory]
    [InlineData("\"bold\":true", "<p><strong>x</strong></p>")]
    [InlineData("\"italic\":true", "<p><em>x</em></p>")]
    [InlineData("\"underline\":true", "<p><u>x</u></p>")]
    [InlineData("\"strike\":true", "<p><s>x</s></p>")]
    [InlineData("\"bold\":true,\"italic\":true", "<p><strong><em>x</em></strong></p>")]
    public void InlineFormattingBecomesItsTag(string attributes, string expected)
    {
        Assert.Equal(expected, AnnouncementBody.ToHtml(Delta(Text("x", attributes), Text("\n"))));
    }

    [Fact]
    public void AFormattedRunInTheMiddleOfALineKeepsTheRestPlain()
    {
        var html = AnnouncementBody.ToHtml(Delta(Text("a "), Text("b", "\"bold\":true"), Text(" c\n")));

        Assert.Equal("<p>a <strong>b</strong> c</p>", html);
    }

    [Theory]
    [InlineData(1, "h1")]
    [InlineData(2, "h2")]
    [InlineData(3, "h3")]
    public void HeadingsOneToThreeAreKept(int level, string tag)
    {
        Assert.Equal($"<{tag}>Title</{tag}>", AnnouncementBody.ToHtml(Delta(Text("Title"), Text("\n", $"\"header\":{level}"))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(-1)]
    public void AnyOtherHeadingLevelIsAPlainParagraph(int level)
    {
        Assert.Equal("<p>Title</p>", AnnouncementBody.ToHtml(Delta(Text("Title"), Text("\n", $"\"header\":{level}"))));
    }

    [Fact]
    public void ConsecutiveListLinesShareOneList()
    {
        var html = AnnouncementBody.ToHtml(Delta(Text("one"), Text("\n", "\"list\":\"bullet\""), Text("two"), Text("\n", "\"list\":\"bullet\""), Text("after\n")));

        Assert.Equal("<ul><li>one</li><li>two</li></ul><p>after</p>", html);
    }

    [Fact]
    public void OrderedAndBulletListsAreDifferentListsAndSeparatedByAParagraph()
    {
        var html = AnnouncementBody.ToHtml(Delta(
            Text("a"), Text("\n", "\"list\":\"ordered\""), Text("b"), Text("\n", "\"list\":\"bullet\"")));

        Assert.Equal("<ol><li>a</li></ol><ul><li>b</li></ul>", html);
    }

    [Fact]
    public void AListItemThatIsEmptyStillRenders()
    {
        Assert.Equal("<ul><li><br></li></ul>", AnnouncementBody.ToHtml(Delta(Text("\n", "\"list\":\"bullet\""))));
    }

    [Fact]
    public void QuotedLinesShareOneBlockquote()
    {
        var html = AnnouncementBody.ToHtml(Delta(Text("q1"), Text("\n", "\"blockquote\":true"), Text("q2"), Text("\n", "\"blockquote\":true"), Text("plain\n")));

        Assert.Equal("<blockquote><p>q1</p><p>q2</p></blockquote><p>plain</p>", html);
    }

    [Fact]
    public void AListEndingTheMessageIsClosed()
    {
        Assert.Equal("<ol><li>only</li></ol>", AnnouncementBody.ToHtml(Delta(Text("only"), Text("\n", "\"list\":\"ordered\""))));
    }

    [Fact]
    public void TextWithNoFinalNewlineIsStillAParagraph()
    {
        Assert.Equal("<p>no newline</p>", AnnouncementBody.ToHtml(Delta(Text("no newline"))));
    }

    [Fact]
    public void BothTheOpsObjectAndABareArrayAreAccepted()
    {
        Assert.Equal("<p>x</p>", AnnouncementBody.ToHtml("[" + Text("x\n") + "]"));
        Assert.Equal("<p>x</p>", AnnouncementBody.ToHtml(Delta(Text("x\n"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingWrittenIsNothing(string? empty)
    {
        Assert.Equal(string.Empty, AnnouncementBody.ToHtml(empty));
        Assert.False(AnnouncementBody.HasContent(AnnouncementBody.ToHtml(empty)));
    }

    [Fact]
    public void WhatTheRealEditorEmitsIsConvertedAsExpected()
    {
        // Captured from Quill running in a browser (announcementEditor.js): attributes come before insert, and a plain newline is merged into the text that
        // follows it ("\none"), neither of which hand-written samples do.
        const string captured = """
            {"ops":[{"insert":"Big news"},{"attributes":{"header":2},"insert":"\n"},{"insert":"Dear "},{"attributes":{"bold":true},"insert":"{{OrganizationName}}"},{"insert":", see "},{"attributes":{"link":"https://example.com/post"},"insert":"our post"},{"insert":"\none"},{"attributes":{"list":"bullet"},"insert":"\n"},{"insert":"two"},{"attributes":{"list":"bullet"},"insert":"\n"},{"insert":"quoted"},{"attributes":{"blockquote":true},"insert":"\n"}]}
            """;

        var html = AnnouncementBody.ToHtml(captured);

        Assert.Equal(
            "<h2>Big news</h2>"
            + "<p>Dear <strong>{{OrganizationName}}</strong>, see <a href=\"https://example.com/post\" rel=\"noopener noreferrer nofollow\" target=\"_blank\">our post</a></p>"
            + "<ul><li>one</li><li>two</li></ul>"
            + "<blockquote><p>quoted</p></blockquote>",
            html);
        Assert.Equal([], AnnouncementBody.UnknownTokens(AnnouncementBody.PlainText(captured)));
    }

    // ---- links ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnHttpsLinkIsKeptWithTheAttributesThatMakeItSafeToFollow()
    {
        var html = AnnouncementBody.ToHtml(Delta(Text("our site", "\"link\":\"https://example.com/page?a=1&b=2\""), Text("\n")));

        Assert.Equal("""<p><a href="https://example.com/page?a=1&amp;b=2" rel="noopener noreferrer nofollow" target="_blank">our site</a></p>""", html);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/")]
    [InlineData("mailto:help@example.com")]
    public void HttpHttpsAndMailtoLinksAreKept(string link)
    {
        var html = AnnouncementBody.ToHtml(Delta(Text("x", $"\"link\":\"{link}\""), Text("\n")));

        Assert.Contains("<a href=\"", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("/relative/path")]
    [InlineData("//example.com/protocol-relative")]
    [InlineData("example.com")]
    [InlineData("https://exa mple.com/with space")]
    [InlineData("https://example.com/\\u0001control")]
    [InlineData("")]
    public void AnyOtherLinkIsDroppedAndTheTextStaysAsPlainText(string link)
    {
        var html = AnnouncementBody.ToHtml(Delta(Text("click", $"\"link\":\"{link}\""), Text("\n")));

        Assert.Equal("<p>click</p>", html);
        Assert.DoesNotContain("<a", html);
    }

    [Fact]
    public void AQuoteInALinkCannotBreakOutOfTheAttribute()
    {
        var html = AnnouncementBody.ToHtml(Delta(Text("x", "\"link\":\"https://example.com/\\\"onmouseover=\\\"alert(1)\""), Text("\n")));

        // Whatever is kept, no attribute can have been added: the address is written back encoded, inside its own quotes.
        Assert.DoesNotContain("onmouseover=\"", html);
        Assert.DoesNotContain(" onmouseover", html);
    }

    // ---- hostile content ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("\"><svg onload=alert(1)>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("</p><script>steal()</script><p>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<style>body{display:none}</style>")]
    public void MarkupWrittenAsTextIsShownAsTextNeverAsMarkup(string hostile)
    {
        var html = AnnouncementBody.ToHtml(Delta(Text(hostile + "\n")));

        Assert.StartsWith("<p>", html);
        Assert.EndsWith("</p>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a ", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, "<p>").Count);        // and it did not manage to open anything of its own
    }

    [Fact]
    public void HostileTextInsideFormattingIsStillEncoded()
    {
        var html = AnnouncementBody.ToHtml(Delta(Text("<b onclick=x>", "\"bold\":true,\"italic\":true"), Text("\n", "\"header\":2")));

        Assert.Equal("<h2><strong><em>&lt;b onclick=x&gt;</em></strong></h2>", html);
    }

    [Fact]
    public void AttributesTheToolbarCannotMakeAreIgnored()
    {
        var html = AnnouncementBody.ToHtml(Delta(
            Text("x", "\"color\":\"red\",\"background\":\"#000\",\"size\":\"huge\",\"font\":\"Comic\",\"script\":true,\"style\":\"display:none\",\"class\":\"a\""), Text("\n")));

        Assert.Equal("<p>x</p>", html);
    }

    [Fact]
    public void AnEmbeddedImageOrVideoIsDropped()
    {
        var html = AnnouncementBody.ToHtml(Delta(
            Text("before "), "{\"insert\":{\"image\":\"https://evil.example/x.png\"}}", "{\"insert\":{\"video\":\"https://evil.example/v\"}}", Text("after\n")));

        Assert.Equal("<p>before after</p>", html);
    }

    [Fact]
    public void NonObjectOperationsAndMissingInsertsAreSkipped()
    {
        Assert.Equal("<p>ok</p>", AnnouncementBody.ToHtml("[1,\"two\",null,{\"retain\":3},{\"delete\":2}," + Text("ok\n") + "]"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"ops\":")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("{\"ops\":\"nope\"}")]
    [InlineData("{\"nothing\":[]}")]
    public void ContentThatIsNotADeltaIsRefused(string bad)
    {
        Assert.Throws<FormatException>(() => AnnouncementBody.ToHtml(bad));
    }

    [Fact]
    public void ADeltaOverTheSizeLimitIsRefused()
    {
        var huge = Delta(Text(new string('x', AnnouncementBody.MaxDeltaLength)));

        Assert.Throws<FormatException>(() => AnnouncementBody.ToHtml(huge));
    }

    [Fact]
    public void ADeltaWithFarTooManyRunsIsRefused()
    {
        var ops = Enumerable.Repeat(Text("a"), AnnouncementBody.MaxOps + 1).ToArray();

        Assert.Throws<FormatException>(() => AnnouncementBody.ToHtml(Delta(ops)));
    }

    [Fact]
    public void DeeplyNestedJsonIsRefusedNotFollowed()
    {
        var nested = new string('[', 200) + new string(']', 200);

        Assert.Throws<FormatException>(() => AnnouncementBody.ToHtml(nested));
    }

    // ---- what counts as a message ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<p><br></p>", false)]
    [InlineData("<p></p><p> </p>", false)]
    [InlineData("<p>&nbsp;</p>", false)]
    [InlineData("<p>Hello</p>", true)]
    [InlineData("<ul><li>one</li></ul>", true)]
    public void EmptyParagraphsAreNotAMessage(string html, bool expected)
    {
        Assert.Equal(expected, AnnouncementBody.HasContent(html));
    }

    [Fact]
    public void ThePlainTextIsTheWordsWithoutTheFormatting()
    {
        Assert.Equal("Hello {{OrganizationName}}\n", AnnouncementBody.PlainText(Delta(Text("Hello ", "\"bold\":true"), Text("{{OrganizationName}}\n"))));
        Assert.Equal(string.Empty, AnnouncementBody.PlainText(null));
    }

    // ---- the subject and the tokens ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Plain subject", "Plain subject")]
    [InlineData("  padded  ", "padded")]
    [InlineData("Two\r\nlines", "Two lines")]
    [InlineData("Header\nBcc: attacker@example.com", "Header Bcc: attacker@example.com")]
    [InlineData("Split?here", "Split here")]
    [InlineData(null, "")]
    public void ASubjectIsOneLineSoAHeaderCannotBeForged(string? subject, string expected)
    {
        var clean = AnnouncementBody.CleanSubject(subject);

        Assert.Equal(expected, clean);
        Assert.DoesNotContain('\n', clean);
        Assert.DoesNotContain('\r', clean);
    }

    [Fact]
    public void OnlyTheThreeKnownTokensAreAllowed()
    {
        Assert.Empty(AnnouncementBody.UnknownTokens("Hello {{OrganizationName}} on {{PlanName}} at {{SiteName}}"));
        Assert.Equal(["Password", "Foo"], AnnouncementBody.UnknownTokens("{{Password}} {{OrganizationName}} {{Foo}} {{Password}}"));
        Assert.Equal(["organizationname"], AnnouncementBody.UnknownTokens("{{organizationname}}"));       // case matters, as it does when they are filled in
        Assert.Empty(AnnouncementBody.UnknownTokens(null));
    }

    [Fact]
    public void ATokenWithSpacesInsideTheBracesIsStillRecognisedAsATokenAndCheckedTheSameWay()
    {
        Assert.Empty(AnnouncementBody.UnknownTokens("{{ OrganizationName }}"));
        Assert.Equal(["Nope"], AnnouncementBody.UnknownTokens("{{ Nope }}"));
    }
}
