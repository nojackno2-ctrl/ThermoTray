using System.Drawing;

namespace ThermoTray;

/// <summary>
/// 計算系統工作列圖示繪製區域與兩行數字（使用率與溫度）位置的佈局計算類別。
/// 與實際繪製邏輯分離，便於進行單元測試與邊界幾何驗證。
/// </summary>
internal static class TrayIconLayout
{
    /// <summary>
    /// 當 Windows 回報不合理的系統匣圖示尺寸時採用的最小圖示尺寸（像素）。
    /// </summary>
    internal const int MinimumIconSize = 16;

    /// <summary>
    /// 超高採樣倍率（Supersampling）。圖示以最終尺寸的倍數進行高解析度繪製後再縮放，
    /// 可顯著改善小圖示（如 16x16 像素）字型筆劃的平滑度與反鋸齒效果。
    /// </summary>
    internal const int SupersampleFactor = 2;

    /// <summary>
    /// 取得超高採樣後的畫布尺寸。
    /// </summary>
    /// <param name="iconSize">原始圖示尺寸。</param>
    /// <returns>畫布總像素尺寸。</returns>
    internal static int GetCanvasSize(int iconSize) => iconSize * SupersampleFactor;

    /// <summary>
    /// 將畫布分割為上方（使用率）與下方（溫度）兩行文字區域。
    /// 確保兩行區域完全在畫布範圍內且不重疊，防止繪製時字體遭到裁切。
    /// </summary>
    /// <param name="canvasSize">畫布總尺寸。</param>
    /// <returns>包含使用率區域與溫度區域的矩形邊界元組。</returns>
    internal static (RectangleF Usage, RectangleF Temperature) GetLines(int canvasSize)
    {
        // 依照畫布尺寸計算 1 像素比例的邊界留白，區隔上下行並保持邊緣適當距離
        var margin = Math.Max(1f, (float)canvasSize / 64f);
        var lineHeight = (canvasSize - margin) / 2f;
        var lineWidth = canvasSize - (2f * margin);

        return (
            new RectangleF(margin, 0f, lineWidth, lineHeight),
            new RectangleF(margin, canvasSize - lineHeight, lineWidth, lineHeight));
    }
}
