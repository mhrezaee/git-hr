using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitHr.App.ViewModels;
using GitHr.App.Views;

namespace GitHr.App.Tests;

/// <summary>The conflict resolver window, driven like a user: open it, pick sides, save, continue the merge.</summary>
public sealed class ConflictResolverTests : UiTestBase
{
    private static readonly string Base = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\n"));

    /// <summary>main and topic change line 2 and line 19 differently; the merge stops with two conflicts in f.txt.</summary>
    private Task<(MainWindow Window, MainViewModel Vm)> OpenWithMergeConflictAsync() => OpenWindowAsync(async () =>
    {
        await CommitAsync("f.txt", Base, "Add f");
        await RunGitAsync("switch", "-c", "topic");
        await CommitAsync("f.txt", Base.Replace("line 2\n", "line 2 feature\n").Replace("line 19\n", "line 19 feature\n"), "Feature");
        await RunGitAsync("switch", "main");
        await CommitAsync("f.txt", Base.Replace("line 2\n", "line 2 main\n").Replace("line 19\n", "line 19 main\n"), "Main");
        await Git.RunAsync(Dir, ["merge", "topic"]); // exits with conflicts, that's the point
    });

    private static async Task<ConflictResolverWindow> WaitForResolverAsync(Window owner)
    {
        await WaitUntilAsync(() => owner.OwnedWindows.OfType<ConflictResolverWindow>().Any(), "resolver window to open");
        return owner.OwnedWindows.OfType<ConflictResolverWindow>().Single();
    }

    /// <summary>The n-th (0-based) button with this text inside the resolver, i.e. that button of the n-th conflict card.</summary>
    private static Button CardButton(Window resolver, string content, int card) =>
        resolver.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, content)).ElementAt(card);

    [Fact]
    public Task Resolver_PickSidePerConflict_Save_ThenContinueMerge() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithMergeConflictAsync();
        Assert.True(vm.HasOperation);
        Assert.True(vm.HasConflictedFiles);

        Click(window, FindButton(window, "Resolve…")); // banner button
        var resolver = await WaitForResolverAsync(window);
        var model = (ConflictResolverViewModel)resolver.DataContext!;
        Assert.Equal(2, model.Blocks.Count);
        Assert.Equal(["Conflict 1 · line 2", "Conflict 2 · line 19"], model.Blocks.Select(b => b.Header)); // lines as in our file
        Assert.StartsWith("Ours: your current branch", model.OursTitle);
        Assert.StartsWith("Theirs: the branch being merged — topic", model.TheirsTitle);
        Assert.False(resolver.FindControl<Button>("SaveButton")!.IsEnabled);

        Click(resolver, CardButton(resolver, "Use theirs", card: 0));
        Click(resolver, CardButton(resolver, "Use ours", card: 1));

        Assert.Equal(Base.Replace("line 2\n", "line 2 feature\n").Replace("line 19\n", "line 19 main\n"), model.ResultText);
        Assert.Equal("All conflicts resolved — ready to save.", model.StatusText);
        Assert.True(resolver.FindControl<Button>("SaveButton")!.IsEnabled);
        SaveFrame(resolver, "githr-conflict-resolver.png");

        Click(resolver, resolver.FindControl<Button>("SaveButton")!);
        await WaitUntilAsync(() => !vm.IsBusy && !vm.HasConflictedFiles, "file marked resolved");
        Assert.Equal(model.ResultText, await File.ReadAllTextAsync(Path.Combine(Dir, "f.txt")));

        Click(window, FindButton(window, "Continue"));
        await WaitUntilAsync(() => !vm.IsBusy && !vm.HasOperation, "merge completed");
        var parents = (await RunGitAsync("log", "-1", "--format=%P")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parents.Length); // a real merge commit
        Assert.Contains("Merge branch 'topic'", await RunGitAsync("log", "-1", "--format=%s"));
    });

    [Fact]
    public Task Resolver_ManualEditEnablesSave_AndChoicesDontSilentlyOverwriteIt() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithMergeConflictAsync();
        Click(window, FindButton(window, "Resolve…"));
        var resolver = await WaitForResolverAsync(window);
        var model = (ConflictResolverViewModel)resolver.DataContext!;

        model.ResultText = "hand-written result\n";
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.CanSave);
        Assert.Equal("Result edited by hand — ready to save.", model.StatusText);

        ConfirmAnswer = false; // "Rebuild result?" → No
        Click(resolver, CardButton(resolver, "Use ours", card: 0));
        await WaitUntilAsync(() => Questions.Any(q => q.StartsWith("Rebuild result")));
        Assert.Equal("hand-written result\n", model.ResultText);

        Click(resolver, FindButton(resolver, "Cancel"));
        await WaitUntilAsync(() => !window.OwnedWindows.Any());
        Assert.True(vm.HasConflictedFiles); // cancelled: nothing written
    });

    [Fact]
    public Task DoubleClickOnConflictedFile_OpensResolver() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithMergeConflictAsync();
        var row = RowOf(window, vm.UnstagedFiles.Single(f => f.IsConflicted));

        DoubleClick(window, row);

        var resolver = await WaitForResolverAsync(window);
        Click(resolver, FindButton(resolver, "Cancel"));
    });

    [Fact]
    public Task ContextMenu_TakeTheirs_ResolvesWholeFile() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithMergeConflictAsync();

        RunContextMenuItem(window, RowOf(window, vm.UnstagedFiles.Single(f => f.IsConflicted)), "Take theirs (whole file)");
        await WaitUntilAsync(() => !vm.IsBusy && !vm.HasConflictedFiles);

        Assert.Equal(Base.Replace("line 2\n", "line 2 feature\n").Replace("line 19\n", "line 19 feature\n"),
            await File.ReadAllTextAsync(Path.Combine(Dir, "f.txt")));
    });

    [Fact]
    public Task DeleteModifyConflict_ExplainsWholeFileChoice() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync(async () =>
        {
            await RunGitAsync("switch", "feature/login");
            await RunGitAsync("rm", "-q", "a.txt");
            await RunGitAsync("commit", "-q", "-m", "Delete a");
            await RunGitAsync("switch", "main");
            await CommitAsync("a.txt", "changed\n", "Change a");
            await Git.RunAsync(Dir, ["merge", "feature/login"]);
        });

        Click(window, FindButton(window, "Resolve…"));
        await WaitUntilAsync(() => vm.ErrorMessage is not null);

        Assert.Contains("deleted on theirs", vm.ErrorMessage);
        Assert.Contains("Take ours", vm.ErrorMessage);
        Assert.Empty(window.OwnedWindows);
    });

    private static void DoubleClick(Window window, Control control)
    {
        Dispatcher.UIThread.RunJobs();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
