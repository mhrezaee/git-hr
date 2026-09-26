using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitHr.App.ViewModels;
using GitHr.Core.Graph;

namespace GitHr.App.Controls;

/// <summary>Draws the lanes, curves and node of one commit row in the history graph.</summary>
public sealed class CommitGraphCell : Control
{
    public static readonly StyledProperty<GraphRow?> RowProperty =
        AvaloniaProperty.Register<CommitGraphCell, GraphRow?>(nameof(Row));

    private const double LeftPadding = 4;
    private const double NodeRadius = 5;

    private static readonly IBrush[] LaneBrushes =
    [
        new SolidColorBrush(Color.Parse("#16A9E0")),
        new SolidColorBrush(Color.Parse("#E8A33D")),
        new SolidColorBrush(Color.Parse("#A66BDB")),
        new SolidColorBrush(Color.Parse("#3DC47E")),
        new SolidColorBrush(Color.Parse("#E5534B")),
        new SolidColorBrush(Color.Parse("#E0C341")),
        new SolidColorBrush(Color.Parse("#2EC4B6")),
        new SolidColorBrush(Color.Parse("#E86BA8")),
    ];

    private static readonly IPen[] LanePens = LaneBrushes.Select(b => (IPen)new Pen(b, 2)).ToArray();
    private static readonly IBrush NodeCenter = new SolidColorBrush(Color.Parse("#1E2129"));

    static CommitGraphCell()
    {
        AffectsRender<CommitGraphCell>(RowProperty);
    }

    public GraphRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Row is not { } row)
        {
            return;
        }

        var height = Bounds.Height;
        var middle = height / 2;

        foreach (var segment in row.Segments)
        {
            var (y1, y2) = segment.Half == GraphHalf.Top ? (0d, middle) : (middle, height);
            var start = new Point(X(segment.FromLane), y1);
            var end = new Point(X(segment.ToLane), y2);
            var pen = LanePens[segment.ColorLane % LanePens.Length];

            if (segment.FromLane == segment.ToLane)
            {
                context.DrawLine(pen, start, end);
                continue;
            }

            // Smooth S-curve between lanes.
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var controlY = (y1 + y2) / 2;
                g.BeginFigure(start, false);
                g.CubicBezierTo(new Point(start.X, controlY), new Point(end.X, controlY), end);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }

        var center = new Point(X(row.NodeLane), middle);
        var brush = LaneBrushes[row.NodeLane % LaneBrushes.Length];
        context.DrawEllipse(brush, null, center, NodeRadius, NodeRadius);
        if (row.IsMerge)
        {
            // Merge commits: hollow node.
            context.DrawEllipse(NodeCenter, null, center, NodeRadius - 2.5, NodeRadius - 2.5);
        }
    }

    private static double X(int lane) => LeftPadding + lane * MainViewModel.GraphLaneWidth + MainViewModel.GraphLaneWidth / 2;
}
