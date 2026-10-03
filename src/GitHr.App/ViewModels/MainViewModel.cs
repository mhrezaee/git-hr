using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHr.Core;
using GitHr.Core.Diff;
using GitHr.Core.Graph;

namespace GitHr.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public const double GraphLaneWidth = 16;
    private const int MaxGraphLanes = 24;
    private const int CommitLimit = 5000;

    private readonly AppSettings _settings;
    private GitRepository? _repository;
    private CancellationTokenSource? _detailsCts;
    private bool _restoringSelection;

    public MainViewModel() : this(AppSettings.Load())
    {
    }

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        IsSplitDiff = settings.SplitDiff;
        Palette = new CommandPaletteViewModel(GetPaletteItems);
        LoadRecent();
    }

    public CommandPaletteViewModel Palette { get; }

    /// <summary>Set by the window: shows the folder picker and opens the chosen repository.</summary>
    public Func<Task>? PickRepository { get; set; }

    public ObservableCollection<CommitItemViewModel> Commits { get; } = [];
    public ObservableCollection<BranchItemViewModel> LocalBranches { get; } = [];
    public ObservableCollection<BranchItemViewModel> RemoteBranches { get; } = [];
    public ObservableCollection<FileChangeItemViewModel> UnstagedFiles { get; } = [];
    public ObservableCollection<FileChangeItemViewModel> StagedFiles { get; } = [];
    public ObservableCollection<FileChangeItemViewModel> SelectedCommitFiles { get; } = [];
    public ObservableCollection<DiffLineViewModel> DiffLines { get; } = [];
    /// <summary>The same diff as <see cref="DiffLines"/>, as side-by-side rows; only filled while <see cref="IsSplitDiff"/>.</summary>
    public ObservableCollection<DiffRowViewModel> DiffRows { get; } = [];
    public ObservableCollection<RecentRepositoryViewModel> RecentRepositories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepository))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand),
        nameof(StageAllCommand), nameof(UnstageAllCommand), nameof(CommitCommand), nameof(StashCommand), nameof(StashPopCommand), nameof(DiscardAllCommand),
        nameof(ForcePushCommand), nameof(StashWithMessageCommand), nameof(PushAllTagsCommand))]
    public partial string? RepositoryName { get; set; }

    public bool HasRepository => RepositoryName is not null;

    [ObservableProperty]
    public partial string? RepositoryPath { get; set; }

    [ObservableProperty]
    public partial string? CurrentBranch { get; set; }

    [ObservableProperty]
    public partial string? SyncStatus { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand),
        nameof(StageAllCommand), nameof(UnstageAllCommand), nameof(CommitCommand), nameof(StashCommand), nameof(StashPopCommand), nameof(DiscardAllCommand),
        nameof(CloneCommand), nameof(ForcePushCommand), nameof(StashWithMessageCommand), nameof(PushAllTagsCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Completes when the current busy period ends.</summary>
    private TaskCompletionSource _idle = CompletedIdle();

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
        {
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        else
        {
            _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = new TaskCompletionSource();
        idle.SetResult();
        return idle;
    }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    public partial CommitItemViewModel? SelectedCommit { get; set; }

    [ObservableProperty]
    public partial string? SelectedCommitMessage { get; set; }

    [ObservableProperty]
    public partial FileChangeItemViewModel? SelectedCommitFile { get; set; }

    [ObservableProperty]
    public partial FileChangeItemViewModel? SelectedUnstagedFile { get; set; }

    [ObservableProperty]
    public partial FileChangeItemViewModel? SelectedStagedFile { get; set; }

    [ObservableProperty]
    public partial string? DiffTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string CommitMessage { get; set; } = "";

    [ObservableProperty]
    public partial string ChangesHeader { get; set; } = "Changes";

    // ---------- Repository ----------

    public async Task OpenRepositoryAsync(string path)
    {
        ErrorMessage = null;
        try
        {
            IsBusy = true;
            BusyText = "Opening repository…";
            var repository = await GitRepository.OpenAsync(path);
            _repository = repository;
            RepositoryName = repository.Name;
            RepositoryPath = repository.Root;
            SelectedCommit = null;
            ResetHistoryFilter();
            IsAmend = false;
            CommitMessage = "";
            ClearDiff();
            _settings.AddRecent(repository.Root);
            LoadRecent();
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private Task OpenRecentAsync(RecentRepositoryViewModel recent) => OpenRepositoryAsync(recent.Path);

    [RelayCommand]
    private void RemoveRecent(RecentRepositoryViewModel recent)
    {
        _settings.RemoveRecent(recent.Path);
        LoadRecent();
    }

    [RelayCommand]
    private void CloseRepository()
    {
        _repository = null;
        RepositoryName = null;
        RepositoryPath = null;
        CurrentBranch = null;
        SyncStatus = null;
        Commits.Clear();
        LocalBranches.Clear();
        RemoteBranches.Clear();
        UnstagedFiles.Clear();
        StagedFiles.Clear();
        SelectedCommitFiles.Clear();
        Stashes.Clear();
        Tags.Clear();
        SelectedStashFiles.Clear();
        UpdateOperation(RepositoryOperation.None, null);
        ResetHistoryFilter();
        ClearDiff();
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private bool CanRunGit => _repository is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    public Task RefreshAsync() => RunGitAsync("Refreshing…", _ => Task.CompletedTask);

    /// <summary>Called when the window regains focus, so changes made outside the app show up.</summary>
    public async Task RefreshIfIdleAsync()
    {
        if (CanRunGit)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task FetchAsync() => RunGitAsync("Fetching…", (r, progress, ct) => r.FetchAsync(progress, ct), cancellable: true);

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task PullAsync() => RunGitAsync("Pulling…", (r, progress, ct) => r.PullAsync(progress, ct), cancellable: true);

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task PushAsync() => RunGitAsync("Pushing…", (r, progress, ct) => r.PushAsync(progress, cancellationToken: ct), cancellable: true);

    [RelayCommand]
    private Task CheckoutAsync(BranchItemViewModel branch)
    {
        if (branch.IsCurrent)
        {
            return Task.CompletedTask;
        }
        // origin/feature when a local "feature" already exists: just switch to the local one.
        var target = branch.Branch;
        if (target.IsRemote && LocalBranches.FirstOrDefault(b => b.Branch.Upstream == target.Name || b.Name == target.Name[(target.Name.IndexOf('/') + 1)..]) is { } local)
        {
            if (local.IsCurrent)
            {
                return Task.CompletedTask;
            }
            target = local.Branch;
        }
        return RunGitAsync($"Checking out {target.Name}…", r => r.CheckoutAsync(target));
    }

    [RelayCommand]
    private Task CreateBranchAsync(string name) =>
        RunGitAsync($"Creating branch {name}…", r => r.CreateBranchAsync(name));

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task StashAsync() => RunGitAsync("Stashing…", r => r.StashAsync());

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task StashPopAsync() => RunGitAsync("Applying stash…", r => r.StashPopAsync());

    // ---------- Command palette (Ctrl+P) ----------

    public void OpenPalette() => Palette.Open();

    private IEnumerable<PaletteItem> GetPaletteItems(string query)
    {
        if (HasRepository)
        {
            if (HasConflictedFiles)
            {
                yield return new PaletteItem("Resolve next conflict…", "Conflict", () => ResolveNextConflictCommand.ExecuteAsync(null),
                    Detail: $"{UnstagedFiles.Count(f => f.IsConflicted)} conflicted file(s)");
            }
            if (HasOperation)
            {
                yield return new PaletteItem($"Continue {OperationName}", "Conflict", () => ContinueOperationCommand.ExecuteAsync(null));
                yield return new PaletteItem($"Abort {OperationName}", "Conflict", () => AbortOperationCommand.ExecuteAsync(null));
            }
            foreach (var item in RepositoryCommands())
            {
                yield return item;
            }

            var branchName = ToBranchName(query);
            if (branchName is not null && CanRunGit && LocalBranches.All(b => b.Name != branchName))
            {
                yield return new PaletteItem($"Create branch “{branchName}”", "Branch",
                    () => CreateBranchCommand.ExecuteAsync(branchName), Detail: "from the current commit and check it out", IsFallback: true);
            }

            foreach (var branch in LocalBranches.Where(b => !b.IsCurrent))
            {
                yield return new PaletteItem($"Checkout {branch.Name}", "Branch", () => CheckoutCommand.ExecuteAsync(branch), Detail: branch.Tracking);
            }
            foreach (var branch in RemoteBranches)
            {
                yield return new PaletteItem($"Checkout {branch.Name}", "Remote branch", () => CheckoutCommand.ExecuteAsync(branch),
                    Detail: "creates a local tracking branch");
            }
            foreach (var item in StashAndTagPaletteItems())
            {
                yield return item;
            }
        }

        if (PickRepository is { } pick)
        {
            yield return new PaletteItem("Open repository…", "Repository", pick, "Ctrl+O");
        }
        if (CloneCommand.CanExecute(null))
        {
            yield return new PaletteItem("Clone repository…", "Repository", () => CloneCommand.ExecuteAsync(null), "Ctrl+Shift+O");
        }
        foreach (var recent in RecentRepositories.Where(r => !string.Equals(r.Path, RepositoryPath, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new PaletteItem($"Open {recent.Name}", "Recent", () => OpenRepositoryAsync(recent.Path), Detail: recent.Path);
        }
        if (HasRepository)
        {
            yield return new PaletteItem("Close repository", "Repository", () => { CloseRepository(); return Task.CompletedTask; });
        }
    }

    private IEnumerable<PaletteItem> RepositoryCommands()
    {
        (string Title, string Category, IAsyncRelayCommand Command, string? Shortcut)[] commands =
        [
            ("Pull", "Remote", PullCommand, null),
            ("Push", "Remote", PushCommand, null),
            ("Fetch", "Remote", FetchCommand, null),
            ("Force push (with lease)…", "Remote", ForcePushCommand, null),
            ("Commit staged changes", "Changes", CommitCommand, "Ctrl+Enter"),
            ("Amend last commit", "Changes", StartAmendCommand, null),
            ("Stage all changes", "Changes", StageAllCommand, null),
            ("Unstage all changes", "Changes", UnstageAllCommand, null),
            ("Discard all changes…", "Changes", DiscardAllCommand, null),
            ("Toggle side-by-side diff", "View", ToggleSplitDiffCommand, null),
            ("Stash all changes", "Stash", StashCommand, null),
            ("Stash with message…", "Stash", StashWithMessageCommand, null),
            ("Pop latest stash", "Stash", StashPopCommand, null),
            ("Create tag at HEAD…", "Tag", CreateTagAtHeadCommand, null),
            ("Push all tags", "Tag", PushAllTagsCommand, null),
            ("Refresh", "Repository", RefreshCommand, "F5"),
        ];
        return commands
            .Where(c => c.Command.CanExecute(null))
            .Select(c => new PaletteItem(c.Title, c.Category, () => c.Command.ExecuteAsync(null), c.Shortcut));
    }

    /// <summary>Turns palette input into a branch name ("my feature" → "my-feature"), or null if it cannot be one.</summary>
    private static string? ToBranchName(string query)
    {
        var name = string.Join('-', query.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0 || name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith(".lock") ||
            name.Contains("..") || name.Any(c => c is '~' or '^' or ':' or '?' or '*' or '[' or '\\' || char.IsControl(c)))
        {
            return null;
        }
        return name;
    }

    // ---------- Staging & committing ----------

    [RelayCommand]
    private Task StageFileAsync(FileChangeItemViewModel file) => RunGitAsync("Staging…", r => r.StageAsync([file.Path]));

    [RelayCommand]
    private Task UnstageFileAsync(FileChangeItemViewModel file) =>
        RunGitAsync("Unstaging…", r => r.UnstageAsync(file.Change.OriginalPath is { } original ? [original, file.Path] : [file.Path]));

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task StageAllAsync() => RunGitAsync("Staging…", r => r.StageAllAsync());

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task UnstageAllAsync() => RunGitAsync("Unstaging…", r => r.UnstageAllAsync());

    private bool CanCommit => CanRunGit && !string.IsNullOrWhiteSpace(CommitMessage);

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        var amend = IsAmend;
        if (StagedFiles.Count == 0 && !amend)
        {
            ErrorMessage = "Nothing staged. Stage files first (double-click a file or use “Stage all”).";
            return;
        }
        if (amend && _headPublished && !await ConfirmAsync("Amend pushed commit",
                $"The last commit is already on “{_upstream}”. Amending replaces it with a new commit, " +
                "so you'll have to force-push afterwards (Ctrl+P → “Force push”).\n\nOnly do this if nobody else has pulled it.", "Amend"))
        {
            return;
        }

        var message = CommitMessage.Trim();
        if (await RunGitAsync(amend ? "Amending…" : "Committing…", r => r.CommitAsync(message, amend)))
        {
            _prefilledAmendMessage = null;
            CommitMessage = "";
            IsAmend = false;
        }
    }

    // ---------- Selection → details & diff ----------

    partial void OnSelectedCommitChanged(CommitItemViewModel? value)
    {
        SelectedCommitFiles.Clear();
        SelectedCommitMessage = null;
        if (value is not null)
        {
            if (!_restoringSelection)
            {
                SelectedTabIndex = 1;
            }
            _ = LoadCommitDetailsAsync(value);
        }
    }

    partial void OnSelectedCommitFileChanged(FileChangeItemViewModel? value)
    {
        if (value is not null && SelectedCommit is { } commit)
        {
            _ = LoadDiffAsync($"{value.Path} @ {commit.ShortSha}", value.Path, DiffMode.ReadOnly,
                (r, ct) => r.GetCommitDiffAsync(commit.Commit, value.Change, ct));
        }
    }

    partial void OnSelectedUnstagedFileChanged(FileChangeItemViewModel? value)
    {
        if (value is not null)
        {
            SelectedStagedFile = null;
            // Untracked and conflicted files have no patchable diff: stage or discard them as a whole.
            var mode = value.Kind is FileChangeKind.Untracked or FileChangeKind.Conflicted ? DiffMode.ReadOnly : DiffMode.Unstaged;
            _ = LoadDiffAsync($"{value.Path} (unstaged)", value.Path, mode, (r, ct) => r.GetWorkingDiffAsync(value.Change, staged: false, ct));
        }
    }

    partial void OnSelectedStagedFileChanged(FileChangeItemViewModel? value)
    {
        if (value is not null)
        {
            SelectedUnstagedFile = null;
            _ = LoadDiffAsync($"{value.Path} (staged)", value.Path, DiffMode.Staged, (r, ct) => r.GetWorkingDiffAsync(value.Change, staged: true, ct));
        }
    }

    private async Task LoadCommitDetailsAsync(CommitItemViewModel item)
    {
        if (_repository is not { } repository)
        {
            return;
        }
        var ct = RestartDetails();
        try
        {
            var messageTask = repository.GetCommitMessageAsync(item.Commit.Sha, ct);
            var changesTask = repository.GetCommitChangesAsync(item.Commit, ct);
            var message = await messageTask;
            var changes = await changesTask;
            if (ct.IsCancellationRequested)
            {
                return;
            }
            SelectedCommitMessage = message;
            foreach (var change in changes)
            {
                SelectedCommitFiles.Add(new FileChangeItemViewModel(change, this));
            }
            SelectFileOfFileHistory(item);
        }
        catch (OperationCanceledException)
        {
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task LoadDiffAsync(string title, string path, DiffMode mode, Func<GitRepository, CancellationToken, Task<IReadOnlyList<DiffLine>>> load)
    {
        if (_repository is not { } repository)
        {
            return;
        }
        var ct = RestartDetails();
        try
        {
            var lines = await load(repository, ct);
            // Syntax highlighting can take a moment on big diffs: keep the UI responsive.
            var styles = await Task.Run(() => DiffStyles.Compute(path, lines), ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            DiffTitle = title;
            _diff = lines;
            _diffStyles = styles;
            CurrentDiffMode = lines.Any(l => l.IsHunk) ? mode : DiffMode.ReadOnly;
            ShowDiff();
        }
        catch (OperationCanceledException)
        {
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private CancellationToken RestartDetails()
    {
        _detailsCts?.Cancel();
        _detailsCts = new CancellationTokenSource();
        return _detailsCts.Token;
    }

    /// <summary>Fills <see cref="DiffLines"/> (and <see cref="DiffRows"/> in split mode) from the loaded diff.</summary>
    private void ShowDiff()
    {
        SetSelectedChangeLines([]);
        var unified = _diff.Select((line, i) => new DiffLineViewModel(line, i, CurrentDiffMode, this, _diffStyles.For(line, i, withPrefix: true))).ToList();
        if (_diff.Count == 0 && DiffTitle is not null)
        {
            var text = "No text changes (binary file, mode change or empty file).";
            unified.Add(new DiffLineViewModel(new DiffLine(DiffLineKind.Header, text), -1, DiffMode.ReadOnly, this, StyledText.Plain(text)));
        }
        Replace(DiffLines, unified);

        if (!IsSplitDiff)
        {
            DiffRows.Clear();
        }
        else if (_diff.Count == 0)
        {
            Replace(DiffRows, unified.Select(line =>
                new DiffRowViewModel(new DiffRow(DiffRowKind.Header, null, null), line, DiffCellViewModel.Empty, DiffCellViewModel.Empty)));
        }
        else
        {
            Replace(DiffRows, DiffRowViewModel.Build(_diff, unified, _diffStyles));
        }
    }

    private void ClearDiff()
    {
        DiffTitle = null;
        _diff = [];
        _diffStyles = DiffStyles.None;
        CurrentDiffMode = DiffMode.ReadOnly;
        SetSelectedChangeLines([]);
        DiffLines.Clear();
        DiffRows.Clear();
    }

    // ---------- Plumbing ----------

    /// <summary>Runs a git operation with busy indicator and error reporting, then reloads the repository state.</summary>
    private Task<bool> RunGitAsync(string busyText, Func<GitRepository, Task> action) =>
        RunGitAsync(busyText, (repository, _, _) => action(repository), cancellable: false);

    /// <summary>
    /// Like <see cref="RunGitAsync(string, Func{GitRepository, Task})"/>, for long network operations:
    /// git's progress is shown live and the user can cancel.
    /// </summary>
    private async Task<bool> RunGitAsync(string busyText, Func<GitRepository, IProgress<string>, CancellationToken, Task> action, bool cancellable)
    {
        // Wait instead of dropping the action: closing a confirmation dialog re-activates the window, which starts a
        // refresh just before the confirmed action (discard, reset, delete…) gets here.
        while (IsBusy)
        {
            await _idle.Task;
        }
        if (_repository is not { } repository)
        {
            return false;
        }

        var (progress, cancellation) = BeginBusy(busyText, cancellable);
        var success = true;
        try
        {
            await action(repository, progress, cancellation);
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
            success = false;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
            success = false;
        }

        try
        {
            BusyText = "Refreshing…";
            ProgressPercent = null;
            CanCancel = false;
            await ReloadAsync(repository);
        }
        catch (GitException ex)
        {
            ErrorMessage ??= ex.Message;
        }
        finally
        {
            EndBusy();
        }
        return success;
    }

    private async Task ReloadAsync(GitRepository repository)
    {
        var statusTask = repository.GetStatusAsync();
        var branchesTask = repository.GetBranchesAsync();
        var operationTask = repository.GetOperationAsync();
        var hasCommitsTask = repository.HasCommitsAsync();
        var stashesTask = repository.GetStashesAsync();
        var tagsTask = repository.GetTagsAsync();
        var filter = _historyFilter;
        var graphTask = Task.Run(async () =>
        {
            var (commits, revisions) = await LoadHistoryAsync(repository, filter);
            return (commits, revisions, rows: CommitGraph.Layout(GraphCommits(commits, filter)));
        });

        var status = await statusTask;
        var branches = await branchesTask;
        var operation = await operationTask;
        var hasCommits = await hasCommitsTask;
        var stashes = await stashesTask;
        var tags = await tagsTask;
        var mergeMessage = operation == RepositoryOperation.Merging ? await repository.GetMergeMessageAsync() : null;
        var (commits, revisions, rows) = await graphTask;

        if (!ReferenceEquals(repository, _repository))
        {
            return; // another repository was opened meanwhile
        }

        CurrentBranch = status.BranchName ?? "(detached HEAD)";
        SyncStatus = status.Upstream is null ? "not published" : $"↑{status.Ahead}  ↓{status.Behind}";
        UpdateOperation(operation, mergeMessage);
        UpdateAmendState(hasCommits, status);
        ApplyStashesAndTags(stashes, tags);

        // Keep showing the selected file; if all of it was just staged (or unstaged), follow it to the other list.
        var (selectedPath, wasStaged) = SelectedStagedFile is { } s ? (s.Path, true) : (SelectedUnstagedFile?.Path, false);
        Replace(UnstagedFiles, status.Unstaged.Select(c => new FileChangeItemViewModel(c, this)));
        Replace(StagedFiles, status.Staged.Select(c => new FileChangeItemViewModel(c, this)));
        NotifyConflictsChanged();
        var sameList = wasStaged ? StagedFiles : UnstagedFiles;
        var otherList = wasStaged ? UnstagedFiles : StagedFiles;
        var follow = sameList.FirstOrDefault(f => f.Path == selectedPath) ?? otherList.FirstOrDefault(f => f.Path == selectedPath);
        if (follow is not null && StagedFiles.Contains(follow))
        {
            SelectedStagedFile = follow;
        }
        else if (follow is not null)
        {
            SelectedUnstagedFile = follow;
        }
        else if (SelectedCommitFile is null)
        {
            ClearDiff();
        }
        var changeCount = status.Unstaged.Count + status.Staged.Count;
        ChangesHeader = changeCount == 0 ? "Changes" : $"Changes ({changeCount})";

        Replace(LocalBranches, branches.Where(b => !b.IsRemote).Select(b => new BranchItemViewModel(b, this)));
        Replace(RemoteBranches, branches.Where(b => b.IsRemote).Select(b => new BranchItemViewModel(b, this)));

        var selectedSha = SelectedCommit?.Commit.Sha;
        var laneCount = Math.Min(rows.Count == 0 ? 1 : rows.Max(r => r.LaneCount), MaxGraphLanes);
        var graphWidth = laneCount * GraphLaneWidth + 8;
        var items = commits.Select((c, i) => new CommitItemViewModel(c, rows[i], graphWidth, this)).ToList();
        _fileRevisions = revisions;
        UpdateHistoryFilterTitle(filter, commits.Count);
        // Only rebuild the graph when history, refs or the filter changed, so scroll position survives a refresh.
        if (!Equals(filter, _shownHistoryFilter) || !SameHistory(commits, Commits.Select(c => c.Commit).ToList()))
        {
            _shownHistoryFilter = filter;
            Replace(Commits, items);
            _restoringSelection = true;
            SelectedCommit = Commits.FirstOrDefault(c => c.Commit.Sha == selectedSha);
            _restoringSelection = false;
        }
    }

    private static bool SameHistory(IReadOnlyList<Commit> a, IReadOnlyList<Commit> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Sha == p.Second.Sha && p.First.Refs.SequenceEqual(p.Second.Refs));

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void LoadRecent() =>
        Replace(RecentRepositories, _settings.RecentRepositories.Select(p => new RecentRepositoryViewModel(p)));
}
