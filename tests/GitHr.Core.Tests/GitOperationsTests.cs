namespace GitHr.Core.Tests;

/// <summary>End-to-end tests for partial staging, discard, branch and commit operations.</summary>
public sealed class GitOperationsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "githr-tests", Guid.NewGuid().ToString("N"));
    private readonly GitRunner _git = new();
    private GitRepository _repo = null!;

    private static readonly string TwentyLines = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\n"));

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        await Git("init", "-b", "main");
        await Git("config", "user.name", "Test User");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "commit.gpgsign", "false");
        await Git("config", "core.autocrlf", "false");
        _repo = await GitRepository.OpenAsync(_dir, _git);
        await CommitFileAsync("f.txt", TwentyLines, "Initial");
    }

    public ValueTask DisposeAsync()
    {
        foreach (var dir in new[] { _dir, _dir + "-remote.git" }.Where(Directory.Exists))
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
        return ValueTask.CompletedTask;
    }

    // ---------- Partial staging ----------

    [Fact]
    public async Task StageOneHunk_LeavesOtherHunkUnstaged()
    {
        await WriteAsync("f.txt", TwentyLines.Replace("line 2\n", "line 2 changed\n").Replace("line 19\n", "line 19 changed\n"));
        var file = new FileChange("f.txt", FileChangeKind.Modified);
        var diff = await _repo.GetWorkingDiffAsync(file, staged: false, cancellationToken: TestContext.Current.CancellationToken);
        var hunks = diff.Select((l, i) => (l, i)).Where(x => x.l.IsHunk).Select(x => x.i).ToList();
        Assert.Equal(2, hunks.Count);

        await _repo.ApplyPatchAsync(PatchBuilder.Build(diff, PatchBuilder.ChangesInHunk(diff, hunks[0]), reverse: false)!, cached: true, reverse: false, cancellationToken: TestContext.Current.CancellationToken);

        var staged = await _repo.GetWorkingDiffAsync(file, staged: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(staged, l => l.Text == "+line 2 changed");
        Assert.DoesNotContain(staged, l => l.Text == "+line 19 changed");
        var unstaged = await _repo.GetWorkingDiffAsync(file, staged: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(unstaged, l => l.Text == "+line 19 changed");
        Assert.DoesNotContain(unstaged, l => l.Text == "+line 2 changed");
    }

    [Fact]
    public async Task StageSelectedLines_ThenUnstageOneOfThem()
    {
        await WriteAsync("f.txt", TwentyLines.Replace("line 5\n", "five\n").Replace("line 6\n", "six\n"));
        var file = new FileChange("f.txt", FileChangeKind.Modified);
        var diff = await _repo.GetWorkingDiffAsync(file, staged: false, cancellationToken: TestContext.Current.CancellationToken);
        var removeFive = IndexOf(diff, "-line 5");
        var addFive = IndexOf(diff, "+five");

        await _repo.ApplyPatchAsync(PatchBuilder.Build(diff, new HashSet<int> { removeFive, addFive }, reverse: false)!, cached: true, reverse: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TwentyLines.Replace("line 5\n", "five\n"), await ShowIndexAsync("f.txt"));

        // Unstage just the "+five" line: index gets "line 5" removed but nothing added.
        var staged = await _repo.GetWorkingDiffAsync(file, staged: true, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.ApplyPatchAsync(PatchBuilder.Build(staged, new HashSet<int> { IndexOf(staged, "+five") }, reverse: true)!, cached: true, reverse: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TwentyLines.Replace("line 5\n", ""), await ShowIndexAsync("f.txt"));
        Assert.Equal(TwentyLines.Replace("line 5\n", "five\n").Replace("line 6\n", "six\n"), await ReadAsync("f.txt")); // working tree untouched
    }

    [Fact]
    public async Task DiscardSelectedLines_RestoresLineInPlace()
    {
        await WriteAsync("f.txt", TwentyLines.Replace("line 5\n", "five\n").Replace("line 6\n", "six\n"));
        var diff = await _repo.GetWorkingDiffAsync(new FileChange("f.txt", FileChangeKind.Modified), staged: false, cancellationToken: TestContext.Current.CancellationToken);

        // Discard only the second replacement (line 6 -> six).
        var patch = PatchBuilder.Build(diff, new HashSet<int> { IndexOf(diff, "-line 6"), IndexOf(diff, "+six") }, reverse: true)!;
        await _repo.ApplyPatchAsync(patch, cached: false, reverse: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TwentyLines.Replace("line 5\n", "five\n"), await ReadAsync("f.txt"));
    }

    [Fact]
    public async Task DiscardHunk_RevertsOnlyThatPartOfTheWorkingTree()
    {
        await WriteAsync("f.txt", TwentyLines.Replace("line 2\n", "line 2 changed\n").Replace("line 19\n", "line 19 changed\n"));
        var diff = await _repo.GetWorkingDiffAsync(new FileChange("f.txt", FileChangeKind.Modified), staged: false, cancellationToken: TestContext.Current.CancellationToken);
        var secondHunk = diff.Select((l, i) => (l, i)).Where(x => x.l.IsHunk).Select(x => x.i).Last();

        await _repo.ApplyPatchAsync(PatchBuilder.Build(diff, PatchBuilder.ChangesInHunk(diff, secondHunk), reverse: true)!, cached: false, reverse: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TwentyLines.Replace("line 2\n", "line 2 changed\n"), await ReadAsync("f.txt"));
    }

    // ---------- Discard ----------

    [Fact]
    public async Task DiscardFile_And_DiscardAll()
    {
        await WriteAsync("f.txt", "changed\n");
        await WriteAsync("new.txt", "new\n");
        await WriteAsync("dir/other.txt", "other\n");

        await _repo.DiscardAsync(new FileChange("f.txt", FileChangeKind.Modified), TestContext.Current.CancellationToken);
        Assert.Equal(TwentyLines, await ReadAsync("f.txt"));

        await _repo.DiscardAsync(new FileChange("new.txt", FileChangeKind.Untracked), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(_dir, "new.txt")));

        await WriteAsync("f.txt", "changed again\n");
        await _repo.DiscardAllAsync(TestContext.Current.CancellationToken);
        var status = await _repo.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Empty(status.Unstaged);
        Assert.Equal(TwentyLines, await ReadAsync("f.txt"));
    }

    [Fact]
    public async Task DiscardKeepsStagedChanges()
    {
        await WriteAsync("f.txt", "staged\n");
        await _repo.StageAllAsync(TestContext.Current.CancellationToken);
        await WriteAsync("f.txt", "staged\nand unstaged\n");

        await _repo.DiscardAsync(new FileChange("f.txt", FileChangeKind.Modified), TestContext.Current.CancellationToken);

        Assert.Equal("staged\n", await ReadAsync("f.txt"));
    }

    // ---------- Branches ----------

    [Fact]
    public async Task CreateRenameDeleteBranch()
    {
        var first = (await _repo.GetCommitsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single();
        await CommitFileAsync("g.txt", "g\n", "Second");

        await _repo.CreateBranchAsync("old-name", first.Sha, checkout: false, cancellationToken: TestContext.Current.CancellationToken);
        await _repo.RenameBranchAsync("old-name", "new-name", TestContext.Current.CancellationToken);
        var branches = await _repo.GetBranchesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.Sha, branches.Single(b => b.Name == "new-name").Sha);
        Assert.DoesNotContain(branches, b => b.Name == "old-name");

        await _repo.DeleteBranchAsync("new-name", cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(await _repo.GetBranchesAsync(TestContext.Current.CancellationToken), b => b.Name == "new-name");
    }

    [Fact]
    public async Task DeleteUnmergedBranch_RequiresForce()
    {
        await _repo.CreateBranchAsync("wip", cancellationToken: TestContext.Current.CancellationToken);
        await CommitFileAsync("wip.txt", "wip\n", "Unmerged work");
        await _repo.CheckoutAsync((await _repo.GetBranchesAsync(TestContext.Current.CancellationToken)).Single(b => b.Name == "main"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(() => _repo.DeleteBranchAsync("wip", cancellationToken: TestContext.Current.CancellationToken));
        await _repo.DeleteBranchAsync("wip", force: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(await _repo.GetBranchesAsync(TestContext.Current.CancellationToken), b => b.Name == "wip");
    }

    [Fact]
    public async Task MergeAndRebase()
    {
        await _repo.CreateBranchAsync("feature", cancellationToken: TestContext.Current.CancellationToken);
        await CommitFileAsync("feature.txt", "f\n", "Feature");
        await _repo.CheckoutAsync((await _repo.GetBranchesAsync(TestContext.Current.CancellationToken)).Single(b => b.Name == "main"), TestContext.Current.CancellationToken);
        await CommitFileAsync("main.txt", "m\n", "Main work");

        // Rebase feature onto main, then merge (fast-forward) into main.
        await _repo.CheckoutAsync((await _repo.GetBranchesAsync(TestContext.Current.CancellationToken)).Single(b => b.Name == "feature"), TestContext.Current.CancellationToken);
        await _repo.RebaseAsync("main", TestContext.Current.CancellationToken);
        Assert.Equal(["Feature", "Main work", "Initial"], (await LogAsync("feature")));

        await _repo.CheckoutAsync((await _repo.GetBranchesAsync(TestContext.Current.CancellationToken)).Single(b => b.Name == "main"), TestContext.Current.CancellationToken);
        await _repo.MergeAsync("feature", TestContext.Current.CancellationToken);
        Assert.Equal(["Feature", "Main work", "Initial"], (await LogAsync("main")));
    }

    [Fact]
    public async Task MergeConflict_IsDetected_AndCanBeAborted()
    {
        await CreateConflictAsync();

        await Assert.ThrowsAsync<GitException>(() => _repo.MergeAsync("other", TestContext.Current.CancellationToken));

        Assert.Equal(RepositoryOperation.Merging, await _repo.GetOperationAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Merge branch 'other'", await _repo.GetMergeMessageAsync(TestContext.Current.CancellationToken));
        Assert.Contains((await _repo.GetStatusAsync(TestContext.Current.CancellationToken)).Unstaged, c => c.Kind == FileChangeKind.Conflicted);

        await _repo.AbortOperationAsync(RepositoryOperation.Merging, TestContext.Current.CancellationToken);
        Assert.Equal(RepositoryOperation.None, await _repo.GetOperationAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RebaseConflict_ResolveAndContinue_WithoutEditor()
    {
        await CreateConflictAsync();
        await _repo.CheckoutAsync((await _repo.GetBranchesAsync(TestContext.Current.CancellationToken)).Single(b => b.Name == "other"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitException>(() => _repo.RebaseAsync("main", TestContext.Current.CancellationToken));
        Assert.Equal(RepositoryOperation.Rebasing, await _repo.GetOperationAsync(TestContext.Current.CancellationToken));

        await WriteAsync("f.txt", "resolved\n");
        await _repo.StageAllAsync(TestContext.Current.CancellationToken);
        await _repo.ContinueOperationAsync(RepositoryOperation.Rebasing, TestContext.Current.CancellationToken);

        Assert.Equal(RepositoryOperation.None, await _repo.GetOperationAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["Other change", "Main change", "Initial"], await LogAsync("other"));
    }

    // ---------- Commits ----------

    [Fact]
    public async Task CherryPickRevertAndTag()
    {
        await _repo.CreateBranchAsync("feature", cancellationToken: TestContext.Current.CancellationToken);
        await CommitFileAsync("picked.txt", "p\n", "To be picked");
        var picked = (await _repo.GetCommitsAsync(cancellationToken: TestContext.Current.CancellationToken))[0];
        await _repo.CheckoutAsync((await _repo.GetBranchesAsync(TestContext.Current.CancellationToken)).Single(b => b.Name == "main"), TestContext.Current.CancellationToken);

        await _repo.CherryPickAsync(picked, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_dir, "picked.txt")));

        var head = (await _repo.GetCommitsAsync(cancellationToken: TestContext.Current.CancellationToken)).First(c => c.Refs.Any(r => r is { Name: "main", IsCurrent: true }));
        await _repo.RevertAsync(head, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(_dir, "picked.txt")));
        Assert.StartsWith("Revert", (await LogAsync("main"))[0]);

        await _repo.CreateTagAsync("v1.0", head.Sha, TestContext.Current.CancellationToken);
        Assert.Contains((await _repo.GetCommitsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single(c => c.Sha == head.Sha).Refs, r => r is { Name: "v1.0", Kind: GitRefKind.Tag });
    }

    [Theory]
    [InlineData(ResetMode.Soft)]
    [InlineData(ResetMode.Mixed)]
    [InlineData(ResetMode.Hard)]
    public async Task Reset_MovesBranch(ResetMode mode)
    {
        var initial = (await _repo.GetCommitsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single();
        await CommitFileAsync("g.txt", "g\n", "Second");

        await _repo.ResetAsync(initial.Sha, mode, TestContext.Current.CancellationToken);

        var status = await _repo.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Initial"], await LogAsync("main"));
        switch (mode)
        {
            case ResetMode.Soft:
                Assert.Contains(status.Staged, c => c.Path == "g.txt");
                break;
            case ResetMode.Mixed:
                Assert.Contains(status.Unstaged, c => c is { Path: "g.txt", Kind: FileChangeKind.Untracked });
                break;
            case ResetMode.Hard:
                Assert.False(File.Exists(Path.Combine(_dir, "g.txt")));
                break;
        }
    }

    [Fact]
    public async Task CheckoutCommit_DetachesHead()
    {
        var initial = (await _repo.GetCommitsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single();
        await CommitFileAsync("g.txt", "g\n", "Second");

        await _repo.CheckoutCommitAsync(initial.Sha, TestContext.Current.CancellationToken);

        Assert.True((await _repo.GetStatusAsync(TestContext.Current.CancellationToken)).IsDetached);
    }

    [Fact]
    public async Task DeleteRemoteBranch()
    {
        var remoteDir = _dir + "-remote.git";
        await _git.RunAsync(Path.GetTempPath(), ["init", "--bare", "-b", "main", remoteDir], cancellationToken: TestContext.Current.CancellationToken);
        await Git("remote", "add", "origin", remoteDir);
        await _repo.PushAsync(cancellationToken: TestContext.Current.CancellationToken);
        await _repo.CreateBranchAsync("to-delete", cancellationToken: TestContext.Current.CancellationToken);
        await _repo.PushAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(await _repo.GetBranchesAsync(TestContext.Current.CancellationToken), b => b.Name == "origin/to-delete");

        await _repo.DeleteRemoteBranchAsync("origin/to-delete", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(await _repo.GetBranchesAsync(TestContext.Current.CancellationToken), b => b.Name == "origin/to-delete");
    }

    // ---------- Helpers ----------

    /// <summary>main and other both change f.txt differently.</summary>
    private async Task CreateConflictAsync()
    {
        await _repo.CreateBranchAsync("other");
        await CommitFileAsync("f.txt", "other\n", "Other change");
        await _repo.CheckoutAsync((await _repo.GetBranchesAsync()).Single(b => b.Name == "main"));
        await CommitFileAsync("f.txt", "main\n", "Main change");
    }

    private async Task CommitFileAsync(string path, string content, string message)
    {
        await WriteAsync(path, content);
        await _repo.StageAllAsync();
        await _repo.CommitAsync(message);
    }

    private async Task WriteAsync(string path, string content)
    {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    private Task<string> ReadAsync(string path) => File.ReadAllTextAsync(Path.Combine(_dir, path));

    private async Task<string> ShowIndexAsync(string path) => (await _git.RunAsync(_dir, ["show", $":{path}"])).EnsureSuccess().Output;

    private async Task<string[]> LogAsync(string branch) =>
        (await _git.RunAsync(_dir, ["log", "--format=%s", branch])).EnsureSuccess().Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static int IndexOf(IReadOnlyList<DiffLine> diff, string text) => diff.Select((l, i) => (l, i)).Single(x => x.l.Text == text).i;

    private async Task Git(params string[] args) => (await _git.RunAsync(_dir, args)).EnsureSuccess();
}
