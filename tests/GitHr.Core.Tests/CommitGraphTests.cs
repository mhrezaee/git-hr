using GitHr.Core.Graph;

namespace GitHr.Core.Tests;

public class CommitGraphTests
{
    private static Commit C(string sha, params string[] parents) =>
        new(sha, parents, "a", "a@x", DateTimeOffset.UnixEpoch, sha, []);

    [Fact]
    public void LinearHistory_StaysInOneLane()
    {
        var rows = CommitGraph.Layout([C("c", "b"), C("b", "a"), C("a")]);

        Assert.All(rows, r => Assert.Equal(0, r.NodeLane));
        Assert.All(rows, r => Assert.Equal(1, r.LaneCount));
        Assert.Contains(new GraphSegment(0, 0, GraphHalf.Top, 0), rows[1].Segments);
        Assert.Empty(rows[2].Segments.Where(s => s.Half == GraphHalf.Bottom));
    }

    [Fact]
    public void MergeOpensSecondLaneAndBranchJoinsBack()
    {
        // m merges f into b; f and b both come from a.
        var rows = CommitGraph.Layout([C("m", "b", "f"), C("f", "a"), C("b", "a"), C("a")]);

        Assert.True(rows[0].IsMerge);
        Assert.Equal(0, rows[0].NodeLane);
        Assert.Contains(new GraphSegment(0, 1, GraphHalf.Bottom, 1), rows[0].Segments);

        Assert.Equal(1, rows[1].NodeLane); // f sits in the second lane
        Assert.Equal(0, rows[2].NodeLane); // b continues the first lane

        // a: both lanes flow into it, graph collapses back to one lane afterwards.
        Assert.Equal(0, rows[3].NodeLane);
        Assert.Contains(new GraphSegment(1, 0, GraphHalf.Top, 1), rows[3].Segments);
        Assert.Contains(new GraphSegment(0, 0, GraphHalf.Top, 0), rows[3].Segments);
    }

    [Fact]
    public void TwoBranchTips_GetSeparateLanes()
    {
        var rows = CommitGraph.Layout([C("x", "a"), C("y", "a"), C("a")]);

        Assert.Equal(0, rows[0].NodeLane);
        Assert.Equal(1, rows[1].NodeLane);
        Assert.Equal(2, rows[1].LaneCount);
        Assert.Equal(0, rows[2].NodeLane);
    }
}
