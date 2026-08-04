using Xunit;

namespace ThermoTray.Tests;

public sealed class VersionFormattingTests
{
    /// <summary>The build appends a fourth component that no release is ever named after.</summary>
    [Fact]
    public void FormatVersion_DropsTheFourthComponent() =>
        Assert.Equal("v1.1.2", MainViewModel.FormatVersion(new Version(1, 1, 2, 0)));

    /// <summary>An absent component is -1, which must not reach the window as "v1.1.-1".</summary>
    [Fact]
    public void FormatVersion_ReadsAMissingComponentAsZero() =>
        Assert.Equal("v1.1.0", MainViewModel.FormatVersion(new Version(1, 1)));

    /// <summary>The assembly can carry no version at all; the header then shows nothing rather than "v".</summary>
    [Fact]
    public void FormatVersion_ShowsNothingWithoutAVersion() =>
        Assert.Equal(string.Empty, MainViewModel.FormatVersion(null));

    /// <summary>What the window binds to has to be the version this build was actually stamped with.</summary>
    [Fact]
    public void FormatVersion_MatchesTheAssemblyTheApplicationRunsFrom()
    {
        var assemblyVersion = typeof(MainViewModel).Assembly.GetName().Version!;

        Assert.Equal(
            $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}",
            MainViewModel.FormatVersion(assemblyVersion));
    }
}
