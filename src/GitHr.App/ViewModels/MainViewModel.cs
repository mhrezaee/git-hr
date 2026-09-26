using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHr.Core;
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
    public ObservableCollection<DiffLine> DiffLines { get; } = [];
    public ObservableCollection<RecentRepositoryViewModel> RecentRepositories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepository))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(FetchCommand), nameof(PullCommand), nameof(PushCommand),
        nameof(StageAllCommand), nameof(UnstageAllCommand), nameof(CommitCommand), nameof(StashCommand), nameof(StashPopCommand))]
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
        nameof(StageAllCommand), nameof(UnstageAllCommand), nameof(CommitCommand), nameof(StashCommand), nameof(StashPopCommand))]
    public partial bool IsBusy { get; set; }

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
        ClearDiff();
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private bool CanRunGit => _repository is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    public Task RefreshAsync() => RunGitAsync("Refreshing…", _ => Task.CompletedTask);

    /// <summary>Called when the window regains focus, so changes made outside the app show up (like Fork).</summary>
    public async Task RefreshIfIdleAsync()
    {
        if (CanRunGit)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task FetchAsync() => RunGitAsync("Fetching…", r => r.FetchAsync());

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task PullAsync() => RunGitAsync("Pulling…", r => r.PullAsync());

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task PushAsync() => RunGitAsync("Pushing…", r => r.PushAsync());

    [RelayCommand]
    private Task CheckoutAsync(BranchItemViewModel branch) =>
        branch.IsCurrent ? Task.CompletedTask : RunGitAsync($"Checking out {branch.Name}…", r => r.CheckoutAsync(branch.Branch));

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
        }

        if (PickRepository is { } pick)
        {
            yield return new PaletteItem("Open repository…", "Repository", pick, "Ctrl+O");
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
            ("Commit staged changes", "Changes", CommitCommand, "Ctrl+Enter"),
            ("Stage all changes", "Changes", StageAllCommand, null),
            ("Unstage all changes", "Changes", UnstageAllCommand, null),
            ("Stash all changes", "Stash", StashCommand, null),
            ("Pop latest stash", "Stash", StashPopCommand, null),
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
        if (StagedFiles.Count == 0)
        {
            ErrorMessage = "Nothing staged. Stage files first (double-click a file or use “Stage all”).";
            return;
        }
        var message = CommitMessage.Trim();
        if (await RunGitAsync("Committing…", r => r.CommitAsync(message)))
        {
            CommitMessage = "";
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
            _ = LoadDiffAsync($"{value.Path} @ {commit.ShortSha}", (r, ct) => r.GetCommitDiffAsync(commit.Commit, value.Change, ct));
        }
    }

    partial void OnSelectedUnstagedFileChanged(FileChangeItemViewModel? value)
    {
        if (value is not null)
        {
            SelectedStagedFile = null;
            _ = LoadDiffAsync($"{value.Path} (unstaged)", (r, ct) => r.GetWorkingDiffAsync(value.Change, staged: false, ct));
        }
    }

    partial void OnSelectedStagedFileChanged(FileChangeItemViewModel? value)
    {
        if (value is not null)
        {
            SelectedUnstagedFile = null;
            _ = LoadDiffAsync($"{value.Path} (staged)", (r, ct) => r.GetWorkingDiffAsync(value.Change, staged: true, ct));
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
                SelectedCommitFiles.Add(new FileChangeItemViewModel(change));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task LoadDiffAsync(string title, Func<GitRepository, CancellationToken, Task<IReadOnlyList<DiffLine>>> load)
    {
        if (_repository is not { } repository)
        {
            return;
        }
        var ct = RestartDetails();
        try
        {
            var lines = await load(repository, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            DiffTitle = title;
            DiffLines.Clear();
            foreach (var line in lines)
            {
                DiffLines.Add(line);
            }
            if (lines.Count == 0)
            {
                DiffLines.Add(new DiffLine(DiffLineKind.Header, "No text changes (binary file, mode change or empty file)."));
            }
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

    private void ClearDiff()
    {
        DiffTitle = null;
        DiffLines.Clear();
    }

    // ---------- Plumbing ----------

    /// <summary>Runs a git operation with busy indicator and error reporting, then reloads the repository state.</summary>
    private async Task<bool> RunGitAsync(string busyText, Func<GitRepository, Task> action)
    {
        if (_repository is not { } repository || IsBusy)
        {
            return false;
        }

        IsBusy = true;
        BusyText = busyText;
        ErrorMessage = null;
        var success = true;
        try
        {
            await action(repository);
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
            success = false;
        }

        try
        {
            await ReloadAsync(repository);
        }
        catch (GitException ex)
        {
            ErrorMessage ??= ex.Message;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
        return success;
    }

    private async Task ReloadAsync(GitRepository repository)
    {
        var statusTask = repository.GetStatusAsync();
        var branchesTask = repository.GetBranchesAsync();
        var graphTask = Task.Run(async () =>
        {
            var commits = await repository.GetCommitsAsync(CommitLimit);
            return (commits, rows: CommitGraph.Layout(commits));
        });

        var status = await statusTask;
        var branches = await branchesTask;
        var (commits, rows) = await graphTask;

        if (!ReferenceEquals(repository, _repository))
        {
            return; // another repository was opened meanwhile
        }

        CurrentBranch = status.BranchName ?? "(detached HEAD)";
        SyncStatus = status.Upstream is null ? "not published" : $"↑{status.Ahead}  ↓{status.Behind}";

        var selectedUnstaged = SelectedUnstagedFile?.Path;
        var selectedStaged = SelectedStagedFile?.Path;
        Replace(UnstagedFiles, status.Unstaged.Select(c => new FileChangeItemViewModel(c)));
        Replace(StagedFiles, status.Staged.Select(c => new FileChangeItemViewModel(c)));
        SelectedUnstagedFile = UnstagedFiles.FirstOrDefault(f => f.Path == selectedUnstaged);
        SelectedStagedFile = SelectedUnstagedFile is null ? StagedFiles.FirstOrDefault(f => f.Path == selectedStaged) : null;
        if (SelectedUnstagedFile is null && SelectedStagedFile is null && SelectedCommitFile is null)
        {
            ClearDiff();
        }
        var changeCount = status.Unstaged.Count + status.Staged.Count;
        ChangesHeader = changeCount == 0 ? "Changes" : $"Changes ({changeCount})";

        Replace(LocalBranches, branches.Where(b => !b.IsRemote).Select(b => new BranchItemViewModel(b)));
        Replace(RemoteBranches, branches.Where(b => b.IsRemote).Select(b => new BranchItemViewModel(b)));

        var selectedSha = SelectedCommit?.Commit.Sha;
        var laneCount = Math.Min(rows.Count == 0 ? 1 : rows.Max(r => r.LaneCount), MaxGraphLanes);
        var graphWidth = laneCount * GraphLaneWidth + 8;
        var items = commits.Select((c, i) => new CommitItemViewModel(c, rows[i], graphWidth)).ToList();
        // Only rebuild the graph when history or refs changed, so scroll position survives a refresh.
        if (!SameHistory(commits, Commits.Select(c => c.Commit).ToList()))
        {
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
