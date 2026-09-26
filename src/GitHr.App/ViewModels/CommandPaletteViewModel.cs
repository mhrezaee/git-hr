using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitHr.App.ViewModels;

/// <summary>One entry in the command palette.</summary>
/// <param name="IsFallback">Suggestions built from the typed text (e.g. "Create branch"); always listed after real matches.</param>
public sealed record PaletteItem(string Title, string Category, Func<Task> Execute, string? Shortcut = null, string? Detail = null, bool IsFallback = false);

/// <summary>Ctrl+P command palette: filters the available commands as you type, Enter runs the selected one.</summary>
public partial class CommandPaletteViewModel : ViewModelBase
{
    private const int MaxResults = 50;
    private readonly Func<string, IEnumerable<PaletteItem>> _source;

    public CommandPaletteViewModel(Func<string, IEnumerable<PaletteItem>> source)
    {
        _source = source;
    }

    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial PaletteItem? SelectedItem { get; set; }

    public void Open()
    {
        Query = "";
        Filter();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }
        var index = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        SelectedItem = Items[(index + delta + Items.Count) % Items.Count];
    }

    public async Task ExecuteAsync(PaletteItem? item = null)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }
        Close();
        await item.Execute();
    }

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        var query = Query.Trim();
        var matches = _source(query)
            .Select((item, order) => (item, order, score: Score(item, query)))
            .Where(m => m.score >= 0)
            .OrderBy(m => m.item.IsFallback)
            .ThenBy(m => m.score)
            .ThenBy(m => m.order)
            .Take(MaxResults)
            .Select(m => m.item)
            .ToList();

        Items.Clear();
        foreach (var item in matches)
        {
            Items.Add(item);
        }
        SelectedItem = Items.FirstOrDefault();
    }

    /// <summary>
    /// Lower is better, -1 means no match. Every word of the query must match the title (or category)
    /// as a prefix, a substring or a fuzzy subsequence ("psh" → "Push").
    /// </summary>
    public static int Score(PaletteItem item, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        var text = $"{item.Category}: {item.Title}";
        var total = 0;
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int score;
            if (item.Title.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                score = 0;
            else if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                score = 1;
            else if (IsSubsequence(word, text))
                score = 3;
            else
                return -1;
            total += score;
        }
        return total;
    }

    private static bool IsSubsequence(string word, string text)
    {
        var i = 0;
        foreach (var c in text)
        {
            if (i < word.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(word[i]))
            {
                i++;
            }
        }
        return i == word.Length;
    }
}
