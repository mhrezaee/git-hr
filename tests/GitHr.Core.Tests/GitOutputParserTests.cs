using GitHr.Core.Parsing;

namespace GitHr.Core.Tests;

public class GitOutputParserTests
{
    [Fact]
    public void ParseLog_ReadsFieldsParentsAndRefs()
    {
        var output =
            "aaa\u001fbbb ccc\u001fJane\u001fjane@example.com\u001f1700000000\u001fHEAD -> refs/heads/main, refs/remotes/origin/main, refs/remotes/origin/HEAD, tag: refs/tags/v1.0\u001fMerge feature\u001e\n" +
            "bbb\u001f\u001fJohn\u001fjohn@example.com\u001f1600000000\u001f\u001fInitial commit\u001e\n";

        var commits = GitOutputParser.ParseLog(output);

        Assert.Equal(2, commits.Count);
        var merge = commits[0];
        Assert.Equal("aaa", merge.Sha);
        Assert.Equal(["bbb", "ccc"], merge.Parents);
        Assert.True(merge.IsMerge);
        Assert.Equal("Jane", merge.AuthorName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), merge.AuthorDate);
        Assert.Equal("Merge feature", merge.Subject);
        Assert.Equal(
            [
                new GitRef("main", GitRefKind.LocalBranch, true),
                new GitRef("origin/main", GitRefKind.RemoteBranch),
                new GitRef("v1.0", GitRefKind.Tag),
            ],
            merge.Refs);

        Assert.Empty(commits[1].Parents);
        Assert.Empty(commits[1].Refs);
    }

    [Fact]
    public void ParseDecorations_DetachedHead()
    {
        var refs = GitOutputParser.ParseDecorations("HEAD, refs/heads/feature/x");

        Assert.Equal([new GitRef("HEAD", GitRefKind.Head, true), new GitRef("feature/x", GitRefKind.LocalBranch)], refs);
    }

    [Fact]
    public void ParseBranches_ReadsTrackingInfoAndSkipsRemoteHead()
    {
        var output =
            "refs/heads/main\u001f111\u001f*\u001forigin/main\u001fahead 2, behind 3\n" +
            "refs/heads/old\u001f222\u001f \u001forigin/old\u001fgone\n" +
            "refs/remotes/origin/HEAD\u001f111\u001f \u001f\u001f\n" +
            "refs/remotes/origin\u001f111\u001f \u001f\u001f\n" +
            "refs/remotes/origin/main\u001f333\u001f \u001f\u001f\n";

        var branches = GitOutputParser.ParseBranches(output);

        Assert.Equal(3, branches.Count);
        Assert.Equal(new Branch("main", "refs/heads/main", "111", false, true, "origin/main", 2, 3, false), branches[0]);
        Assert.True(branches[1].IsUpstreamGone);
        Assert.Equal("origin/main", branches[2].Name);
        Assert.True(branches[2].IsRemote);
    }

    [Fact]
    public void ParseStatus_SplitsStagedAndUnstaged()
    {
        var output = string.Join('\0',
            "# branch.oid 1234",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +1 -4",
            "1 M. N... 100644 100644 100644 aaa bbb staged only.txt",
            "1 .M N... 100644 100644 100644 aaa bbb unstaged.txt",
            "1 MM N... 100644 100644 100644 aaa bbb both.txt",
            "1 A. N... 000000 100644 100644 000 bbb new.txt",
            "2 R. N... 100644 100644 100644 aaa bbb R100 renamed.txt",
            "original.txt",
            "u UU N... 100644 100644 100644 100644 a b c conflict.txt",
            "? untracked dir/file.txt",
            "");

        var status = GitOutputParser.ParseStatus(output);

        Assert.Equal("main", status.BranchName);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(1, status.Ahead);
        Assert.Equal(4, status.Behind);
        Assert.Equal(
            [
                new FileChange("staged only.txt", FileChangeKind.Modified),
                new FileChange("both.txt", FileChangeKind.Modified),
                new FileChange("new.txt", FileChangeKind.Added),
                new FileChange("renamed.txt", FileChangeKind.Renamed, "original.txt"),
            ],
            status.Staged);
        Assert.Equal(
            [
                new FileChange("unstaged.txt", FileChangeKind.Modified),
                new FileChange("both.txt", FileChangeKind.Modified),
                new FileChange("conflict.txt", FileChangeKind.Conflicted),
                new FileChange("untracked dir/file.txt", FileChangeKind.Untracked),
            ],
            status.Unstaged);
    }

    [Fact]
    public void ParseStatus_DetachedHead()
    {
        var status = GitOutputParser.ParseStatus("# branch.oid 1234\0# branch.head (detached)\0");

        Assert.True(status.IsDetached);
    }

    [Fact]
    public void ParseNameStatus_HandlesRenames()
    {
        var changes = GitOutputParser.ParseNameStatus("M\0a.txt\0R087\0old.txt\0new.txt\0D\0gone.txt\0");

        Assert.Equal(
            [
                new FileChange("a.txt", FileChangeKind.Modified),
                new FileChange("new.txt", FileChangeKind.Renamed, "old.txt"),
                new FileChange("gone.txt", FileChangeKind.Deleted),
            ],
            changes);
    }

    [Fact]
    public void ParseDiff_ClassifiesLines()
    {
        var output = "diff --git a/f b/f\nindex 1..2 100644\n--- a/f\n+++ b/f\n@@ -1,2 +1,2 @@\n same\n-old\n+new\n";

        var lines = GitOutputParser.ParseDiff(output);

        Assert.Equal(
            [
                DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Header,
                DiffLineKind.Hunk, DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added,
            ],
            lines.Select(l => l.Kind));
    }
}
