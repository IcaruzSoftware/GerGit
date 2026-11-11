# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.3] - 2026-11-11

### Fixed

- Context menus were sometimes cut off on both sides (with the window narrower than the menu). Context menus are no longer narrowed to fit a screen, and at 100 % zoom a menu that still comes out too narrow keeps its left edge, so the beginning of every entry stays readable.

## [1.0.2] - 2026-11-06

### Added

- Help menu with About GerGit (version, license, links, "Copy Version Info" for bug reports), GitHub Page and Report an Issue. About is also in Quick Launch.
- Preference "Load avatars (Gravatar, GitHub, GitLab)" to turn off all avatar downloads.
- Preference "Update submodules automatically after checkout, pull and merge".
- README section "Privacy and network access".
- Warning in the login dialog when a GitLab server URL uses plain http.
- "Recurse submodules" checkbox in the Clone dialog (off by default).
- "Initialize Submodules..." in the menu of the sidebar's Submodules header: lists the URLs the submodules will be cloned from and asks before it runs `git submodule update --init --recursive`.

### Changed

- Submodules are no longer initialized automatically. To fetch the submodules of a repository, tick "Recurse submodules" when cloning, use "Initialize Submodules..." on the sidebar's Submodules header, or run `git submodule update --init`.
- The Checks window shows "name ✓/✗" progress lines under their hook and the pytest summary line next to a failed hook.
- Opening a file from history or the file list uses the default application only when `xdg-mime` reports a passive type: plain text, C/C++/C#/Java/Kotlin/Go/Rust source, CSS/SCSS, SQL, logs, reStructuredText, diffs, JSON, YAML, TOML, PNG, JPEG, GIF, WebP and PDF (files with such an extension, no extension, or dotfiles like `.gitignore`). Anything else, including scripts, HTML, XML, SVG and Markdown, shows its folder with a short note.
- "Mark repository as safe" now asks first and names the folder's owner.
- A long branch name in the toolbar status box is trimmed, with the full name in a tooltip.
- Temporary copies opened from file history are kept in a private folder and deleted when GerGit exits.

### Removed

- The Checks window no longer shows a retry command with a Copy button or per-module summary rows; these only applied to one specific hook's output format.

### Security

- Submodules are never initialized or cloned automatically. Only submodules that are already initialized and checked out are updated, with strict path checks; pulls run with `--no-recurse-submodules`.
- Remote branch and tag names can no longer be read as git options; branch names starting with `-` are refused for checkout and worktree creation.
- Symlinks in the work tree are resolved before any file is opened.
- Image previews and avatars refuse images with huge pixel dimensions; downloads have size limits and a body timeout, and avatar URLs must be https (plain http only on a configured GitLab server's own address).
- Passwords and access tokens in remote URLs are masked in job output (for http(s) URLs the whole user name part when there is no password).
- The local APT repository created by `packaging/setup-local-repo.sh` is now owned by root. If you set it up with an earlier version, run `sudo packaging/setup-local-repo.sh` again.

## [1.0.1] - 2026-11-06

### Fixed

- Opening a folder whose `.git` file points to a missing repository (for example a deleted worktree) now shows git's own message in every git version, instead of offering to initialize a new repository there.

## [1.0.0] - 2026-11-06

### Added

- History and refs: commit graph with search; sidebar with branches, remotes, tags, stashes, worktrees and submodules; branch and tag actions; repository tabs and workspaces.
- Local changes: stage and unstage files, hunks and single lines; commit, amend, Commit and Push.
- Live hook and test progress for commit, merge and push.
- Diff and history: inline and side-by-side diff with find, image diff, blame, file history, external diff and merge tools.
- Interactive rebase with drag-and-drop and a three-pane conflict resolver.
- Remotes and hosting: fetch, pull and push dialogs, automatic background fetch, GitHub and GitLab accounts, clone from a project list, pull and merge requests, notifications.
- Desktop: Quick Launch (Ctrl+P), working-tree watcher, repository settings window with issue links, light and dark theme following the OS, zoom, single instance.
- Debian packaging scripts.
