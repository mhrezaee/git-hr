using System.Diagnostics;
using System.Text;

namespace GitHr.Core;

/// <summary>
/// Runs the installed <c>git</c> executable. Using the real CLI (instead of a library)
/// means authentication for public and private repositories works exactly as it does in
/// a terminal: Git Credential Manager (HTTPS: GitHub, Azure DevOps, GitLab, Bitbucket...),
/// SSH keys / ssh-agent, credential helpers, hooks, LFS and the user's git config.
/// </summary>
public sealed class GitRunner
{
    public GitRunner(string gitExecutable = "git")
    {
        GitExecutable = gitExecutable;
    }

    public string GitExecutable { get; }

    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        var args = arguments.ToList();
        var startInfo = new ProcessStartInfo(GitExecutable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };

        // Global options: stable, machine-readable output regardless of the user's config.
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("color.ui=false");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // A GUI has no terminal: never let git block waiting for terminal input.
        // Credential helpers (e.g. Git Credential Manager) still show their own login windows.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Never open an editor (merge/rebase/cherry-pick --continue): accept the prepared message as-is.
        startInfo.Environment["GIT_EDITOR"] = "true";
        // Background refreshes (status) must not take locks that conflict with the user's own git commands.
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitException($"Could not start '{GitExecutable}'. Is Git installed and on the PATH?", ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
        }
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return new GitResult(args, process.ExitCode, await outputTask, await errorTask);
    }
}

public sealed record GitResult(IReadOnlyList<string> Arguments, int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    public GitResult EnsureSuccess()
    {
        if (!Success)
        {
            var message = string.IsNullOrWhiteSpace(Error) ? Output : Error;
            throw new GitException($"git {string.Join(' ', Arguments)} failed: {message.Trim()}");
        }
        return this;
    }
}

public sealed class GitException : Exception
{
    public GitException(string message, Exception? innerException = null) : base(message, innerException) { }
}
