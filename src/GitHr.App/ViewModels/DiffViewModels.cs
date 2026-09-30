using System.Collections.Generic;
using System.Linq;
using GitHr.Core;
using GitHr.Core.Diff;

namespace GitHr.App.ViewModels;

/// <summary>Text with syntax colors and an emphasized (changed) range, all in <see cref="Text"/> coordinates.</summary>
public sealed record StyledText(string Text, IReadOnlyList<SyntaxSpan> Syntax, TextRange? Emphasis)
{
    public static StyledText Plain(string text) => new(text, [], null);
}

/// <summary>Syntax colors and changed ranges of one diff, computed once (off the UI thread) for both layouts.</summary>
public sealed class DiffStyles
{
    public static readonly DiffStyles None = new(new Dictionary<int, IReadOnlyList<SyntaxSpan>>(), new Dictionary<int, TextRange>());

    private readonly IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>> _syntax;
    private readonly IReadOnlyDictionary<int, TextRange> _changes;

    private DiffStyles(IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>> syntax, IReadOnlyDictionary<int, TextRange> changes)
    {
        _syntax = syntax;
        _changes = changes;
    }

    public static DiffStyles Compute(string path, IReadOnlyList<DiffLine> lines) =>
        new(SyntaxHighlighter.Shared.Highlight(path, lines), InlineChanges.Compute(lines));

    /// <summary>
    /// The line as shown: unified keeps the "+"/"-"/" " prefix (so ranges shift by one), side by side shows only
    /// the content. Headers and hunk headers are plain.
    /// </summary>
    public StyledText For(DiffLine line, int index, bool withPrefix)
    {
        var hasPrefix = line.Kind is DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context;
        if (!hasPrefix)
        {
            return StyledText.Plain(line.Text);
        }

        var shift = withPrefix ? 1 : 0;
        var syntax = _syntax.TryGetValue(index, out var spans)
            ? spans.Select(s => s with { Start = s.Start + shift }).ToList()
            : [];
        TextRange? emphasis = _changes.TryGetValue(index, out var range) ? range with { Start = range.Start + shift } : null;
        return new StyledText(withPrefix ? line.Text : InlineChanges.Content(line), syntax, emphasis);
    }
}

public sealed class DiffLineViewModel(DiffLine line, int index, DiffMode mode, MainViewModel owner, StyledText styled)
{
    public DiffLine Line { get; } = line;
    /// <summary>Position in the parsed diff; what <see cref="PatchBuilder"/> selections refer to.</summary>
    public int Index { get; } = index;
    public MainViewModel Owner { get; } = owner;
    public StyledText Styled { get; } = styled;

    public string Text => Line.Text;
    public string OldNumber => Line.OldLineNumber?.ToString() ?? "";
    public string NewNumber => Line.NewLineNumber?.ToString() ?? "";
    public bool IsHeader => Line.IsHeader;
    public bool IsHunk => Line.IsHunk;
    public bool IsAdded => Line.IsAdded;
    public bool IsRemoved => Line.IsRemoved;

    public bool ShowStageHunk => Line.IsHunk && mode == DiffMode.Unstaged;
    public bool ShowUnstageHunk => Line.IsHunk && mode == DiffMode.Staged;
}

/// <summary>One side of a side-by-side row; <see cref="Empty"/> where the other side has a line and this one doesn't.</summary>
public sealed class DiffCellViewModel
{
    public static readonly DiffCellViewModel Empty = new(null, "", StyledText.Plain(""));

    public DiffCellViewModel(DiffLine? line, string number, StyledText styled)
    {
        Line = line;
        Number = number;
        Styled = styled;
    }

    public DiffLine? Line { get; }
    public string Number { get; }
    public StyledText Styled { get; }

    public string Text => Styled.Text;
    public bool IsEmpty => Line is null;
    public bool IsAdded => Line?.IsAdded == true;
    public bool IsRemoved => Line?.IsRemoved == true;
    public bool IsMarker => Line?.Kind == DiffLineKind.NoNewline;
}

/// <summary>
/// A row of the side-by-side diff: either a full-width header / hunk header (<see cref="Line"/>, which also carries
/// the hunk buttons) or an old (left) and a new (right) cell.
/// </summary>
public sealed class DiffRowViewModel
{
    public DiffRowViewModel(DiffRow row, DiffLineViewModel? spanning, DiffCellViewModel left, DiffCellViewModel right)
    {
        Row = row;
        Line = spanning;
        Left = left;
        Right = right;
    }

    public DiffRow Row { get; }
    public DiffLineViewModel? Line { get; }
    public DiffCellViewModel Left { get; }
    public DiffCellViewModel Right { get; }

    public bool IsSpanning => Line is not null;
    public bool IsLines => Line is null;
    public bool IsHunk => Line?.IsHunk == true;
    public bool IsHeader => Line?.IsHeader == true;

    public static IEnumerable<DiffRowViewModel> Build(IReadOnlyList<DiffLine> lines, IReadOnlyList<DiffLineViewModel> unified, DiffStyles styles)
    {
        foreach (var row in SideBySideDiff.Build(lines))
        {
            if (row.Kind != DiffRowKind.Lines)
            {
                yield return new DiffRowViewModel(row, unified[row.Left!.Index], DiffCellViewModel.Empty, DiffCellViewModel.Empty);
                continue;
            }
            yield return new DiffRowViewModel(row, null,
                Cell(row.Left, styles, line => line.OldLineNumber),
                Cell(row.Right, styles, line => line.NewLineNumber));
        }
    }

    private static DiffCellViewModel Cell(DiffCell? cell, DiffStyles styles, System.Func<DiffLine, int?> number) => cell is null
        ? DiffCellViewModel.Empty
        : new DiffCellViewModel(cell.Line, number(cell.Line)?.ToString() ?? "", styles.For(cell.Line, cell.Index, withPrefix: false));
}
