using FluentAssertions;

using MapaTur.Application.Localization;

namespace MapaTur.Application.Tests.Localization;

public sealed class AppLanguageTests
{
    [Fact]
    public void Supported_ContainsExactlyPolishAndEnglish()
    {
        AppLanguage.Supported.Should().Equal("pl", "en");
    }

    [Theory]
    [InlineData("pl", "pl")]
    [InlineData("en", "en")]
    [InlineData("PL", "pl")]
    [InlineData("EN", "en")]
    [InlineData("pl-PL", "pl")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    public void Normalize_KnownCodes_ReturnsBareLanguage(string input, string expected)
    {
        AppLanguage.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("de")]
    [InlineData("xx-YY")]
    [InlineData("klingon")]
    public void Normalize_NullEmptyOrUnsupported_FallsBackToPolish(string? input)
    {
        AppLanguage.Normalize(input).Should().Be("pl");
    }

    [Theory]
    [InlineData("pl", "pl-PL")]
    [InlineData("en", "en-US")]
    [InlineData("EN", "en-US")]
    [InlineData("pl-PL", "pl-PL")]
    [InlineData(null, "pl-PL")]
    [InlineData("de", "pl-PL")]
    public void ToCultureName_MapsToConcreteCulture_WithPolishFallback(string? input, string expected)
    {
        AppLanguage.ToCultureName(input).Should().Be(expected);
    }
}