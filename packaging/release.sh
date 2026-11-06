#!/usr/bin/env bash
# Runs the tests, builds the .deb and publishes it to the local APT repo (keeps the newest 3 versions).
# Repo folder: $GERGIT_APT_REPO, default /var/local/gergit-apt (create it once with setup-local-repo.sh). The folder is
# root-owned because apt trusts it without a signature, so publishing asks for sudo.
set -euo pipefail
umask 022
root=$(cd "$(dirname "$0")/.." && pwd)
repo=${GERGIT_APT_REPO:-/var/local/gergit-apt}
# Everything in it must be root's and writable by root only, or apt could be made to install planted packages.
[[ -d $repo && ! -L $repo && -z $(find "$repo" \( ! -user 0 -o -perm /022 \) -print -quit) ]] ||
  { echo "$repo is missing, or something in it is not owned by root or is writable by others; run once: sudo $root/packaging/setup-local-repo.sh" >&2; exit 1; }

dotnet test "$root/GerGit.sln"
deb=$("$root/packaging/build-deb.sh")
sudo install -m 644 -o root -g root "$deb" "$repo/"

sudo bash -s "$repo" <<'INDEX'
set -euo pipefail
umask 022
cd "$1"
ls -1t gergit_*_amd64.deb | tail -n +4 | xargs -r rm -f --
# Write the index as .new files and move them in place, Release last, so apt never sees a half-written index.
dpkg-scanpackages --multiversion . > Packages.new
gzip -9nc Packages.new > Packages.gz.new
{
  echo "Date: $(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S UTC')"
  for sum in MD5Sum:md5sum SHA256:sha256sum; do
    echo "${sum%%:*}:"
    for f in Packages Packages.gz; do
      echo " $(${sum#*:} "$f.new" | cut -d' ' -f1) $(stat -c %s "$f.new") $f"
    done
  done
} > Release.new
mv -f Packages.new Packages
mv -f Packages.gz.new Packages.gz
mv -f Release.new Release
INDEX

echo "Published $(basename "$deb") to $repo"
if dpkg-query -W -f='${Status}' gergit 2>/dev/null | command grep -q 'ok installed'; then
  echo "Next: sudo apt update && sudo apt upgrade"
else
  echo "Next: sudo apt update && sudo apt install gergit"
fi
