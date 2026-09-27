using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHr.Core;
using GitHr.Core.Conflicts;

namespace GitHr.App.ViewModels;

/// <summary>
/// Resolves one conflicted text file: a choice per conflict rebuilds the result, which stays editable.
/// Saving is allowed once the result contains no conflict markers.
/// </summary>
public partial class ConflictResolverViewModel : ViewModelBase
{
    private const int ContextLines = 3;

    private readonly ConflictDocument _document;
    private readonly Func<string, string, string, bool, Task<bool>>? _confirm;
    private bool _settingResult;
    private bool _manuallyEdited;

    public ConflictResolverViewModel(ConflictInfo conflict, RepositoryOperation operation,
        Func<string, string, string, bool, Task<bool>>? confirm = null)
    {
        Path = conflict.Path;
        _confirm = confirm;
        _document = ConflictDocument.Parse(conflict.WorkingText ?? "");
        (OursTitle, TheirsTitle, SidesHint) = DescribeSides(operation, _document.Conflicts.FirstOrDefault());

        var number = 0;
        var oursLine = 1; // line number in our version of the file (no markers), which is what people recognise
        IReadOnlyList<string> previousCommon = [];
        foreach (var segment in _document.Segments)
        {
            if (segment is CommonSegment common)
            {
                previousCommon = common.Lines;
                oursLine += common.Lines.Count;
            }
            else if (segment is ConflictBlock block)
            {
                Blocks.Add(new ConflictBlockViewModel(block, ++number, oursLine, previousCommon.TakeLast(ContextLines), this));
                previousCommon = [];
                oursLine += block.OursLines.Count;
            }
        }
        Rebuild();
    }

    public string Path { get; }
    public string OursTitle { get; }
    public string TheirsTitle { get; }
    public string SidesHint { get; }
    public ObservableCollection<ConflictBlockViewModel> Blocks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(StatusText), nameof(IsResultValid))]
    public partial string ResultText { get; set; } = "";

    public int UnresolvedCount => Blocks.Count(b => b.Choice is null);

    public bool IsResultValid => !ConflictDocument.HasConflictMarkers(ResultText);

    public bool CanSave => IsResultValid;

    public string StatusText => IsResultValid
        ? _manuallyEdited ? "Result edited by hand — ready to save." : "All conflicts resolved — ready to save."
        : UnresolvedCount > 0
            ? $"{UnresolvedCount} of {Blocks.Count} conflict(s) still unresolved."
            : "The result still contains conflict markers.";

    partial void OnResultTextChanged(string value)
    {
        if (!_settingResult)
        {
            _manuallyEdited = true;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Sets a conflict's choice and rebuilds the result (asking first if that would overwrite manual edits).</summary>
    public async Task ChooseAsync(ConflictBlockViewModel block, ConflictChoice choice) => await ChooseAsync([block], choice);

    [RelayCommand]
    private Task ChooseAllAsync(ConflictChoice choice) => ChooseAsync(Blocks, choice);

    private async Task ChooseAsync(IEnumerable<ConflictBlockViewModel> blocks, ConflictChoice choice)
    {
        if (_manuallyEdited && _confirm is not null && !await _confirm("Rebuild result",
                "You edited the result by hand. Rebuilding it from your choices replaces those edits.", "Rebuild", true))
        {
            return;
        }
        foreach (var block in blocks)
        {
            block.Choice = choice;
        }
        Rebuild();
    }

    private void Rebuild()
    {
        _settingResult = true;
        ResultText = _document.Render(Blocks.Select(b => b.Choice).ToList());
        _settingResult = false;
        _manuallyEdited = false;
        OnPropertyChanged(nameof(UnresolvedCount));
        OnPropertyChanged(nameof(StatusText));
    }

    private static (string Ours, string Theirs, string Hint) DescribeSides(RepositoryOperation operation, ConflictBlock? first)
    {
        var oursLabel = string.IsNullOrEmpty(first?.OursLabel) ? "" : $" — {first.OursLabel}";
        var theirsLabel = string.IsNullOrEmpty(first?.TheirsLabel) ? "" : $" — {first.TheirsLabel}";
        var (ours, theirs) = operation switch
        {
            // During a rebase git replays *your* commits onto the other branch, so the sides are swapped.
            RepositoryOperation.Rebasing => ("Ours: the branch you are rebasing onto", "Theirs: your commit being replayed"),
            RepositoryOperation.CherryPicking => ("Ours: your current branch", "Theirs: the commit being cherry-picked"),
            RepositoryOperation.Reverting => ("Ours: your current branch", "Theirs: the revert of the commit"),
            _ => ("Ours: your current branch", "Theirs: the branch being merged"),
        };
        var hint = operation == RepositoryOperation.Rebasing
            ? "Rebase: “ours” is the branch you are rebasing onto and “theirs” is your own commit — the opposite of a merge."
            : "Pick a side (or both) for each conflict, or edit the result directly.";
        return (ours + oursLabel, theirs + theirsLabel, hint);
    }
}

public partial class ConflictBlockViewModel : ViewModelBase
{
    private readonly ConflictResolverViewModel _owner;

    public ConflictBlockViewModel(ConflictBlock block, int number, int oursLine, IEnumerable<string> contextBefore, ConflictResolverViewModel owner)
    {
        Block = block;
        Number = number;
        OursLine = oursLine;
        _owner = owner;
        ContextBefore = string.Join('\n', contextBefore);
    }

    public ConflictBlock Block { get; }
    public int Number { get; }

    /// <summary>Where the conflict starts in our version of the file.</summary>
    public int OursLine { get; }

    public string Header => $"Conflict {Number} · line {OursLine}";
    public string ContextBefore { get; }
    public bool HasContext => ContextBefore.Length > 0;
    public string OursText => Block.OursLines.Count == 0 ? "(nothing)" : string.Join('\n', Block.OursLines);
    public string TheirsText => Block.TheirsLines.Count == 0 ? "(nothing)" : string.Join('\n', Block.TheirsLines);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOurs), nameof(IsTheirs), nameof(IsOursThenTheirs), nameof(IsTheirsThenOurs), nameof(ChoiceText))]
    public partial ConflictChoice? Choice { get; set; }

    public bool IsOurs => Choice == ConflictChoice.Ours;
    public bool IsTheirs => Choice == ConflictChoice.Theirs;
    public bool IsOursThenTheirs => Choice == ConflictChoice.OursThenTheirs;
    public bool IsTheirsThenOurs => Choice == ConflictChoice.TheirsThenOurs;

    public string ChoiceText => Choice switch
    {
        ConflictChoice.Ours => "✓ using ours",
        ConflictChoice.Theirs => "✓ using theirs",
        ConflictChoice.OursThenTheirs => "✓ using both (ours first)",
        ConflictChoice.TheirsThenOurs => "✓ using both (theirs first)",
        _ => "unresolved",
    };

    [RelayCommand]
    private Task ChooseAsync(ConflictChoice choice) => _owner.ChooseAsync(this, choice);
}
