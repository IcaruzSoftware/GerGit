#!/usr/bin/env bash
# Builds artifacts/gergit-<version>-linux-x64.tar.gz (+ .sha256) and prints its path on stdout (all other output goes to stderr).
# Version = <Version> from GerGit.csproj. The archive holds one folder with the self-contained app, desktop file, icons and licences.
set -euo pipefail
umask 022
root=$(cd "$(dirname "$0")/.." && pwd)
out=$root/artifacts

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/GerGit/GerGit.csproj")
[[ -n $version ]] || { echo "no <Version> in src/GerGit/GerGit.csproj" >&2; exit 1; }
name=gergit-$version-linux-x64
stage=$out/tarball-stage
dir=$stage/$name
tarball=$out/$name.tar.gz

rm -rf "$stage" "$tarball" "$tarball.sha256"
# Native libraries next to the executable instead of self-extracting to ~/.net/GerGit/<id> (as in build-deb.sh).
dotnet publish "$root/src/GerGit" -c Release -p:IncludeNativeLibrariesForSelfExtract=false -o "$dir" >&2

install -m644 "$root/packaging/gergit.desktop" "$root/packaging/gergit.svg" "$root/src/GerGit/Assets/gergit.png" \
  "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$root/packaging/OFL-1.1.txt" "$dir/"

chmod -R u=rwX,go=rX "$dir"
tar -C "$stage" --owner=0 --group=0 --numeric-owner --sort=name -czf "$tarball" "$name"
(cd "$out" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
rm -rf "$stage"
echo "$tarball"
