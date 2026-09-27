using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHr.Core;

namespace GitHr.App.ViewModels;

/// <summary>Stash list and tags: sidebar sections, their context menus, and the stash details tab.</summary>
public partial class MainViewModel
{
    public const int StashTabIndex = 2;

    public ObservableCollection<StashItemViewModel> Stashes { get; } = [];
    public ObservableCollection<TagItemViewModel> Tags { get; } = [];
    public ObservableCollection<FileChangeItemViewModel> SelectedStashFiles { get; } = [];

    private void ApplyStashesAndTags(IReadOnlyList<Stash> stashes, IReadOnlyList<Tag> tags)
    {
        var selectedStash = SelectedStash?.Stash.Sha;
        Replace(Stashes, stashes.Select(s => new StashItemViewModel(s, this)));
        SelectedStash = Stashes.FirstOrDefault(s => s.Stash.Sha == selectedStash);
        if (SelectedStash is null && SelectedTabIndex == StashTabIndex)
        {
            SelectedTabIndex = 0; // the stash we were showing was popped or dropped
        }

        var selectedTag = SelectedTag?.Name;
        Replace(Tags, tags.Select(t => new TagItemViewModel(t, this)));
        _restoringSelection = true;
        SelectedTag = Tags.FirstOrDefault(t => t.Name == selectedTag);
        _restoringSelection = false;
    }

    // ---------- Stash details ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStash))]
    public partial StashItemViewModel? SelectedStash { get; set; }

    public bool HasSelectedStash => SelectedStash is not null;

    [ObservableProperty]
    public partial FileChangeItemViewModel? SelectedStashFile { get; set; }

    partial void OnSelectedStashChanged(StashItemViewModel? value)
    {
        SelectedStashFiles.Clear();
        if (value is not null)
        {
            SelectedTabIndex = StashTabIndex;
            _ = LoadStashFilesAsync(value);
        }
    }

    partial void OnSelectedStashFileChanged(FileChangeItemViewModel? value)
    {
        if (value is not null && SelectedStash is { } stash)
        {
            _ = LoadDiffAsync($"{value.Path} @ {stash.Name}", DiffMode.ReadOnly,
                (r, ct) => r.GetStashDiffAsync(stash.Stash, value.Change, ct));
        }
    }

