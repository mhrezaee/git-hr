# GitHr

A fast, free, cross-platform Git GUI built with C#, .NET 10 and [Avalonia UI](https://avaloniaui.net/).
Windows first; macOS and Linux run from the same code.

## Features

### History
- Commit graph with colored lanes, merge nodes, and branch and tag badges
- Commit details: full message, author, date, SHA and changed files, with a diff per file
- Right-click a commit to check it out (detached), create a branch or tag there, cherry-pick, revert, reset the current branch (soft / mixed / hard), or copy its SHA or subject

### Branches
- Local and remote branches with ahead/behind counts; double-click to check out (a remote branch becomes a local tracking branch)
- Right-click a branch to merge it into the current branch, rebase onto it, create a branch from it, rename it, or delete it (locally or on the remote)

### Changes
- Stage and unstage whole files, **single hunks, or selected lines** (Ctrl/Shift+click lines in the diff)
- Discard changes per hunk, per selected line, per file, or all at once; destructive actions always ask first
- Diff viewer with old/new line numbers
- Commit with Ctrl+Enter; stash and pop stash

### Remotes
- Fetch (all remotes, prune deleted branches), pull and push; the first push of a new branch publishes it and sets its upstream

### Conflicts
- When a merge, rebase, cherry-pick or revert stops on conflicts, a banner offers **Continue** and **Abort**
- Conflicted files are marked `!` and the merge message is pre-filled

### Productivity
- **Command palette** (`Ctrl+P` / `Ctrl+Shift+P`): fuzzy search over all commands, check out any branch, open recent repositories, or type a new name to create a branch
- Refreshes automatically when the window regains focus
- Recent repositories on the start screen

## Public and private repositories

GitHr has no accounts, no paywall and no repository limits. All operations run the **installed `git` CLI**, so authentication works exactly like in your terminal:

- **HTTPS** (GitHub, Azure DevOps, GitLab, Bitbucket…): handled by [Git Credential Manager](https://github.com/git-ecosystem/git-credential-manager), which ships with Git for Windows. A login window appears the first time; the credentials are then stored in the OS credential store.
- **SSH**: uses your SSH keys and `ssh-agent` (for example the Windows OpenSSH agent service, or Pageant).
- Hooks, LFS, `includeIf` configs and signing settings from your git config are all respected.

GitHr runs git with `GIT_TERMINAL_PROMPT=0` and `GIT_EDITOR=true`, so it never hangs waiting for terminal input or an editor.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Git 2.30+ on the `PATH`

## Build, run and test

```sh
dotnet build
dotnet run --project src/GitHr.App                 # start screen
dotnet run --project src/GitHr.App -- C:\path\repo # open a repository directly
dotnet test
```

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+P` / `Ctrl+Shift+P` | Command palette |
| `Ctrl+O` | Open repository |
| `F5` | Refresh |
| `Ctrl+Enter` | Commit (in the commit message box) |
| `Ctrl/Shift+click` | Select several lines in the diff |

On macOS, `Cmd+P` also opens the command palette.

## Project structure

| Project | Purpose |
|---|---|
| `src/GitHr.Core` | UI-independent Git layer: `GitRunner` (runs the git CLI), `GitRepository` (operations), `Parsing/GitOutputParser` (porcelain parsing), `Graph/CommitGraph` (lane layout), `PatchBuilder` (partial hunk/line patches for `git apply`) |
| `src/GitHr.App` | Avalonia desktop app (MVVM with CommunityToolkit.Mvvm): `Views` (main window, dialogs), `ViewModels`, `Controls/CommitGraphCell` (draws the graph) |
| `tests/GitHr.Core.Tests` | xUnit tests: parsers, graph layout, patch building, and end-to-end tests against real temporary repositories |
| `tests/GitHr.App.Tests` | Headless UI tests (Avalonia.Headless): drive the real window with keyboard and mouse input, off-screen |

## Roadmap

- Clone dialog and live progress output for fetch/pull/push/clone
- Merge conflict resolver (ours / theirs / result)
- Interactive rebase (reorder, squash, reword, drop)
- Side-by-side diff with syntax highlighting
- Stash list and tags in the sidebar
- Search and filtering in history, file history, blame
- GitHub / Azure DevOps pull requests
- Multiple repository tabs, settings, light theme
- Packaging: installer (Windows), .dmg (macOS), AppImage/Flatpak (Linux)

## License

[MIT](LICENSE)
