using System.Text;
using GitHr.Core.Parsing;

namespace GitHr.Core;

/// <summary>
/// Builds a patch containing only some hunks or lines of a single-file diff, for partial
/// staging, unstaging and discarding with <c>git apply</c> (same approach as git-gui / Fork).
/// </summary>
public static class PatchBuilder
{
    /// <summary>A diff line plus the "\ No newline at end of file" marker that may follow it.</summary>
    private sealed record Item(int Index, DiffLine Line, string? NoNewlineMarker);

    /// <param name="lines">Parsed diff of one file (<see cref="GitOutputParser.ParseDiff"/>).</param>
    /// <param name="selected">Indexes into <paramref name="lines"/> of the added/removed lines to include.</param>
    /// <param name="reverse">
    /// True when the patch will be applied with <c>--reverse</c> (unstage, discard). Unselected changes are then
    /// already present in the target, so unselected "+" lines become context and unselected "-" lines are dropped.
    /// Forward (stage) is the opposite.
    /// </param>
    /// <returns>The patch text, or null when no selected change remains.</returns>
    public static string? Build(IReadOnlyList<DiffLine> lines, IReadOnlySet<int> selected, bool reverse)
    {
        var patch = new StringBuilder();
        var hasHunk = false;
        var index = 0;

        for (; index < lines.Count && lines[index].Kind == DiffLineKind.Header; index++)
        {
            patch.Append(lines[index].Text).Append('\n');
        }

        while (index < lines.Count)
        {
            if (lines[index].Kind != DiffLineKind.Hunk)
            {
                index++;
                continue;
            }

            var (oldStart, newStart) = GitOutputParser.ParseHunkStarts(lines[index].Text);
            var items = new List<Item>();
            for (index++; index < lines.Count && lines[index].Kind != DiffLineKind.Hunk; index++)
            {
                if (lines[index].Kind == DiffLineKind.NoNewline)
                {
                    if (items.Count > 0)
                    {
                        items[^1] = items[^1] with { NoNewlineMarker = lines[index].RawText };
                    }
                }
                else
                {
                    items.Add(new Item(index, lines[index], null));
                }
            }

            var hunk = new HunkWriter();
            for (var i = 0; i < items.Count;)
            {
                if (!items[i].Line.IsChange)
                {
                    hunk.Context(items[i++]);
                    continue;
                }

                // A change block: git always lists its removed lines before its added lines.
                var removed = new List<Item>();
                var added = new List<Item>();
                for (; i < items.Count && items[i].Line.IsChange; i++)
                {
                    (items[i].Line.IsAdded ? added : removed).Add(items[i]);
                }
                if (reverse)
                {
                    WriteReverseBlock(hunk, removed, added, selected);
                }
                else
                {
                    WriteForwardBlock(hunk, removed, added, selected);
                }
            }

            if (hunk.HasChanges)
            {
                hasHunk = true;
                patch.Append($"@@ -{oldStart},{hunk.OldCount} +{newStart},{hunk.NewCount} @@\n").Append(hunk.Body);
            }
        }

        return hasHunk ? patch.ToString() : null;
    }

    /// <summary>
    /// Staging: unselected removals stay in the index as context. Those after the first selected removal are
    /// written after the selected additions, so a replacement lands where the replaced line was.
    /// </summary>
    private static void WriteForwardBlock(HunkWriter hunk, List<Item> removed, List<Item> added, IReadOnlySet<int> selected)
    {
        var deferred = new List<Item>();
        var seenSelected = false;
        foreach (var item in removed)
        {
            if (selected.Contains(item.Index))
            {
                seenSelected = true;
                hunk.Removed(item);
            }
            else if (seenSelected)
            {
                deferred.Add(item);
            }
            else
            {
                hunk.Context(item);
            }
        }
        foreach (var item in added.Where(a => selected.Contains(a.Index)))
        {
            hunk.Added(item);
        }
        foreach (var item in deferred)
        {
            hunk.Context(item);
        }
    }

    /// <summary>
    /// Unstaging/discarding: unselected additions stay in the target as context. Those before the first selected
    /// addition are written before the removals, so restored lines land where they were replaced.
    /// </summary>
    private static void WriteReverseBlock(HunkWriter hunk, List<Item> removed, List<Item> added, IReadOnlySet<int> selected)
    {
        var firstSelectedAdded = added.FindIndex(a => selected.Contains(a.Index));
        var hoisted = firstSelectedAdded > 0 ? added[..firstSelectedAdded] : [];
        foreach (var item in hoisted)
        {
            hunk.Context(item);
        }
        foreach (var item in removed.Where(r => selected.Contains(r.Index)))
        {
            hunk.Removed(item);
        }
        foreach (var item in added.Skip(hoisted.Count))
        {
            if (selected.Contains(item.Index))
            {
                hunk.Added(item);
            }
            else
            {
                hunk.Context(item);
            }
        }
    }

    /// <summary>Indexes of the added/removed lines in the hunk whose header is at <paramref name="hunkIndex"/>.</summary>
    public static IReadOnlySet<int> ChangesInHunk(IReadOnlyList<DiffLine> lines, int hunkIndex)
    {
        var result = new HashSet<int>();
        for (var i = hunkIndex + 1; i < lines.Count && lines[i].Kind != DiffLineKind.Hunk; i++)
        {
            if (lines[i].IsChange)
            {
                result.Add(i);
            }
        }
        return result;
    }

    private sealed class HunkWriter
    {
        public StringBuilder Body { get; } = new();
        public int OldCount { get; private set; }
        public int NewCount { get; private set; }
        public bool HasChanges { get; private set; }

        public void Context(Item item)
        {
            var raw = item.Line.RawText;
            Write(item.Line.IsChange ? " " + raw[1..] : raw, item.NoNewlineMarker);
            OldCount++;
            NewCount++;
        }

        public void Removed(Item item)
        {
            Write(item.Line.RawText, item.NoNewlineMarker);
            OldCount++;
            HasChanges = true;
        }

        public void Added(Item item)
        {
            Write(item.Line.RawText, item.NoNewlineMarker);
            NewCount++;
            HasChanges = true;
        }

        private void Write(string line, string? noNewlineMarker)
        {
            Body.Append(line).Append('\n');
            if (noNewlineMarker is not null)
            {
                Body.Append(noNewlineMarker).Append('\n');
            }
        }
    }
}
