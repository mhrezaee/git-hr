namespace GitHr.Core;

public static class GitUrl
{
    /// <summary>
    /// The folder name <c>git clone</c> would pick: the last path segment without ".git". Handles HTTPS, SSH
    /// (<c>git@host:owner/repo.git</c>), Azure DevOps (<c>.../_git/repo</c>) and local paths. Null if none can be found.
    /// </summary>
    public static string? GetRepositoryName(string url)
    {
        var trimmed = url.Trim().TrimEnd('/', '\\');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        var start = trimmed.LastIndexOfAny(['/', '\\', ':']) + 1;
        var name = trimmed[start..];
        return name.Length == 0 || name.Any(c => Path.GetInvalidFileNameChars().Contains(c)) ? null : name;
    }
}
