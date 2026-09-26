namespace GitHr.Core.Tests;

/// <summary>End-to-end tests against a real temporary repository using the installed git.</summary>
public sealed class GitRepositoryTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "githr-tests", Guid.NewGuid().ToString("N"));
    private readonly GitRunner _git = new();
    private GitRepository _repo = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        await Git("init", "-b", "main");
        // Isolate from the developer's global config (signing, hooks, etc.).
        await Git("config", "user.name", "Test User");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "commit.gpgsign", "false");
        await Git("config", "core.autocrlf", "false");
        _repo = await GitRepository.OpenAsync(_dir, _git);
    }

    public Task DisposeAsync()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // git objects are read-only
            }
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EmptyRepository_HasNoCommits()
    {
        Assert.Empty(await _repo.GetCommitsAsync());
        var status = await _repo.GetStatusAsync();
        Assert.Equal("main", status.BranchName);
    }

    [Fact]
    public async Task OpenAsync_FromSubfolder_FindsRoot()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "a", "b")).FullName;

        var repo = await GitRepository.OpenAsync(sub, _git);

        Assert.Equal(Path.GetFullPath(_dir), repo.Root, ignoreCase: true);
    }

    [Fact]
    public async Task OpenAsync_NonRepository_Throws()
    {
        var outside = Directory.CreateTempSubdirectory("githr-norepo").FullName;
        try
        {
            await Assert.ThrowsAsync<GitException>(() => GitRepository.OpenAsync(outside, _git));
        }
        finally
        {
            Directory.Delete(outside);
        }
    }

    [Fact]
    public async Task StageUnstageCommitAndBranch_FullWorkflow()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "readme.md"), "hello\n");

        var status = await _repo.GetStatusAsync();
        Assert.Equal([new FileChange("readme.md", FileChangeKind.Untracked)], status.Unstaged);
        var untrackedDiff = await _repo.GetWorkingDiffAsync(status.Unstaged[0], staged: false);
        Assert.Contains(untrackedDiff, l => l is { Kind: DiffLineKind.Added, Text: "+hello" });

        // Unstage works even before the first commit (no HEAD yet).
        await _repo.StageAsync(["readme.md"]);
        await _repo.UnstageAsync(["readme.md"]);
        Assert.Empty((await _repo.GetStatusAsync()).Staged);

        await _repo.StageAllAsync();
        status = await _repo.GetStatusAsync();
        Assert.Equal([new FileChange("readme.md", FileChangeKind.Added)], status.Staged);

        await _repo.CommitAsync("First commit\n\nWith a body");
        await File.WriteAllTextAsync(Path.Combine(_dir, "readme.md"), "hello world\n");
        await _repo.StageAllAsync();
        await _repo.CommitAsync("Second commit");

        await _repo.CreateBranchAsync("feature/x");
        await File.WriteAllTextAsync(Path.Combine(_dir, "feature.txt"), "feature\n");
        await _repo.StageAllAsync();
        await _repo.CommitAsync("Feature work");

        var commits = await _repo.GetCommitsAsync();
        Assert.Equal(["Feature work", "Second commit", "First commit"], commits.Select(c => c.Subject));
        Assert.Contains(new GitRef("feature/x", GitRefKind.LocalBranch, IsCurrent: true), commits[0].Refs);
        Assert.Contains(new GitRef("main", GitRefKind.LocalBranch), commits[1].Refs);
        Assert.Equal("First commit\n\nWith a body", await _repo.GetCommitMessageAsync(commits[2].Sha));

        var branches = await _repo.GetBranchesAsync();
        Assert.Equal(["feature/x", "main"], branches.Select(b => b.Name));
        Assert.True(branches.Single(b => b.Name == "feature/x").IsCurrent);

        // Commit details, including the root commit.
        Assert.Equal([new FileChange("feature.txt", FileChangeKind.Added)], await _repo.GetCommitChangesAsync(commits[0]));
        Assert.Equal([new FileChange("readme.md", FileChangeKind.Added)], await _repo.GetCommitChangesAsync(commits[2]));
        var diff = await _repo.GetCommitDiffAsync(commits[1], new FileChange("readme.md", FileChangeKind.Modified));
        Assert.Contains(diff, l => l is { Kind: DiffLineKind.Removed, Text: "-hello" });
        Assert.Contains(diff, l => l is { Kind: DiffLineKind.Added, Text: "+hello world" });

        await _repo.CheckoutAsync(branches.Single(b => b.Name == "main"));
        Assert.Equal("main", (await _repo.GetStatusAsync()).BranchName);
    }

    [Fact]
    public async Task PushPullFetch_AgainstLocalRemote()
    {
        var remoteDir = _dir + "-remote.git";
        await _git.RunAsync(Path.GetTempPath(), ["init", "--bare", "-b", "main", remoteDir]);
        try
        {
            await Git("remote", "add", "origin", remoteDir);
            await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "a\n");
            await _repo.StageAllAsync();
            await _repo.CommitAsync("Initial");

            await _repo.PushAsync(); // first push sets upstream
            var status = await _repo.GetStatusAsync();
            Assert.Equal("origin/main", status.Upstream);

            await _repo.FetchAsync();
            await _repo.PullAsync();
            var branches = await _repo.GetBranchesAsync();
            Assert.Contains(branches, b => b is { Name: "origin/main", IsRemote: true });
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(remoteDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(remoteDir, recursive: true);
        }
    }

    private async Task Git(params string[] args) => (await _git.RunAsync(_dir, args)).EnsureSuccess();
}
