using GitHr.Core.Diff;
using GitHr.Core.Parsing;

namespace GitHr.Core.Tests;

/// <summary>Side-by-side rows, changed-part emphasis and syntax highlighting for the diff viewer.</summary>
public class DiffViewTests
{
    private static IReadOnlyList<DiffLine> Parse(params string[] body) => GitOutputParser.ParseDiff(
        "diff --git a/f.cs b/f.cs\n--- a/f.cs\n+++ b/f.cs\n" + string.Concat(body.Select(line => line + "\n")));

    private static string Describe(DiffRow row) => row.Kind switch
    {
        DiffRowKind.Lines => $"{row.Left?.Line.Text ?? "·"} | {row.Right?.Line.Text ?? "·"}",
        _ => $"{row.Kind}: {row.Left!.Line.Text}",
    };

    // ---------- Side by side ----------

    [Fact]
    public void SideBySide_PairsReplacements_AndPadsTheLongerSide()
    {
        var rows = SideBySideDiff.Build(Parse("@@ -1,4 +1,4 @@", " one", "-two", "-three", "+TWO", " four", "+five"));

        Assert.Equal(
        [
            "Header: diff --git a/f.cs b/f.cs", "Header: --- a/f.cs", "Header: +++ b/f.cs",
            "Hunk: @@ -1,4 +1,4 @@",
            " one |  one",
            "-two | +TWO",
            "-three | ·",
            " four |  four",
            "· | +five",
        ], rows.Select(Describe));
    }

    [Fact]
    public void SideBySide_ARemovalAfterAdditionsStartsANewBlock()
    {
        var rows = SideBySideDiff.Build(Parse("@@ -1,2 +1,2 @@", "-a", "+A", "-b", "+B"));
        Assert.Equal(["-a | +A", "-b | +B"], rows.Where(r => r.Kind == DiffRowKind.Lines).Select(Describe));
    }

    [Fact]
    public void SideBySide_NoNewlineMarkerStaysOnItsSide()
    {
        var rows = SideBySideDiff.Build(Parse("@@ -1 +1 @@", "-old", "\\ No newline at end of file", "+new"));
        Assert.Equal(["-old | +new", "\\ No newline at end of file | ·"], rows.Where(r => r.Kind == DiffRowKind.Lines).Select(Describe));
    }

    [Fact]
    public void SideBySide_ChangeIndexesPointIntoTheParsedDiff()
    {
        var lines = Parse("@@ -1,2 +1,2 @@", " same", "-old", "+new");
        var rows = SideBySideDiff.Build(lines);

        Assert.Empty(rows.Single(r => r.Left?.Line.Text == " same").ChangeIndexes);
        var replaced = rows.Single(r => r.Left?.Line.Text == "-old").ChangeIndexes.Select(i => lines[i].Text);
        Assert.Equal(["-old", "+new"], replaced);
    }

    // ---------- Changed part of a line ----------

    [Theory]
    [InlineData("var count = 1;", "var total = 1;", "count", "total")]
    [InlineData("Call(a, b);", "Call(a, b, c);", "", ", c")]
    [InlineData("return x;", "return x + y;", "", " + y")]
    [InlineData("value = 42;", "value = 43;", "42", "43")] // digits count as a word
    public void Compare_FindsTheChangedWords(string oldText, string newText, string oldChange, string newChange)
    {
        var (oldRange, newRange) = InlineChanges.Compare(oldText, newText)!.Value;
        Assert.Equal(oldChange, oldText.Substring(oldRange.Start, oldRange.Length));
        Assert.Equal(newChange, newText.Substring(newRange.Start, newRange.Length));
    }

    [Theory]
    [InlineData("same", "same")]
    [InlineData("abc", "xyz")]
    public void Compare_IsNull_ForIdenticalOrUnrelatedLines(string oldText, string newText) =>
        Assert.Null(InlineChanges.Compare(oldText, newText));

    [Fact]
    public void InlineChanges_OnlyForPairedLines()
    {
        var lines = Parse("@@ -1,2 +1,3 @@", "-int a = 1;", "+int a = 2;", "+int b = 3;", " end");
        var changes = InlineChanges.Compute(lines);

        Assert.Equal(2, changes.Count);
        Assert.Equal("1", InlineChanges.Content(lines[4]).Substring(changes[4].Start, changes[4].Length));
        Assert.Equal("2", InlineChanges.Content(lines[5]).Substring(changes[5].Start, changes[5].Length));
        Assert.False(changes.ContainsKey(6)); // "+int b = 3;" has no removed counterpart
    }

    // ---------- Syntax highlighting ----------

    private static string ColorOf(IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>> spans, IReadOnlyList<DiffLine> lines, int index, string word)
    {
        var start = InlineChanges.Content(lines[index]).IndexOf(word, StringComparison.Ordinal);
        return spans[index].Single(s => s.Start <= start && start < s.Start + s.Length).Color;
    }

    [Fact]
    public void Highlight_ColorsCSharpKeywordsAndStrings()
    {
        var lines = Parse("@@ -1 +1 @@", "-public class A { }", "+public class B { string s = \"hi\"; }");
        var spans = SyntaxHighlighter.Shared.Highlight("src/f.cs", lines);

        var keyword = ColorOf(spans, lines, 5, "public");
        Assert.Equal(keyword, ColorOf(spans, lines, 4, "public"));
        Assert.NotEqual(keyword, ColorOf(spans, lines, 5, "\"hi\""));
    }

    [Fact]
    public void Highlight_CodeIsNotBoldOrItalic_WhenTheThemeSaysNothing()
    {
        var lines = Parse("@@ -1 +1 @@", "+public class A { }");
        Assert.All(SyntaxHighlighter.Shared.Highlight("f.cs", lines)[4], span => Assert.False(span.Bold || span.Italic));
    }

    [Fact]
    public void Highlight_KeepsOldAndNewSidesApart()
    {
        // The removed line opens a block comment; the added lines must not be treated as inside it.
        var lines = Parse("@@ -1,2 +1,2 @@", "-/* start", "+int x = 1;", " int y = 2; */");
        var spans = SyntaxHighlighter.Shared.Highlight("f.cs", lines);

        Assert.NotEqual(ColorOf(spans, lines, 4, "start"), ColorOf(spans, lines, 5, "int"));
    }

    [Theory]
    [InlineData("MainWindow.axaml", true)]
    [InlineData("App.csproj", true)]
    [InlineData("script.py", true)]
    [InlineData("notes.unknownext", false)]
    [InlineData("LICENSE", false)]
    public void Highlight_KnowsFileTypes(string path, bool supported) =>
        Assert.Equal(supported, SyntaxHighlighter.Shared.Supports(path));

    [Fact]
    public void Highlight_SkipsUnknownFileTypes() =>
        Assert.Empty(SyntaxHighlighter.Shared.Highlight("LICENSE", Parse("@@ -1 +1 @@", "-a", "+b")));
}
