using Avalonia.Controls;
using Avalonia.Threading;

namespace GitHr.App.Tests;

/// <summary>The version a release build was made with, and where the app shows it.</summary>
public sealed class AppInfoTests : UiTestBase
{
    [Fact]
    public void Version_ComesFromTheBuild()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", AppInfo.Version);
        Assert.StartsWith($"GitHr {AppInfo.Version}", AppInfo.DisplayVersion);
        if (AppInfo.Commit is { } commit)
        {
            Assert.Matches("^[0-9a-f]{7}$", commit);
            Assert.EndsWith($"({commit})", AppInfo.DisplayVersion);
        }
    }

    [Fact]
    public Task StartScreen_ShowsTheVersion_AndTheWindowHasTheAppIcon() => RunUi(() =>
    {
        var (window, _) = CreateWindow();
        Dispatcher.UIThread.RunJobs();

        var version = window.FindControl<TextBlock>("VersionText")!;
        Assert.True(version.IsEffectivelyVisible);
        Assert.Equal(AppInfo.DisplayVersion, version.Text);
        Assert.NotNull(window.Icon);
        SaveFrame(window, "githr-start-screen.png");
        return Task.CompletedTask;
    });
}
