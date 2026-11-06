#!/usr/bin/env bash
# Builds artifacts/gergit_<version>_amd64.deb and prints its path on stdout (all other output goes to stderr).
# Version = <Version> from GerGit.csproj + UTC build timestamp, so every build sorts newer than the last.
set -euo pipefail
umask 022
root=$(cd "$(dirname "$0")/.." && pwd)
out=$root/artifacts
pkg=$out/stage
# The public maintainer identity, as in debian/control; never the local git identity of whoever builds.
name='Gerrit Visser' email=developericaruz@gmail.com

base=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/src/GerGit/GerGit.csproj")
[[ -n $base ]] || { echo "no <Version> in src/GerGit/GerGit.csproj" >&2; exit 1; }
version="$base+$(date -u +%Y%m%d%H%M%S)"

rm -rf "$pkg" "$out"/gergit_*.deb
# Native libraries (SkiaSharp, HarfBuzz) next to the executable instead of self-extracting to ~/.net/GerGit/<id>,
# which would leave a new folder in every user's home on each upgrade.
dotnet publish "$root/src/GerGit" -c Release -p:IncludeNativeLibrariesForSelfExtract=false -o "$pkg/opt/gergit" >&2

mkdir -p "$pkg/usr/bin"
ln -s /opt/gergit/GerGit "$pkg/usr/bin/gergit"
install -Dm644 "$root/packaging/gergit.desktop" "$pkg/usr/share/applications/gergit.desktop"
install -Dm644 "$root/packaging/gergit.svg" "$pkg/usr/share/icons/hicolor/scalable/apps/gergit.svg"
install -Dm644 "$root/src/GerGit/Assets/gergit.png" "$pkg/usr/share/icons/hicolor/256x256/apps/gergit.png"

# DEP-5 copyright; licence texts are indented by one space, blank lines become " .".
indent() { sed 's/^$/./; s/^/ /' "$1"; }
mkdir -p "$pkg/usr/share/doc/gergit"
{
  cat <<COPYRIGHT
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: GerGit

Files: *
Copyright: $name
License: GPL-3+

Files: opt/gergit/*
Copyright: their respective authors
License: MIT
Comment: .NET runtime, Avalonia, AvaloniaEdit, CommunityToolkit.Mvvm and SkiaSharp (NuGet packages).

Files: opt/gergit/*.pdb
Copyright: $name
License: GPL-3+

Files: opt/gergit/GerGit
Copyright: $name
 their respective authors
 2020 The Inter Project Authors (https://github.com/rsms/inter)
License: GPL-3+ and MIT and OFL-1.1
Comment: The single-file executable combines GerGit's own code (GPL-3+), the MIT-licensed
 libraries above and the Inter font from Avalonia.Fonts.Inter (OFL-1.1).

Files: src/GerGit/Assets/Icons.axaml src/GerGit/Assets/gergit.png packaging/gergit.svg
Copyright: 2026 Lucide Icons and Contributors
 2013-present Cole Bemis
License: ISC and MIT
COPYRIGHT
  indent "$root/src/GerGit/Assets/LUCIDE-LICENSE.txt"
  printf '\nFiles: src/GerGit/Assets/BrandIcons.axaml\nCopyright: Simple Icons contributors\nLicense: CC0-1.0\n'
  indent "$root/src/GerGit/Assets/SIMPLE-ICONS-LICENSE.txt"
  cat <<COPYRIGHT

License: GPL-3+
 GerGit is free software: you can redistribute it and/or modify it under the
 terms of the GNU General Public License as published by the Free Software
 Foundation, either version 3 of the License, or (at your option) any later version.
 .
 On Debian systems, the full text of the GNU General Public License version 3
 is in /usr/share/common-licenses/GPL-3.

License: MIT
 Permission is hereby granted, free of charge, to any person obtaining a copy
 of this software and associated documentation files (the "Software"), to deal
 in the Software without restriction, including without limitation the rights
 to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the Software is
 furnished to do so, subject to the following conditions:
 .
 The above copyright notice and this permission notice shall be included in all
 copies or substantial portions of the Software.
 .
 THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 SOFTWARE.

License: OFL-1.1
COPYRIGHT
  indent "$root/packaging/OFL-1.1.txt"
} > "$pkg/usr/share/doc/gergit/copyright"

# Depends: map the shared libraries the app loads to the packages that ship them on this system.
# .NET runtime: libc, libgcc_s, libstdc++, ICU (newest installed), OpenSSL. SkiaSharp: fontconfig.
# Avalonia X11: libX*, ICE, SM. Keyring (P/Invoke): libsecret, glib.
lib=/usr/lib/x86_64-linux-gnu
icu=$(ls "$lib"/libicuuc.so.[0-9]* | sort -V | tail -1)
libs=(libc.so.6 libgcc_s.so.1 libstdc++.so.6 "${icu##*/}" libssl.so.3 libfontconfig.so.1
      libX11.so.6 libXext.so.6 libXi.so.6 libXrandr.so.2 libXcursor.so.1 libXfixes.so.3 libICE.so.6 libSM.so.6
      libsecret-1.so.0 libglib-2.0.so.0)
deps=$(for l in "${libs[@]}"; do
         dpkg -S "$lib/$l" || { echo "no package ships $l" >&2; exit 1; }
       done | cut -d: -f1 | sort -u | paste -sd, | sed 's/,/, /g')

mkdir -p "$pkg/DEBIAN"
cat > "$pkg/DEBIAN/control" <<CONTROL
Package: gergit
Version: $version
Architecture: amd64
Maintainer: $name <$email>
Installed-Size: $(du -sk --exclude=DEBIAN "$pkg" | cut -f1)
Depends: git (>= 1:2.38), $deps
Recommends: libnotify-bin, xdg-utils
Section: vcs
Priority: optional
Description: Git client for Linux
 GerGit is a graphical Git client built with .NET and Avalonia.
CONTROL

chmod -R u=rwX,go=rX "$pkg"
deb=$out/gergit_${version}_amd64.deb
dpkg-deb --root-owner-group --build "$pkg" "$deb" >&2
rm -rf "$pkg"
echo "$deb"
