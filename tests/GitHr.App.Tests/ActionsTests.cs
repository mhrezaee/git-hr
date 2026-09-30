using Avalonia.Controls;
using Avalonia.VisualTree;
using GitHr.App.ViewModels;

namespace GitHr.App.Tests;

/// <summary>Hunk/line staging, discard, and the branch/commit context menus, driven through the real window.</summary>
public sealed class ActionsTests : UiTestBase
{
    private static readonly string TwentyLines = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\n"));

    private async Task<(Views.MainWindow Window, MainViewModel Vm)> OpenWithTwoHunksAsync()
    {
        var (window, vm) = await OpenWindowAsync(() => CommitAsync("f.txt", TwentyLines, "Add f"));
        await WriteAsync("f.txt", TwentyLines.Replace("line 2\n", "line 2 changed\n").Replace("line 19\n", "line 19 changed\n"));
        await vm.RefreshAsync();
        vm.SelectedUnstagedFile = vm.UnstagedFiles.Single(f => f.Path == "f.txt");
        await WaitUntilAsync(() => vm.DiffLines.Count(l => l.IsHunk) == 2, "diff with two hunks");
        return (window, vm);
    }

    [Fact]
    public Task StageHunkButton_StagesOnlyThatHunk_AndDiffFollowsTheFile() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithTwoHunksAsync();
        SaveFrame(window, "githr-diff-hunks.png");

