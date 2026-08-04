using System.Drawing;

namespace ThermoTray;

/// <summary>
/// Works out how big a tray icon should be drawn and where its two lines of digits go. It is separate
/// from the drawing so the geometry can be tested on its own.
/// </summary>
internal static class TrayIconLayout
{
    /// <summary>Used when Windows reports an implausible notification-area icon size.</summary>
    internal const int MinimumIconSize = 16;

    /// <summary>
    /// The icon is drawn at this multiple of its final size and then resampled down. Filling a glyph
    /// outline straight into a 16-pixel bitmap rounds every stroke to whole pixels and leaves the digits
    /// visibly uneven, whereas drawing twice as large and averaging gives each stroke its true weight.
    /// </summary>
    internal const int SupersampleFactor = 2;

    internal static int GetCanvasSize(int iconSize) => iconSize * SupersampleFactor;

    /// <summary>
    /// Splits the canvas into an upper line for utilization and a lower one for temperature. Both lines
    /// stay inside the canvas and never touch, which is what keeps the digits whole: text drawn into a
    /// rectangle that reaches past the bitmap, or that is shorter than the glyphs, is silently cut off.
    /// </summary>
    internal static (RectangleF Usage, RectangleF Temperature) GetLines(int canvasSize)
    {
        // One pixel at the tray's own scale: enough to separate the lines and hold the digits off the
        // edge, without spending height that the digits themselves could use.
        var margin = Math.Max(1f, (float)canvasSize / 64f);
        var lineHeight = (canvasSize - margin) / 2f;
        var lineWidth = canvasSize - (2f * margin);

        return (
            new RectangleF(margin, 0f, lineWidth, lineHeight),
            new RectangleF(margin, canvasSize - lineHeight, lineWidth, lineHeight));
    }
}
