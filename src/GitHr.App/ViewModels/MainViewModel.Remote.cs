using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHr.Core;

namespace GitHr.App.ViewModels;

/// <summary>Long-running operations (live progress + cancel), cloning, force push and amending.</summary>
public partial class MainViewModel
{
    private CancellationTokenSource? _busyCts;
    private bool _headPublished;
    private string? _upstream;
    private string? _prefilledAmendMessage;

    // ---------- Busy state, progress & cancel ----------

    /// <summary>Percentage parsed from git's progress ("Receiving objects:  45% (450/1000)"), or null if unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    public partial double? ProgressPercent { get; set; }

    public bool IsProgressIndeterminate => ProgressPercent is null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool CanCancel { get; set; }

    private (IProgress<string> Progress, CancellationToken Cancellation) BeginBusy(string busyText, bool cancellable)
    {
        IsBusy = true;
        BusyText = busyText;
        ErrorMessage = null;
        ProgressPercent = null;
        _busyCts = new CancellationTokenSource();
        CanCancel = cancellable;
        // Progress<T> captures the UI synchronization context, so reports arrive on the UI thread.
        var progress = new Progress<string>(line => OnGitProgress(busyText, line));
        return (progress, _busyCts.Token);
    }

    private void EndBusy()
    {
        IsBusy = false;
        CanCancel = false;
        BusyText = null;
        ProgressPercent = null;
        _busyCts?.Dispose();
        _busyCts = null;
    }

    private void OnGitProgress(string operation, string line)
    {
        if (!IsBusy)
        {
            return; // late report after the operation finished
        }
        var text = line.StartsWith("remote: ", StringComparison.Ordinal) ? line["remote: ".Length..] : line;
        BusyText = $"{operation.TrimEnd('…')}: {text.Trim()}";
        ProgressPercent = ParsePercent(text);
    }

    public static double? ParsePercent(string progressLine)
    {
        var match = PercentRegex().Match(progressLine);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex PercentRegex();

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        BusyText = "Cancelling…";
        _busyCts?.Cancel();
    }

    // ---------- Clone ----------

    /// <summary>Set by the window: shows the clone dialog for the given input; true when the user clicked Clone.</summary>
    public Func<CloneDialogViewModel, Task<bool>>? RequestClone { get; set; }

    private bool CanStartClone => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStartClone))]
    private async Task CloneAsync()
    {
        if (RequestClone is null)
        {
            return;
        }
        var dialog = new CloneDialogViewModel(_settings.EffectiveCloneFolder);
        if (!await RequestClone(dialog) || !dialog.CanClone)
        {
            return;
        }
        _settings.SetCloneFolder(dialog.ParentFolder.Trim());
        await CloneRepositoryAsync(dialog.Url.Trim(), dialog.TargetPath);
    }

    /// <summary>Clones with live progress (cancellable), then opens the new repository.</summary>
    public async Task<bool> CloneRepositoryAsync(string url, string targetPath)
    {
        if (IsBusy)
        {
            return false;
        }

        var name = System.IO.Path.GetFileName(targetPath);
        var (progress, cancellation) = BeginBusy($"Cloning {name}…", cancellable: true);
        GitRepository repository;
        try
        {
            repository = await GitRepository.CloneAsync(url, targetPath, progress, cancellationToken: cancellation);
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Clone cancelled.";
            return false;
        }
        finally
        {
            EndBusy();
        }

        await OpenRepositoryAsync(repository.Root);
        return true;
    }

    // ---------- Force push ----------

    [RelayCommand(CanExecute = nameof(CanRunGit))]
    private async Task ForcePushAsync()
    {
        if (await ConfirmAsync("Force push",
                $"Overwrite “{_upstream ?? "the remote branch"}” with your local “{CurrentBranch}”?\n\n" +
                "Uses --force-with-lease: git refuses if someone else pushed in the meantime, so their work isn't lost.",
                "Force push", destructive: true))
        {
            await RunGitAsync("Force pushing…", (r, progress, ct) => r.PushAsync(progress, forceWithLease: true, ct), cancellable: true);
        }
    }

    // ---------- Amend ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText))]
    public partial bool IsAmend { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAmendCommand), nameof(CreateTagAtHeadCommand))]
    public partial bool CanAmend { get; set; }

    public string CommitButtonText => IsAmend ? "Amend last commit" : $"Commit {StagedFiles.Count} staged file(s)";

    [RelayCommand(CanExecute = nameof(CanAmend))]
    private Task StartAmendAsync()
    {
        SelectedTabIndex = 0;
        IsAmend = true;
        return Task.CompletedTask;
    }

    partial void OnIsAmendChanged(bool value)
    {
        if (value)
        {
            _ = PrefillAmendMessageAsync();
        }
        else if (_prefilledAmendMessage is not null)
        {
            if (CommitMessage == _prefilledAmendMessage)
            {
                CommitMessage = "";
            }
            _prefilledAmendMessage = null;
        }
    }

    /// <summary>Amending usually keeps the message: start from the last commit's message if the box is empty.</summary>
    private async Task PrefillAmendMessageAsync()
    {
        if (_repository is not { } repository || !string.IsNullOrWhiteSpace(CommitMessage))
        {
            return;
        }
        try
        {
            var message = await repository.GetCommitMessageAsync("HEAD");
            if (IsAmend && string.IsNullOrWhiteSpace(CommitMessage))
            {
                CommitMessage = _prefilledAmendMessage = message;
            }
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void UpdateAmendState(bool hasCommits, RepositoryStatus status)
    {
        CanAmend = hasCommits;
        if (!hasCommits)
        {
            IsAmend = false;
        }
        _upstream = status.Upstream;
        // HEAD is already on the remote when the branch tracks one and has nothing unpushed.
        _headPublished = hasCommits && status.Upstream is not null && status.Ahead == 0;
        OnPropertyChanged(nameof(CommitButtonText));
    }
}
