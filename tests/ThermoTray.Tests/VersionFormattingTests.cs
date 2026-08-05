using Xunit;

namespace ThermoTray.Tests;

/// <summary>
/// <see cref="MainViewModel.FormatVersion"/> 版本字串格式化單元測試。
/// </summary>
public sealed class VersionFormattingTests
{
    /// <summary>
    /// 驗證格式化時會捨棄第四位組件 (Revision)。
    /// </summary>
    [Fact]
    public void FormatVersion_DropsTheFourthComponent() =>
        Assert.Equal("v1.1.2", MainViewModel.FormatVersion(new Version(1, 1, 2, 0)));

    /// <summary>
    /// 驗證缺失的 Component (-1) 會自動補 0。
    /// </summary>
    [Fact]
    public void FormatVersion_ReadsAMissingComponentAsZero() =>
        Assert.Equal("v1.1.0", MainViewModel.FormatVersion(new Version(1, 1)));

    /// <summary>
    /// 驗證傳入 null 版本時傳回空字串。
    /// </summary>
    [Fact]
    public void FormatVersion_ShowsNothingWithoutAVersion() =>
        Assert.Equal(string.Empty, MainViewModel.FormatVersion(null));

    /// <summary>
    /// 驗證與實際當前組件版本號一致。
    /// </summary>
    [Fact]
    public void FormatVersion_MatchesTheAssemblyTheApplicationRunsFrom()
    {
        var assemblyVersion = typeof(MainViewModel).Assembly.GetName().Version!;

        Assert.Equal(
            $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}",
            MainViewModel.FormatVersion(assemblyVersion));
    }
}
