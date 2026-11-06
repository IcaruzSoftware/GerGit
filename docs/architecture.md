# Architecture

A short tour for contributors.

## Projects

| Project | Role |
| --- | --- |
| `src/GerGit.Core` | Git engine on top of the git CLI (`GitRunner`, `GitRepository.*`), models, diff parsing and building, commit graph, rebase todo, conflict handling, jobs, settings folder, single-instance support. No UI dependencies. |
| `src/GerGit.Hosting` | GitHub and GitLab clients, accounts, remote URL parsing, avatars, token storage (libsecret via P/Invoke). |
| `src/GerGit` | The Avalonia app: `Views/`, `Controls/` (custom-drawn graph, diff, blame, merge views), `ViewModels/` (MVVM with CommunityToolkit.Mvvm), `Themes/`, `Assets/`. |
| `tests/GerGit.Tests` | xUnit v3 tests: `Core/` and `Hosting/` against real temporary git repositories, `Ui/` with Avalonia.Headless. |

Dependencies point one way: `GerGit` -> `GerGit.Hosting`, `GerGit.Core`; the test project references all three.

## Key mechanics

- **Git access.** All git work goes through `GitRunner`, which starts the system `git`. External programs (git, setsid, xdg-open, notify-send, editors, terminals) are started by absolute path from `ProgramPath`, never through a shell lookup at call time.
- **Jobs and Activity.** A `Job` is one unit of background work: name, progress, message, output, cancellation token and outcome (Running, Succeeded, Failed, Canceled). Workers write to it from any thread; the UI polls it on a timer for the status box and the Activity Manager. Hidden jobs (refresh, probes) are not kept in the history.
- **Pty for hook progress.** `GitRunner` can run git under `setsid` with a pseudo-terminal, so that hooks and tests writing to `/dev/tty` show live output in the job (`Job.TtyText`). stdout and stderr stay pipes. Without `setsid` the command runs without a pty.
- **Working-tree watcher.** `WorkTreeWatcher` wraps `FileSystemWatcher` (inotify) on the work tree, ignores `.git` internals and filters ignored paths per batch; the repository tab refreshes from it.
- **Settings.** Stored under `~/.config/gergit` (`$XDG_CONFIG_HOME/gergit`); `ConfigFolder` resolves it, and `GERGIT_CONFIG_DIR` overrides it (used by tests).
- **Theming.** Colours live in `Themes/Colors.axaml` as `ThemeDictionaries` with Light and Dark variants; the app follows the OS colour scheme.

## Adding a feature

1. Git behaviour: add a method to `GerGit.Core` (usually a `GitRepository.*.cs` partial) and test it in `tests/GerGit.Tests/Core` using `TempRepo`.
2. Hosting: extend the client in `GerGit.Hosting`, test with the fake servers in `tests/GerGit.Tests/Hosting`.
3. UI: put logic in a view model, keep views thin, run long work as a `Job`. Test view models and controls in `tests/GerGit.Tests/Ui` (headless).
4. Use theme resources instead of fixed colours so both themes work.
5. Run `dotnet build GerGit.sln -warnaserror` and `dotnet test GerGit.sln`.

Tests isolate themselves: `TestSettings.cs` points `GERGIT_CONFIG_DIR` at a temp folder and stubs the OS theme, and `TempRepo` ignores the global git config.
