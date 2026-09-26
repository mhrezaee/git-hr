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

    /// <summary>
    /// Clones <paramref name="url"/> into <paramref name="targetDirectory"/> (which must be missing or empty) and opens it.
    /// Private repositories authenticate through the user's credential helper or SSH agent, like any git command.
    /// </summary>
    public static async Task<GitRepository> CloneAsync(
        string url,
        string targetDirectory,
        IProgress<string>? progress = null,
        GitRunner? git = null,
        CancellationToken cancellationToken = default)
    {
        git ??= new GitRunner();
        var target = Path.GetFullPath(targetDirectory);
        var existed = Directory.Exists(target);
        if (existed && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new GitException($"'{target}' already exists and is not empty.");
        }
        var parent = Path.GetDirectoryName(target) ?? throw new GitException($"'{target}' is not a valid folder.");
        Directory.CreateDirectory(parent);

        try
        {
            var result = await git.RunAsync(parent, ["clone", "--progress", "--", url, target], progress: progress, cancellationToken: cancellationToken);
            result.EnsureSuccess();
        }
        catch (OperationCanceledException)
        {
            // git was killed and could not clean up its half-finished clone.
            if (!existed)
            {
                TryDeleteDirectory(target);
            }
            throw;
        }
        return await OpenAsync(target, git, cancellationToken);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // git object files are read-only
            }
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a locked file must not hide the cancellation.
        }
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

    /// <summary>Whether HEAD points at a commit (false in a brand-new repository).</summary>
    public Task<bool> HasCommitsAsync(CancellationToken cancellationToken = default) => HasHeadAsync(cancellationToken);

    /// <summary>With <paramref name="amend"/>, replaces the last commit with the staged changes and the new message.</summary>
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

    /// <summary>Creates a branch at <paramref name="startPoint"/> (default: HEAD), optionally checking it out.</summary>
    public Task CreateBranchAsync(string name, string? startPoint = null, bool checkout = true, CancellationToken cancellationToken = default)
    {
        List<string> args = checkout ? ["switch", "-c", name] : ["branch", name];
        if (startPoint is not null)
        {
            args.Add(startPoint);
        }
        return RunAsync(args, cancellationToken);
    }

    public Task RenameBranchAsync(string oldName, string newName, CancellationToken cancellationToken = default) =>
        RunAsync(["branch", "-m", oldName, newName], cancellationToken);

    /// <summary>Deletes a local branch. Without <paramref name="force"/> git refuses to delete unmerged work.</summary>
    public Task DeleteBranchAsync(string name, bool force = false, CancellationToken cancellationToken = default) =>
        RunAsync(["branch", force ? "-D" : "-d", name], cancellationToken);

    /// <summary>Deletes a branch on its remote, e.g. <c>origin/feature</c>.</summary>
    public async Task DeleteRemoteBranchAsync(string remoteBranch, CancellationToken cancellationToken = default)
    {
        var remotes = await GetRemotesAsync(cancellationToken);
        var remote = remotes.OrderByDescending(r => r.Length).FirstOrDefault(r => remoteBranch.StartsWith(r + "/", StringComparison.Ordinal))
            ?? throw new GitException($"No remote found for '{remoteBranch}'.");
        await RunAsync(["push", remote, "--delete", remoteBranch[(remote.Length + 1)..]], cancellationToken);
    }

    public Task CreateTagAsync(string name, string target, CancellationToken cancellationToken = default) =>
        RunAsync(["tag", name, target], cancellationToken);

    public Task CheckoutCommitAsync(string sha, CancellationToken cancellationToken = default) =>
        RunAsync(["switch", "--detach", sha], cancellationToken);

    /// <summary>Merges <paramref name="revision"/> into the current branch. Conflicts leave a merge in progress.</summary>
    public Task MergeAsync(string revision, CancellationToken cancellationToken = default) =>
        RunAsync(["merge", "--no-edit", revision], cancellationToken);

    /// <summary>Rebases the current branch onto <paramref name="upstream"/>.</summary>
    public Task RebaseAsync(string upstream, CancellationToken cancellationToken = default) =>
        RunAsync(["rebase", upstream], cancellationToken);

    public Task CherryPickAsync(Commit commit, CancellationToken cancellationToken = default) =>
        RunAsync(commit.IsMerge ? ["cherry-pick", "-m", "1", commit.Sha] : ["cherry-pick", commit.Sha], cancellationToken);

    public Task RevertAsync(Commit commit, CancellationToken cancellationToken = default) =>
        RunAsync(commit.IsMerge ? ["revert", "--no-edit", "-m", "1", commit.Sha] : ["revert", "--no-edit", commit.Sha], cancellationToken);

    public Task ResetAsync(string revision, ResetMode mode, CancellationToken cancellationToken = default) =>
        RunAsync(["reset", mode switch
        {
            ResetMode.Soft => "--soft",
            ResetMode.Hard => "--hard",
            _ => "--mixed",
        }, revision, "--"], cancellationToken);

    /// <summary>Detects a merge, rebase, cherry-pick or revert that stopped (usually on conflicts).</summary>
    public async Task<RepositoryOperation> GetOperationAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["rev-parse", "--git-path", "rebase-merge", "--git-path", "rebase-apply", "--git-path", "MERGE_HEAD",
                "--git-path", "CHERRY_PICK_HEAD", "--git-path", "REVERT_HEAD"],
            cancellationToken);
        var paths = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Path.GetFullPath(p, Root))
            .ToArray();
        if (paths.Length < 5) return RepositoryOperation.None;
        if (Directory.Exists(paths[0]) || Directory.Exists(paths[1])) return RepositoryOperation.Rebasing;
        if (File.Exists(paths[2])) return RepositoryOperation.Merging;
        if (File.Exists(paths[3])) return RepositoryOperation.CherryPicking;
        if (File.Exists(paths[4])) return RepositoryOperation.Reverting;
        return RepositoryOperation.None;
    }

    /// <summary>The message git prepared for the pending merge commit (comments removed), if any.</summary>
    public async Task<string?> GetMergeMessageAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath((await RunAsync(["rev-parse", "--git-path", "MERGE_MSG"], cancellationToken)).Output.Trim(), Root);
        if (!File.Exists(path))
        {
            return null;
        }
        var lines = (await File.ReadAllLinesAsync(path, cancellationToken)).Where(l => !l.StartsWith('#'));
        return string.Join('\n', lines).Trim();
    }

    public Task ContinueOperationAsync(RepositoryOperation operation, CancellationToken cancellationToken = default) =>
        RunAsync([OperationCommand(operation), "--continue"], cancellationToken);

    public Task AbortOperationAsync(RepositoryOperation operation, CancellationToken cancellationToken = default) =>
        RunAsync([OperationCommand(operation), "--abort"], cancellationToken);

    private static string OperationCommand(RepositoryOperation operation) => operation switch
    {
        RepositoryOperation.Merging => "merge",
        RepositoryOperation.Rebasing => "rebase",
        RepositoryOperation.CherryPicking => "cherry-pick",
        RepositoryOperation.Reverting => "revert",
        _ => throw new GitException("No merge, rebase, cherry-pick or revert is in progress."),
    };

    // ---------- Partial staging & discarding ----------

    /// <summary>Applies a patch built by <see cref="PatchBuilder"/> to the index (<paramref name="cached"/>) or the working tree.</summary>
    public async Task ApplyPatchAsync(string patch, bool cached, bool reverse, CancellationToken cancellationToken = default)
    {
        List<string> args = ["apply", "--recount", "--whitespace=nowarn"];
        if (cached) args.Add("--cached");
        if (reverse) args.Add("--reverse");
        args.Add("-");
        var result = await _git.RunAsync(Root, args, standardInput: patch, cancellationToken: cancellationToken);
        result.EnsureSuccess();
    }

    /// <summary>Reverts a file's unstaged changes to the staged version; deletes it if it is untracked.</summary>
    public Task DiscardAsync(FileChange change, CancellationToken cancellationToken = default) =>
        change.Kind == FileChangeKind.Untracked
            ? RunAsync(["clean", "-f", "-q", "--", change.Path], cancellationToken)
            : RunAsync(["restore", "--worktree", "--", change.Path], cancellationToken);

    /// <summary>Discards all unstaged changes and deletes all untracked files (ignored files are kept).</summary>
    public async Task DiscardAllAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        var tracked = status.Unstaged.Where(c => c.Kind != FileChangeKind.Untracked).Select(c => c.Path).ToList();
        if (tracked.Count > 0)
        {
            var input = string.Join('\0', tracked);
            var result = await _git.RunAsync(Root, ["restore", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul"],
                standardInput: input, cancellationToken: cancellationToken);
            result.EnsureSuccess();
        }
        if (status.Unstaged.Any(c => c.Kind == FileChangeKind.Untracked))
        {
            await RunAsync(["clean", "-f", "-d", "-q"], cancellationToken);
        }
    }

    private async Task<string[]> GetRemotesAsync(CancellationToken cancellationToken) =>
        (await RunAsync(["remote"], cancellationToken)).Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Stashes all local changes, including untracked files.</summary>
    public Task StashAsync(string? message = null, CancellationToken cancellationToken = default) =>
        RunAsync(message is null ? ["stash", "push", "-u"] : ["stash", "push", "-u", "-m", message], cancellationToken);

    public Task StashPopAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["stash", "pop"], cancellationToken);

    public Task FetchAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(["fetch", "--all", "--prune", "--progress"], progress, cancellationToken);

    public Task PullAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        RunAsync(["pull", "--progress"], progress, cancellationToken);

    /// <param name="forceWithLease">Overwrite the remote branch (after amend/rebase), but only if nobody else pushed to it meanwhile.</param>
    public async Task PushAsync(IProgress<string>? progress = null, bool forceWithLease = false, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (status.Upstream is not null)
        {
            await RunAsync(forceWithLease ? ["push", "--progress", "--force-with-lease"] : ["push", "--progress"], progress, cancellationToken);
            return;
        }

        if (status.BranchName is null)
        {
            throw new GitException("Cannot push a detached HEAD. Check out a branch first.");
        }

        // First push of a new branch: publish it and set the upstream.
        var remotes = await GetRemotesAsync(cancellationToken);
        if (remotes.Length == 0)
        {
            throw new GitException("This repository has no remote to push to.");
        }
        var remote = remotes.Contains("origin") ? "origin" : remotes[0];
        await RunAsync(["push", "--progress", "--set-upstream", remote, status.BranchName], progress, cancellationToken);
    }

    private async Task<bool> HasHeadAsync(CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(Root, ["rev-parse", "--verify", "-q", "HEAD"], cancellationToken: cancellationToken);
        return result.Success;
    }

    private Task<GitResult> RunAsync(IEnumerable<string> args, CancellationToken cancellationToken) =>
        RunAsync(args, progress: null, cancellationToken);

    private async Task<GitResult> RunAsync(IEnumerable<string> args, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(Root, args, progress: progress, cancellationToken: cancellationToken);
        return result.EnsureSuccess();
    }
}
