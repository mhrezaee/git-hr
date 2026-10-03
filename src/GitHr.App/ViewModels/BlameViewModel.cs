using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using GitHr.Core.Diff;
using GitHr.Core.History;

namespace GitHr.App.ViewModels;

/// <summary>Who last changed each line of a file, with syntax colors; a line's commit can be shown in the history.</summary>
public sealed partial class BlameViewModel : ViewModelBase
{
    private readonly Func<string, Task> _showCommit;

    public BlameViewModel(Blame blame, IReadOnlyDictionary<int, IReadOnlyList<SyntaxSpan>> syntax, Func<string, Task> showCommit)
    {
        Blame = blame;
        _showCommit = showCommit;

        // Age bar: newest commits brightest. Ranked by date, so a file with one ancient and many recent
        // commits still shows the differences between the recent ones.
        var dates = blame.Lines.Select(l => l.Commit.Date).Distinct().Order().ToList();
        var rank = dates.Select((date, i) => (date, i)).ToDictionary(x => x.date, x => x.i);
        double Age(DateTimeOffset date) => dates.Count <= 1 ? 1 : (double)rank[date] / (dates.Count - 1);

        Lines = blame.Lines.Select((line, i) => new BlameLineViewModel(
            line,
            new StyledText(line.Text, syntax.TryGetValue(i, out var spans) ? spans : [], null),
            IsBlockStart: i == 0 || !ReferenceEquals(blame.Lines[i - 1].Commit, line.Commit),
            AgeOpacity: 0.15 + 0.85 * Age(line.Commit.Date),
            Owner: this)).ToList();
        CommitCount = blame.Lines.Select(l => l.Commit.Sha).Distinct().Count();
    }

    public Blame Blame { get; }
    public IReadOnlyList<BlameLineViewModel> Lines { get; }
    public int CommitCount { get; }

    public string Title => Blame.Revision is null
        ? $"Blame — {Blame.Path}"
        : $"Blame — {Blame.Path} @ {Blame.Revision[..Math.Min(7, Blame.Revision.Length)]}{(Blame.Revision.EndsWith('^') ? "^" : "")}";

    public string Summary => $"{Lines.Count} lines from {CommitCount} commit(s). Double-click a line to show its commit.";

    /// <summary>Selects the line's commit in the main window's history.</summary>
    [RelayCommand]
    private Task ShowCommitAsync(BlameLineViewModel line) =>
        line.Line.Commit.IsUncommitted ? Task.CompletedTask : _showCommit(line.Line.Commit.Sha);
}

/// <param name="IsBlockStart">First line of a run of lines from the same commit: the commit details are shown here only.</param>
/// <param name="AgeOpacity">0.15 (oldest commit in the file) to 1 (newest).</param>
public sealed record BlameLineViewModel(BlameLine Line, StyledText Styled, bool IsBlockStart, double AgeOpacity, BlameViewModel Owner)
{
    public int Number => Line.Number;
    public string ShortSha => IsBlockStart ? Line.Commit.ShortSha : "";
    public string Author => IsBlockStart ? Line.Commit.Author : "";
    public string Date => IsBlockStart ? Line.Commit.Date.LocalDateTime.ToString("yyyy-MM-dd") : "";
    public string CommitSummary => IsBlockStart ? Line.Commit.Summary : "";
    public bool IsUncommitted => Line.Commit.IsUncommitted;

    public string ToolTip => Line.Commit.IsUncommitted
        ? "Not committed yet"
        : $"{Line.Commit.ShortSha} · {Line.Commit.Author} <{Line.Commit.AuthorEmail}> · {Line.Commit.Date.LocalDateTime:yyyy-MM-dd HH:mm}\n{Line.Commit.Summary}";
}
