using System;
using System.Collections.Generic;
using System.Linq;
using GitHr.Core;
using GitHr.Core.Graph;

namespace GitHr.App.ViewModels;

// Item view models keep a reference to their MainViewModel ("Owner") so context menus, which live in
// their own popup visual tree, can bind to the main commands: {Binding Owner.CherryPickCommand}.

public sealed class CommitItemViewModel(Commit commit, GraphRow graph, double graphWidth, MainViewModel owner)
{
    public Commit Commit { get; } = commit;
    public GraphRow Graph { get; } = graph;
    public double GraphWidth { get; } = graphWidth;
    public MainViewModel Owner { get; } = owner;

    public string Subject => Commit.Subject;
    public string ShortSha => Commit.ShortSha;
    public string Author => Commit.AuthorName;
    public string Date => Commit.AuthorDate.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public IReadOnlyList<GitRef> Refs => Commit.Refs;
    public bool HasRefs => Commit.Refs.Count > 0;
}

public sealed class BranchItemViewModel(Branch branch, MainViewModel owner)
{
    public Branch Branch { get; } = branch;
    public MainViewModel Owner { get; } = owner;
    public string Name => Branch.Name;
    public bool IsCurrent => Branch.IsCurrent;
    public bool IsNotCurrent => !Branch.IsCurrent;
    public bool IsLocal => !Branch.IsRemote;

    public string Tracking => Branch switch
    {
        { IsUpstreamGone: true } => "gone",
        { Ahead: > 0, Behind: > 0 } => $"↑{Branch.Ahead} ↓{Branch.Behind}",
        { Ahead: > 0 } => $"↑{Branch.Ahead}",
        { Behind: > 0 } => $"↓{Branch.Behind}",
        _ => "",
    };
}

public sealed class FileChangeItemViewModel(FileChange change, MainViewModel owner)
{
    public FileChange Change { get; } = change;
    public MainViewModel Owner { get; } = owner;
    public string Path => Change.Path;
    public string FileName => System.IO.Path.GetFileName(Change.Path);
    public string Directory => System.IO.Path.GetDirectoryName(Change.Path)?.Replace('\\', '/') ?? "";
    public FileChangeKind Kind => Change.Kind;
    public bool IsUntracked => Change.Kind == FileChangeKind.Untracked;
    public bool IsConflicted => Change.Kind == FileChangeKind.Conflicted;

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

/// <summary>What the diff panel currently shows; decides which hunk/line actions are offered.</summary>
public enum DiffMode
{
    /// <summary>Read-only (commit diffs, untracked or conflicted files).</summary>
    ReadOnly,
    Unstaged,
    Staged,
}

public sealed class DiffLineViewModel(DiffLine line, int index, DiffMode mode, MainViewModel owner)
{
    public DiffLine Line { get; } = line;
    /// <summary>Position in the parsed diff; what <see cref="PatchBuilder"/> selections refer to.</summary>
    public int Index { get; } = index;
    public MainViewModel Owner { get; } = owner;

    public string Text => Line.Text;
    public string OldNumber => Line.OldLineNumber?.ToString() ?? "";
    public string NewNumber => Line.NewLineNumber?.ToString() ?? "";
    public bool IsHeader => Line.IsHeader;
    public bool IsHunk => Line.IsHunk;
    public bool IsAdded => Line.IsAdded;
    public bool IsRemoved => Line.IsRemoved;

    public bool ShowStageHunk => Line.IsHunk && mode == DiffMode.Unstaged;
    public bool ShowUnstageHunk => Line.IsHunk && mode == DiffMode.Staged;
}

public sealed class StashItemViewModel(Stash stash, MainViewModel owner)
{
    public Stash Stash { get; } = stash;
    public MainViewModel Owner { get; } = owner;
    public string Name => Stash.Name;
    public string Message => Stash.Message;

    /// <summary>"stash@{0} · main · 2026-09-27 10:15"</summary>
    public string Detail => string.Join(" · ", new[] { Stash.Name, Stash.Branch, Stash.Date.LocalDateTime.ToString("yyyy-MM-dd HH:mm") }
        .Where(part => !string.IsNullOrEmpty(part)));
}

public sealed class TagItemViewModel(Tag tag, MainViewModel owner)
{
    public Tag Tag { get; } = tag;
    public MainViewModel Owner { get; } = owner;
    public string Name => Tag.Name;
    public string ShortSha => Tag.ShortSha;
    public bool IsAnnotated => Tag.IsAnnotated;

    public string ToolTip => Tag.Message is { } message
        ? $"{Tag.Name} → {Tag.ShortSha}\n{message}"
        : $"{Tag.Name} → {Tag.ShortSha} (lightweight tag)";
}

public sealed class RecentRepositoryViewModel(string path)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}
