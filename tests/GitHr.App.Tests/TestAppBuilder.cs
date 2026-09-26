using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(GitHr.App.Tests.TestAppBuilder))]

namespace GitHr.App.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering (not headless drawing) so tests can capture frames.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
