using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using GitHr.App.ViewModels;

namespace GitHr.App.Tests;

public sealed class CommandPaletteTests : UiTestBase
{
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
        await WriteAsync("wip.txt", "work in progress\n");
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
}
