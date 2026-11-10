# Arch Linux: AUR package gergit-bin

`gergit-bin` repackages the portable tarball (`gergit-<version>-linux-x64.tar.gz`) from the GitHub release. The `PKGBUILD` and `.SRCINFO` live in `packaging/aur/`; the AUR has its own git repository for them.

## How the tarball is made

- `packaging/build-tarball.sh` builds `artifacts/gergit-<version>-linux-x64.tar.gz` and a `.sha256` (same `dotnet publish` as the `.deb`). Version = `<Version>` in `src/GerGit/GerGit.csproj`.
- `.github/workflows/release.yml` runs it when a GitHub release is published and attaches both files to the release. It fails if the tag (without `v`) does not match `<Version>`. For an existing release, run it by hand: Actions > Release > Run workflow, with the tag (for example `v1.0.2`). Tags older than the script still build: the workflow takes `build-tarball.sh` from its own commit.
- **Never re-run the workflow for a tag once its checksum is on the AUR.** `--clobber` replaces the tarball with a new build that is not byte-identical, so the AUR checksum no longer matches and every install fails. If it happens anyway: in `packaging/aur/`, run `updpkgsums`, bump `pkgrel`, regenerate `.SRCINFO` and push (steps 3 to 5 below).

## One-time setup

- AUR account with your SSH public key (<https://aur.archlinux.org/register>).
- Tools: `sudo pacman -S --needed base-devel pacman-contrib namcap` (`updpkgsums` is in `pacman-contrib`).
- Clone the AUR repository next to this repository, so it ends up at `../gergit-bin` (the first push creates the package). From the repository root:

  ```bash
  git clone ssh://aur@aur.archlinux.org/gergit-bin.git ../gergit-bin
  ```

## Each release

1. Publish the GitHub release and wait until the Release workflow has attached the tarball and `.sha256`.
2. In `packaging/aur/PKGBUILD`, set `pkgver` and reset `pkgrel=1` (only bump `pkgrel` for packaging fixes of the same version).
3. Fill in the checksum and regenerate `.SRCINFO`, from the repository root (build output in `packaging/aur/` is git-ignored):

   ```bash
   cd packaging/aur
   rm -f gergit-*-linux-x64.tar.gz   # makepkg reuses a cached download; force the published asset
   updpkgsums
   makepkg --printsrcinfo > .SRCINFO
   ```

4. Test: `makepkg -si`, start `gergit`, and run `namcap PKGBUILD gergit-bin-*.pkg.tar.zst`. Without Arch, in docker:

   ```bash
   docker run --rm -v "$PWD:/aur:ro" archlinux:latest bash -c '
     pacman -Syu --noconfirm --needed base-devel namcap && useradd -m b && cp -r /aur /home/b/aur && chown -R b /home/b/aur &&
     echo "b ALL=(ALL) NOPASSWD: ALL" > /etc/sudoers.d/b && su b -c "cd ~/aur && makepkg -si --noconfirm" &&
     namcap ~b/aur/PKGBUILD ~b/aur/gergit-bin-*.pkg.tar.zst && ls -l /usr/bin/gergit'
   ```

   Expected namcap noise: ELF files in `/opt`, unstripped/no full RELRO (prebuilt SkiaSharp, `!strip` on purpose), and "may not be needed" for git and the libraries .NET and Avalonia load at runtime.
5. Commit `packaging/aur/` here, then copy `PKGBUILD` and `.SRCINFO` into the AUR clone, commit and push. Still in `packaging/aur/` from steps 3 and 4:

   ```bash
   cd ../..
   cp packaging/aur/PKGBUILD packaging/aur/.SRCINFO ../gergit-bin/
   cd ../gergit-bin && git commit -am "Update to 1.0.3" && git push
   ```

   The first push needs `git add PKGBUILD .SRCINFO` instead of `-a`.

## Installing (users)

```bash
yay -S gergit-bin
```

Any AUR helper works, or by hand: `git clone https://aur.archlinux.org/gergit-bin.git && cd gergit-bin && makepkg -si`.
