using System.Drawing;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="TrayIconService.CreateIconBitmap"/> 位元圖繪製視覺效果單元測試。
/// 確保數字不會被截斷或毀損。
/// </summary>
public sealed class TrayIconRenderingTests
{
    private const int IconSize = 16;

    /// <summary>
    /// 驗證三位數字 ("100") 的繪製像素分佈與二位數字 ("10") 不同，確保三位數字不被截斷為二位數。
    /// </summary>
    [Fact]
    public void CreateIconBitmap_DrawsThreeDigitsDifferentlyFromTwo()
    {
        using var three = Render("100", "100");
        using var two = Render("10", "10");

        Assert.NotEqual(Describe(three), Describe(two));
    }

    /// <summary>
    /// 驗證上下兩行（使用率與溫度）均包含實際繪製的墨水像素。
    /// </summary>
    [Theory]
    [InlineData("1", "49")]
    [InlineData("100", "89")]
    [InlineData("7", "100")]
    [InlineData("100", "100")]
    public void CreateIconBitmap_DrawsBothLines(string usageDigits, string temperatureDigits)
    {
        using var bitmap = Render(usageDigits, temperatureDigits);

        Assert.True(CountInk(bitmap, 0, IconSize / 2) > 0, "the utilization line is missing");
        Assert.True(CountInk(bitmap, IconSize / 2, IconSize) > 0, "the temperature line is missing");
    }

    /// <summary>
    /// 驗證數字自動縮放後填滿絕大部分行高度，不致因縮小過度而無法辨識。
    /// </summary>
    [Theory]
    [InlineData("1", "49")]
    [InlineData("35", "72")]
    [InlineData("100", "89")]
    public void CreateIconBitmap_FillsMostOfEachLine(string usageDigits, string temperatureDigits)
    {
        using var bitmap = Render(usageDigits, temperatureDigits);

        Assert.True(InkRowCount(bitmap, 0, IconSize / 2) >= (IconSize / 2) - 1, "the utilization digits are too short");
        Assert.True(InkRowCount(bitmap, IconSize / 2, IconSize) >= (IconSize / 2) - 1, "the temperature digits are too short");
    }

    /// <summary>
    /// 驗證輸出的位元圖像素尺寸精確符合請求的尺寸。
    /// </summary>
    [Fact]
    public void CreateIconBitmap_UsesTheRequestedSize()
    {
        using var bitmap = Render("100", "100");

        Assert.Equal(IconSize, bitmap.Width);
        Assert.Equal(IconSize, bitmap.Height);
    }

    private static Bitmap Render(string usageDigits, string temperatureDigits) =>
        TrayIconService.CreateIconBitmap(usageDigits, temperatureDigits, Brushes.White, IconSize);

    private static int CountInk(Bitmap bitmap, int topRow, int bottomRow)
    {
        var count = 0;
        for (var y = topRow; y < bottomRow; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A > 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// 計算包含非透明像素的總行數。
    /// </summary>
    private static int InkRowCount(Bitmap bitmap, int topRow, int bottomRow)
    {
        var rows = 0;
        for (var y = topRow; y < bottomRow; y++)
        {
            if (CountInk(bitmap, y, y + 1) > 0)
            {
                rows++;
            }
        }

        return rows;
    }

    private static string Describe(Bitmap bitmap)
    {
        var pixels = new char[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                pixels[(y * bitmap.Width) + x] = bitmap.GetPixel(x, y).A > 0 ? '#' : '.';
            }
        }

        return new string(pixels);
    }
}
