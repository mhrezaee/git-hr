using GitHr.Core.Parsing;

namespace GitHr.Core;

/// <summary>High-level operations on one working copy. All calls go through the git CLI.</summary>
public sealed class GitRepository
{
    private readonly GitRunner _git;

    private GitRepository(string root, GitRunner git)
    {
        Root = root;
        _git = git;
    }

    /// <summary>Top-level directory of the working copy.</summary>
    public string Root { get; }

    public string Name => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Opens the repository containing <paramref name="path"/> (any sub-folder works).</summary>
    public static async Task<GitRepository> OpenAsync(string path, GitRunner? git = null, CancellationToken cancellationToken = default)
    {
        git ??= new GitRunner();
        if (!Directory.Exists(path))
        {
            throw new GitException($"Folder '{path}' does not exist.");
        }

        var result = await git.RunAsync(path, ["rev-parse", "--show-toplevel"], cancellationToken: cancellationToken);
        if (!result.Success)
        {
            throw new GitException($"'{path}' is not a git repository.");
        }

        var root = Path.GetFullPath(result.Output.Trim());
        return new GitRepository(root, git);
    }

    public async Task<IReadOnlyList<Commit>> GetCommitsAsync(int maxCount = 2000, CancellationToken cancellationToken = default)
    {
        List<string> args =
        [
            "log", "--date-order", "--decorate=full", $"--max-count={maxCount}",
            $"--format={GitOutputParser.LogFormat}",
            "--branches", "--remotes", "--tags",
        ];
        if (await HasHeadAsync(cancellationToken))
        {
            args.Add("HEAD"); // include a detached HEAD
        }
        else if ((await GetBranchesAsync(cancellationToken)).Count == 0)
        {
            return []; // brand-new repository without commits
        }
        args.Add("--");

        var result = await RunAsync(args, cancellationToken);
        return GitOutputParser.ParseLog(result.Output);
    }

