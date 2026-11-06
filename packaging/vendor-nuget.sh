#!/usr/bin/env bash
# Collects every NuGet package an offline `dotnet publish` of src/GerGit needs as flat *.nupkg files in <outdir>.
# Usage: packaging/vendor-nuget.sh <outdir> [<source-tree>]   (source tree defaults to this checkout)
#
# Restores for the RID of the installed SDK (Ubuntu's dotnet-sdk-10.0: ubuntu.<release>-<arch>), the same RID
# debian/rules publishes for. The runtime and apphost packs for that RID ship with the SDK package and
# Microsoft.NET.ILLink.Tasks ships in its library-packs folder, so none of them are vendored: the set is only
# the RID-independent app dependencies (Avalonia, SkiaSharp, ...) and stays valid across SDK updates.
# Restores from a copy of the tree, so the checkout's obj/ (and other builds running in it) are never touched.
set -euo pipefail
umask 022
[[ $# -ge 1 ]] || { echo "usage: $0 <outdir> [<source-tree>]" >&2; exit 2; }
out=$(realpath -m "$1")
src=$(realpath "${2:-$(dirname "$0")/..}")
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 AVALONIA_TELEMETRY_OPTOUT=1

sdk=$(dirname "$(realpath "$(command -v dotnet)")")
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -n1)
[[ -n $rid ]] || { echo "cannot read the SDK's RID from dotnet --info" >&2; exit 1; }

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
tar -C "$src" --exclude=bin --exclude=obj -c Directory.Build.props src | tar -C "$tmp" -x
cat > "$tmp/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="sdk-library-packs" value="$sdk/library-packs" />
  </packageSources>
</configuration>
EOF

# _IsPublishing=true applies the same csproj/SDK publish settings (self-contained, single file) as `dotnet publish`.
dotnet restore "$tmp/src/GerGit" -p:RuntimeIdentifier="$rid" -p:_IsPublishing=true -p:NuGetAudit=false \
  --configfile "$tmp/NuGet.config" --packages "$tmp/packages" --disable-build-servers >&2

rm -rf "$out"
mkdir -p "$out"
# The global packages folder lowercases file names, library-packs does not.
find "$tmp/packages" -name '*.nupkg' | while read -r nupkg; do
  ls "$sdk/library-packs" | grep -qixF "${nupkg##*/}" || cp "$nupkg" "$out/"
done
echo "Vendored $(ls "$out" | wc -l) packages for $rid into $out ($(du -sh "$out" | cut -f1))" >&2
