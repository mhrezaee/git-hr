namespace GitHr.Core.History;

public enum HistorySearchKind
{
    /// <summary>Commit message contains the text (case-insensitive).</summary>
    Message,

    /// <summary>Author name or e-mail contains the text (case-insensitive).</summary>
    Author,

    /// <summary>A change added or removed the text (git's "pickaxe", <c>log -S</c>; case-sensitive).</summary>
    Code,

    /// <summary>A commit SHA (or any revision git understands, e.g. a tag).</summary>
    Sha,
}

public sealed record HistorySearch(HistorySearchKind Kind, string Text);

/// <summary>A commit in a file's history, with the file's path in that commit (it changes across renames).</summary>
/// <param name="OriginalPath">The path before the commit, when the commit renamed or copied the file.</param>
public sealed record FileRevision(Commit Commit, string Path, string? OriginalPath = null);

/// <summary>The commit a blamed line comes from.</summary>
/// <param name="IsUncommitted">The line is changed in the working tree and not committed yet.</param>
public sealed record BlameCommit(string Sha, string Author, string AuthorEmail, DateTimeOffset Date, string Summary, bool IsUncommitted)
{
    public string ShortSha => IsUncommitted ? "" : Sha[..Math.Min(7, Sha.Length)];
}

/// <param name="Number">1-based line number in the blamed version of the file.</param>
public sealed record BlameLine(int Number, string Text, BlameCommit Commit);

public sealed record Blame(string Path, string? Revision, IReadOnlyList<BlameLine> Lines);
