namespace GitHr.Core.Conflicts;

public enum ConflictSide
{
    Ours,
    Theirs,
}

/// <summary>State of one conflicted path during a merge, rebase, cherry-pick or revert.</summary>
/// <param name="HasBase">A common ancestor version exists (stage 1); false for add/add conflicts.</param>
/// <param name="HasOurs">Our side still has the file (stage 2); false when we deleted it.</param>
/// <param name="HasTheirs">Their side still has the file (stage 3); false when they deleted it.</param>
/// <param name="WorkingText">The working-tree file with conflict markers; null if it is missing or binary.</param>
public sealed record ConflictInfo(string Path, bool HasBase, bool HasOurs, bool HasTheirs, string? WorkingText, bool IsBinary)
{
    /// <summary>One side deleted the file and the other changed it: only a whole-side choice makes sense.</summary>
    public bool IsDeleteConflict => !HasOurs || !HasTheirs;

    /// <summary>The file can be resolved conflict by conflict in the text resolver.</summary>
    public bool CanResolveInText => !IsBinary && !IsDeleteConflict && WorkingText is not null;
}
