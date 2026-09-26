namespace GitHr.Core.Tests;

public sealed class CloneAndProgressTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "githr-tests", Guid.NewGuid().ToString("N"));
    private readonly GitRunner _git = new();
    private string _source = null!;

    public async ValueTask InitializeAsync()
    {
        _source = Path.Combine(_root, "source");
        Directory.CreateDirectory(_source);
        await Git(_source, "init", "-b", "main");
        await Git(_source, "config", "user.name", "Test User");
        await Git(_source, "config", "user.email", "test@example.com");
        await Git(_source, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(_source, "readme.md"), "hello\n");
        await Git(_source, "add", "-A");
        await Git(_source, "commit", "-m", "Initial");
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    /// <summary>file:// makes git use its network transport, so it prints real progress like a remote clone.</summary>
    private string SourceUrl => new Uri(_source).AbsoluteUri;

    [Fact]
    public async Task Clone_ReportsProgress_AndOpensTheRepository()
    {
        var lines = new List<string>();
        var progress = new SynchronousProgress(lines.Add);

        var repo = await GitRepository.CloneAsync(SourceUrl, Path.Combine(_root, "clone"), progress, _git, TestContext.Current.CancellationToken);

        Assert.Equal("clone", repo.Name);
        Assert.Equal(["Initial"], (await repo.GetCommitsAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(c => c.Subject));
        Assert.Contains(lines, l => l.Contains("objects", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains('\r') || l.Contains('\n'));
        var status = await repo.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal("origin/main", status.Upstream);
    }

    [Fact]
    public async Task Clone_IntoNonEmptyFolder_Throws()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "busy")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "x", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<GitException>(() => GitRepository.CloneAsync(SourceUrl, target, git: _git, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("not empty", ex.Message);
    }

    [Fact]
    public async Task Clone_BadUrl_ReportsGitError()
    {
        var target = Path.Combine(_root, "missing");

        var ex = await Assert.ThrowsAsync<GitException>(() =>
            GitRepository.CloneAsync(new Uri(Path.Combine(_root, "does-not-exist")).AbsoluteUri, target, git: _git, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("git clone", ex.Message);
    }

    [Fact]
    public async Task Clone_Cancelled_RemovesPartialFolder()
    {
        var target = Path.Combine(_root, "cancelled");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitRepository.CloneAsync(SourceUrl, target, git: _git, cancellationToken: cts.Token));

        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Fetch_ReportsProgress()
    {
        var repo = await GitRepository.CloneAsync(SourceUrl, Path.Combine(_root, "clone"), git: _git, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_source, "second.txt"), "2\n", TestContext.Current.CancellationToken);
        await Git(_source, "add", "-A");
        await Git(_source, "commit", "-m", "Second");

        var lines = new List<string>();
        await repo.FetchAsync(new SynchronousProgress(lines.Add), TestContext.Current.CancellationToken);

        Assert.NotEmpty(lines);
        Assert.Equal(1, (await repo.GetStatusAsync(TestContext.Current.CancellationToken)).Behind);
    }

    [Fact]
    public async Task Amend_ReplacesLastCommit()
    {
        var repo = await GitRepository.OpenAsync(_source, _git, TestContext.Current.CancellationToken);
        Assert.True(await repo.HasCommitsAsync(TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(Path.Combine(_source, "forgotten.txt"), "oops\n", TestContext.Current.CancellationToken);
        await repo.StageAllAsync(TestContext.Current.CancellationToken);

        await repo.CommitAsync("Initial, with the forgotten file", amend: true, cancellationToken: TestContext.Current.CancellationToken);

        var commits = await repo.GetCommitsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["Initial, with the forgotten file"], commits.Select(c => c.Subject));
        Assert.Contains(await repo.GetCommitChangesAsync(commits[0], TestContext.Current.CancellationToken), c => c.Path == "forgotten.txt");
    }

    [Fact]
    public async Task AmendPublishedCommit_NeedsForcePushWithLease()
    {
        var bare = Path.Combine(_root, "remote.git");
        await Git(_root, "clone", "--bare", _source, bare);
        var repo = await GitRepository.CloneAsync(new Uri(bare).AbsoluteUri, Path.Combine(_root, "work"), git: _git, cancellationToken: TestContext.Current.CancellationToken);
        await Git(repo.Root, "config", "user.name", "Test User");
        await Git(repo.Root, "config", "user.email", "test@example.com");
        await Git(repo.Root, "config", "commit.gpgsign", "false");

        await repo.CommitAsync("Reworded initial commit", amend: true, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<GitException>(() => repo.PushAsync(cancellationToken: TestContext.Current.CancellationToken)); // rejected: non-fast-forward

        await repo.PushAsync(forceWithLease: true, cancellationToken: TestContext.Current.CancellationToken);
        var remoteSubject = (await _git.RunAsync(bare, ["log", "-1", "--format=%s", "main"], cancellationToken: TestContext.Current.CancellationToken)).EnsureSuccess().Output.Trim();
        Assert.Equal("Reworded initial commit", remoteSubject);
    }

    [Theory]
    [InlineData("https://github.com/mhrezaee/git-hr.git", "git-hr")]
    [InlineData("https://github.com/mhrezaee/git-hr", "git-hr")]
    [InlineData("https://github.com/mhrezaee/git-hr/", "git-hr")]
    [InlineData("git@github.com:mhrezaee/git-hr.git", "git-hr")]
    [InlineData("ssh://git@host:2222/team/app.git", "app")]
    [InlineData("https://dev.azure.com/org/project/_git/backend", "backend")]
    [InlineData(@"C:\repos\local-repo", "local-repo")]
    [InlineData("  https://example.com/x/Repo.GIT  ", "Repo")]
    [InlineData("", null)]
    [InlineData("https://", null)]
    public void GetRepositoryName(string url, string? expected)
    {
        Assert.Equal(expected, GitUrl.GetRepositoryName(url));
    }

    private async Task Git(string dir, params string[] args) => (await _git.RunAsync(dir, args)).EnsureSuccess();

    /// <summary>Progress&lt;T&gt; posts to a thread pool without a sync context; tests need the calls in order.</summary>
    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        private readonly object _lock = new();

        public void Report(string value)
        {
            lock (_lock)
            {
                report(value);
            }
        }
    }
}
