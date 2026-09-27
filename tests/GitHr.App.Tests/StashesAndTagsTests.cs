using GitHr.App.ViewModels;

namespace GitHr.App.Tests;

/// <summary>The TAGS and STASHES sidebar sections, their context menus and the stash details tab.</summary>
public sealed class StashesAndTagsTests : UiTestBase
{
    private string RemoteDir => Dir + "-remote.git";

    [Fact]
    public Task SelectingAStash_ShowsItsFilesAndDiff() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync(async () =>
        {
            await WriteAsync("a.txt", "changed\n");
            await WriteAsync("notes.txt", "untracked notes\n");
            await RunGitAsync("stash", "push", "-u", "-m", "my work");
        });

        var stash = Assert.Single(vm.Stashes);
        Assert.Equal("my work", stash.Message);
        Assert.StartsWith("stash@{0} · main · ", stash.Detail);

        Click(window, RowOf(window, stash));
        await WaitUntilAsync(() => vm.SelectedStashFiles.Count == 2);
        Assert.Equal(MainViewModel.StashTabIndex, vm.SelectedTabIndex);
        Assert.Equal(["a.txt", "notes.txt"], vm.SelectedStashFiles.Select(f => f.Path));

        vm.SelectedStashFile = vm.SelectedStashFiles.Single(f => f.Path == "notes.txt");
        await WaitUntilAsync(() => vm.DiffLines.Any(l => l.Text == "+untracked notes"));
        SaveFrame(window, "githr-stash.png");
    });

    [Fact]
    public Task StashMenu_ApplyKeeps_PopRemoves_DropAsks() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync(async () =>
        {
            await WriteAsync("a.txt", "first\n");
            await RunGitAsync("stash", "push", "-m", "first");
            await WriteAsync("a.txt", "second\n");
            await RunGitAsync("stash", "push", "-m", "second");
        });
        Assert.Equal(["second", "first"], vm.Stashes.Select(s => s.Message));

        RunContextMenuItem(window, RowOf(window, vm.Stashes[1]), "Apply (keep stash)");
        await WaitUntilAsync(() => !vm.IsBusy && vm.UnstagedFiles.Count == 1);
        Assert.Equal("first\n", await File.ReadAllTextAsync(Path.Combine(Dir, "a.txt")));
        Assert.Equal(2, vm.Stashes.Count);

        await RunGitAsync("checkout", "--", "a.txt");
        RunContextMenuItem(window, RowOf(window, vm.Stashes[0]), "Pop (apply and remove)");
        await WaitUntilAsync(() => !vm.IsBusy && vm.Stashes.Count == 1);
        Assert.Equal("second\n", await File.ReadAllTextAsync(Path.Combine(Dir, "a.txt")));

        RunContextMenuItem(window, RowOf(window, vm.Stashes[0]), "Drop…");
        await WaitUntilAsync(() => !vm.IsBusy && vm.Stashes.Count == 0);
        Assert.Contains(Questions, q => q.StartsWith("Drop stash: Delete stash@{0} “first”?"));
    });

    [Fact]
    public Task StashButton_AsksForMessage() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync();
        await WriteAsync("wip.txt", "wip\n");
        await vm.RefreshAsync();

        PromptAnswer = "halfway through the parser";
        Click(window, FindButton(window, "Stash…"));
        await WaitUntilAsync(() => !vm.IsBusy && vm.Stashes.Count == 1);

        Assert.Equal("halfway through the parser", vm.Stashes[0].Message);
        Assert.Empty(vm.UnstagedFiles); // untracked file was stashed too
    });

    [Fact]
    public Task SelectingATag_JumpsToItsCommit() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync(async () =>
        {
            await RunGitAsync("tag", "-a", "v1.0", "-m", "First release");
            await CommitAsync("b.txt", "b\n", "Second");
        });

        var tag = Assert.Single(vm.Tags);
        Assert.Equal("v1.0 → " + tag.ShortSha + "\nFirst release", tag.ToolTip);

        Click(window, RowOf(window, tag));
        await WaitUntilAsync(() => vm.SelectedCommit is not null);
        Assert.Equal("Initial", vm.SelectedCommit!.Subject);
    });

    [Fact]
    public Task TagMenu_PushDeleteRemoteAndLocal() => RunUi(async () =>
    {
        try
        {
            var (window, vm) = await OpenWindowAsync(async () =>
            {
                await Git.RunAsync(Path.GetTempPath(), ["init", "--bare", "-b", "main", RemoteDir]);
                await RunGitAsync("remote", "add", "origin", RemoteDir);
                await RunGitAsync("tag", "v1.0");
            });
            var tag = Assert.Single(vm.Tags);

            RunContextMenuItem(window, RowOf(window, tag), "Push tag to remote");
            await WaitUntilAsync(() => !vm.IsBusy);
            Assert.Null(vm.ErrorMessage);
            Assert.Equal("v1.0", (await Git.RunAsync(RemoteDir, ["tag", "--list"])).Output.Trim());

            RunContextMenuItem(window, RowOf(window, vm.Tags.Single()), "Delete tag on remote…");
            await WaitUntilAsync(() => !vm.IsBusy);
            Assert.Equal("", (await Git.RunAsync(RemoteDir, ["tag", "--list"])).Output.Trim());
            Assert.Single(vm.Tags); // local tag kept

            RunContextMenuItem(window, RowOf(window, vm.Tags.Single()), "Delete tag…");
            await WaitUntilAsync(() => !vm.IsBusy && vm.Tags.Count == 0);
        }
        finally
        {
            DeleteDirectory(RemoteDir);
        }
    });

    [Fact]
    public Task CommitMenu_CreateAnnotatedTag_AsksNameThenMessage() => RunUi(async () =>
    {
        var (window, vm) = await OpenWindowAsync();

        PromptAnswers.Enqueue("v2.0");
        PromptAnswers.Enqueue("Release notes for 2.0");
        RunContextMenuItem(window, RowOf(window, vm.Commits.Single()), "Create annotated tag here…");
        await WaitUntilAsync(() => !vm.IsBusy && vm.Tags.Count == 1);

        Assert.True(vm.Tags[0].IsAnnotated);
        Assert.Equal("Release notes for 2.0", vm.Tags[0].Tag.Message);
        Assert.Contains(vm.Commits.Single().Refs, r => r.Name == "v2.0"); // badge in the graph too

        // The right-clicked commit stays selected across the refresh, with its details reloaded.
        await WaitUntilAsync(() => vm.SelectedCommit is not null && vm.SelectedCommitFiles.Count == 1 && vm.SelectedCommitMessage == "Initial",
            "commit details after refresh");
        SaveFrame(window, "githr-tags.png");
    });

    private static void DeleteDirectory(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
    }
}
