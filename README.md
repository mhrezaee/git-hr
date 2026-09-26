# GitHr

A fast, free, cross-platform Git GUI (in the spirit of GitKraken and Fork), built with C#, .NET 10 and [Avalonia UI](https://avaloniaui.net/).
Windows first; macOS and Linux run from the same code.

## Features (so far)

- Commit graph with colored lanes, merge nodes, branch and tag badges
- Local and remote branches with ahead/behind info; double-click to check out (remote → local tracking branch)
- Staging: stage and unstage single files or all files, commit (Ctrl+Enter)
- Diff viewer for working-copy files and for files in any commit
- Fetch (all remotes, prune), pull, and push (first push publishes the branch and sets its upstream)
- Refreshes automatically when the window regains focus; recent repositories on the start screen

## Public and private repositories

GitHr has no accounts, no paywall and no repo limits. All operations run the **installed `git` CLI**, so authentication works exactly like in your terminal:

- **HTTPS** (GitHub, Azure DevOps, GitLab, Bitbucket…): handled by [Git Credential Manager](https://github.com/git-ecosystem/git-credential-manager), which ships with Git for Windows. A login window appears the first time, and the credentials are then stored in the OS credential store.
- **SSH**: uses your SSH keys and `ssh-agent` (e.g. the Windows OpenSSH agent service, or Pageant).
- Hooks, LFS, `includeIf` configs and signing settings from your git config are all respected.

GitHr runs git with `GIT_TERMINAL_PROMPT=0`, so it never hangs waiting for terminal input.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Git 2.30+ on the `PATH`

## Build, run and test

```sh
dotnet build
dotnet run --project src/GitHr.App                 # start screen
dotnet run --project src/GitHr.App -- C:\path\repo # open a repo directly
dotnet test
```

Shortcuts: `Ctrl+O` open, `F5` refresh, `Ctrl+Enter` commit.

## Structure

| Project | Purpose |
|---|---|
| `src/GitHr.Core` | UI-independent Git layer: `GitRunner` (CLI process), `GitRepository` (operations), `Parsing/GitOutputParser` (porcelain parsing), `Graph/CommitGraph` (lane layout) |
| `src/GitHr.App` | Avalonia desktop app (MVVM with CommunityToolkit.Mvvm); `Controls/CommitGraphCell` draws the graph |
| `tests/GitHr.Core.Tests` | xUnit tests: parsers, graph layout, and end-to-end tests against real temporary repositories |

## Roadmap

- Line and hunk staging, discard changes
- Stash, tags, create/delete/rename branches, merge and rebase from the UI
- Interactive rebase, conflict resolution
- Clone dialog; GitHub/Azure DevOps integration (PRs)
- Search and filtering in history, file history, blame
- Multiple repository tabs, settings, light theme
- Packaging: MSIX/installer (Windows), .app/.dmg (macOS), AppImage/Flatpak (Linux)
