using System.Globalization;

namespace GitHr.Core.Parsing;

/// <summary>Parses machine-readable git output. Pure functions, so they are easy to unit test.</summary>
public static partial class GitOutputParser
{
    public const char FieldSeparator = '\x1f';
    public const char RecordSeparator = '\x1e';

    /// <summary>Format passed to <c>git log --format</c>; matches <see cref="ParseLog"/>.</summary>
    public const string LogFormat = "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s%x1e";

    /// <summary>Format passed to <c>git for-each-ref --format</c>; matches <see cref="ParseBranches"/>.</summary>
    public const string BranchFormat =
        "%(refname)%1f%(objectname)%1f%(HEAD)%1f%(upstream:short)%1f%(upstream:track,nobracket)";

    public static IReadOnlyList<Commit> ParseLog(string output)
    {
        var commits = new List<Commit>();
        foreach (var rawRecord in output.Split(RecordSeparator))
        {
            var record = rawRecord.TrimStart('\r', '\n');
            if (record.Length == 0)
            {
                continue;
            }

            var fields = record.Split(FieldSeparator);
            if (fields.Length < 7)
            {
                continue;
            }

            var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var date = DateTimeOffset.FromUnixTimeSeconds(long.Parse(fields[4], CultureInfo.InvariantCulture));
            commits.Add(new Commit(
                fields[0],
                parents,
                fields[2],
                fields[3],
                date,
                fields[6],
                ParseDecorations(fields[5])));
        }
        return commits;
    }

    /// <summary>Parses <c>%D</c> output produced with <c>--decorate=full</c>.</summary>
    public static IReadOnlyList<GitRef> ParseDecorations(string decorations)
    {
        var refs = new List<GitRef>();
        foreach (var part in decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var item = part.Trim();
            var isCurrent = false;
            if (item.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                isCurrent = true;
                item = item["HEAD -> ".Length..];
            }
            else if (item == "HEAD")
            {
                refs.Add(new GitRef("HEAD", GitRefKind.Head, IsCurrent: true));
                continue;
            }

            if (item.StartsWith("tag: ", StringComparison.Ordinal))
            {
                item = item["tag: ".Length..];
            }

            if (item.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                refs.Add(new GitRef(item["refs/heads/".Length..], GitRefKind.LocalBranch, isCurrent));
            }
            else if (item.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var name = item["refs/remotes/".Length..];
                if (!name.EndsWith("/HEAD", StringComparison.Ordinal))
                {
                    refs.Add(new GitRef(name, GitRefKind.RemoteBranch));
                }
            }
            else if (item.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                refs.Add(new GitRef(item["refs/tags/".Length..], GitRefKind.Tag));
            }
        }
        return refs;
    }

