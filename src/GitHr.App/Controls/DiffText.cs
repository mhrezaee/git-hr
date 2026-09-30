using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GitHr.App.ViewModels;

namespace GitHr.App.Controls;

/// <summary>
/// A text block for one diff line: syntax colors as runs, and the changed part of a replaced line on
/// <see cref="EmphasisBrush"/> (set by the added/removed line styles).
/// </summary>
public sealed class DiffText : TextBlock
{
    public static readonly StyledProperty<StyledText?> StyledProperty =
        AvaloniaProperty.Register<DiffText, StyledText?>(nameof(Styled));

    public static readonly StyledProperty<IBrush?> EmphasisBrushProperty =
        AvaloniaProperty.Register<DiffText, IBrush?>(nameof(EmphasisBrush));

    private static readonly Dictionary<string, IBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    public StyledText? Styled
    {
        get => GetValue(StyledProperty);
        set => SetValue(StyledProperty, value);
    }

    public IBrush? EmphasisBrush
    {
        get => GetValue(EmphasisBrushProperty);
        set => SetValue(EmphasisBrushProperty, value);
    }

    // Styled like any TextBlock ("Border.diffline.header TextBlock" etc.).
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StyledProperty || change.Property == EmphasisBrushProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var inlines = Inlines ??= [];
        inlines.Clear();
        if (Styled is not { } styled || styled.Text.Length == 0)
        {
            return;
        }

        var text = styled.Text;
        var emphasis = styled.Emphasis is { Length: > 0 } range && EmphasisBrush is not null ? range : (Core.Diff.TextRange?)null;
        var cuts = new SortedSet<int> { 0, text.Length };
        foreach (var span in styled.Syntax)
        {
            cuts.Add(Math.Clamp(span.Start, 0, text.Length));
            cuts.Add(Math.Clamp(span.Start + span.Length, 0, text.Length));
        }
        if (emphasis is { } e)
        {
            cuts.Add(Math.Clamp(e.Start, 0, text.Length));
            cuts.Add(Math.Clamp(e.End, 0, text.Length));
        }

        var bounds = cuts.ToArray();
        var spanIndex = 0;
        for (var i = 0; i < bounds.Length - 1; i++)
        {
            var (start, end) = (bounds[i], bounds[i + 1]);
            var run = new Run(text[start..end]);

            // Spans are sorted and don't overlap: advance to the one that may cover this piece.
            while (spanIndex < styled.Syntax.Count && styled.Syntax[spanIndex].Start + styled.Syntax[spanIndex].Length <= start)
            {
                spanIndex++;
            }
            if (spanIndex < styled.Syntax.Count && styled.Syntax[spanIndex] is var span && span.Start <= start)
            {
                run.Foreground = BrushFor(span.Color);
                if (span.Bold)
                {
                    run.FontWeight = FontWeight.Bold;
                }
                if (span.Italic)
                {
                    run.FontStyle = FontStyle.Italic;
                }
            }
            if (emphasis is { } em && start >= em.Start && end <= em.End)
            {
                run.Background = EmphasisBrush;
            }
            inlines.Add(run);
        }
    }

    private static IBrush BrushFor(string color)
    {
        if (!BrushCache.TryGetValue(color, out var brush))
        {
            brush = Color.TryParse(color, out var parsed) ? new ImmutableSolidColorBrush(parsed) : Avalonia.Media.Brushes.White;
            BrushCache[color] = brush;
        }
        return brush;
    }
}