    public async Task<string> GetCommitMessageAsync(string sha, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["show", "-s", "--format=%B", sha, "--"], cancellationToken);
        return result.Output.TrimEnd();
    }

    public async Task<IReadOnlyList<Branch>> GetBranchesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["for-each-ref", $"--format={GitOutputParser.BranchFormat}", "refs/heads", "refs/remotes"],
            cancellationToken);
        return GitOutputParser.ParseBranches(result.Output);
    }

    public async Task<RepositoryStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"],
            cancellationToken);
        return GitOutputParser.ParseStatus(result.Output);
    }

    /// <summary>Diff of a working-copy file, either the staged (index) or unstaged version.</summary>
    public async Task<IReadOnlyList<DiffLine>> GetWorkingDiffAsync(FileChange change, bool staged, CancellationToken cancellationToken = default)
    {
        GitResult result;
        if (change.Kind == FileChangeKind.Untracked)
        {
            // Exit code 1 just means "there are differences".
            result = await _git.RunAsync(Root, ["diff", "--no-index", "--no-ext-diff", "--", "/dev/null", change.Path], cancellationToken: cancellationToken);
            if (result.ExitCode > 1)
            {
                result.EnsureSuccess();
            }
        }
        else
        {
            List<string> args = ["diff", "--no-ext-diff", "-M"];
            if (staged)
            {
                args.Add("--cached");
            }
            args.Add("--");
            if (change.OriginalPath is not null)
            {
                args.Add(change.OriginalPath);
            }
            args.Add(change.Path);
            result = await RunAsync(args, cancellationToken);
        }
        return GitOutputParser.ParseDiff(result.Output);
    }

    /// <summary>Files changed by a commit (compared to its first parent).</summary>
    public async Task<IReadOnlyList<FileChange>> GetCommitChangesAsync(Commit commit, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync([.. CommitDiffArgs(commit), "--name-status", "-z"], cancellationToken);
        return GitOutputParser.ParseNameStatus(result.Output);
    }

    public async Task<IReadOnlyList<DiffLine>> GetCommitDiffAsync(Commit commit, FileChange change, CancellationToken cancellationToken = default)
    {
        List<string> args = [.. CommitDiffArgs(commit), "-p", "--no-ext-diff", "--"];
        if (change.OriginalPath is not null)
        {
            args.Add(change.OriginalPath);
        }
        args.Add(change.Path);
        var result = await RunAsync(args, cancellationToken);
        return GitOutputParser.ParseDiff(result.Output);
    }

    private static List<string> CommitDiffArgs(Commit commit) => commit.Parents.Count == 0
        ? ["diff-tree", "--root", "--no-commit-id", "-r", "-M", commit.Sha]
        : ["diff-tree", "--no-commit-id", "-r", "-M", commit.Parents[0], commit.Sha];

    public Task StageAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        RunAsync(["add", "-A", "--", .. paths], cancellationToken);

    public Task StageAllAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["add", "-A"], cancellationToken);

    public async Task UnstageAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        if (await HasHeadAsync(cancellationToken))
        {
            await RunAsync(["restore", "--staged", "--", .. paths], cancellationToken);
        }
        else
        {
            await RunAsync(["rm", "--cached", "-r", "-q", "--", .. paths], cancellationToken);
        }
    }

    public async Task UnstageAllAsync(CancellationToken cancellationToken = default)
    {
        if (await HasHeadAsync(cancellationToken))
        {
            await RunAsync(["reset", "-q"], cancellationToken);
        }
        else
        {
            await RunAsync(["rm", "--cached", "-r", "-q", "."], cancellationToken);
        }
    }

    public async Task CommitAsync(string message, bool amend = false, CancellationToken cancellationToken = default)
    {
        List<string> args = ["commit", "-F", "-"];
        if (amend)
        {
            args.Add("--amend");
        }
        var result = await _git.RunAsync(Root, args, standardInput: message, cancellationToken: cancellationToken);
        result.EnsureSuccess();
    }

    public Task CheckoutAsync(Branch branch, CancellationToken cancellationToken = default) => branch.IsRemote
        // Remote branch: create a local tracking branch with the same name (origin/feature -> feature).
        ? RunAsync(["switch", "--track", branch.Name], cancellationToken)
        : RunAsync(["switch", branch.Name], cancellationToken);

    public Task CreateBranchAsync(string name, bool checkout = true, CancellationToken cancellationToken = default) => checkout
        ? RunAsync(["switch", "-c", name], cancellationToken)
        : RunAsync(["branch", name], cancellationToken);

    /// <summary>Stashes all local changes, including untracked files.</summary>
    public Task StashAsync(string? message = null, CancellationToken cancellationToken = default) =>
        RunAsync(message is null ? ["stash", "push", "-u"] : ["stash", "push", "-u", "-m", message], cancellationToken);

    public Task StashPopAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["stash", "pop"], cancellationToken);

    public Task FetchAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["fetch", "--all", "--prune"], cancellationToken);

    public Task PullAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["pull"], cancellationToken);

    public async Task PushAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (status.Upstream is not null)
        {
            await RunAsync(["push"], cancellationToken);
            return;
        }

        if (status.BranchName is null)
        {
            throw new GitException("Cannot push a detached HEAD. Check out a branch first.");
        }

        // First push of a new branch: publish it and set the upstream.
        var remotes = (await RunAsync(["remote"], cancellationToken)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (remotes.Length == 0)
        {
            throw new GitException("This repository has no remote to push to.");
        }
        var remote = remotes.Contains("origin") ? "origin" : remotes[0];
        await RunAsync(["push", "--set-upstream", remote, status.BranchName], cancellationToken);
    }

    private async Task<bool> HasHeadAsync(CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(Root, ["rev-parse", "--verify", "-q", "HEAD"], cancellationToken: cancellationToken);
        return result.Success;
    }

    private async Task<GitResult> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(Root, args, cancellationToken: cancellationToken);
        return result.EnsureSuccess();
    }
}
