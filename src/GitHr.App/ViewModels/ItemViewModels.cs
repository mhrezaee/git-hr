using System;
using System.Collections.Generic;
using GitHr.Core;
using GitHr.Core.Graph;

namespace GitHr.App.ViewModels;

public sealed class CommitItemViewModel(Commit commit, GraphRow graph, double graphWidth)
{
    public Commit Commit { get; } = commit;
    public GraphRow Graph { get; } = graph;
    public double GraphWidth { get; } = graphWidth;

    public string Subject => Commit.Subject;
    public string ShortSha => Commit.ShortSha;
    public string Author => Commit.AuthorName;
    public string Date => Commit.AuthorDate.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public IReadOnlyList<GitRef> Refs => Commit.Refs;
    public bool HasRefs => Commit.Refs.Count > 0;
}

public sealed class BranchItemViewModel(Branch branch)
{
    public Branch Branch { get; } = branch;
    public string Name => Branch.Name;
    public bool IsCurrent => Branch.IsCurrent;

    public string Tracking => Branch switch
    {
        { IsUpstreamGone: true } => "gone",
        { Ahead: > 0, Behind: > 0 } => $"↑{Branch.Ahead} ↓{Branch.Behind}",
        { Ahead: > 0 } => $"↑{Branch.Ahead}",
        { Behind: > 0 } => $"↓{Branch.Behind}",
        _ => "",
    };
}

public sealed class FileChangeItemViewModel(FileChange change)
{
    public FileChange Change { get; } = change;
    public string Path => Change.Path;
    public string FileName => System.IO.Path.GetFileName(Change.Path);
    public string Directory => System.IO.Path.GetDirectoryName(Change.Path)?.Replace('\\', '/') ?? "";
    public FileChangeKind Kind => Change.Kind;

    public string KindLetter => Change.Kind switch
    {
        FileChangeKind.Modified => "M",
        FileChangeKind.Added => "A",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        FileChangeKind.Copied => "C",
        FileChangeKind.TypeChanged => "T",
        FileChangeKind.Untracked => "+",
        FileChangeKind.Conflicted => "!",
        _ => "?",
    };

    public string ToolTip => Change.OriginalPath is null ? Change.Path : $"{Change.OriginalPath} → {Change.Path}";
}

public sealed class RecentRepositoryViewModel(string path)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}
