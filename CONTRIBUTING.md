# Contributing to GerGit

Thanks for helping. Bug reports, feature requests and pull requests are all welcome.

## Build and test

You need the .NET 10 SDK and git 2.38 or newer.

```bash
dotnet build GerGit.sln -warnaserror
dotnet test GerGit.sln
dotnet run --project src/GerGit -- /path/to/repo
```

See [docs/architecture.md](docs/architecture.md) for how the code is organised.

## Code style

- Match the style of the surrounding code.
- The build must have 0 warnings (CI uses `-warnaserror`).
- New behaviour comes with tests; bug fixes come with a test that fails without the fix.
- Keep changes small and focused. Avoid unrelated refactoring in the same PR.

## Issues

Use the issue templates. For bugs, include the GerGit version, distro, desktop (GNOME/KDE, X11/Wayland), git version and steps to reproduce. Check [ROADMAP.md](ROADMAP.md) and existing issues first. Report security problems privately, see [SECURITY.md](SECURITY.md).

## Pull requests

1. Copy the repository to your GitHub account and create a branch.
2. Make your change, with tests.
3. Make sure build and tests pass locally.
4. Open a PR and fill in the template. For larger features, open an issue first to agree on the approach.

## License

GerGit is licensed under GPL-3.0-or-later. By contributing you agree that your contribution is licensed under the same terms.

Please follow the [Code of Conduct](CODE_OF_CONDUCT.md).
