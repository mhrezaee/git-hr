using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using GitHr.App.ViewModels;
using GitHr.App.Views;
using GitHr.Core;

namespace GitHr.App.Tests;

/// <summary>Drives the real main window headlessly: keyboard input goes to the window in memory, not the desktop.</summary>
public sealed class CommandPaletteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "githr-apptests", Guid.NewGuid().ToString("N"));
    private readonly GitRunner _git = new();

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task CtrlP_OpensPalette_TypingFilters_EscapeCloses()
    {
        var (window, vm) = await OpenWindowAsync();

        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.Palette.IsOpen);
        Assert.True(window.FindControl<TextBox>("PaletteBox")!.IsFocused);
        Assert.Contains(vm.Palette.Items, i => i.Title == "Pull");
        Assert.Contains(vm.Palette.Items, i => i.Title == "Checkout feature/login");

        window.KeyTextInput("psh");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Push", vm.Palette.SelectedItem?.Title);

        SaveFrame(window, "githr-palette.png");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.Palette.IsOpen);
    }

    [AvaloniaFact]
    public async Task Enter_RunsSelectedCommand_Stash()
    {
        var (window, vm) = await OpenWindowAsync();
        await File.WriteAllTextAsync(Path.Combine(_dir, "wip.txt"), "work in progress\n");
        await vm.RefreshAsync();
        Assert.Single(vm.UnstagedFiles);

        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("stash all");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Stash all changes", vm.Palette.SelectedItem?.Title);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        await WaitUntilAsync(() => !vm.IsBusy && vm.UnstagedFiles.Count == 0);
        Assert.False(vm.Palette.IsOpen);
    }

    [AvaloniaFact]
    public async Task TypingNewName_OffersCreateBranch_ArrowKeysSelect()
    {
        var (window, vm) = await OpenWindowAsync();

        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("my feature");
        Dispatcher.UIThread.RunJobs();

        var create = Assert.Single(vm.Palette.Items, i => i.Title == "Create branch “my-feature”");
        while (vm.Palette.SelectedItem != create)
        {
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        await WaitUntilAsync(() => !vm.IsBusy && vm.CurrentBranch == "my-feature");
    }

    [Fact]
    public void Score_MatchesPrefixSubstringAndFuzzy()
    {
        var push = new PaletteItem("Push", "Remote", () => Task.CompletedTask);

        Assert.Equal(0, CommandPaletteViewModel.Score(push, "pu"));
        Assert.Equal(1, CommandPaletteViewModel.Score(push, "remote"));
        Assert.Equal(3, CommandPaletteViewModel.Score(push, "psh"));
        Assert.Equal(-1, CommandPaletteViewModel.Score(push, "pull"));
    }

    private async Task<(MainWindow Window, MainViewModel Vm)> OpenWindowAsync()
    {
        Directory.CreateDirectory(_dir);
        foreach (var args in new[]
        {
            new[] { "init", "-b", "main" },
            ["config", "user.name", "Test"],
            ["config", "user.email", "test@example.com"],
            ["config", "commit.gpgsign", "false"],
        })
        {
            (await _git.RunAsync(_dir, args)).EnsureSuccess();
        }
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "a\n");
        (await _git.RunAsync(_dir, ["add", "-A"])).EnsureSuccess();
        (await _git.RunAsync(_dir, ["commit", "-m", "Initial"])).EnsureSuccess();
        (await _git.RunAsync(_dir, ["branch", "feature/login"])).EnsureSuccess();

        // Keep tests away from the user's real settings file.
        var vm = new MainViewModel(AppSettings.Load(Path.Combine(_dir, "..", Guid.NewGuid() + ".json")));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.OpenRepositoryAsync(_dir);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasRepository, vm.ErrorMessage);
        return (window, vm);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
    }

    private static void SaveFrame(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        using var stream = File.Create(Path.Combine(Path.GetTempPath(), name));
        frame?.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
