using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using GitHr.Core;
using GitHr.Core.Conflicts;

namespace GitHr.App.ViewModels;

/// <summary>Resolving conflicted files: the resolver window, whole-side choices and "mark resolved".</summary>
public partial class MainViewModel
{
    /// <summary>Set by the window: shows the resolver; true when the user saved the result.</summary>
    public Func<ConflictResolverViewModel, Task<bool>>? OpenConflictResolver { get; set; }

    public bool HasConflictedFiles => UnstagedFiles.Any(f => f.Kind == FileChangeKind.Conflicted);

    private void NotifyConflictsChanged() => OnPropertyChanged(nameof(HasConflictedFiles));

    [RelayCommand]
    private async Task ResolveConflictAsync(FileChangeItemViewModel file)
    {
        if (_repository is not { } repository || OpenConflictResolver is null || IsBusy)
        {
            return;
        }

        ConflictInfo conflict;
        try
        {
            conflict = await repository.GetConflictAsync(file.Path);
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        if (!conflict.CanResolveInText)
        {
            var reason = conflict.IsBinary ? "is a binary file"
                : !conflict.HasOurs ? "was deleted on our side and changed on theirs"
                : !conflict.HasTheirs ? "was changed on our side and deleted on theirs"
                : "has no text to merge";
            ErrorMessage = $"“{file.Path}” {reason}, so it can't be merged line by line. " +
                           "Right-click it and choose “Take ours” or “Take theirs”.";
            return;
        }

        var resolver = new ConflictResolverViewModel(conflict, Operation, Confirm);
        if (await OpenConflictResolver(resolver) && resolver.CanSave)
        {
            var content = resolver.ResultText;
            await RunGitAsync($"Resolving {file.FileName}…", r => r.ResolveWithContentAsync(file.Path, content));
        }
    }

    /// <summary>Opens the resolver for the first conflicted file (banner button).</summary>
    [RelayCommand]
    private Task ResolveNextConflictAsync() =>
        UnstagedFiles.FirstOrDefault(f => f.Kind == FileChangeKind.Conflicted) is { } file
            ? ResolveConflictAsync(file)
            : Task.CompletedTask;

    [RelayCommand]
    private Task TakeOursAsync(FileChangeItemViewModel file) => TakeSideAsync(file, ConflictSide.Ours);

    [RelayCommand]
    private Task TakeTheirsAsync(FileChangeItemViewModel file) => TakeSideAsync(file, ConflictSide.Theirs);

    private async Task TakeSideAsync(FileChangeItemViewModel file, ConflictSide side)
    {
        var (mine, other) = side == ConflictSide.Ours ? ("our", "their") : ("their", "our");
        if (await ConfirmAsync($"Take {mine}s",
                $"Resolve “{file.Path}” by using {mine} version of the whole file?\n\n{char.ToUpperInvariant(other[0])}{other[1..]} changes to this file are dropped.",
                $"Take {mine}s"))
        {
            await RunGitAsync($"Resolving {file.FileName}…", r => r.TakeSideAsync(file.Path, side));
        }
    }

    [RelayCommand]
    private async Task MarkResolvedAsync(FileChangeItemViewModel file)
    {
        if (_repository is not { } repository)
        {
            return;
        }
        var conflict = await repository.GetConflictAsync(file.Path);
        if (conflict.WorkingText is { } text && ConflictDocument.HasConflictMarkers(text) &&
            !await ConfirmAsync("Mark as resolved", $"“{file.Path}” still contains conflict markers. Mark it as resolved anyway?", "Mark resolved"))
        {
            return;
        }
        await RunGitAsync($"Marking {file.FileName} resolved…", r => r.MarkResolvedAsync(file.Path));
    }
}
