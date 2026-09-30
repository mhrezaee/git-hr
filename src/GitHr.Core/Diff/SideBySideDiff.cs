namespace GitHr.Core.Diff;

/// <summary>One side of a side-by-side row: a line of the parsed diff and its position in it.</summary>
public sealed record DiffCell(int Index, DiffLine Line);

public enum DiffRowKind
{
    /// <summary>File header ("diff --git", "index", "---", "+++"): spans both columns, stored in <see cref="DiffRow.Left"/>.</summary>
    Header,

    /// <summary>Hunk header ("@@ -1,4 +1,5 @@"): spans both columns, stored in <see cref="DiffRow.Left"/>.</summary>
    Hunk,

    /// <summary>Old version on the left, new version on the right; either side may be empty.</summary>
    Lines,
}

public sealed record DiffRow(DiffRowKind Kind, DiffCell? Left, DiffCell? Right)
{
    /// <summary>Indexes of the added/removed lines in this row (what line staging works with).</summary>
    public IEnumerable<int> ChangeIndexes => new[] { Left, Right }
        .Where(cell => cell is not null && cell.Line.IsChange)
        .Select(cell => cell!.Index)
        .Distinct();
}

/// <summary>Turns a parsed unified diff into rows for a two-column view.</summary>
public static class SideBySideDiff
{
    /// <summary>
    /// Context lines appear on both sides. In each change block the n-th removed line is paired with the n-th added
    /// line (a replacement); the longer side continues against empty cells.
    /// </summary>
    public static IReadOnlyList<DiffRow> Build(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<DiffRow>();
        var removed = new List<DiffCell>();
        var added = new List<DiffCell>();
        var previous = DiffLineKind.Header;

        void Flush()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                rows.Add(new DiffRow(DiffRowKind.Lines, i < removed.Count ? removed[i] : null, i < added.Count ? added[i] : null));
            }
            removed.Clear();
            added.Clear();
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var cell = new DiffCell(i, lines[i]);
            switch (lines[i].Kind)
            {
                case DiffLineKind.Removed:
                    if (added.Count > 0)
                    {
                        Flush(); // git lists a block's removals first, so a removal after additions starts a new block
                    }
                    removed.Add(cell);
                    break;
                case DiffLineKind.Added:
                    added.Add(cell);
                    break;
                case DiffLineKind.NoNewline when previous == DiffLineKind.Removed:
                    removed.Add(cell);
                    break;
                case DiffLineKind.NoNewline when previous == DiffLineKind.Added:
                    added.Add(cell);
                    break;
                case DiffLineKind.Context or DiffLineKind.NoNewline:
                    Flush();
                    rows.Add(new DiffRow(DiffRowKind.Lines, cell, cell));
                    break;
                case DiffLineKind.Hunk:
                    Flush();
                    rows.Add(new DiffRow(DiffRowKind.Hunk, cell, null));
                    break;
                default:
                    Flush();
                    rows.Add(new DiffRow(DiffRowKind.Header, cell, null));
                    break;
            }
            previous = lines[i].Kind;
        }
        Flush();
        return rows;
    }
}
