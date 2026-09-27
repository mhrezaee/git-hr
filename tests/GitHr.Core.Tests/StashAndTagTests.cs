using GitHr.Core.Parsing;

namespace GitHr.Core.Tests;

public sealed class StashAndTagTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "githr-tests", Guid.NewGuid().ToString("N"));
    private readonly GitRunner _git = new();
    private GitRepository _repo = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        await Git(_dir, "init", "-b", "main");
        await Git(_dir, "config", "user.name", "Test User");
        await Git(_dir, "config", "user.email", "test@example.com");
        await Git(_dir, "config", "commit.gpgsign", "false");
        await Git(_dir, "config", "tag.gpgsign", "false");
        await Git(_dir, "config", "core.autocrlf", "false");
        _repo = await GitRepository.OpenAsync(_dir, _git, Ct);
        await WriteAsync("a.txt", "a\n");
        await _repo.StageAllAsync(Ct);
        await _repo.CommitAsync("Initial", cancellationToken: Ct);
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

    // ---------- Parsing ----------

    [Fact]
    public void ParseStashes_ReadsBranchAndMessage()
    {
        var output =
            "aaa\u001f1700000000\u001fOn feature/x: half-done refactor\u001e\n" +
            "bbb\u001f1600000000\u001fWIP on main: 1a2b3c4 Initial\u001e\n";

        var stashes = GitOutputParser.ParseStashes(output);

        Assert.Equal(
            [
                new Stash(0, "aaa", "half-done refactor", "feature/x", DateTimeOffset.FromUnixTimeSeconds(1700000000)),
                new Stash(1, "bbb", "WIP on main: 1a2b3c4 Initial", "main", DateTimeOffset.FromUnixTimeSeconds(1600000000)),
            ],
            stashes);
        Assert.Equal("stash@{1}", stashes[1].Name);
    }

    [Fact]
    public void ParseTags_PeelsAnnotatedTags()
    {
        var output =
            "v2.0\u001ftag\u001ftagobj\u001fcommit2\u001f1700000000\u001fRelease 2.0\u001e\n" +
            "v1.0\u001fcommit\u001fcommit1\u001f\u001f1600000000\u001fInitial\u001e\n";

        var tags = GitOutputParser.ParseTags(output);

        Assert.Equal(new Tag("v2.0", "commit2", true, "Release 2.0", DateTimeOffset.FromUnixTimeSeconds(1700000000)), tags[0]);
        // Lightweight tags have no message of their own (the subject is the commit's), so it is not shown.
        Assert.Equal(new Tag("v1.0", "commit1", false, null, DateTimeOffset.FromUnixTimeSeconds(1600000000)), tags[1]);
    }

    // ---------- Stashes ----------

    [Fact]
    public async Task Stash_ListsFilesIncludingUntracked_AndShowsDiffs()
    {
        await WriteAsync("a.txt", "changed\n");
        await WriteAsync("new.txt", "brand new\n");

        await _repo.StashAsync("my work", Ct);

        var stash = Assert.Single(await _repo.GetStashesAsync(Ct));
        Assert.Equal("my work", stash.Message);
        Assert.Equal("main", stash.Branch);
        Assert.Empty((await _repo.GetStatusAsync(Ct)).Unstaged);

        var changes = await _repo.GetStashChangesAsync(stash, Ct);
        Assert.Equal(
            [new FileChange("a.txt", FileChangeKind.Modified), new FileChange("new.txt", FileChangeKind.Untracked)],
            changes);

        var trackedDiff = await _repo.GetStashDiffAsync(stash, changes[0], Ct);
        Assert.Contains(trackedDiff, l => l is { Kind: DiffLineKind.Added, Text: "+changed" });
        var untrackedDiff = await _repo.GetStashDiffAsync(stash, changes[1], Ct);
        Assert.Contains(untrackedDiff, l => l is { Kind: DiffLineKind.Added, Text: "+brand new" });
    }

    [Fact]
    public async Task ApplyKeepsStash_PopRemovesIt_DropDeletes()
    {
        await WriteAsync("a.txt", "first\n");
        await _repo.StashAsync("first", Ct);
        await WriteAsync("a.txt", "second\n");
        await _repo.StashAsync("second", Ct);
        Assert.Equal(["second", "first"], (await _repo.GetStashesAsync(Ct)).Select(s => s.Message));

        // Apply the older one: working tree gets it, list unchanged.
        await _repo.StashApplyAsync(1, Ct);
        Assert.Equal("first\n", await ReadAsync("a.txt"));
        Assert.Equal(2, (await _repo.GetStashesAsync(Ct)).Count);

        await _repo.DiscardAllAsync(Ct);
        await _repo.StashPopAsync(0, Ct);
        Assert.Equal("second\n", await ReadAsync("a.txt"));
        Assert.Equal(["first"], (await _repo.GetStashesAsync(Ct)).Select(s => s.Message));

        await _repo.StashDropAsync(0, Ct);
        Assert.Empty(await _repo.GetStashesAsync(Ct));
    }

    [Fact]
    public async Task Stash_WithoutCommits_HasNoStashes()
    {
        var emptyDir = Directory.CreateDirectory(_dir + "-empty").FullName;
        try
        {
            await Git(emptyDir, "init", "-b", "main");
            var empty = await GitRepository.OpenAsync(emptyDir, _git, Ct);
            Assert.Empty(await empty.GetStashesAsync(Ct));
            Assert.Empty(await empty.GetTagsAsync(Ct));
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    // ---------- Tags ----------

    [Fact]
    public async Task Tags_LightweightAndAnnotated_NewestFirst_ThenDelete()
    {
        var initial = (await _repo.GetCommitsAsync(cancellationToken: Ct)).Single();
        await _repo.CreateTagAsync("v1.0", initial.Sha, cancellationToken: Ct);
        await WriteAsync("b.txt", "b\n");
        await _repo.StageAllAsync(Ct);
        await _repo.CommitAsync("Second", cancellationToken: Ct);
        var second = (await _repo.GetCommitsAsync(cancellationToken: Ct))[0];
        await Task.Delay(1100, Ct); // creator dates have one-second resolution
        await _repo.CreateTagAsync("v2.0", second.Sha, "Release 2.0", Ct);

        var tags = await _repo.GetTagsAsync(Ct);

        Assert.Equal(["v2.0", "v1.0"], tags.Select(t => t.Name));
        Assert.Equal((second.Sha, true, "Release 2.0"), (tags[0].CommitSha, tags[0].IsAnnotated, tags[0].Message));
        Assert.Equal((initial.Sha, false, (string?)null), (tags[1].CommitSha, tags[1].IsAnnotated, tags[1].Message));

        await _repo.DeleteTagAsync("v1.0", Ct);
        Assert.Equal(["v2.0"], (await _repo.GetTagsAsync(Ct)).Select(t => t.Name));
    }

    [Fact]
    public async Task PushTag_ThenDeleteOnRemote()
    {
        var remote = _dir + "-remote.git";
        await Git(Path.GetTempPath(), "init", "--bare", "-b", "main", remote);
        await Git(_dir, "remote", "add", "origin", remote);
        var head = (await _repo.GetCommitsAsync(cancellationToken: Ct)).Single();
        await _repo.CreateTagAsync("v1.0", head.Sha, "First release", Ct);
        await _repo.CreateTagAsync("v1.1", head.Sha, cancellationToken: Ct);

        await _repo.PushTagAsync("v1.0", cancellationToken: Ct);
        Assert.Equal(["v1.0"], await RemoteTagsAsync(remote));

        await _repo.PushTagAsync(null, cancellationToken: Ct); // all tags
        Assert.Equal(["v1.0", "v1.1"], await RemoteTagsAsync(remote));

        await _repo.DeleteRemoteTagAsync("v1.0", Ct);
        Assert.Equal(["v1.1"], await RemoteTagsAsync(remote));
        Assert.Contains(await _repo.GetTagsAsync(Ct), t => t.Name == "v1.0"); // local tag untouched
    }

    [Fact]
    public async Task PushTag_WithoutRemote_ExplainsWhy()
    {
        var ex = await Assert.ThrowsAsync<GitException>(() => _repo.PushTagAsync(null, cancellationToken: Ct));
        Assert.Contains("no remote", ex.Message);
    }

    // ---------- Helpers ----------

    private async Task<string[]> RemoteTagsAsync(string remote) =>
        (await _git.RunAsync(remote, ["tag", "--list"], cancellationToken: Ct)).EnsureSuccess().Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order().ToArray();

    private Task WriteAsync(string path, string content) => File.WriteAllTextAsync(Path.Combine(_dir, path), content, Ct);

    private Task<string> ReadAsync(string path) => File.ReadAllTextAsync(Path.Combine(_dir, path), Ct);

    private async Task Git(string dir, params string[] args) => (await _git.RunAsync(dir, args, cancellationToken: Ct)).EnsureSuccess();
}
