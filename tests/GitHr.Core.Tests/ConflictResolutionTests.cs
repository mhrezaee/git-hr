using GitHr.Core.Conflicts;

namespace GitHr.Core.Tests;

/// <summary>Resolving real merge conflicts end to end: read, resolve, continue.</summary>
public sealed class ConflictResolutionTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "githr-tests", Guid.NewGuid().ToString("N"));
    private readonly GitRunner _git = new();
    private GitRepository _repo = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Base = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\n"));

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        await Git("init", "-b", "main");
        await Git("config", "user.name", "Test User");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "commit.gpgsign", "false");
        await Git("config", "core.autocrlf", "false");
        _repo = await GitRepository.OpenAsync(_dir, _git, Ct);
        await CommitAsync("f.txt", Base, "Base");
    }

    public ValueTask DisposeAsync()
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
        return ValueTask.CompletedTask;
    }

    /// <summary>main and feature both change line 2 and line 19 differently → two conflicts in f.txt.</summary>
    private async Task MergeWithTwoConflictsAsync()
    {
        await Git("switch", "-c", "feature");
        await CommitAsync("f.txt", Base.Replace("line 2\n", "line 2 feature\n").Replace("line 19\n", "line 19 feature\n"), "Feature");
        await Git("switch", "main");
        await CommitAsync("f.txt", Base.Replace("line 2\n", "line 2 main\n").Replace("line 19\n", "line 19 main\n"), "Main");
        await Assert.ThrowsAsync<GitException>(() => _repo.MergeAsync("feature", Ct));
    }

    [Fact]
    public async Task ReadConflict_ResolvePerConflict_ThenContinueMerge()
    {
        await MergeWithTwoConflictsAsync();

        var conflict = await _repo.GetConflictAsync("f.txt", Ct);
        Assert.True(conflict.CanResolveInText);
        Assert.True(conflict is { HasBase: true, HasOurs: true, HasTheirs: true, IsBinary: false });
        var doc = ConflictDocument.Parse(conflict.WorkingText!);
        Assert.Equal(2, doc.Conflicts.Count);
        Assert.Equal(["line 2 main"], doc.Conflicts[0].OursLines);
        Assert.Equal(["line 2 feature"], doc.Conflicts[0].TheirsLines);
        Assert.StartsWith("feature", doc.Conflicts[0].TheirsLabel);

        await _repo.ResolveWithContentAsync("f.txt", doc.Render([ConflictChoice.Theirs, ConflictChoice.Ours]), Ct);

        Assert.Equal(Base.Replace("line 2\n", "line 2 feature\n").Replace("line 19\n", "line 19 main\n"), await ReadAsync("f.txt"));
        Assert.DoesNotContain((await _repo.GetStatusAsync(Ct)).Unstaged, c => c.Kind == FileChangeKind.Conflicted);

        await _repo.ContinueOperationAsync(RepositoryOperation.Merging, Ct);
        Assert.Equal(RepositoryOperation.None, await _repo.GetOperationAsync(Ct));
        var head = (await _repo.GetCommitsAsync(cancellationToken: Ct))[0];
        Assert.True(head.IsMerge);
    }

    [Theory]
    [InlineData(ConflictSide.Ours, "main")]
    [InlineData(ConflictSide.Theirs, "feature")]
    public async Task TakeSide_UsesThatWholeFile(ConflictSide side, string winner)
    {
        await MergeWithTwoConflictsAsync();

        await _repo.TakeSideAsync("f.txt", side, Ct);

        Assert.Equal(Base.Replace("line 2\n", $"line 2 {winner}\n").Replace("line 19\n", $"line 19 {winner}\n"), await ReadAsync("f.txt"));
        Assert.Empty((await _repo.GetStatusAsync(Ct)).Unstaged);
    }

    [Fact]
    public async Task DeleteModifyConflict_TakeDeletingSide_RemovesFile()
    {
        await Git("switch", "-c", "feature");
        await Git("rm", "-q", "f.txt");
        await Git("commit", "-q", "-m", "Delete f");
        await Git("switch", "main");
        await CommitAsync("f.txt", Base + "more\n", "Modify f");
        await Assert.ThrowsAsync<GitException>(() => _repo.MergeAsync("feature", Ct));

        var conflict = await _repo.GetConflictAsync("f.txt", Ct);
        Assert.True(conflict.IsDeleteConflict);
        Assert.False(conflict.HasTheirs);
        Assert.False(conflict.CanResolveInText);

        await _repo.TakeSideAsync("f.txt", ConflictSide.Theirs, Ct);

        Assert.False(File.Exists(Path.Combine(_dir, "f.txt")));
        await _repo.ContinueOperationAsync(RepositoryOperation.Merging, Ct);
        Assert.Equal(RepositoryOperation.None, await _repo.GetOperationAsync(Ct));
    }

    [Fact]
    public async Task EditedElsewhere_MarkResolved_StagesTheFile()
    {
        await MergeWithTwoConflictsAsync();
        await File.WriteAllTextAsync(Path.Combine(_dir, "f.txt"), "resolved in my editor\n", Ct);

        await _repo.MarkResolvedAsync("f.txt", Ct);

        var status = await _repo.GetStatusAsync(Ct);
        Assert.Empty(status.Unstaged);
        Assert.Contains(status.Staged, c => c.Path == "f.txt");
    }

    [Theory]
    [InlineData("title\n", "Conflict below line 1: BOM is before everything")]
    [InlineData("", "Conflict on line 1: BOM ends up inside each side")]
    public async Task Resolve_KeepsUtf8Bom_ExactlyOnce(string firstLine, string scenario)
    {
        var bom = new System.Text.UTF8Encoding(true);
        await Git("switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(_dir, "bom.txt"), firstLine + "feature\n", bom, Ct);
        await Git("add", "-A");
        await Git("commit", "-q", "-m", "Feature bom");
        await Git("switch", "main");
        await File.WriteAllTextAsync(Path.Combine(_dir, "bom.txt"), firstLine + "main\n", bom, Ct);
        await Git("add", "-A");
        await Git("commit", "-q", "-m", "Main bom");
        await Assert.ThrowsAsync<GitException>(() => _repo.MergeAsync("feature", Ct));

        var conflict = await _repo.GetConflictAsync("bom.txt", Ct);
        var resolved = ConflictDocument.Parse(conflict.WorkingText!).Render([ConflictChoice.Theirs]);
        await _repo.ResolveWithContentAsync("bom.txt", resolved, Ct);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_dir, "bom.txt"), Ct);
        byte[] expected = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(firstLine + "feature\n")];
        Assert.True(expected.SequenceEqual(bytes), scenario);
    }

    private async Task CommitAsync(string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, path), content, Ct);
        await Git("add", "-A");
        await Git("commit", "-q", "-m", message);
    }

    private Task<string> ReadAsync(string path) => File.ReadAllTextAsync(Path.Combine(_dir, path), Ct);

    private async Task Git(params string[] args) => (await _git.RunAsync(_dir, args, cancellationToken: Ct)).EnsureSuccess();
}
