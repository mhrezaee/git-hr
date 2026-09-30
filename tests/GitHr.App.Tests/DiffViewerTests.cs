using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitHr.App.Controls;
using GitHr.App.ViewModels;

namespace GitHr.App.Tests;

/// <summary>Syntax highlighting, changed-word emphasis and the side-by-side layout, driven through the real window.</summary>
public sealed class DiffViewerTests : UiTestBase
{
    private const string Original =
        "namespace Demo;\n\npublic class Greeter\n{\n    public string Name = \"world\";\n    public int Count = 1;\n}\n";

    private async Task<(Views.MainWindow Window, MainViewModel Vm)> OpenWithCSharpChangeAsync()
    {
        var (window, vm) = await OpenWindowAsync(() => CommitAsync("Greeter.cs", Original, "Add greeter"));
        await WriteAsync("Greeter.cs", Original.Replace("\"world\"", "\"GitHr\"").Replace("    public int Count = 1;\n", ""));
        await vm.RefreshAsync();
        vm.SelectedUnstagedFile = vm.UnstagedFiles.Single();
        await WaitUntilAsync(() => vm.DiffLines.Any(l => l.IsHunk), "diff loaded");
        return (window, vm);
    }

    private static IEnumerable<DiffText> VisibleDiffTexts(Window window, string listName)
    {
        Dispatcher.UIThread.RunJobs();
        return window.FindControl<ListBox>(listName)!.GetVisualDescendants().OfType<DiffText>().Where(t => t.IsEffectivelyVisible);
    }

    [Fact]
    public Task UnifiedDiff_ColorsCodeAndEmphasizesTheChangedWord() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithCSharpChangeAsync();
        SaveFrame(window, "githr-diff-unified.png");

        var added = VisibleDiffTexts(window, "DiffList").Single(t => t.Styled?.Text.Contains("GitHr") == true);
        var runs = added.Inlines!.OfType<Run>().ToList();
        Assert.Equal(added.Styled!.Text, string.Concat(runs.Select(r => r.Text)));

        // "public" is a keyword, the string literal has another color.
        var keyword = runs.First(r => r.Text == "public").Foreground;
        Assert.NotNull(keyword);
        Assert.NotEqual(keyword, runs.First(r => r.Text!.Contains("GitHr")).Foreground);

        // Only the changed word sits on the emphasis background.
        Assert.Equal("GitHr", string.Concat(runs.Where(r => r.Background is not null).Select(r => r.Text)));
    });

    [Fact]
    public Task SideBySide_PairsLines_AndIsRemembered() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithCSharpChangeAsync();

        Click(window, window.FindControl<ToggleButton>("SplitDiffToggle")!);
        await WaitUntilAsync(() => vm.IsSplitDiff && vm.DiffRows.Count > 0, "side-by-side rows");
        SaveFrame(window, "githr-diff-side-by-side.png");

        var replaced = vm.DiffRows.Single(r => r.Left.Text.Contains("\"world\""));
        Assert.Contains("\"GitHr\"", replaced.Right.Text);
        Assert.Equal(replaced.Left.Number, replaced.Right.Number); // same line in old and new
        var deleted = vm.DiffRows.Single(r => r.Left.Text.Contains("Count"));
        Assert.True(deleted.Right.IsEmpty);
        Assert.False(window.FindControl<ListBox>("DiffList")!.IsEffectivelyVisible);
        Assert.Contains(VisibleDiffTexts(window, "SplitDiffList"), t => t.Styled?.Text.Contains("\"GitHr\"") == true);

        // A new window (next app start) opens in the same layout.
        var settings = AppSettings.Load(Path.Combine(Path.GetTempPath(), "githr-apptests", "split-" + Guid.NewGuid() + ".json"));
        settings.SetSplitDiff(true);
        Assert.True(new MainViewModel(AppSettings.Load(settings.FilePath)).IsSplitDiff);

        Click(window, window.FindControl<ToggleButton>("SplitDiffToggle")!);
        await WaitUntilAsync(() => !vm.IsSplitDiff && vm.DiffRows.Count == 0);
    });

    [Fact]
    public Task SideBySide_SelectingARowStagesBothOfItsLines() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithCSharpChangeAsync();
        vm.IsSplitDiff = true;
        await WaitUntilAsync(() => vm.DiffRows.Count > 0);

        var list = window.FindControl<ListBox>("SplitDiffList")!;
        list.SelectedItems!.Add(vm.DiffRows.Single(r => r.Left.Text.Contains("\"world\"")));
        await WaitUntilAsync(() => vm.SelectedChangeLineCount == 2);

        Click(window, FindButton(window, "Stage lines"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.StagedFiles.Count == 1);

        var staged = await RunGitAsync("diff", "--cached");
        Assert.Contains("+    public string Name = \"GitHr\";", staged);
        Assert.DoesNotContain("-    public int Count", staged); // the deleted line was not selected
    });

    [Fact]
    public Task SideBySide_HunkButtonsWork() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithCSharpChangeAsync();
        vm.IsSplitDiff = true;
        await WaitUntilAsync(() => vm.DiffRows.Any(r => r.IsHunk));

        Click(window, FindButton(window, "Stage hunk"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.StagedFiles.Count == 1 && vm.UnstagedFiles.Count == 0);
    });

    [Fact]
    public Task UnknownFileTypes_StayPlain() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync();
        await WriteAsync("a.txt", "changed\n");
        await vm.RefreshAsync();
        vm.SelectedUnstagedFile = vm.UnstagedFiles.Single();
        await WaitUntilAsync(() => vm.DiffLines.Any(l => l.Text == "+changed"));

        Assert.All(vm.DiffLines, l => Assert.Empty(l.Styled.Syntax));
        var added = VisibleDiffTexts(window, "DiffList").Single(t => t.Styled?.Text == "+changed");
        Assert.Equal("+changed", string.Concat(added.Inlines!.OfType<Run>().Select(r => r.Text)));
    });
}
