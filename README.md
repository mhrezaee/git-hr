# GitHr

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Avalonia 12](https://img.shields.io/badge/UI-Avalonia%2012-8B44AC)](https://avaloniaui.net/)
![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20macOS%20%7C%20Linux-2EA44F)
![Tests](https://img.shields.io/badge/tests-95%20passing-2EA44F)
![Core coverage](https://img.shields.io/badge/core%20coverage-91.5%25%20lines-2EA44F)
![xUnit v4](https://img.shields.io/badge/xUnit-v4%20%C2%B7%20Microsoft.Testing.Platform-5C2D91)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

A fast, free, cross-platform Git GUI built with C#, .NET 10 and Avalonia UI.
Windows first; macOS and Linux run from the same code. No accounts, no paywall, no repository limits — public and private repositories alike.

- [Features](#features)
- [Architecture](#architecture)
- [Design decisions](#design-decisions)
- [Key flows](#key-flows)
- [Testing strategy](#testing-strategy)
- [Security and privacy](#security-and-privacy)
- [Getting started](#getting-started)
- [Project structure](#project-structure)
- [Roadmap](#roadmap)

---

## Features

### Repositories
- **Clone** public or private repositories (HTTPS or SSH), with the folder name derived from the URL and a copied git URL pre-filled from the clipboard
- Open any local repository, or pass its path on the command line; recent repositories on the start screen
- **Live progress** for clone, fetch, pull and push ("Receiving objects: 45%"), with a **Cancel** button

### History
- Commit graph with colored lanes, merge nodes, and branch and tag badges
- Commit details: full message, author, date, SHA and changed files, with a diff per file
- Right-click a commit to check it out (detached), create a branch or tag there, cherry-pick, revert, reset the current branch (soft / mixed / hard), or copy its SHA or subject

### Branches
- Local and remote branches with ahead/behind counts; double-click to check out (a remote branch becomes a local tracking branch)
- Right-click a branch to merge it into the current branch, rebase onto it, create a branch from it, rename it, or delete it (locally or on the remote)

### Changes
- Stage and unstage whole files, **single hunks, or selected lines** (Ctrl/Shift+click lines in the diff)
- Discard changes per hunk, per selected line, per file, or all at once
- Commit with Ctrl+Enter, or **amend the last commit** (message pre-filled; warns if it was already pushed)
- Diff viewer with old/new line numbers

### Stashes and tags
- **Stash list** in the sidebar with message, branch and date; stash with a message (untracked files included)
- Click a stash to see its files — including stashed untracked files — and their diffs; **apply** (keep), **pop** (apply and remove) or **drop** any stash, not just the latest
- **Tags** in the sidebar (newest first): click one to jump to its commit; check it out, create a branch from it, push it, delete it locally or on the remote
- Create lightweight or **annotated** tags (with a message) from any commit or at HEAD; push all tags at once

### Remotes
- Fetch (all remotes, prune deleted branches), pull and push; the first push of a new branch publishes it and sets its upstream
- **Force push with lease** after amending or rebasing — refuses to overwrite commits someone else pushed

### Conflicts
- When a merge, rebase, cherry-pick or revert stops on conflicts, a banner offers **Continue** and **Abort**
- Conflicted files are marked `!` and the merge message is pre-filled

### Productivity
- **Command palette** (`Ctrl+P`): fuzzy search over all commands, check out any branch, open recent repositories, or type a new name to create a branch
- Refreshes automatically when the window regains focus

---

## Architecture

GitHr is split into a UI-independent **Core** and an Avalonia **App**. Core knows nothing about the UI and can be reused by a CLI, tests or another front end. Every Git operation goes through the real `git` executable.

```mermaid
flowchart TB
    subgraph App["GitHr.App — Avalonia desktop app"]
        direction TB
        Views["Views<br/>MainWindow · CloneDialog · Dialogs"]
        VMs["ViewModels (MVVM)<br/>MainViewModel · CommandPalette · CloneDialog · item VMs"]
        Controls["Controls<br/>CommitGraphCell (custom rendering)"]
        Views -- "compiled bindings" --> VMs
        Views --> Controls
    end

    subgraph Core["GitHr.Core — no UI dependencies"]
        direction TB
        Repo["GitRepository<br/>high-level operations"]
        Runner["GitRunner<br/>process, stdin/stdout, live progress, cancel"]
        Parser["GitOutputParser<br/>porcelain v2, log, diff, refs"]
        Graph["CommitGraph<br/>lane layout"]
        Patch["PatchBuilder<br/>partial hunk / line patches"]
        Url["GitUrl"]
        Repo --> Runner
        Repo --> Parser
    end

    subgraph System["Installed on the machine"]
        Git["git CLI"]
        GCM["Git Credential Manager<br/>HTTPS sign-in"]
        SSH["ssh / ssh-agent"]
    end

    Remotes[("GitHub · Azure DevOps · GitLab · Bitbucket · any git server")]

    VMs --> Repo
    VMs --> Graph
    VMs --> Patch
    VMs --> Url
    Runner -- "child process" --> Git
    Git --> GCM
    Git --> SSH
    Git <--> Remotes
```

### Layers and responsibilities

| Layer | Responsibility | Must not |
|---|---|---|
| **Views** (`*.axaml`, code-behind) | Layout, styling, input routing, native dialogs (folder picker, clipboard) | Contain Git logic or state |
| **ViewModels** | UI state, commands, confirmation rules, busy/progress/cancel handling, selection | Start processes or parse Git output |
| **Core: `GitRepository`** | One method per Git operation, typed results, error translation (`GitException`) | Know about windows, threads or dialogs |
| **Core: `GitRunner`** | Run `git` safely: argument lists (no shell quoting), UTF-8, no terminal prompts, streamed progress, kill on cancel | Interpret results |
| **Core: pure algorithms** | `GitOutputParser`, `CommitGraph`, `PatchBuilder`, `GitUrl` — deterministic functions over text | Perform I/O |

### Threading model

- The UI thread only updates view models. Git runs in child processes awaited asynchronously, so the window never freezes.
- Commit graph layout for thousands of commits runs on the thread pool (`Task.Run`), then results are applied on the UI thread.
- Live progress is read from git's stderr as it arrives and marshalled to the UI thread via `Progress<T>` (which captures the UI synchronization context).
- Every long operation has a `CancellationToken`; cancelling kills the whole git process tree and, for clones, removes the half-written folder.

---

## Design decisions

| Decision | Why | Trade-off |
|---|---|---|
| **Drive the `git` CLI instead of a library (libgit2)** | 100% compatible with the user's git: credential helpers, SSH agents, hooks, LFS, `includeIf`, signing. Private repositories need no extra auth code. | Process start per command (milliseconds); output must be parsed |
| **Machine-readable output only** (`status --porcelain=v2 -z`, custom `log --format` with control-character separators, `for-each-ref` formats) | Stable across git versions and user locales; file names with spaces or Unicode are safe | Parsers are hand-written — and therefore unit-tested |
| **`GIT_TERMINAL_PROMPT=0`, `GIT_EDITOR=true`, stdin closed** | A GUI has no terminal: git must never block waiting for input or an editor. GUI credential helpers still show their sign-in windows. | Features that need an editor (interactive rebase) get their own UI |
| **Partial staging via generated patches + `git apply --cached`** | Exactly the same result as command-line git; works for index and working tree, forward and reverse | Patch building needs care when removed/added lines interleave — covered by dedicated tests |
| **Avalonia UI** | One codebase for Windows, macOS and Linux; Skia rendering looks identical everywhere; a real headless mode for UI tests | Smaller ecosystem than web UI stacks |
| **MVVM with CommunityToolkit.Mvvm source generators** | Testable view models, no reflection-heavy frameworks, compile-time checked bindings | Some boilerplate in item view models (`Owner` references for context menus) |
| **Destructive actions always confirm; Enter never confirms them** | Discard, reset hard, force delete and force push cannot be undone | One extra click |
| **`--force-with-lease` instead of `--force`** | Never silently overwrites commits pushed by someone else | Rejects when the remote-tracking ref is stale (fetch first) |

---

## Key flows

### Staging selected lines

```mermaid
sequenceDiagram
    actor User
    participant View as Diff view
    participant VM as MainViewModel
    participant PB as PatchBuilder
    participant Repo as GitRepository
    participant Git as git

    User->>View: Ctrl/Shift+click lines, "Stage lines"
    View->>VM: StageLinesCommand (selected line indexes)
    VM->>PB: Build(diff, selection, reverse: false)
    Note over PB: unselected "-" lines become context,<br/>unselected "+" lines are dropped,<br/>order kept so replacements land in place
    PB-->>VM: patch text
    VM->>Repo: ApplyPatchAsync(patch, cached: true)
    Repo->>Git: git apply --cached --recount -  (patch on stdin)
    Git-->>Repo: exit 0
    VM->>Repo: reload status, branches, graph, operation (in parallel)
    Repo-->>VM: new state
    VM-->>View: diff re-rendered; file follows to "Staged" if fully staged
```

### Cloning a private repository

```mermaid
sequenceDiagram
    actor User
    participant Dialog as CloneDialog
    participant VM as MainViewModel
    participant Runner as GitRunner
    participant Git as git clone --progress
    participant GCM as Credential Manager
    participant Remote as Remote server

    User->>Dialog: paste URL (pre-filled from clipboard), pick folder
    Dialog-->>VM: URL + target path (validated: folder missing or empty)
    VM->>Runner: RunAsync(clone, progress, cancellation)
    Runner->>Git: start process (no terminal prompts)
    Git->>Remote: request
    Remote-->>Git: 401 authentication required
    Git->>GCM: get credentials
    GCM->>User: sign-in window (browser / device login)
    GCM-->>Git: token (stored in the OS credential store)
    loop while downloading
        Git-->>Runner: stderr "Receiving objects: 45% ..." (\r-updated)
        Runner-->>VM: IProgress.Report(line)
        VM-->>User: progress bar + text, Cancel button
    end
    Git-->>Runner: exit 0
    VM->>VM: open the cloned repository
```

### Merge / rebase / cherry-pick / revert with conflicts

```mermaid
stateDiagram-v2
    [*] --> Clean
    Clean --> InProgress: merge / rebase / cherry-pick / revert<br/>stops on conflicts
    InProgress --> InProgress: resolve files, stage them
    InProgress --> Clean: Continue (all conflicts staged)
    InProgress --> Clean: Abort (back to the previous state)
    Clean --> Clean: operation without conflicts
    note right of InProgress
        Detected from git's own state files
        (MERGE_HEAD, rebase-merge, CHERRY_PICK_HEAD, REVERT_HEAD);
        banner shown, merge message pre-filled
    end note
```

### Stash lifecycle

```mermaid
stateDiagram-v2
    direction LR
    Working: Uncommitted changes<br/>(tracked + untracked)
    Stashed: Stash list<br/>stash@{0}, stash@{1}, ...
    Working --> Stashed: Stash… (message)
    Stashed --> Working: Pop — apply, then remove from list
    Stashed --> Stashed: Apply — apply, keep in list
    Stashed --> [*]: Drop (asks first)
    note right of Stashed
        A stash is a commit: parent 1 = HEAD at stash time,
        parent 3 = untracked files. GitHr diffs against
        those parents to show exactly what is inside.
    end note
```

### Commit graph layout

`CommitGraph.Layout` assigns each commit (in `git log --date-order`) to a vertical lane in a single pass. Each lane remembers which commit it is heading to. A commit takes the lane that points to it (or the first free one); its first parent continues straight down the same lane, other parents open or join lanes; lanes that meet at a commit merge into it. The renderer (`CommitGraphCell`) then draws straight lines and S-curves per row — O(commits × lanes), fast enough for thousands of commits.

---

## Testing strategy

**95 automated tests**, run with `dotnet test`. Every test uses real git against throwaway repositories — nothing is mocked at the Git boundary. **GitHr.Core has 91.5% line and 83.3% branch coverage.**

The suites use **xUnit v4** on **Microsoft.Testing.Platform** (the .NET 10 test runner, enabled for the repo in `global.json`). Test projects are self-hosting executables, so they also run directly (`tests/GitHr.Core.Tests/bin/Debug/net10.0/GitHr.Core.Tests.exe`).

```mermaid
flowchart TB
    UI["UI tests · 23<br/>real MainWindow, headless (Avalonia.Headless + Skia)<br/>keyboard & mouse input, context menus, dialogs"]
    INT["Integration tests · 36<br/>GitRepository against real temporary repositories<br/>(clone, push/pull to local bare remotes, conflicts, partial staging, stashes, tags)"]
    UNIT["Unit tests · 36<br/>parsers (incl. stash list, tags) · commit graph · patch builder · URL parsing<br/>palette scoring · progress parsing · clone dialog rules"]
    UI --- INT --- UNIT
    style UI fill:#8B44AC,color:#fff
    style INT fill:#16A9E0,color:#fff
    style UNIT fill:#2EA44F,color:#fff
```

| Suite | Project | What it proves |
|---|---|---|
| Parser, graph, patch and URL unit tests | `tests/GitHr.Core.Tests` | Porcelain/log/diff parsing, lane assignment, patch generation incl. CRLF and interleaved changes, URL → folder name |
| Git integration tests | `tests/GitHr.Core.Tests` | Every repository operation end to end: staging hunks/lines, discard, branches, merge/rebase conflicts with continue/abort, cherry-pick, revert, reset, tags, clone with progress and cancel, fetch/pull/push, force-with-lease |
| Headless UI tests | `tests/GitHr.App.Tests` | Clicking the real buttons and context menus, command palette keyboard flow, clone dialog behavior, amend, conflict banner — plus rendered frames saved for visual inspection |
| View-model unit tests | `tests/GitHr.App.Tests` | Palette fuzzy scoring, progress percentage parsing, clipboard URL suggestion rules |
| Stash & tag tests | both | Parsing `stash list` / tags (annotated tags peeled to their commit), stashed untracked files and their diffs, apply/pop/drop by index, pushing and deleting tags on a bare remote, sidebar menus and the stash tab |

Test isolation:
- Each test creates its own repository under the temp folder and deletes it afterwards.
- UI tests use a temporary settings file, so they never touch the user's recent repositories.
- Remote scenarios use local bare repositories over `file://`, which exercises git's real transport (including progress output) without network access.
- UI tests run off-screen: input goes to the window in memory, never to the desktop.
- Every git call in the tests receives the test's cancellation token, so a cancelled or timed-out run also stops the git processes it started.

### How the UI tests run

UI tests are plain xUnit `[Fact]`s whose body runs on Avalonia's UI thread through a `HeadlessUnitTestSession` (see `tests/GitHr.App.Tests/TestAppBuilder.cs`):

```csharp
[Fact]
public Task StageHunkButton_StagesOnlyThatHunk() => RunUi(async () =>
{
    var (window, vm) = await OpenWithTwoHunksAsync();
    Click(window, FindButton(window, "Stage hunk"));
    await WaitUntilAsync(() => vm.StagedFiles.Count == 1);
    // ...
});
```

```mermaid
flowchart LR
    X["xUnit v4<br/>[Fact]"] --> R["RunUi(...)"]
    R --> S["HeadlessUnitTestSession<br/>(PerTest isolation: fresh App per test)"]
    S --> D["Avalonia UI thread<br/>headless platform + Skia"]
    D --> W["real MainWindow<br/>simulated keyboard & mouse"]
```

- **Why not `Avalonia.Headless.XUnit`'s `[AvaloniaFact]`?** It is compiled against xUnit v3 and breaks on v4 (`MissingMethodException` during discovery). Driving the session directly keeps the suite on the latest xUnit and independent of that adapter's release schedule.
- **Each test gets a fresh `App`** (`AvaloniaTestIsolationLevel.PerTest`), like a real application start.
- **The UI suite runs sequentially** (`[assembly: Parallelization(Mode = ParallelMode.None)]`): all UI tests share one dispatcher thread, and parallel classes would interleave their steps on it. The Core suite runs in parallel.

---

## Security and privacy

- **No telemetry, no accounts, no network calls of its own.** GitHr only talks to the remotes you configure, through git.
- **Credentials never pass through GitHr.** HTTPS sign-in is handled by Git Credential Manager (credentials in the OS credential store); SSH uses your keys and agent.
- **Arguments are passed as an argument list**, never through a shell, so branch names or paths cannot inject commands.
- **Destructive operations ask first** (discard, reset hard, force delete, force push, abort), and Enter never confirms them.

---

## Getting started

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/) and Git 2.30+ on the `PATH`.

```sh
dotnet build
dotnet run --project src/GitHr.App                 # start screen
dotnet run --project src/GitHr.App -- C:\path\repo # open a repository directly
dotnet test                                        # all 95 tests
dotnet test --project tests/GitHr.App.Tests        # one suite
dotnet test --project tests/GitHr.Core.Tests --coverlet --coverlet-output-format cobertura   # coverage report in TestResults/
```

### Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+P` / `Ctrl+Shift+P` | Command palette (`Cmd+P` on macOS) |
| `Ctrl+O` | Open repository |
| `Ctrl+Shift+O` | Clone repository |
| `F5` | Refresh |
| `Ctrl+Enter` | Commit (in the commit message box) |
| `Ctrl/Shift+click` | Select several lines in the diff |

---

## Project structure

```
GitHr/
├── src/
│   ├── GitHr.Core/                 # UI-independent Git layer
│   │   ├── GitRunner.cs            # runs git: args, env, streamed progress, cancellation
│   │   ├── GitRepository.cs        # all repository operations
│   │   ├── Models.cs               # Commit, Branch, FileChange, DiffLine, RepositoryStatus, ...
│   │   ├── PatchBuilder.cs         # partial hunk/line patches for git apply
│   │   ├── GitUrl.cs               # repository name from a clone URL
│   │   ├── Graph/CommitGraph.cs    # commit graph lane layout
│   │   └── Parsing/GitOutputParser.cs
│   └── GitHr.App/                  # Avalonia desktop app
│       ├── Views/                  # MainWindow, CloneDialog, Dialogs
│       ├── ViewModels/             # MainViewModel (+ Actions, Remote, StashesAndTags partials), palette, clone dialog, items
│       ├── Controls/CommitGraphCell.cs
│       ├── AppSettings.cs          # recent repositories, clone folder (per-user app data)
│       └── Converters.cs
├── tests/
│   ├── GitHr.Core.Tests/           # unit + integration tests against real repositories (xUnit v4)
│   └── GitHr.App.Tests/            # headless UI tests (xUnit v4 + HeadlessUnitTestSession)
└── global.json                     # opts `dotnet test` into Microsoft.Testing.Platform
```

---

## Roadmap

- Merge conflict resolver (ours / theirs / result)
- Interactive rebase (reorder, squash, reword, drop)
- Side-by-side diff with syntax highlighting
- Search and filtering in history, file history, blame
- GitHub / Azure DevOps pull requests
- Multiple repository tabs, settings, light theme
- CI (GitHub Actions on Windows, macOS, Linux) and packaging: installer (Windows), .dmg (macOS), AppImage/Flatpak (Linux)

## License

[MIT](LICENSE)
