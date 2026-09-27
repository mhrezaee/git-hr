namespace GitHr.Core.Conflicts;

/// <summary>How one conflict is resolved.</summary>
public enum ConflictChoice
{
    Ours,
    Theirs,
    OursThenTheirs,
    TheirsThenOurs,
}

/// <summary>A piece of a conflicted file: either text both sides agree on, or a conflict.</summary>
public abstract record ConflictSegment;

public sealed record CommonSegment(IReadOnlyList<string> Lines) : ConflictSegment;

/// <param name="StartLine">1-based line of the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> marker in the conflicted file.</param>
/// <param name="BaseLines">The common ancestor's lines (only with <c>merge.conflictStyle = diff3/zdiff3</c>).</param>
public sealed record ConflictBlock(
    int StartLine,
    string OursLabel,
    IReadOnlyList<string> OursLines,
    string? BaseLabel,
    IReadOnlyList<string>? BaseLines,
    string TheirsLabel,
    IReadOnlyList<string> TheirsLines) : ConflictSegment;

/// <summary>
/// A file containing git conflict markers, split into agreed text and conflicts. Rendering with a choice per
/// conflict produces the resolved file; conflicts without a choice keep their markers.
/// </summary>
public sealed class ConflictDocument
{
    private const int MarkerSize = 7; // git's default conflict-marker-size

    private ConflictDocument(IReadOnlyList<ConflictSegment> segments, string newLine, bool endsWithNewLine)
    {
        Segments = segments;
        NewLine = newLine;
        EndsWithNewLine = endsWithNewLine;
        Conflicts = segments.OfType<ConflictBlock>().ToList();
    }

    public IReadOnlyList<ConflictSegment> Segments { get; }
    public IReadOnlyList<ConflictBlock> Conflicts { get; }

    /// <summary>The file's line ending ("\r\n" if it uses any CRLF), reused when rendering.</summary>
    public string NewLine { get; }

    public bool EndsWithNewLine { get; }

    public static ConflictDocument Parse(string content)
    {
        var newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewLine = content.EndsWith('\n');
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (endsWithNewLine)
        {
            lines.RemoveAt(lines.Count - 1); // the empty string after the final newline
        }

        var segments = new List<ConflictSegment>();
        var common = new List<string>();
        var i = 0;
        while (i < lines.Count)
        {
            if (IsMarker(lines[i], '<') && TryReadConflict(lines, i, out var block, out var next))
            {
                if (common.Count > 0)
                {
                    segments.Add(new CommonSegment(common));
                    common = [];
                }
                segments.Add(block);
                i = next;
            }
            else
            {
                common.Add(lines[i++]);
            }
        }
        if (common.Count > 0)
        {
            segments.Add(new CommonSegment(common));
        }
        return new ConflictDocument(segments, newLine, endsWithNewLine);
    }

    /// <summary>True if the text still contains a complete conflict (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> … <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>).</summary>
    public static bool HasConflictMarkers(string content) => Parse(content).Conflicts.Count > 0;

    /// <summary>Builds the file; <paramref name="choices"/> has one entry per conflict (null = keep the markers).</summary>
    public string Render(IReadOnlyList<ConflictChoice?> choices)
    {
        if (choices.Count != Conflicts.Count)
        {
            throw new ArgumentException($"Expected {Conflicts.Count} choices, got {choices.Count}.", nameof(choices));
        }

        var output = new List<string>();
        var conflictIndex = 0;
        foreach (var segment in Segments)
        {
            switch (segment)
            {
                case CommonSegment common:
                    output.AddRange(common.Lines);
                    break;
                case ConflictBlock conflict:
                    output.AddRange(Resolve(conflict, choices[conflictIndex++]));
                    break;
            }
        }

        var text = string.Join(NewLine, output);
        return EndsWithNewLine && output.Count > 0 ? text + NewLine : text;
    }

    private static IEnumerable<string> Resolve(ConflictBlock conflict, ConflictChoice? choice) => choice switch
    {
        ConflictChoice.Ours => conflict.OursLines,
        ConflictChoice.Theirs => conflict.TheirsLines,
        ConflictChoice.OursThenTheirs => conflict.OursLines.Concat(conflict.TheirsLines),
        ConflictChoice.TheirsThenOurs => conflict.TheirsLines.Concat(conflict.OursLines),
        _ => WithMarkers(conflict),
    };

    private static IEnumerable<string> WithMarkers(ConflictBlock conflict)
    {
        yield return Marker('<', conflict.OursLabel);
        foreach (var line in conflict.OursLines) yield return line;
        if (conflict.BaseLines is not null)
        {
            yield return Marker('|', conflict.BaseLabel);
            foreach (var line in conflict.BaseLines) yield return line;
        }
        yield return new string('=', MarkerSize);
        foreach (var line in conflict.TheirsLines) yield return line;
        yield return Marker('>', conflict.TheirsLabel);
    }

    private static string Marker(char c, string? label) =>
        string.IsNullOrEmpty(label) ? new string(c, MarkerSize) : $"{new string(c, MarkerSize)} {label}";

    /// <summary>Reads one conflict starting at a "&lt;&lt;&lt;&lt;&lt;&lt;&lt;" line; false if it is not terminated properly.</summary>
    private static bool TryReadConflict(List<string> lines, int start, out ConflictBlock block, out int next)
    {
        block = null!;
        next = start;
        var ours = new List<string>();
        List<string>? baseLines = null;
        string? baseLabel = null;
        var theirs = new List<string>();
        var section = 0; // 0 = ours, 1 = base, 2 = theirs

        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (section == 0 && IsMarker(line, '|'))
            {
                section = 1;
                baseLines = [];
                baseLabel = Label(line);
            }
            else if (section < 2 && line == new string('=', MarkerSize))
            {
                section = 2;
            }
            else if (section == 2 && IsMarker(line, '>'))
            {
                block = new ConflictBlock(start + 1, Label(lines[start]), ours, baseLabel, baseLines, Label(line), theirs);
                next = i + 1;
                return true;
            }
            else if (IsMarker(line, '<'))
            {
                return false; // a new conflict starts before this one ended: not a well-formed conflict
            }
            else
            {
                (section switch { 0 => ours, 1 => baseLines!, _ => theirs }).Add(line);
            }
        }
        return false;
    }

    private static bool IsMarker(string line, char c) =>
        line.Length >= MarkerSize && line.AsSpan(0, MarkerSize).IndexOfAnyExcept(c) < 0 &&
        (line.Length == MarkerSize || line[MarkerSize] == ' ');

    private static string Label(string markerLine) => markerLine.Length > MarkerSize + 1 ? markerLine[(MarkerSize + 1)..] : "";
}
