## Download

| System | File |
|---|---|
| **Windows** (x64) | `GitHr-{{VERSION}}-win-x64-setup.exe` (installer) or `GitHr-{{VERSION}}-win-x64.zip` (portable) |
| **Windows** (ARM64) | `GitHr-{{VERSION}}-win-arm64-setup.exe` or `GitHr-{{VERSION}}-win-arm64.zip` |
| **macOS** (Apple Silicon) | `GitHr-{{VERSION}}-osx-arm64.dmg` |
| **macOS** (Intel) | `GitHr-{{VERSION}}-osx-x64.dmg` |
| **Linux** (x64) | `GitHr-{{VERSION}}-linux-x64.AppImage` or `GitHr-{{VERSION}}-linux-x64.tar.gz` |
| **Linux** (ARM64) | `GitHr-{{VERSION}}-linux-arm64.tar.gz` |

GitHr only needs **git** to be installed (2.30 or newer); .NET is included. `SHA256SUMS.txt` lists the checksum of every file.

## First start

The builds are not code-signed yet, so your system asks once:

- **Windows**: SmartScreen may say "Windows protected your PC" → *More info* → *Run anyway*. The installer needs no administrator rights.
- **macOS**: open the `.dmg`, drag GitHr to *Applications*, then right-click GitHr → *Open* → *Open* (only the first time). Or run `xattr -dr com.apple.quarantine /Applications/GitHr.app`.
- **Linux**: `chmod +x GitHr-{{VERSION}}-linux-x64.AppImage`, then run it. From the tarball, run `./GitHr`; `githr.desktop` and `githr.png` are included for a menu entry.
