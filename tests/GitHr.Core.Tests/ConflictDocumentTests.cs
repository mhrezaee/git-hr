using GitHr.Core.Conflicts;

namespace GitHr.Core.Tests;

public class ConflictDocumentTests
{
    private const string TwoConflicts =
        "header\n" +
        "<<<<<<< HEAD\n" +
        "ours 1\n" +
        "=======\n" +
        "theirs 1\n" +
        ">>>>>>> feature\n" +
        "middle\n" +
        "<<<<<<< HEAD\n" +
        "ours 2a\n" +
        "ours 2b\n" +
        "=======\n" +
        "theirs 2\n" +
        ">>>>>>> feature\n" +
        "footer\n";

    [Fact]
    public void Parse_SplitsCommonTextAndConflicts()
    {
        var doc = ConflictDocument.Parse(TwoConflicts);

        Assert.Equal(5, doc.Segments.Count);
        Assert.Equal(2, doc.Conflicts.Count);
        var first = doc.Conflicts[0];
        Assert.Equal((2, "HEAD", "feature"), (first.StartLine, first.OursLabel, first.TheirsLabel));
        Assert.Equal(["ours 1"], first.OursLines);
        Assert.Equal(["theirs 1"], first.TheirsLines);
        Assert.Null(first.BaseLines);
        Assert.Equal(["ours 2a", "ours 2b"], doc.Conflicts[1].OursLines);
    }

    [Theory]
    [InlineData(ConflictChoice.Ours, ConflictChoice.Theirs, "header\nours 1\nmiddle\ntheirs 2\nfooter\n")]
    [InlineData(ConflictChoice.Theirs, ConflictChoice.Ours, "header\ntheirs 1\nmiddle\nours 2a\nours 2b\nfooter\n")]
    [InlineData(ConflictChoice.OursThenTheirs, ConflictChoice.TheirsThenOurs, "header\nours 1\ntheirs 1\nmiddle\ntheirs 2\nours 2a\nours 2b\nfooter\n")]
    public void Render_AppliesChoicePerConflict(ConflictChoice first, ConflictChoice second, string expected)
    {
        Assert.Equal(expected, ConflictDocument.Parse(TwoConflicts).Render([first, second]));
    }

    [Fact]
    public void Render_WithoutChoices_ReproducesTheFile()
    {
        var doc = ConflictDocument.Parse(TwoConflicts);

        var rendered = doc.Render([null, null]);

        Assert.Equal(TwoConflicts, rendered);
        Assert.True(ConflictDocument.HasConflictMarkers(rendered));
        Assert.False(ConflictDocument.HasConflictMarkers(doc.Render([ConflictChoice.Ours, ConflictChoice.Ours])));
    }

    [Fact]
    public void Parse_Diff3Style_ReadsBaseSection()
    {
        const string diff3 = "<<<<<<< ours\na\n||||||| base\nb\n=======\nc\n>>>>>>> theirs\n";

        var doc = ConflictDocument.Parse(diff3);

        var conflict = Assert.Single(doc.Conflicts);
        Assert.Equal("base", conflict.BaseLabel);
        Assert.Equal(["b"], conflict.BaseLines);
        Assert.Equal(diff3, doc.Render([null])); // round-trips including the base section
        Assert.Equal("c\n", doc.Render([ConflictChoice.Theirs]));
    }

    [Fact]
    public void KeepsCrlfAndMissingFinalNewline()
    {
        const string crlf = "x\r\n<<<<<<< HEAD\r\nours\r\n=======\r\ntheirs\r\n>>>>>>> b\r\ny";

        var doc = ConflictDocument.Parse(crlf);

        Assert.Equal("\r\n", doc.NewLine);
        Assert.False(doc.EndsWithNewLine);
        Assert.Equal("x\r\ntheirs\r\ny", doc.Render([ConflictChoice.Theirs]));
    }

    [Theory]
    [InlineData("a\n<<<<<<< HEAD\nno end marker\n")]
    [InlineData("a\n<<<<<<<< eight is not a marker\n=======\nb\n>>>>>>> x\n")]
    [InlineData("<<<<<<<not followed by space\n=======\n>>>>>>>\n")]
    public void MalformedMarkers_AreTreatedAsText(string content)
    {
        var doc = ConflictDocument.Parse(content);

        Assert.Empty(doc.Conflicts);
        Assert.Equal(content, doc.Render([]));
    }

    [Fact]
    public void Render_RequiresOneChoicePerConflict()
    {
        Assert.Throws<ArgumentException>(() => ConflictDocument.Parse(TwoConflicts).Render([ConflictChoice.Ours]));
    }
}
