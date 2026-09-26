using Avalonia;
using Avalonia.Headless;

// All UI tests share one Avalonia dispatcher thread; running test classes in parallel would interleave
// their steps on it, so the UI suite runs sequentially (it takes well under a minute).
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace GitHr.App.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering (not headless drawing) so tests can capture frames.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// The headless Avalonia session all UI tests run in. <see cref="AvaloniaTestIsolationLevel.PerTest"/> gives every
/// test a fresh <see cref="App"/> and services, like a real start of the application.
/// This replaces Avalonia.Headless.XUnit's [AvaloniaFact], which is not compatible with xunit.v3 4.x.
/// </summary>
internal static class UiSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder), AvaloniaTestIsolationLevel.PerTest));

    /// <summary>Runs <paramref name="test"/> on the Avalonia UI thread and completes when it does.</summary>
    public static Task RunAsync(Func<Task> test) =>
        Session.Value.Dispatch(async () =>
        {
            await test();
            return true;
        }, TestContext.Current.CancellationToken);
}
