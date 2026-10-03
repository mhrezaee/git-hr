using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHr.Core;
using GitHr.Core.Diff;
using GitHr.Core.History;

namespace GitHr.App.ViewModels;

/// <summary>History search, file history and blame.</summary>
public partial class MainViewModel
{
    /// <summary>What the history list shows instead of the full graph: a search, or the commits of one file.</summary>
    private abstract record HistoryFilter;

    private sealed record SearchFilter(HistorySearch Search) : HistoryFilter;

    private sealed record FileFilter(string Path) : HistoryFilter;

    private HistoryFilter? _historyFilter;
    /// <summary>The filter the history list was last built with; a different one forces a rebuild.</summary>
    private HistoryFilter? _shownHistoryFilter;
    /// <summary>In file history: the file's path in each commit (it changes across renames), by SHA.</summary>
    private IReadOnlyDictionary<string, FileRevision> _fileRevisions = new Dictionary<string, FileRevision>();

    public static IReadOnlyList<HistorySearchKind> SearchKinds { get; } = Enum.GetValues<HistorySearchKind>();

    [ObservableProperty]
    public partial HistorySearchKind SearchKind { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>Banner above the history while it is filtered, e.g. "History of src/App.cs · 12 commits".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistoryFiltered))]
    public partial string? HistoryFilterTitle { get; set; }

    public bool IsHistoryFiltered => HistoryFilterTitle is not null;

    /// <summary>Set by the window: shows a blame (non-modal, so the main window stays usable).</summary>
    public Func<BlameViewModel, Task>? OpenBlame { get; set; }

    [RelayCommand]
    private Task SearchHistoryAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return ClearHistoryFilterAsync();
        }
        return ApplyHistoryFilterAsync(new SearchFilter(new HistorySearch(SearchKind, SearchText.Trim())), "Searching history…");
    }

    [RelayCommand]
    private Task ClearHistoryFilterAsync()
    {
        SearchText = "";
        return _historyFilter is null ? Task.CompletedTask : ApplyHistoryFilterAsync(null, "Loading history…");
    }

    [RelayCommand]
    private Task ShowFileHistoryAsync(FileChangeItemViewModel file) =>
        ApplyHistoryFilterAsync(new FileFilter(file.Path), $"Loading history of {file.FileName}…");

    private async Task ApplyHistoryFilterAsync(HistoryFilter? filter, string busyText)
    {
        _historyFilter = filter;
        // The reload after the (empty) action runs the filtered query; see LoadHistoryAsync.
        await RunGitAsync(busyText, _ => Task.CompletedTask);
        if (filter is not null && Commits.Count > 0 && SelectedCommit is null)
        {
            SelectedCommit = Commits[0];
        }
    }

    private void ResetHistoryFilter()
    {
        _historyFilter = null;
        _fileRevisions = new Dictionary<string, FileRevision>();
        HistoryFilterTitle = null;
        SearchText = "";
    }

    /// <summary>The commits for the history list: everything, or what the active filter selects.</summary>
    private async Task<(IReadOnlyList<Commit> Commits, IReadOnlyDictionary<string, FileRevision> Revisions)> LoadHistoryAsync(
        GitRepository repository, HistoryFilter? filter)
    {
        switch (filter)
        {
            case SearchFilter search:
                return (await repository.SearchCommitsAsync(search.Search, CommitLimit), new Dictionary<string, FileRevision>());
            case FileFilter file:
                var revisions = await repository.GetFileHistoryAsync(file.Path, CommitLimit);
                return (revisions.Select(r => r.Commit).ToList(), revisions.ToDictionary(r => r.Commit.Sha));
            default:
                return (await repository.GetCommitsAsync(CommitLimit), new Dictionary<string, FileRevision>());
        }
    }

    /// <summary>
    /// A filtered list skips commits, so real parent links would dangle: draw it as one straight line in list order.
    /// The commits themselves keep their real parents (diffs need them).
    /// </summary>
    private static IReadOnlyList<Commit> GraphCommits(IReadOnlyList<Commit> commits, HistoryFilter? filter) => filter is null
        ? commits
        : commits.Select((c, i) => c with { Parents = i + 1 < commits.Count ? [commits[i + 1].Sha] : [] }).ToList();

    private void UpdateHistoryFilterTitle(HistoryFilter? filter, int count)
    {
        var found = count == 0 ? "no commits found" : count == 1 ? "1 commit" : $"{count} commits";
        HistoryFilterTitle = filter switch
        {
            FileFilter file => $"History of {file.Path} · {found}",
            SearchFilter { Search: var search } => $"{Describe(search.Kind)} “{search.Text}” · {found}",
            _ => null,
        };
    }

    private static string Describe(HistorySearchKind kind) => kind switch
    {
        HistorySearchKind.Author => "Author matches",
        HistorySearchKind.Code => "Changes adding or removing",
        HistorySearchKind.Sha => "Commit",
        _ => "Message contains",
    };

    /// <summary>In file history, selecting a commit opens that file's diff right away.</summary>
    private void SelectFileOfFileHistory(CommitItemViewModel commit)
    {
        if (_historyFilter is FileFilter && _fileRevisions.TryGetValue(commit.Commit.Sha, out var revision))
        {
            SelectedCommitFile = SelectedCommitFiles.FirstOrDefault(f => f.Path == revision.Path) ?? SelectedCommitFiles.FirstOrDefault();
        }
    }

    /// <summary>Selects a commit in the history (blame → "show commit"); searches for it when the list doesn't contain it.</summary>
    public async Task ShowCommitAsync(string sha)
    {
        if (Commits.FirstOrDefault(c => c.Commit.Sha == sha) is not { } commit)
        {
            SearchKind = HistorySearchKind.Sha;
            SearchText = sha;
            await ApplyHistoryFilterAsync(new SearchFilter(new HistorySearch(HistorySearchKind.Sha, sha)), "Finding commit…");
            commit = Commits.FirstOrDefault(c => c.Commit.Sha == sha);
        }
        if (commit is not null)
        {
            SelectedCommit = commit;
        }
    }

    // ---------- Blame ----------

    /// <summary>Blame of the file as it is in the working tree (changes, staged files).</summary>
    [RelayCommand]
    private Task BlameFileAsync(FileChangeItemViewModel file) => OpenBlameAsync(file.Path, revision: null);

    /// <summary>Blame of the file as it was in the selected commit (a deleted file: just before the commit).</summary>
    [RelayCommand]
    private Task BlameFileAtCommitAsync(FileChangeItemViewModel file)
    {
        if (SelectedCommit is not { } commit)
        {
            return Task.CompletedTask;
        }
        return file.Kind == FileChangeKind.Deleted
            ? OpenBlameAsync(file.Path, commit.Commit.Sha + "^")
            : OpenBlameAsync(file.Path, commit.Commit.Sha);
    }

    private async Task OpenBlameAsync(string path, string? revision)
    {
        if (_repository is not { } repository || OpenBlame is null)
        {
            return;
        }
        try
        {
            var blame = await repository.GetBlameAsync(path, revision);
            var syntax = await Task.Run(() => SyntaxHighlighter.Shared.Highlight(path, blame.Lines.Select(l => l.Text).ToList()));
            await OpenBlame(new BlameViewModel(blame, syntax, ShowCommitAsync));
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
