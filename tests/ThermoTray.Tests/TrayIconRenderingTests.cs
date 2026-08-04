using System.Drawing;
using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// Guards what the tray icon actually puts on screen. The readings it shows are the whole point of the
/// application, so a digit that is dropped or cut in half is a correctness problem, not a cosmetic one.
/// </summary>
public sealed class TrayIconRenderingTests
{
    private const int IconSize = 16;

    /// <summary>
    /// A three-digit reading used to be drawn into a rectangle it could not fit, and the part that did
    /// not fit was cut off rather than scaled down, so 100 appeared as 10.
    /// </summary>
    [Fact]
    public void CreateIconBitmap_DrawsThreeDigitsDifferentlyFromTwo()
    {
        using var three = Render("100", "100");
        using var two = Render("10", "10");

        Assert.NotEqual(Describe(three), Describe(two));
    }

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
    /// Each line is scaled to fill its half of the icon, so a reading that covers only a sliver of its
    /// half means the digits were clipped or shrunk away rather than fitted.
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

    /// <summary>The digits have to stay inside the icon; anything drawn outside it is simply lost.</summary>
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

    /// <summary>Rows carrying any ink, which is how tall the digits ended up rather than how wide.</summary>
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
