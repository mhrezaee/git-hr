using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHr.Core;

namespace GitHr.App.ViewModels;

/// <summary>Branch, commit, discard, partial-staging and conflict actions (context menus, diff panel, banner).</summary>
public partial class MainViewModel
{
    private IReadOnlyList<DiffLine> _diff = [];
    private IReadOnlySet<int> _selectedDiffLines = new HashSet<int>();
    private string? _prefilledMergeMessage;

    // ---------- Dialog hooks (set by the window; tests replace them) ----------

    /// <summary>Asks the user to confirm: (title, message, confirm button text, destructive) → confirmed.</summary>
    public Func<string, string, string, bool, Task<bool>>? Confirm { get; set; }

    /// <summary>Asks the user for text: (title, message, initial text) → text or null when cancelled.</summary>
    public Func<string, string, string, Task<string?>>? Prompt { get; set; }

    public Func<string, Task>? CopyText { get; set; }

    private Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false) =>
        Confirm?.Invoke(title, message, confirmText, destructive) ?? Task.FromResult(false);

    private async Task<string?> PromptAsync(string title, string message, string initial = "")
    {
        var text = Prompt is null ? null : await Prompt(title, message, initial);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // ---------- Branch context menu ----------

    [RelayCommand]
    private Task MergeBranchAsync(BranchItemViewModel branch) => branch.IsCurrent
        ? Task.CompletedTask
        : RunGitAsync($"Merging {branch.Name} into {CurrentBranch}…", r => r.MergeAsync(branch.Name));

    [RelayCommand]
    private async Task RebaseOntoBranchAsync(BranchItemViewModel branch)
    {
        if (branch.IsCurrent || !await ConfirmAsync("Rebase",
                $"Rebase “{CurrentBranch}” onto “{branch.Name}”?\n\nYour commits are replayed on top of {branch.Name} and get new IDs. " +
                "Avoid rebasing commits that others already pulled.", "Rebase"))
        {
            return;
        }
        await RunGitAsync($"Rebasing onto {branch.Name}…", r => r.RebaseAsync(branch.Name));
    }

    [RelayCommand]
    private async Task CreateBranchFromAsync(BranchItemViewModel branch)
    {
        if (await PromptAsync("Create branch", $"New branch starting at “{branch.Name}”:") is { } name)
        {
            await RunGitAsync($"Creating branch {name}…", r => r.CreateBranchAsync(name, branch.Name));
        }
    }

    [RelayCommand]
    private async Task RenameBranchAsync(BranchItemViewModel branch)
    {
        if (branch.IsLocal && await PromptAsync("Rename branch", $"New name for “{branch.Name}”:", branch.Name) is { } name && name != branch.Name)
        {
            await RunGitAsync($"Renaming {branch.Name}…", r => r.RenameBranchAsync(branch.Name, name));
        }
    }

    [RelayCommand]
    private async Task DeleteBranchAsync(BranchItemViewModel branch)
    {
        if (branch.IsCurrent)
        {
            ErrorMessage = "You can't delete the branch that is checked out. Check out another branch first.";
            return;
        }

        if (branch.Branch.IsRemote)
        {
            if (await ConfirmAsync("Delete remote branch",
                    $"Delete “{branch.Name}” on the remote?\n\nThis removes the branch for everyone using this remote.", "Delete from remote", destructive: true))
            {
                await RunGitAsync($"Deleting {branch.Name} on the remote…", r => r.DeleteRemoteBranchAsync(branch.Name));
            }
            return;
        }

        if (!await ConfirmAsync("Delete branch", $"Delete local branch “{branch.Name}”?", "Delete", destructive: true))
        {
            return;
        }
        if (!await RunGitAsync($"Deleting {branch.Name}…", r => r.DeleteBranchAsync(branch.Name)))
        {
            // Git refuses when the branch has commits that aren't merged anywhere.
            var reason = ErrorMessage;
            if (await ConfirmAsync("Force delete branch",
                    $"Git refused to delete “{branch.Name}”:\n\n{reason}\n\nForce delete it? Its unmerged commits will no longer be reachable from any branch.",
                    "Force delete", destructive: true))
            {
                await RunGitAsync($"Deleting {branch.Name}…", r => r.DeleteBranchAsync(branch.Name, force: true));
            }
        }
    }

    // ---------- Commit context menu ----------

    [RelayCommand]
    private async Task CheckoutCommitAsync(CommitItemViewModel commit)
    {
        if (await ConfirmAsync("Checkout commit",
                $"Check out {commit.ShortSha} as a detached HEAD?\n\nNew commits won't belong to any branch until you create one.", "Checkout"))
        {
            await RunGitAsync($"Checking out {commit.ShortSha}…", r => r.CheckoutCommitAsync(commit.Commit.Sha));
        }
    }

    [RelayCommand]
    private async Task CreateBranchAtCommitAsync(CommitItemViewModel commit)
    {
        if (await PromptAsync("Create branch", $"New branch at {commit.ShortSha} “{commit.Subject}”:") is { } name)
        {
            await RunGitAsync($"Creating branch {name}…", r => r.CreateBranchAsync(name, commit.Commit.Sha));
        }
    }

    [RelayCommand]
    private async Task CreateTagAtCommitAsync(CommitItemViewModel commit)
    {
        if (await PromptAsync("Create tag", $"Tag name for {commit.ShortSha} “{commit.Subject}”:") is { } name)
        {
            await RunGitAsync($"Creating tag {name}…", r => r.CreateTagAsync(name, commit.Commit.Sha));
        }
    }

    [RelayCommand]
    private Task CherryPickAsync(CommitItemViewModel commit) =>
        RunGitAsync($"Cherry-picking {commit.ShortSha}…", r => r.CherryPickAsync(commit.Commit));

    [RelayCommand]
    private Task RevertCommitAsync(CommitItemViewModel commit) =>
        RunGitAsync($"Reverting {commit.ShortSha}…", r => r.RevertAsync(commit.Commit));

    [RelayCommand]
    private Task ResetSoftAsync(CommitItemViewModel commit) => ResetToAsync(commit, ResetMode.Soft);

    [RelayCommand]
    private Task ResetMixedAsync(CommitItemViewModel commit) => ResetToAsync(commit, ResetMode.Mixed);

    [RelayCommand]
    private Task ResetHardAsync(CommitItemViewModel commit) => ResetToAsync(commit, ResetMode.Hard);

    private async Task ResetToAsync(CommitItemViewModel commit, ResetMode mode)
    {
        var effect = mode switch
        {
            ResetMode.Soft => "Changes of the later commits stay staged.",
            ResetMode.Mixed => "Changes of the later commits stay in your files, unstaged.",
            _ => "ALL uncommitted changes and the changes of the later commits are permanently discarded.",
        };
        if (await ConfirmAsync($"Reset ({mode.ToString().ToLowerInvariant()})",
                $"Move “{CurrentBranch}” to {commit.ShortSha} “{commit.Subject}”?\n\n{effect}", "Reset", destructive: mode == ResetMode.Hard))
        {
            await RunGitAsync($"Resetting to {commit.ShortSha}…", r => r.ResetAsync(commit.Commit.Sha, mode));
        }
    }

    [RelayCommand]
    private Task CopyShaAsync(CommitItemViewModel commit) => CopyText?.Invoke(commit.Commit.Sha) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopySubjectAsync(CommitItemViewModel commit) => CopyText?.Invoke(commit.Subject) ?? Task.CompletedTask;

    // ---------- Discard ----------

    [RelayCommand]
    private async Task DiscardFileAsync(FileChangeItemViewModel file)
    {
        var message = file.IsUntracked
            ? $"Delete the untracked file “{file.Path}”?"
            : $"Discard all unstaged changes in “{file.Path}”?";
        if (await ConfirmAsync("Discard changes", message + "\n\nThis cannot be undone.", "Discard", destructive: true))
        {
            await RunGitAsync($"Discarding {file.FileName}…", r => r.DiscardAsync(file.Change));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private async Task DiscardAllAsync()
    {
        if (UnstagedFiles.Count == 0)
        {
            return;
        }
        var untracked = UnstagedFiles.Count(f => f.IsUntracked);
        var message = $"Discard all unstaged changes in {UnstagedFiles.Count} file(s)?" +
            (untracked > 0 ? $"\n\n{untracked} untracked file(s) will be deleted." : "") +
            "\n\nStaged changes are kept. This cannot be undone.";
        if (await ConfirmAsync("Discard all changes", message, "Discard all", destructive: true))
        {
            await RunGitAsync("Discarding changes…", r => r.DiscardAllAsync());
        }
    }

    // ---------- Hunks & lines ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnstagedDiff), nameof(IsStagedDiff))]
    public partial DiffMode CurrentDiffMode { get; set; }

    public bool IsUnstagedDiff => CurrentDiffMode == DiffMode.Unstaged;
    public bool IsStagedDiff => CurrentDiffMode == DiffMode.Staged;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StageLinesCommand), nameof(UnstageLinesCommand), nameof(DiscardLinesCommand))]
    public partial int SelectedChangeLineCount { get; set; }

    /// <summary>Called by the diff list when its selection changes (Ctrl/Shift+click for several lines).</summary>
    public void SetDiffSelection(IEnumerable<DiffLineViewModel> lines)
    {
        _selectedDiffLines = lines.Where(l => l.Line.IsChange).Select(l => l.Index).ToHashSet();
        SelectedChangeLineCount = _selectedDiffLines.Count;
    }

    private bool HasSelectedLines => SelectedChangeLineCount > 0;

    [RelayCommand]
    private Task StageHunkAsync(DiffLineViewModel hunk) => CurrentDiffMode == DiffMode.Unstaged
        ? ApplyAsync("Staging hunk…", PatchBuilder.ChangesInHunk(_diff, hunk.Index), cached: true, reverse: false)
        : Task.CompletedTask;

    [RelayCommand]
    private Task UnstageHunkAsync(DiffLineViewModel hunk) => CurrentDiffMode == DiffMode.Staged
        ? ApplyAsync("Unstaging hunk…", PatchBuilder.ChangesInHunk(_diff, hunk.Index), cached: true, reverse: true)
        : Task.CompletedTask;

    [RelayCommand]
    private async Task DiscardHunkAsync(DiffLineViewModel hunk)
    {
        if (CurrentDiffMode == DiffMode.Unstaged &&
            await ConfirmAsync("Discard hunk", "Discard the changes in this hunk?\n\nThis cannot be undone.", "Discard", destructive: true))
        {
            await ApplyAsync("Discarding hunk…", PatchBuilder.ChangesInHunk(_diff, hunk.Index), cached: false, reverse: true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedLines))]
    private Task StageLinesAsync() => CurrentDiffMode == DiffMode.Unstaged
        ? ApplyAsync("Staging lines…", _selectedDiffLines, cached: true, reverse: false)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelectedLines))]
    private Task UnstageLinesAsync() => CurrentDiffMode == DiffMode.Staged
        ? ApplyAsync("Unstaging lines…", _selectedDiffLines, cached: true, reverse: true)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelectedLines))]
    private async Task DiscardLinesAsync()
    {
        if (CurrentDiffMode == DiffMode.Unstaged &&
            await ConfirmAsync("Discard lines", $"Discard the {SelectedChangeLineCount} selected line(s)?\n\nThis cannot be undone.", "Discard", destructive: true))
        {
            await ApplyAsync("Discarding lines…", _selectedDiffLines, cached: false, reverse: true);
        }
    }

    private Task ApplyAsync(string busyText, IReadOnlySet<int> lines, bool cached, bool reverse)
    {
        var patch = PatchBuilder.Build(_diff, lines, reverse);
        return patch is null ? Task.CompletedTask : RunGitAsync(busyText, r => r.ApplyPatchAsync(patch, cached, reverse));
    }

    // ---------- Merge / rebase / cherry-pick / revert in progress ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperation), nameof(OperationName), nameof(OperationHint))]
    public partial RepositoryOperation Operation { get; set; }

    public bool HasOperation => Operation != RepositoryOperation.None;

    public string OperationName => Operation switch
    {
        RepositoryOperation.Merging => "merge",
        RepositoryOperation.Rebasing => "rebase",
        RepositoryOperation.CherryPicking => "cherry-pick",
        RepositoryOperation.Reverting => "revert",
        _ => "",
    };

    public string OperationHint => HasOperation
        ? $"A {OperationName} is in progress. Resolve the conflicts (files marked “!”), stage them, then continue — or abort to go back."
        : "";

    private void UpdateOperation(RepositoryOperation operation, string? mergeMessage)
    {
        Operation = operation;
        if (mergeMessage is not null && string.IsNullOrWhiteSpace(CommitMessage))
        {
            CommitMessage = _prefilledMergeMessage = mergeMessage;
        }
        else if (operation == RepositoryOperation.None && _prefilledMergeMessage is not null)
        {
            if (CommitMessage == _prefilledMergeMessage)
            {
                CommitMessage = "";
            }
            _prefilledMergeMessage = null;
        }
    }

    [RelayCommand]
    private Task ContinueOperationAsync() => HasOperation
        ? RunGitAsync($"Continuing {OperationName}…", r => r.ContinueOperationAsync(Operation))
        : Task.CompletedTask;

    [RelayCommand]
    private async Task AbortOperationAsync()
    {
        if (HasOperation && await ConfirmAsync($"Abort {OperationName}",
                $"Abort the {OperationName} and go back to the state before it started?\n\nConflict resolutions you made are lost.", "Abort", destructive: true))
        {
            var operation = Operation;
            await RunGitAsync($"Aborting {OperationName}…", r => r.AbortOperationAsync(operation));
        }
    }
}