    public static IReadOnlyList<Branch> ParseBranches(string output)
    {
        var branches = new List<Branch>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split(FieldSeparator);
            if (fields.Length < 5)
            {
                continue;
            }

            var fullName = fields[0];
            bool isRemote;
            string name;
            if (fullName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                isRemote = false;
                name = fullName["refs/heads/".Length..];
            }
            else if (fullName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                isRemote = true;
                name = fullName["refs/remotes/".Length..];
                if (name.EndsWith("/HEAD", StringComparison.Ordinal) || !name.Contains('/'))
                {
                    continue; // symbolic refs/remotes/origin/HEAD
                }
            }
            else
            {
                continue;
            }

            var (ahead, behind, gone) = ParseTrack(fields[4]);
            branches.Add(new Branch(
                name,
                fullName,
                fields[1],
                isRemote,
                IsCurrent: fields[2] == "*",
                Upstream: fields[3].Length == 0 ? null : fields[3],
                ahead,
                behind,
                gone));
        }
        return branches;
    }

    /// <summary>Parses <c>ahead 1, behind 2</c> / <c>gone</c> from <c>%(upstream:track,nobracket)</c>.</summary>
    public static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
    {
        if (track == "gone")
        {
            return (0, 0, true);
        }

        int ahead = 0, behind = 0;
        foreach (var part in track.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(' ');
            if (pieces.Length != 2 || !int.TryParse(pieces[1], CultureInfo.InvariantCulture, out var count))
            {
                continue;
            }
            if (pieces[0] == "ahead") ahead = count;
            else if (pieces[0] == "behind") behind = count;
        }
        return (ahead, behind, false);
    }

    /// <summary>Parses <c>git status --porcelain=v2 --branch -z</c>.</summary>
    public static RepositoryStatus ParseStatus(string output)
    {
        string? branch = null;
        string? upstream = null;
        int ahead = 0, behind = 0;
        var staged = new List<FileChange>();
        var unstaged = new List<FileChange>();

        var entries = output.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 2)
            {
                continue;
            }

            switch (entry[0])
            {
                case '#':
                    ParseStatusHeader(entry, ref branch, ref upstream, ref ahead, ref behind);
                    break;

                case '1':
                {
                    // 1 XY sub mH mI mW hH hI path
                    var fields = entry.Split(' ', 9);
                    AddOrdinary(fields[1], fields[8], null, staged, unstaged);
                    break;
                }

                case '2':
                {
                    // 2 XY sub mH mI mW hH hI Xscore path \0 origPath
                    var fields = entry.Split(' ', 10);
                    var originalPath = i + 1 < entries.Length ? entries[++i] : null;
                    AddOrdinary(fields[1], fields[9], originalPath, staged, unstaged);
                    break;
                }

                case 'u':
                {
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    var fields = entry.Split(' ', 11);
                    unstaged.Add(new FileChange(fields[10], FileChangeKind.Conflicted));
                    break;
                }

                case '?':
                    unstaged.Add(new FileChange(entry[2..], FileChangeKind.Untracked));
                    break;
            }
        }

        return new RepositoryStatus(branch, upstream, ahead, behind, staged, unstaged);
    }

    private static void ParseStatusHeader(string entry, ref string? branch, ref string? upstream, ref int ahead, ref int behind)
    {
        var parts = entry.Split(' ');
        if (parts.Length < 3)
        {
            return;
        }

        switch (parts[1])
        {
            case "branch.head":
                branch = parts[2] == "(detached)" ? null : parts[2];
                break;
            case "branch.upstream":
                upstream = parts[2];
                break;
            case "branch.ab" when parts.Length >= 4:
                ahead = int.Parse(parts[2].TrimStart('+'), CultureInfo.InvariantCulture);
                behind = int.Parse(parts[3].TrimStart('-'), CultureInfo.InvariantCulture);
                break;
        }
    }

    private static void AddOrdinary(string xy, string path, string? originalPath, List<FileChange> staged, List<FileChange> unstaged)
    {
        if (ToKind(xy[0]) is { } indexKind)
        {
            staged.Add(new FileChange(path, indexKind, indexKind is FileChangeKind.Renamed or FileChangeKind.Copied ? originalPath : null));
        }
        if (ToKind(xy[1]) is { } workTreeKind)
        {
            unstaged.Add(new FileChange(path, workTreeKind));
        }
    }

    private static FileChangeKind? ToKind(char code) => code switch
    {
        'M' => FileChangeKind.Modified,
        'A' => FileChangeKind.Added,
        'D' => FileChangeKind.Deleted,
        'R' => FileChangeKind.Renamed,
        'C' => FileChangeKind.Copied,
        'T' => FileChangeKind.TypeChanged,
        'U' => FileChangeKind.Conflicted,
        _ => null,
    };

    /// <summary>Parses <c>git diff --name-status -z</c> / <c>git diff-tree --name-status -z</c>.</summary>
    public static IReadOnlyList<FileChange> ParseNameStatus(string output)
    {
        var changes = new List<FileChange>();
        var entries = output.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var status = entries[i].Trim();
            if (status.Length == 0 || i + 1 >= entries.Length)
            {
                continue;
            }

            var kind = ToKind(status[0]) ?? FileChangeKind.Modified;
            if (kind is FileChangeKind.Renamed or FileChangeKind.Copied && i + 2 < entries.Length)
            {
                var original = entries[++i];
                changes.Add(new FileChange(entries[++i], kind, original));
            }
            else
            {
                changes.Add(new FileChange(entries[++i], kind));
            }
        }
        return changes;
    }

    public static IReadOnlyList<DiffLine> ParseDiff(string output)
    {
        var lines = new List<DiffLine>();
        var inHeader = false;
        int oldLine = 0, newLine = 0;
        var rawLines = output.Split('\n');
        for (var i = 0; i < rawLines.Length; i++)
        {
            var raw = rawLines[i];
            if (i == rawLines.Length - 1 && raw.Length == 0)
            {
                break; // text after the final newline
            }

            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHeader = true;
                lines.Add(new DiffLine(DiffLineKind.Header, line, Raw: raw));
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHeader = false;
                (oldLine, newLine) = ParseHunkStarts(line);
                lines.Add(new DiffLine(DiffLineKind.Hunk, line, Raw: raw));
            }
            else if (inHeader)
            {
                lines.Add(new DiffLine(DiffLineKind.Header, line, Raw: raw));
            }
            else if (line.StartsWith('+'))
            {
                lines.Add(new DiffLine(DiffLineKind.Added, line, NewLineNumber: newLine++, Raw: raw));
            }
            else if (line.StartsWith('-'))
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, line, OldLineNumber: oldLine++, Raw: raw));
            }
            else if (line.StartsWith('\\'))
            {
                lines.Add(new DiffLine(DiffLineKind.NoNewline, line, Raw: raw));
            }
            else
            {
                lines.Add(new DiffLine(DiffLineKind.Context, line, oldLine++, newLine++, raw));
            }
        }
        return lines;
    }

    /// <summary>Reads the start lines from <c>@@ -12,5 +14,7 @@</c>.</summary>
    public static (int OldStart, int NewStart) ParseHunkStarts(string hunkHeader)
    {
        var match = HunkHeaderRegex().Match(hunkHeader);
        return match.Success
            ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : (1, 1);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial System.Text.RegularExpressions.Regex HunkHeaderRegex();
}
