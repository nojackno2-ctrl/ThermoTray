using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="Localizer"/> 多語系服務單元測試。
/// </summary>
public sealed class LocalizerTests
{
    /// <summary>
    /// 驗證預設語言 (zh-TW) 索引鍵能正確傳回繁體中文文字。
    /// </summary>
    [Fact]
    public void Indexer_ReturnsTraditionalChinese_ForTheDefaultLanguage() =>
        Assert.Equal("無法取得", new Localizer(Localizer.DefaultLanguage)["Unavailable"]);

    /// <summary>
    /// 驗證英文語系 (en-US) 索引鍵能正確傳回英文文字。
    /// </summary>
    [Fact]
    public void Indexer_ReturnsEnglish_ForEnglish() =>
        Assert.Equal("Unavailable", new Localizer(Localizer.EnglishLanguage)["Unavailable"]);

    /// <summary>
    /// 驗證當 Key 不存在時傳回 Key 原字串。
    /// </summary>
    [Fact]
    public void Indexer_ReturnsTheKey_WhenNoTranslationExists() =>
        Assert.Equal("MissingKey", new Localizer(Localizer.EnglishLanguage)["MissingKey"]);

    /// <summary>
    /// 驗證未支援的語言代碼會自動退回使用預設語言 (zh-TW)。
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void Constructor_FallsBackToTheDefault_ForAnUnsupportedLanguage(string language) =>
        Assert.Equal(Localizer.DefaultLanguage, new Localizer(language).Language);

    /// <summary>
    /// 驗證語言代碼比較不分大小寫。
    /// </summary>
    [Fact]
    public void Constructor_AcceptsEnglishRegardlessOfCasing() =>
        Assert.Equal(Localizer.EnglishLanguage, new Localizer("EN-us").Language);

    /// <summary>
    /// 驗證呼叫 <see cref="Localizer.SetLanguage"/> 設定未支援語言時自動標準化為預設語言。
    /// </summary>
    [Fact]
    public void SetLanguage_NormalisesUnsupportedValues()
    {
        var localizer = new Localizer(Localizer.EnglishLanguage);

        localizer.SetLanguage("ja-JP");

        Assert.Equal(Localizer.DefaultLanguage, localizer.Language);
    }
}
