// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using RustArchon.Api.Administration;

namespace RustArchon.Api.Tests;

/// <summary>Tests for <see cref="EmailTemplateRenderer"/> - the only place a template translation's
/// <c>{{Token}}</c> placeholders are actually substituted.</summary>
public class EmailTemplateRendererTests
{
    [Fact]
    public void SubstitutesATokenInBothSubjectAndBody()
    {
        var (subject, htmlBody) = EmailTemplateRenderer.Render(
            "Hello {{Name}}", "<p>Hi {{Name}}, welcome.</p>",
            new Dictionary<string, string> { ["Name"] = "Acme" });

        Assert.Equal("Hello Acme", subject);
        Assert.Equal("<p>Hi Acme, welcome.</p>", htmlBody);
    }

    /// <summary>
    /// The whole reason this exists: an Organization picks its own name, and that name ends up
    /// substituted into HTML nobody reviewed. A name containing HTML must not be interpreted as markup
    /// by whatever renders the sent email.
    /// </summary>
    [Fact]
    public void HtmlEncodesValuesSubstitutedIntoTheBodyButNotTheSubject()
    {
        var (subject, htmlBody) = EmailTemplateRenderer.Render(
            "Subject: {{Name}}", "<p>{{Name}}</p>",
            new Dictionary<string, string> { ["Name"] = "<script>evil()</script>" });

        Assert.Equal("Subject: <script>evil()</script>", subject);
        Assert.DoesNotContain("<script>", htmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", htmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesAnUnsuppliedTokenVerbatimRatherThanBlankingIt()
    {
        var (subject, htmlBody) = EmailTemplateRenderer.Render(
            "Hi {{Name}}", "<p>{{Name}} - {{Missing}}</p>",
            new Dictionary<string, string> { ["Name"] = "Acme" });

        Assert.Equal("Hi Acme", subject);
        Assert.Contains("{{Missing}}", htmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstitutesEveryOccurrenceOfARepeatedToken()
    {
        var (_, htmlBody) = EmailTemplateRenderer.Render(
            "{{Name}}", "<p>{{Name}} and {{Name}} again</p>",
            new Dictionary<string, string> { ["Name"] = "Acme" });

        Assert.Equal("<p>Acme and Acme again</p>", htmlBody);
    }
}