        Click(window, FindButton(window, "Stage hunk"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.StagedFiles.Count == 1);

        var staged = await RunGitAsync("diff", "--cached");
        Assert.Contains("+line 2 changed", staged);
        Assert.DoesNotContain("+line 19 changed", staged);
        Assert.Contains(vm.UnstagedFiles, f => f.Path == "f.txt"); // second hunk still unstaged

        // The unstaged diff is reloaded and now has only the remaining hunk.
        await WaitUntilAsync(() => vm.DiffLines.Count(l => l.IsHunk) == 1);
        Assert.Contains(vm.DiffLines, l => l.Text == "+line 19 changed");
    });

    [Fact]
    public Task SelectLines_ThenStageLinesButton() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithTwoHunksAsync();
        var list = window.FindControl<ListBox>("DiffList")!;

        list.SelectedItems!.Add(vm.DiffLines.Single(l => l.Text == "-line 19"));
        list.SelectedItems.Add(vm.DiffLines.Single(l => l.Text == "+line 19 changed"));
        await WaitUntilAsync(() => vm.SelectedChangeLineCount == 2);

        Click(window, FindButton(window, "Stage lines"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.StagedFiles.Count == 1);

        var staged = await RunGitAsync("diff", "--cached");
        Assert.Contains("+line 19 changed", staged);
        Assert.DoesNotContain("line 2 changed", staged);
    });

    [Fact]
    public Task UnstageHunk_FromStagedDiff() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithTwoHunksAsync();
        await RunGitAsync("add", "f.txt");
        await vm.RefreshAsync();
        vm.SelectedStagedFile = vm.StagedFiles.Single();
        await WaitUntilAsync(() => vm.IsStagedDiff && vm.DiffLines.Count(l => l.IsHunk) == 2);

        Click(window, FindButton(window, "Unstage hunk"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.UnstagedFiles.Count == 1);

        var staged = await RunGitAsync("diff", "--cached");
        Assert.DoesNotContain("line 2 changed", staged);
        Assert.Contains("+line 19 changed", staged);
    });

    [Fact]
    public Task DiscardHunk_AsksFirst_AndRespectsCancel() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithTwoHunksAsync();

        ConfirmAnswer = false;
        Click(window, FindButton(window, "Discard hunk"));
        await WaitUntilAsync(() => Questions.Count == 1 && !vm.IsBusy);
        Assert.Contains("line 2 changed", await File.ReadAllTextAsync(Path.Combine(Dir, "f.txt")));

        ConfirmAnswer = true;
        Click(window, FindButton(window, "Discard hunk"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.DiffLines.Count(l => l.IsHunk) == 1);
        var content = await File.ReadAllTextAsync(Path.Combine(Dir, "f.txt"));
        Assert.DoesNotContain("line 2 changed", content);
        Assert.Contains("line 19 changed", content);
    });

    [Fact]
    public Task DiscardAll_RemovesChangesAndUntrackedFiles() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync();
        await WriteAsync("a.txt", "changed\n");
        await WriteAsync("new.txt", "new\n");
        await vm.RefreshAsync();
        Assert.Equal(2, vm.UnstagedFiles.Count);

        Click(window, FindButton(window, "Discard all"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.UnstagedFiles.Count == 0);

        Assert.Contains(Questions, q => q.Contains("1 untracked file(s) will be deleted"));
        Assert.False(File.Exists(Path.Combine(Dir, "new.txt")));
    });

    [Fact]
    public Task ConfirmedAction_StillRuns_WhenClosingTheDialogStartsARefresh() => RunUi(async () =>
    {
        var (window, vm) = await OpenWithTwoHunksAsync();
        // Closing a real dialog re-activates the main window, which starts a refresh before the confirmed action runs.
        vm.Confirm = (_, _, _, _) =>
        {
            _ = vm.RefreshIfIdleAsync();
            return Task.FromResult(true);
        };

        Click(window, FindButton(window, "Discard hunk"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.DiffLines.Count(l => l.IsHunk) == 1, "hunk discarded");
        Assert.DoesNotContain("line 2 changed", await File.ReadAllTextAsync(Path.Combine(Dir, "f.txt")));

        Click(window, FindButton(window, "Discard all"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.UnstagedFiles.Count == 0, "all discarded");
    });

    [Fact]
    public Task DiscardFile_FromContextMenu() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync();
        await WriteAsync("a.txt", "changed\n");
        await vm.RefreshAsync();

        RunContextMenuItem(window, RowOf(window, vm.UnstagedFiles.Single()), "Discard changes…");
        await WaitUntilAsync(() => !vm.IsBusy && vm.UnstagedFiles.Count == 0);

        Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(Dir, "a.txt")));
    });

    [Fact]
    public Task CommitContextMenu_CreateTagAndBranch_CherryPick() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync(async () =>
        {
            await RunGitAsync("switch", "feature/login");
            await CommitAsync("login.txt", "login\n", "Add login");
            await RunGitAsync("switch", "main");
        });
        var loginCommit = vm.Commits.Single(c => c.Subject == "Add login");

        PromptAnswer = "v1.0";
        RunContextMenuItem(window, RowOf(window, loginCommit), "Create tag here…");
        await WaitUntilAsync(() => !vm.IsBusy && vm.Commits.Any(c => c.Refs.Any(r => r.Name == "v1.0")));

        PromptAnswer = "hotfix";
        RunContextMenuItem(window, RowOf(window, vm.Commits.Single(c => c.Subject == "Initial")), "Create branch here…");
        await WaitUntilAsync(() => !vm.IsBusy && vm.CurrentBranch == "hotfix");

        RunContextMenuItem(window, RowOf(window, vm.Commits.Single(c => c.Subject == "Add login")), "Cherry-pick onto current branch");
        await WaitUntilAsync(() => !vm.IsBusy && File.Exists(Path.Combine(Dir, "login.txt")));
        Assert.Equal("Add login", (await RunGitAsync("log", "-1", "--format=%s", "hotfix")).Trim());
    });

    [Fact]
    public Task CommitContextMenu_ResetHard_AsksAsDestructive() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync(() => CommitAsync("b.txt", "b\n", "Second"));

        RunContextMenuItem(window, RowOf(window, vm.Commits.Single(c => c.Subject == "Initial")), "Hard — discard all changes…");
        await WaitUntilAsync(() => !vm.IsBusy && vm.Commits.All(c => c.Subject != "Second" || !c.Refs.Any(r => r.Name == "main")));

        Assert.Contains(Questions, q => q.StartsWith("Reset (hard)"));
        Assert.False(File.Exists(Path.Combine(Dir, "b.txt")));
    });

    [Fact]
    public Task BranchContextMenu_RenameAndDelete() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync();

        PromptAnswer = "feature/signin";
        RunContextMenuItem(window, RowOf(window, vm.LocalBranches.Single(b => b.Name == "feature/login")), "Rename…");
        await WaitUntilAsync(() => !vm.IsBusy && vm.LocalBranches.Any(b => b.Name == "feature/signin"));

        RunContextMenuItem(window, RowOf(window, vm.LocalBranches.Single(b => b.Name == "feature/signin")), "Delete…");
        await WaitUntilAsync(() => !vm.IsBusy && vm.LocalBranches.All(b => b.Name != "feature/signin"));
    });

    [Fact]
    public Task MergeConflict_ShowsBanner_AbortRestores() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync(async () =>
        {
            await RunGitAsync("switch", "feature/login");
            await CommitAsync("a.txt", "feature\n", "Feature edit");
            await RunGitAsync("switch", "main");
            await CommitAsync("a.txt", "main\n", "Main edit");
        });

        RunContextMenuItem(window, RowOf(window, vm.LocalBranches.Single(b => b.Name == "feature/login")), "Merge into current branch");
        await WaitUntilAsync(() => !vm.IsBusy && vm.HasOperation);

        Assert.Equal(Core.RepositoryOperation.Merging, vm.Operation);
        Assert.Contains(vm.UnstagedFiles, f => f.Kind == Core.FileChangeKind.Conflicted);
        Assert.StartsWith("Merge branch 'feature/login'", vm.CommitMessage); // prefilled merge message
        SaveFrame(window, "githr-conflict.png");

        Click(window, FindButton(window, "Abort"));
        await WaitUntilAsync(() => !vm.IsBusy && !vm.HasOperation);

        Assert.Equal("main\n", await File.ReadAllTextAsync(Path.Combine(Dir, "a.txt")));
        Assert.Equal("", vm.CommitMessage); // prefilled message cleared again
    });
}
