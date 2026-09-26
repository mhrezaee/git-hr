namespace GitHr.Core.Graph;

/// <summary>Which half of a row a segment is drawn in: top edge to node, or node to bottom edge.</summary>
public enum GraphHalf
{
    Top,
    Bottom,
}

/// <summary>
/// A line inside one commit row. Top-half segments go from <see cref="FromLane"/> at the top edge
/// to <see cref="ToLane"/> at the vertical middle; bottom-half segments from the middle to the bottom edge.
/// <see cref="ColorLane"/> decides the color so a branch keeps one color along its whole lane.
/// </summary>
public readonly record struct GraphSegment(int FromLane, int ToLane, GraphHalf Half, int ColorLane);

public sealed record GraphRow(int NodeLane, int LaneCount, IReadOnlyList<GraphSegment> Segments, bool IsMerge);

/// <summary>
/// Assigns commits (newest first, as <c>git log --date-order</c> returns them) to vertical lanes
/// and computes the connecting lines between them.
/// </summary>
public static class CommitGraph
{
    public static IReadOnlyList<GraphRow> Layout(IReadOnlyList<Commit> commits)
    {
        var rows = new List<GraphRow>(commits.Count);
        // lanes[i] = SHA of the commit the line in lane i is heading to, or null for a free lane.
        var lanes = new List<string?>();

        foreach (var commit in commits)
        {
            var segments = new List<GraphSegment>();

            var nodeLane = lanes.IndexOf(commit.Sha);
            if (nodeLane < 0)
            {
                // Branch tip nobody pointed at yet: take the first free lane.
                nodeLane = FirstFree(lanes);
            }

            // Top half: every active lane either flows into this commit or passes straight by.
            var passThrough = new List<int>();
            for (var i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] is null)
                {
                    continue;
                }
                if (lanes[i] == commit.Sha)
                {
                    segments.Add(new GraphSegment(i, nodeLane, GraphHalf.Top, i));
                    lanes[i] = null;
                }
                else
                {
                    segments.Add(new GraphSegment(i, i, GraphHalf.Top, i));
                    passThrough.Add(i);
                }
            }

            // Bottom half: lines from this commit to each of its parents.
            for (var p = 0; p < commit.Parents.Count; p++)
            {
                var parent = commit.Parents[p];
                int parentLane;
                if (p == 0 && lanes[nodeLane] is null)
                {
                    // First parent always continues straight down this commit's lane, even when another
                    // lane already heads to it; both lanes then join at the parent (keeps main lines straight).
                    parentLane = nodeLane;
                    lanes[parentLane] = parent;
                }
                else
                {
                    parentLane = lanes.IndexOf(parent);
                    if (parentLane < 0)
                    {
                        parentLane = FirstFree(lanes);
                        lanes[parentLane] = parent;
                    }
                }
                segments.Add(new GraphSegment(nodeLane, parentLane, GraphHalf.Bottom, parentLane));
            }

            foreach (var i in passThrough)
            {
                segments.Add(new GraphSegment(i, i, GraphHalf.Bottom, i));
            }

            var laneCount = Math.Max(lanes.Count, nodeLane + 1);
            TrimFreeLanes(lanes);
            rows.Add(new GraphRow(nodeLane, laneCount, segments, commit.IsMerge));
        }

        return rows;
    }

    private static int FirstFree(List<string?> lanes)
    {
        var free = lanes.IndexOf(null);
        if (free >= 0)
        {
            return free;
        }
        lanes.Add(null);
        return lanes.Count - 1;
    }

    private static void TrimFreeLanes(List<string?> lanes)
    {
        while (lanes.Count > 0 && lanes[^1] is null)
        {
            lanes.RemoveAt(lanes.Count - 1);
        }
    }
}
