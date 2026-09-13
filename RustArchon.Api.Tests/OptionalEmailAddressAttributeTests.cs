// Copyright ©2026 Scott Blomfield

using RustArchon.Shared.DTOs;

namespace RustArchon.Api.Tests;

/// <summary>
/// Tests for <see cref="OptionalEmailAddressAttribute"/> - the whole reason it exists is a gap in the
/// built-in <see cref="System.ComponentModel.DataAnnotations.EmailAddressAttribute"/>, so that gap is
/// exactly what these cover.
/// </summary>
public class OptionalEmailAddressAttributeTests
{
    private readonly OptionalEmailAddressAttribute _attribute = new();

    [Fact]
    public void ANullValueIsValid()
    {
        Assert.True(_attribute.IsValid(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankStringIsValid(string value)
    {
        // The gap in the built-in attribute: it only exempts null, so a cleared Blazor <input> -
        // which binds an empty string, never null, into a string? property - fails it.
        Assert.True(_attribute.IsValid(value));
    }

    [Fact]
    public void AWellFormedAddressIsValid()
    {
        Assert.True(_attribute.IsValid("owner@example.com"));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.com")]
    [InlineData("two@@signs.com")]
    public void AMalformedAddressIsInvalid(string value)
    {
        Assert.False(_attribute.IsValid(value));
    }
}
