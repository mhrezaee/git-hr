namespace GitHr.Core;

public enum GitRefKind
{
    Head,
    LocalBranch,
    RemoteBranch,
    Tag,
}

/// <summary>A ref decorating a commit, e.g. a branch or tag label in the graph.</summary>
public sealed record GitRef(string Name, GitRefKind Kind, bool IsCurrent = false);

public sealed record Commit(
    string Sha,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string Subject,
    IReadOnlyList<GitRef> Refs)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
    public bool IsMerge => Parents.Count > 1;
}

public sealed record Branch(
    string Name,
    string FullName,
    string Sha,
    bool IsRemote,
    bool IsCurrent,
    string? Upstream,
    int Ahead,
    int Behind,
    bool IsUpstreamGone);

/// <summary>An entry of <c>git stash list</c>.</summary>
/// <param name="Index">Position in the stash list; 0 is the newest (<c>stash@{0}</c>).</param>
/// <param name="Branch">Branch the changes were stashed from, if git recorded one.</param>
public sealed record Stash(int Index, string Sha, string Message, string? Branch, DateTimeOffset Date)
{
    public string Name => $"stash@{{{Index}}}";
}

/// <param name="CommitSha">The commit the tag points at (for annotated tags, the peeled target).</param>
/// <param name="Message">Annotation subject for annotated tags; null for lightweight tags.</param>
public sealed record Tag(string Name, string CommitSha, bool IsAnnotated, string? Message, DateTimeOffset? Date)
{
    public string ShortSha => CommitSha.Length > 7 ? CommitSha[..7] : CommitSha;
}

public enum FileChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
}

public sealed record FileChange(string Path, FileChangeKind Kind, string? OriginalPath = null);

public sealed record RepositoryStatus(
    string? BranchName,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<FileChange> Staged,
    IReadOnlyList<FileChange> Unstaged)
{
    public bool IsDetached => BranchName is null;
}

public enum DiffLineKind
{
    Header,
    Hunk,
    Context,
    Added,
    Removed,
    /// <summary>The "\ No newline at end of file" marker.</summary>
    NoNewline,
}

/// <param name="Text">Display text (line ending removed).</param>
/// <param name="Raw">Exact line as git printed it (may keep a trailing CR); used to build patches.</param>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber = null, int? NewLineNumber = null, string? Raw = null)
{
    public string RawText => Raw ?? Text;
    public bool IsHeader => Kind is DiffLineKind.Header or DiffLineKind.NoNewline;
    public bool IsHunk => Kind == DiffLineKind.Hunk;
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsChange => Kind is DiffLineKind.Added or DiffLineKind.Removed;
}

/// <summary>A multi-step operation that is paused, typically because of conflicts.</summary>
public enum RepositoryOperation
{
    None,
    Merging,
    Rebasing,
    CherryPicking,
    Reverting,
}

public enum ResetMode
{
    Soft,
    Mixed,
    Hard,
}
