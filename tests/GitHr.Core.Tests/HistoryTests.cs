using GitHr.Core.Diff;
using GitHr.Core.History;

namespace GitHr.Core.Tests;

/// <summary>History search, file history (across renames) and blame, against real repositories.</summary>
public sealed class HistoryTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "githr-tests", Guid.NewGuid().ToString("N"));
    private readonly GitRunner _git = new();
    private GitRepository _repo = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        await Git("init", "-b", "main");
        await Git("config", "user.name", "Test User");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "commit.gpgsign", "false");
        await Git("config", "core.autocrlf", "false");
        _repo = await GitRepository.OpenAsync(_dir, _git, Ct);

        // History (oldest first): add old.txt, change it on a side branch, rename it, change it as another author.
        await CommitAsync("old.txt", "one\ntwo\nthree\nfour\nfive\nsix\n", "Add numbers file");
        await CommitAsync("other.txt", "unrelated\n", "Add other file");
        await Git("checkout", "-q", "-b", "side");
        await CommitAsync("side.txt", "secret token\n", "Side work: add token");
        await Git("checkout", "-q", "main");
        await CommitAsync("old.txt", "one\nTWO\nthree\nfour\nfive\nsix\n", "Shout two");
        await Git("mv", "old.txt", "numbers.txt");
        await Git("commit", "-q", "-m", "Rename to numbers.txt");
        await WriteAsync("numbers.txt", "one\nTWO\nthree\nfour\nfive\nSIX\n");
        await Git("add", "-A");
        await Git("-c", "user.name=Ada Lovelace", "-c", "user.email=ada@example.com", "commit", "-q", "-m", "Shout six");
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

    // ---------- Search ----------

    [Fact]
    public async Task Search_ByMessage_IsCaseInsensitive_AndTakesTextLiterally()
    {
        var found = await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Message, "SHOUT"), cancellationToken: Ct);
        Assert.Equal(["Shout six", "Shout two"], found.Select(c => c.Subject));

        // As a regular expression, "n.mbers" would match "numbers".
        Assert.Contains(await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Message, "numbers.txt"), cancellationToken: Ct),
            c => c.Subject == "Rename to numbers.txt");
        Assert.Empty(await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Message, "n.mbers"), cancellationToken: Ct));
    }

    [Fact]
    public async Task Search_ByAuthor_MatchesNameOrEmail()
    {
        var byName = await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Author, "lovelace"), cancellationToken: Ct);
        var byEmail = await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Author, "ada@example"), cancellationToken: Ct);
        Assert.Equal(["Shout six"], byName.Select(c => c.Subject));
        Assert.Equal(["Shout six"], byEmail.Select(c => c.Subject));
    }

    [Fact]
    public async Task Search_ByCode_FindsTheCommitThatAddedTheText_OnAnyBranch()
    {
        var found = await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Code, "secret token"), cancellationToken: Ct);
        Assert.Equal(["Side work: add token"], found.Select(c => c.Subject));
        Assert.Contains(found[0].Refs, r => r.Name == "side");
    }

    [Fact]
    public async Task Search_BySha_AcceptsAbbreviationsAndRevisions_AndIgnoresUnknownOnes()
    {
        var head = (await _git.RunAsync(_dir, ["rev-parse", "HEAD~1"], cancellationToken: Ct)).Output.Trim();

        var byPrefix = await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Sha, head[..8]), cancellationToken: Ct);
        Assert.Equal(head, Assert.Single(byPrefix).Sha);
        Assert.Equal(head, Assert.Single(await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Sha, "HEAD~1"), cancellationToken: Ct)).Sha);
        Assert.Empty(await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Sha, "deadbeef"), cancellationToken: Ct));
        Assert.Empty(await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Sha, "--all"), cancellationToken: Ct));
    }

    [Fact]
    public async Task Search_WithBlankText_FindsNothing() =>
        Assert.Empty(await _repo.SearchCommitsAsync(new HistorySearch(HistorySearchKind.Message, "  "), cancellationToken: Ct));

    // ---------- File history ----------

    [Fact]
    public async Task FileHistory_FollowsRenames_WithThePathOfEachCommit()
    {
        var history = await _repo.GetFileHistoryAsync("numbers.txt", cancellationToken: Ct);

        Assert.Equal(
            [
                ("Shout six", "numbers.txt", null),
                ("Rename to numbers.txt", "numbers.txt", "old.txt"),
                ("Shout two", "old.txt", null),
                ("Add numbers file", "old.txt", null),
            ],
            history.Select(r => (r.Commit.Subject, r.Path, r.OriginalPath)));
    }

    [Fact]
    public async Task FileHistory_PathsWithSpaces()
    {
        await CommitAsync("my notes.md", "# notes\n", "Add notes");
        var history = await _repo.GetFileHistoryAsync("my notes.md", cancellationToken: Ct);
        Assert.Equal("my notes.md", Assert.Single(history).Path);
    }

    [Fact]
    public void ParseFileLog_MergeWithoutFileList_KeepsTheNewerPath()
    {
        var output =
            "\u001eaaa\u001fp1\u001fA\u001fa@x\u001f1700000000\u001f\u001fChange\0\nM\0new.txt\0" +
            "\u001ebbb\u001fp2 p3\u001fA\u001fa@x\u001f1600000000\u001f\u001fMerge\0" +
            "\u001eccc\u001f\u001fA\u001fa@x\u001f1500000000\u001f\u001fRename\0\nR087\0old.txt\0new.txt\0";

        var history = HistoryParser.ParseFileLog(output, "new.txt");

        Assert.Equal([("aaa", "new.txt"), ("bbb", "new.txt"), ("ccc", "new.txt")], history.Select(r => (r.Commit.Sha, r.Path)));
        Assert.Equal("old.txt", history[2].OriginalPath);
    }

    // ---------- Blame ----------

    [Fact]
    public async Task Blame_AttributesEachLineToItsCommit_AcrossTheRename()
    {
        var blame = await _repo.GetBlameAsync("numbers.txt", cancellationToken: Ct);

        Assert.Equal(["one", "TWO", "three", "four", "five", "SIX"], blame.Lines.Select(l => l.Text));
        Assert.Equal(Enumerable.Range(1, 6), blame.Lines.Select(l => l.Number));
        Assert.Equal("Add numbers file", blame.Lines[0].Commit.Summary);
        Assert.Equal("Shout two", blame.Lines[1].Commit.Summary);
        Assert.Equal("Shout six", blame.Lines[5].Commit.Summary);
        Assert.Equal("Ada Lovelace", blame.Lines[5].Commit.Author);
        Assert.Equal("ada@example.com", blame.Lines[5].Commit.AuthorEmail);
        Assert.Same(blame.Lines[0].Commit, blame.Lines[2].Commit); // one object per commit
    }

    [Fact]
    public async Task Blame_OfTheWorkingTree_MarksUncommittedLines()
    {
        await WriteAsync("numbers.txt", "one\nTWO\nthree\nFOUR\nfive\nSIX\n");

        var blame = await _repo.GetBlameAsync("numbers.txt", cancellationToken: Ct);

        Assert.True(blame.Lines[3].Commit.IsUncommitted);
        Assert.Equal("Not committed yet", blame.Lines[3].Commit.Summary);
        Assert.False(blame.Lines[0].Commit.IsUncommitted);
    }

    [Fact]
    public async Task Blame_AtARevision_ShowsTheFileAsItWasThen()
    {
        var blame = await _repo.GetBlameAsync("old.txt", "HEAD~2", Ct);
        Assert.Equal("TWO", blame.Lines[1].Text);
        Assert.Equal("six", blame.Lines[5].Text);
        Assert.Equal("HEAD~2", blame.Revision);
    }

    [Fact]
    public void ParseBlame_KeepsCrlfOutOfTheText_AndTabsInside()
    {
        var sha = new string('a', 40);
        var output = $"{sha} 1 1 2\nauthor X\nauthor-mail <x@y>\nauthor-time 1700000000\nsummary S\nboundary\nfilename f\n\tif (x)\r\n" +
                     $"{sha} 2 2\n\t\treturn;\r\n";

        var blame = HistoryParser.ParseBlame(output, "f", null);

        Assert.Equal(["if (x)", "\treturn;"], blame.Lines.Select(l => l.Text));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), blame.Lines[0].Commit.Date);
    }

    [Fact]
    public void HighlightLines_ColorsAWholeFile()
    {
        var spans = SyntaxHighlighter.Shared.Highlight("f.cs", ["/* a", "   b */", "int x;"]);
        Assert.Equal(spans[0][0].Color, spans[1][0].Color); // still inside the comment
        Assert.NotEqual(spans[0][0].Color, spans[2][0].Color);
    }

    private async Task CommitAsync(string path, string content, string message)
    {
        await WriteAsync(path, content);
        await Git("add", "-A");
        await Git("commit", "-q", "-m", message);
    }

    private Task WriteAsync(string path, string content) => File.WriteAllTextAsync(Path.Combine(_dir, path), content, Ct);

    private async Task Git(params string[] args) => (await _git.RunAsync(_dir, args, cancellationToken: Ct)).EnsureSuccess();
}
