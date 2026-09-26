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
}

public sealed record DiffLine(DiffLineKind Kind, string Text)
{
    public bool IsHeader => Kind == DiffLineKind.Header;
    public bool IsHunk => Kind == DiffLineKind.Hunk;
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
}
