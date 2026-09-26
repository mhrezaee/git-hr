using GitHr.Core.Parsing;

namespace GitHr.Core.Tests;

public class PatchBuilderTests
{
    private const string Diff =
        "diff --git a/f.txt b/f.txt\n" +
        "index 111..222 100644\n" +
        "--- a/f.txt\n" +
        "+++ b/f.txt\n" +
        "@@ -1,4 +1,4 @@\n" +
        " one\n" +
        "-two\n" +
        "-three\n" +
        "+TWO\n" +
        "+THREE\n" +
        " four\n";

    private static readonly IReadOnlyList<DiffLine> Lines = GitOutputParser.ParseDiff(Diff);

    [Fact]
    public void ParseDiff_TracksLineNumbers()
    {
        Assert.Equal((1, 1), (Lines[5].OldLineNumber, Lines[5].NewLineNumber)); // " one"
        Assert.Equal((2, null), (Lines[6].OldLineNumber, Lines[6].NewLineNumber)); // "-two"
        Assert.Equal((null, 2), (Lines[8].OldLineNumber, Lines[8].NewLineNumber)); // "+TWO"
        Assert.Equal((4, 4), (Lines[10].OldLineNumber, Lines[10].NewLineNumber)); // " four"
    }

    [Fact]
    public void WholeHunk_IsReproduced()
    {
        var patch = PatchBuilder.Build(Lines, PatchBuilder.ChangesInHunk(Lines, 4), reverse: false);

        Assert.Equal(Diff, patch);
    }

    [Fact]
    public void Forward_UnselectedRemovalsBecomeContext_UnselectedAdditionsDropped()
    {
        // Stage only "-two" and "+TWO".
        var patch = PatchBuilder.Build(Lines, new HashSet<int> { 6, 8 }, reverse: false);

        // "-three" stays as context but moves after "+TWO", so TWO replaces two in place.
        Assert.EndsWith("@@ -1,4 +1,4 @@\n one\n-two\n+TWO\n three\n four\n", patch);
    }

    [Fact]
    public void Reverse_UnselectedAdditionsBecomeContext_UnselectedRemovalsDropped()
    {
        // Unstage/discard only "+THREE" (and nothing else).
        var patch = PatchBuilder.Build(Lines, new HashSet<int> { 9 }, reverse: true);

        Assert.EndsWith("@@ -1,3 +1,4 @@\n one\n TWO\n+THREE\n four\n", patch);
    }

    [Fact]
    public void NothingSelected_ReturnsNull()
    {
        Assert.Null(PatchBuilder.Build(Lines, new HashSet<int>(), reverse: false));
    }

    [Fact]
    public void KeepsCarriageReturnsOfCrlfFiles()
    {
        var lines = GitOutputParser.ParseDiff("diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n-a\r\n+b\r\n");

        Assert.Equal("-a", lines[4].Text);
        var patch = PatchBuilder.Build(lines, new HashSet<int> { 4, 5 }, reverse: false);
        Assert.EndsWith("-a\r\n+b\r\n", patch);
    }
}