    private async Task LoadStashFilesAsync(StashItemViewModel item)
    {
        if (_repository is not { } repository)
        {
            return;
        }
        try
        {
            var changes = await repository.GetStashChangesAsync(item.Stash);
            if (SelectedStash == item)
            {
                Replace(SelectedStashFiles, changes.Select(c => new FileChangeItemViewModel(c, this)));
            }
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    // ---------- Stash actions ----------

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private async Task StashWithMessageAsync()
    {
        if (await PromptAsync("Stash changes", "Message for this stash (helps you find it later):") is { } message)
        {
            await RunGitAsync("Stashing…", r => r.StashAsync(message));
        }
    }

    [RelayCommand]
    private Task ApplyStashAsync(StashItemViewModel stash) =>
        RunGitAsync($"Applying {stash.Name}…", r => r.StashApplyAsync(stash.Stash.Index));

    [RelayCommand]
    private Task PopStashAsync(StashItemViewModel stash) =>
        RunGitAsync($"Popping {stash.Name}…", r => r.StashPopAsync(stash.Stash.Index));

    [RelayCommand]
    private async Task DropStashAsync(StashItemViewModel stash)
    {
        if (await ConfirmAsync("Drop stash", $"Delete {stash.Name} “{stash.Message}”?\n\nIts changes are lost. This cannot be undone.",
                "Drop", destructive: true))
        {
            await RunGitAsync($"Dropping {stash.Name}…", r => r.StashDropAsync(stash.Stash.Index));
        }
    }

    // ---------- Tags ----------

    [ObservableProperty]
    public partial TagItemViewModel? SelectedTag { get; set; }

    /// <summary>Selecting a tag jumps to its commit in the history.</summary>
    partial void OnSelectedTagChanged(TagItemViewModel? value)
    {
        if (value is not null && !_restoringSelection)
        {
            ShowTagInHistory(value);
        }
    }

    [RelayCommand]
    private void ShowTagInHistory(TagItemViewModel tag)
    {
        var commit = Commits.FirstOrDefault(c => c.Commit.Sha == tag.Tag.CommitSha);
        if (commit is null)
        {
            ErrorMessage = $"{tag.Name} points at {tag.ShortSha}, which is older than the loaded history.";
            return;
        }
        SelectedCommit = commit;
    }

    [RelayCommand]
    private async Task CheckoutTagAsync(TagItemViewModel tag)
    {
        if (await ConfirmAsync("Checkout tag",
                $"Check out {tag.Name} as a detached HEAD?\n\nTo make changes on top of it, create a branch from the tag instead.", "Checkout"))
        {
            await RunGitAsync($"Checking out {tag.Name}…", r => r.CheckoutCommitAsync(tag.Tag.CommitSha));
        }
    }

    [RelayCommand]
    private async Task CreateBranchFromTagAsync(TagItemViewModel tag)
    {
        if (await PromptAsync("Create branch", $"New branch starting at tag “{tag.Name}”:") is { } name)
        {
            await RunGitAsync($"Creating branch {name}…", r => r.CreateBranchAsync(name, tag.Tag.CommitSha));
        }
    }

    [RelayCommand]
    private Task PushTagAsync(TagItemViewModel tag) =>
        RunGitAsync($"Pushing {tag.Name}…", (r, progress, ct) => r.PushTagAsync(tag.Name, progress, ct), cancellable: true);

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private Task PushAllTagsAsync() =>
        RunGitAsync("Pushing tags…", (r, progress, ct) => r.PushTagAsync(null, progress, ct), cancellable: true);

    [RelayCommand]
    private async Task DeleteTagAsync(TagItemViewModel tag)
    {
        if (await ConfirmAsync("Delete tag", $"Delete the local tag “{tag.Name}”?\n\nIf it was pushed, it stays on the remote.",
                "Delete", destructive: true))
        {
            await RunGitAsync($"Deleting {tag.Name}…", r => r.DeleteTagAsync(tag.Name));
        }
    }

    [RelayCommand]
    private async Task DeleteRemoteTagAsync(TagItemViewModel tag)
    {
        if (await ConfirmAsync("Delete tag on remote",
                $"Delete “{tag.Name}” on the remote?\n\nThis removes the tag for everyone using this remote. Your local tag is kept.",
                "Delete from remote", destructive: true))
        {
            await RunGitAsync($"Deleting {tag.Name} on the remote…", r => r.DeleteRemoteTagAsync(tag.Name));
        }
    }

    [RelayCommand]
    private Task CopyTagNameAsync(TagItemViewModel tag) => CopyText?.Invoke(tag.Name) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanAmend))]
    private async Task CreateTagAtHeadAsync()
    {
        if (await PromptAsync("Create tag", "Tag name for the current commit (HEAD):") is { } name)
        {
            await RunGitAsync($"Creating tag {name}…", r => r.CreateTagAsync(name, "HEAD"));
        }
    }

    [RelayCommand]
    private async Task CreateAnnotatedTagAtCommitAsync(CommitItemViewModel commit)
    {
        if (await PromptAsync("Create annotated tag", $"Tag name for {commit.ShortSha} “{commit.Subject}”:") is not { } name ||
            await PromptAsync("Create annotated tag", $"Message for {name} (e.g. release notes):") is not { } message)
        {
            return;
        }
        await RunGitAsync($"Creating tag {name}…", r => r.CreateTagAsync(name, commit.Commit.Sha, message));
    }

    private IEnumerable<PaletteItem> StashAndTagPaletteItems()
    {
        foreach (var stash in Stashes)
        {
            yield return new PaletteItem($"Apply {stash.Name}: {stash.Message}", "Stash", () => ApplyStashCommand.ExecuteAsync(stash), Detail: stash.Detail);
        }
        foreach (var tag in Tags)
        {
            yield return new PaletteItem($"Show tag {tag.Name}", "Tag", () => { ShowTagInHistory(tag); return Task.CompletedTask; }, Detail: tag.ShortSha);
        }
    }
}
