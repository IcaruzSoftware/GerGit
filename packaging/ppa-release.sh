#!/usr/bin/env bash
# Builds the signed source package for ppa:icaruzsoftware/gergit and prints the dput command. It never uploads.
#
# Usage: packaging/ppa-release.sh [--unsigned] <debian-version> <git-tag>
#   <debian-version>  for example 1.0.0-1~ppa1. If it differs from the top of debian/changelog, dch adds an entry.
#   <git-tag>         the upstream sources come from this tag (git archive), never from the working tree. Its
#                     <Version> in src/GerGit/GerGit.csproj must match the upstream version.
#   debian/ comes from the working tree (tracked files only); apart from debian/changelog it must be committed.
#   --unsigned        debuild -us -uc, for local tests.
#
# Output folder: $GERGIT_PPA_OUT, default ~/.cache/gergit-ppa. It holds
#   gergit_<upstream>.orig.tar.xz        sources: the files of <git-tag>, minus debian/
#   gergit_<upstream>.orig-nuget.tar.xz  NuGet packages for the offline build (packaging/vendor-nuget.sh)
#   gergit-<upstream>/                   the unpacked source package
#   gergit_<version>_source.changes      and the other files debuild -S writes
# Launchpad rejects a second, different tarball under the same name, so existing orig tarballs of an upstream
# version are reused. Delete them only if that upstream version was never uploaded.
set -euo pipefail
umask 022
key=F0FB57429CE52205D9DD42580F9FAD5EBD310579
dist=resolute
root=$(cd "$(dirname "$0")/.." && pwd)
out=${GERGIT_PPA_OUT:-$HOME/.cache/gergit-ppa}

sign=(-k"$key")
if [[ ${1:-} == --unsigned ]]; then sign=(-us -uc); shift; fi
[[ $# -eq 2 ]] || { sed -n '4,9s/^# \{0,1\}//p' "$0" >&2; exit 2; }
version=$1 tag=$2
ref=refs/tags/$tag
git -C "$root" rev-parse -q --verify "$ref^{commit}" >/dev/null || { echo "no such tag: $tag" >&2; exit 2; }
mtime=$(git -C "$root" log -1 --format=%ct "$ref")
[[ $version == *-* && $version != *:* ]] || { echo "version needs a Debian revision and no epoch, e.g. 1.0.0-1~ppa1" >&2; exit 2; }
upstream=${version%-*}
tagged=$(git -C "$root" show "$ref:src/GerGit/GerGit.csproj" | sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p')
[[ $tagged == "$upstream" ]] || { echo "$tag has <Version>$tagged</Version> in GerGit.csproj, not $upstream" >&2; exit 2; }
# debian/ is packed from the working tree (tracked files only); apart from debian/changelog it must be committed.
dirty=$(git -C "$root" status --porcelain --untracked-files=all -- debian ':(exclude)debian/changelog')
[[ -z $dirty ]] || { printf 'debian/ has untracked or uncommitted files; commit or remove them first:\n%s\n' "$dirty" >&2; exit 2; }

if [[ $(dpkg-parsechangelog -l "$root/debian/changelog" -S Version) != "$version" ]]; then
  (cd "$root" && DEBFULLNAME='Gerrit Visser' DEBEMAIL=developericaruz@gmail.com \
     dch -v "$version" -D "$dist" --force-distribution "New upstream release $upstream.")
  echo "Added debian/changelog entry $version (commit it with the release)." >&2
fi

mkdir -p "$out"
src=$out/gergit-$upstream
orig=$out/gergit_$upstream.orig.tar.xz
nuget=$out/gergit_$upstream.orig-nuget.tar.xz
rm -rf "$src"
mkdir "$src"
# The orig tarballs go up only with the first upload of an upstream version; a later Debian revision (rebuild,
# packaging fix) refers to the ones Launchpad already has.
revision=${version##*-}
if [[ $revision == 1 || $revision == 1~ppa1 ]]; then orig_opt=-sa; else orig_opt=-sd; fi
# Sorted, root-owned, fixed mtime: the same tree always gives the same tarball.
pack() { tar -C "$1" --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$mtime" -c "$2" | xz -T0 > "$3"; }

if [[ -e $orig ]]; then
  echo "Reusing $orig" >&2
  tar -C "$src" --strip-components=1 -xf "$orig"
  check=$(mktemp -d -p "$out")
  trap 'rm -rf "$check"' EXIT
  git -C "$root" archive "$ref" | tar -C "$check" -x
  rm -rf "$check/debian"
  if ! diff -rq "$check" "$src" >&2; then
    # Uploading it would publish files that are not in the tag; for a later revision it is already on Launchpad.
    [[ $orig_opt == -sd ]] || { echo "$orig differs from $tag (see above); delete it if it was never uploaded." >&2; exit 2; }
    echo "WARNING: $orig differs from $tag (see above)." >&2
  fi
else
  git -C "$root" archive "$ref" | tar -C "$src" -x
  rm -rf "$src/debian"
  pack "$out" "gergit-$upstream" "$orig.tmp"
  mv "$orig.tmp" "$orig"
fi

if [[ -e $nuget ]]; then
  echo "Reusing $nuget" >&2
  tar -C "$src" -xf "$nuget"
else
  "$root/packaging/vendor-nuget.sh" "$src/nuget" "$src"
  pack "$src" nuget "$nuget.tmp"
  mv "$nuget.tmp" "$nuget"
fi

(cd "$root" && git ls-files -z debian | tar --null -T - -c) | tar -C "$src" -x
(cd "$src" && debuild -S "$orig_opt" "${sign[@]}")

echo
echo "Source package ready. Upload it with:"
echo "  dput ppa:icaruzsoftware/gergit $out/gergit_${version}_source.changes"
