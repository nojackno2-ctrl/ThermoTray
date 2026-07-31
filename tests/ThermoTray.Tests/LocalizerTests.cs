using Xunit;

namespace ThermoTray.Tests;

public sealed class LocalizerTests
{
    [Fact]
    public void Indexer_ReturnsTraditionalChinese_ForTheDefaultLanguage() =>
        Assert.Equal("無法取得", new Localizer(Localizer.DefaultLanguage)["Unavailable"]);

    [Fact]
    public void Indexer_ReturnsEnglish_ForEnglish() =>
        Assert.Equal("Unavailable", new Localizer(Localizer.EnglishLanguage)["Unavailable"]);

    [Fact]
    public void Indexer_ReturnsTheKey_WhenNoTranslationExists() =>
        Assert.Equal("MissingKey", new Localizer(Localizer.EnglishLanguage)["MissingKey"]);

    [Theory]
    [InlineData("de-DE")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void Constructor_FallsBackToTheDefault_ForAnUnsupportedLanguage(string language) =>
        Assert.Equal(Localizer.DefaultLanguage, new Localizer(language).Language);

    [Fact]
    public void Constructor_AcceptsEnglishRegardlessOfCasing() =>
        Assert.Equal(Localizer.EnglishLanguage, new Localizer("EN-us").Language);

    [Fact]
    public void SetLanguage_NormalisesUnsupportedValues()
    {
        var localizer = new Localizer(Localizer.EnglishLanguage);

        localizer.SetLanguage("ja-JP");

        Assert.Equal(Localizer.DefaultLanguage, localizer.Language);
    }
}
