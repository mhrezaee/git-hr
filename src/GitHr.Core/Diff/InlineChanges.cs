namespace GitHr.Core.Diff;

/// <summary>A range of characters in a diff line's content (the text after its "+", "-" or " " prefix).</summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>Finds the part of a replaced line that actually changed, so it can be emphasized.</summary>
public static class InlineChanges
{
    /// <summary>
    /// Pairs the n-th removed with the n-th added line of every change block (as the side-by-side view does) and
    /// returns, per diff line index, the range that differs from its counterpart. Lines without a counterpart,
    /// or whose counterpart shares nothing with them, get no range: the whole line is the change.
    /// </summary>
    public static IReadOnlyDictionary<int, TextRange> Compute(IReadOnlyList<DiffLine> lines)
    {
        var result = new Dictionary<int, TextRange>();
        var removed = new List<int>();
        var added = new List<int>();

        void Flush()
        {
            for (var i = 0; i < Math.Min(removed.Count, added.Count); i++)
            {
                if (Compare(Content(lines[removed[i]]), Content(lines[added[i]])) is var (oldRange, newRange))
                {
                    if (oldRange.Length > 0)
                    {
                        result[removed[i]] = oldRange;
                    }
                    if (newRange.Length > 0)
                    {
                        result[added[i]] = newRange;
                    }
                }
            }
            removed.Clear();
            added.Clear();
        }

        for (var i = 0; i < lines.Count; i++)
        {
            switch (lines[i].Kind)
            {
                case DiffLineKind.Removed:
                    if (added.Count > 0)
                    {
                        Flush();
                    }
                    removed.Add(i);
                    break;
                case DiffLineKind.Added:
                    added.Add(i);
                    break;
                case DiffLineKind.NoNewline:
                    break;
                default:
                    Flush();
                    break;
            }
        }
        Flush();
        return result;
    }

    /// <summary>The line's text without its one-character diff prefix.</summary>
    public static string Content(DiffLine line) =>
        line.Kind is DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context && line.Text.Length > 0 ? line.Text[1..] : line.Text;

    /// <summary>
    /// The differing middle of two lines, after their common prefix and suffix. The ranges are widened to whole
    /// words, so "count" → "total" is shown as a word change rather than a few letters. Null when the lines have
    /// nothing in common or are identical.
    /// </summary>
    public static (TextRange Old, TextRange New)? Compare(string oldText, string newText)
    {
        var max = Math.Min(oldText.Length, newText.Length);
        var prefix = 0;
        while (prefix < max && oldText[prefix] == newText[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < max - prefix && oldText[^(suffix + 1)] == newText[^(suffix + 1)])
        {
            suffix++;
        }

        if (prefix == oldText.Length && prefix == newText.Length)
        {
            return null; // identical
        }

        while (prefix > 0 && IsWord(oldText[prefix - 1]) && (StartsWord(oldText, prefix) || StartsWord(newText, prefix)))
        {
            prefix--;
        }
        while (suffix > 0 && IsWord(oldText[^suffix]) &&
               (EndsWord(oldText, oldText.Length - suffix) || EndsWord(newText, newText.Length - suffix)))
        {
            suffix--;
        }

        if (prefix == 0 && suffix == 0)
        {
            return null; // nothing in common: the whole line changed
        }
        return (new TextRange(prefix, oldText.Length - prefix - suffix), new TextRange(prefix, newText.Length - prefix - suffix));
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>True when the character at <paramref name="index"/> (inside the changed part) is a word character.</summary>
    private static bool StartsWord(string text, int index) => index < text.Length && IsWord(text[index]);

    /// <summary>True when the character before <paramref name="index"/> (the end of the changed part) is a word character.</summary>
    private static bool EndsWord(string text, int index) => index > 0 && index <= text.Length && IsWord(text[index - 1]);
}
