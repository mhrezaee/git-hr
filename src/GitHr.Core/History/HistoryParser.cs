using System.Globalization;
using GitHr.Core.Parsing;

namespace GitHr.Core.History;

/// <summary>Parses the output of the file-history and blame commands.</summary>
public static class HistoryParser
{
    /// <summary>
    /// Format for <c>git log --name-status -z</c>: the record separator comes first, because git prints each
    /// commit's file list (NUL-separated) after the formatted header.
    /// </summary>
    public const string FileLogFormat = "%x1e" + "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s";

    /// <summary>
    /// Parses <c>git log --follow --name-status -z --format=FileLogFormat -- path</c>, newest first. Commits that
    /// list no file (merges) keep the path of the newer commit; a rename switches the path for older commits.
    /// </summary>
    public static IReadOnlyList<FileRevision> ParseFileLog(string output, string path)
    {
        var revisions = new List<FileRevision>();
        var currentPath = path;
        foreach (var record in output.Split(GitOutputParser.RecordSeparator))
        {
            var parts = record.Split('\0');
            if (GitOutputParser.ParseLog(parts[0] + GitOutputParser.RecordSeparator) is not [var commit])
            {
                continue;
            }

            var tokens = parts.Skip(1).Select(t => t.TrimStart('\n', '\r')).Where(t => t.Length > 0).ToList();
            if (tokens.Count >= 3 && tokens[0][0] is 'R' or 'C')
            {
                revisions.Add(new FileRevision(commit, tokens[2], tokens[1]));
                currentPath = tokens[1]; // older commits know the file under its old name
            }
            else if (tokens.Count >= 2)
            {
                revisions.Add(new FileRevision(commit, tokens[1]));
                currentPath = tokens[1];
            }
            else
            {
                revisions.Add(new FileRevision(commit, currentPath));
            }
        }
        return revisions;
    }

    /// <summary>Parses <c>git blame --porcelain</c>.</summary>
    public static Blame ParseBlame(string output, string path, string? revision)
    {
        var commits = new Dictionary<string, BlameCommit>();
        var details = new Dictionary<string, string>();
        var lines = new List<BlameLine>();
        string? sha = null;
        var number = 0;

        foreach (var rawLine in output.Split('\n'))
        {
            if (rawLine.StartsWith('\t'))
            {
                // The line itself ends the entry; the commit details appear only with a commit's first line.
                if (sha is null)
                {
                    continue;
                }
                if (!commits.TryGetValue(sha, out var commit))
                {
                    commit = CreateCommit(sha, details);
                    commits[sha] = commit;
                }
                lines.Add(new BlameLine(number, rawLine[1..].TrimEnd('\r'), commit));
                details.Clear();
                sha = null;
                continue;
            }

            var line = rawLine.TrimEnd('\r');
            if (sha is null)
            {
                // Header: "<sha> <original line> <final line> [<lines in group>]"
                var header = line.Split(' ');
                if (header.Length >= 3 && header[0].Length >= 40 && int.TryParse(header[2], NumberStyles.None, CultureInfo.InvariantCulture, out var final))
                {
                    sha = header[0];
                    number = final;
                }
                continue;
            }

            var space = line.IndexOf(' ');
            if (space > 0)
            {
                details[line[..space]] = line[(space + 1)..];
            }
            else if (line.Length > 0)
            {
                details[line] = ""; // flags such as "boundary"
            }
        }
        return new Blame(path, revision, lines);
    }

    private static BlameCommit CreateCommit(string sha, Dictionary<string, string> details)
    {
        var uncommitted = sha.All(c => c == '0');
        var time = details.TryGetValue("author-time", out var t) && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.Now;
        var email = details.GetValueOrDefault("author-mail", "").Trim('<', '>');
        return new BlameCommit(
            sha,
            uncommitted ? "You" : details.GetValueOrDefault("author", ""),
            uncommitted ? "" : email,
            time,
            uncommitted ? "Not committed yet" : details.GetValueOrDefault("summary", ""),
            uncommitted);
    }
}
