#!/usr/bin/env bash
# One-time setup, run as: sudo packaging/setup-local-repo.sh
# Creates the local APT repo folder (owned by root; packaging/release.sh fills it with sudo) and the APT source that
# points at it. Safe to run again; an existing folder that is not entirely root's is replaced by an empty one.
# Users who only want to install GerGit should use the PPA instead (see packaging/PPA.md).
set -euo pipefail
[[ $EUID -eq 0 && -n ${SUDO_USER:-} && $SUDO_USER != root ]] || { echo "run as: sudo $0" >&2; exit 1; }
repo=/var/local/gergit-apt

# "Trusted: yes" below is only safe while nobody but root can swap the repo folder, i.e. /var/local is
# root-owned and not group- or world-writable (Ubuntu: 755 root:root; plain Debian: 2775 root:staff).
if [[ $(stat -c %u /var/local) != 0 || -n $(find /var/local -maxdepth 0 -perm /022) ]]; then
  echo "/var/local is not root-owned or is group/world-writable ($(stat -c '%a %U:%G' /var/local)); refusing." >&2
  echo "Fix it first, e.g.: sudo chmod g-w,o-w /var/local" >&2
  exit 1
fi

# Root-owned and not writable for anyone else: whoever can change the folder or its files could make apt install
# anything as root, because the source below is trusted without a signature.
# An existing folder with anything in it (itself included) that is not root's or that others can write may hold
# planted files: it is moved aside in one step (safe, /var/local is root's) and deleted, and an empty folder is created.
# release.sh fills it again.
if [[ -e $repo || -L $repo ]] && [[ ! -d $repo || -L $repo || -n $(find "$repo" \( ! -user 0 -o -perm /022 \) -print -quit) ]]; then
  old=$repo.old.$$
  echo "Replacing $repo (not entirely owned by root)." >&2
  mv "$repo" "$old"
  rm -rf "$old" || echo "Could not delete all of $old; remove it by hand." >&2
fi
install -d -m 755 -o root -g root "$repo"

cat > /etc/apt/sources.list.d/gergit.sources <<SOURCES
# Local GerGit repository, filled by packaging/release.sh (with sudo).
# "Trusted: yes" skips the signature check. That is acceptable here: the repo is a local
# folder that only root can write to, so changing it already needs root.
# This assumes /var/local is root-owned and not group- or world-writable (checked by setup-local-repo.sh).
Types: deb
URIs: file:$repo
Suites: ./
Trusted: yes
SOURCES
chmod 644 /etc/apt/sources.list.d/gergit.sources
echo "Set up $repo and /etc/apt/sources.list.d/gergit.sources. Next: packaging/release.sh"
