# Ubuntu PPA: ppa:icaruzsoftware/gergit

GerGit is published as a source package to Launchpad, which builds the `.deb` for Ubuntu 26.04 (resolute, amd64).

## How the build works

- Launchpad builds from a signed source package, without network access, with build dependencies from the Ubuntu archive only (`debhelper`, `dotnet-sdk-10.0`).
- The source package has two upstream tarballs:
  - `gergit_<version>.orig.tar.xz`: the sources, exactly the files of the release tag (`git archive`), without `debian/`.
  - `gergit_<version>.orig-nuget.tar.xz`: every NuGet package the build restores, unpacked to `nuget/` (made by `packaging/vendor-nuget.sh`). The `.nupkg` files are never committed.
- `debian/rules` runs `dotnet publish` for the SDK's own runtime identifier (`ubuntu.26.04-x64`). The .NET runtime and apphost for that RID ship with Ubuntu's `dotnet-sdk-10.0`, so they are not vendored, and a newer SDK in the archive does not break the build. `debian/nuget.config` clears all package sources and only lists `nuget/` and the SDK's `/usr/lib/dotnet/library-packs`.
- The package installs the self-contained app to `/usr/lib/gergit/`, `/usr/bin/gergit` as a symlink, the desktop file, icons and AppStream metadata. It has no maintainer scripts.

## One-time setup (done)

- PPA `ppa:icaruzsoftware/gergit` on Launchpad.
- GPG key `F0FB57429CE52205D9DD42580F9FAD5EBD310579` (Gerrit Visser <developericaruz@gmail.com>) uploaded to keyserver.ubuntu.com and added to the Launchpad account.
- Tools: `sudo apt install devscripts debhelper dput lintian` plus the .NET 10 SDK (`dotnet-sdk-10.0`).

## Each release

1. Commit the release, tag it (for example `v1.0.0`), and keep the local `dotnet-sdk-10.0` up to date.
2. Build the signed source package:

   ```bash
   packaging/ppa-release.sh 1.0.0-1~ppa1 v1.0.0
   ```

   - The first argument is the Debian version `<upstream>-<revision>~ppa<n>`. If it is not the top entry of `debian/changelog`, the script adds one with `dch` ("New upstream release ..."). For your own text, run it yourself first and edit the entry:
     `DEBFULLNAME='Gerrit Visser' DEBEMAIL=developericaruz@gmail.com dch -v 1.0.1-1~ppa1 -D resolute`
   - The second argument (required) is the git tag for the sources. The script packs them with `git archive <tag>`, so uncommitted or untracked files never end up in the public tarball. The tag's `<Version>` in `src/GerGit/GerGit.csproj` must match the upstream version.
   - `debian/` comes from the working tree, tracked files only. The script refuses to build while `debian/` has untracked files or uncommitted changes other than `debian/changelog`.
   - When it reuses an existing orig tarball, the script compares it with the tag and prints a warning if they differ.
   - Output goes to `~/.cache/gergit-ppa/` (or `$GERGIT_PPA_OUT`). `debuild -S -k<key>` asks gpg for the key passphrase; run it in a real terminal. The first revision of an upstream version (`-1` or `-1~ppa1`) uploads the orig tarballs (`-sa`), later revisions do not (`-sd`).
   - Commit the `debian/changelog` change.
3. Upload. The script prints the exact command, for example:

   ```bash
   dput ppa:icaruzsoftware/gergit ~/.cache/gergit-ppa/gergit_1.0.0-1~ppa1_source.changes
   ```

4. Watch the build: Launchpad mails the upload result (accepted or rejected). Build logs are at <https://launchpad.net/~icaruzsoftware/+archive/ubuntu/gergit/+packages>. A successful build is published to the PPA after a few minutes.

### Rebuilding the same upstream version

Launchpad rejects a second, different file under an existing name. To fix only packaging, bump the revision (`1.0.0-1~ppa2`): the script reuses the orig tarballs already in the output folder. Delete them only if that upstream version was never uploaded (for example after a failed local build).

### Testing before the upload

Build the source package unsigned and the binary package offline from it, as Launchpad does. The sources come from a tag; to test commits that are not released yet, use a local tag (for example `git tag v1.0.0-test`) and delete it afterwards:

```bash
GERGIT_PPA_OUT=/tmp/gergit-ppa packaging/ppa-release.sh --unsigned 1.0.0-1~ppa1 v1.0.0
cd /tmp/gergit-ppa && dpkg-source -x gergit_1.0.0-1~ppa1.dsc build
cd build && env HOME=/nonexistent unshare -rn dpkg-buildpackage -b -us -uc
dpkg -x ../gergit_1.0.0-1~ppa1_amd64.deb /tmp/gergit-root && ls -l /tmp/gergit-root/usr/bin/gergit
```

For a clean chroot like Launchpad's: `sudo apt install sbuild mmdebstrap uidmap`, then
`sbuild -d resolute --chroot-mode=unshare gergit_1.0.0-1~ppa1.dsc` (the unshare mode builds without network).

## Installing (users)

```bash
sudo add-apt-repository ppa:icaruzsoftware/gergit
sudo apt install gergit
```

Updates come with `sudo apt update && sudo apt upgrade`. Remove with `sudo apt remove gergit`.
