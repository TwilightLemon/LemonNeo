#!/usr/bin/env bash
# Assemble LemonNeo.Desktop.app from a `dotnet publish` output directory.
#
# The whole publish output — the host executable, its assemblies, and the `plugins/` folder
# the host looks for next to itself — is placed in Contents/MacOS. Only the metadata macOS
# reads is added on top. Must run on macOS: ditto, plutil, and codesign are used.
#
# Usage: Create-AppBundle.sh <publish-dir> <bundle-path> <version> <build-number>
set -euo pipefail

if [ "$#" -ne 4 ]; then
    echo "usage: $(basename "$0") <publish-dir> <bundle-path> <version> <build-number>" >&2
    exit 2
fi

publish_dir=$(cd "$1" && pwd)
bundle=$2
version=$3
build_number=$4

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
executable=LemonNeo.Desktop
icon_source="$repository_root/src/LemonNeo.Desktop/Resources/Icons/App.icns"
plist_template="$repository_root/build/macos/Info.plist"

for required in "$publish_dir/$executable" "$icon_source" "$plist_template"; do
    [ -e "$required" ] || { echo "error: $required not found" >&2; exit 1; }
done

rm -rf "$bundle"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
ditto "$publish_dir" "$bundle/Contents/MacOS"
cp "$icon_source" "$bundle/Contents/Resources/App.icns"
printf 'APPL????' > "$bundle/Contents/PkgInfo"
sed -e "s/@@VERSION@@/$version/g" -e "s/@@BUILD@@/$build_number/g" \
    "$plist_template" > "$bundle/Contents/Info.plist"

plutil -lint "$bundle/Contents/Info.plist"
chmod +x "$bundle/Contents/MacOS/$executable"

# Extended attributes picked up from restored packages break the signature seal.
xattr -cr "$bundle" || true

# An ad-hoc signature is enough for the app to run from this machine. Downloading it still
# needs a Developer ID signature and notarization, which this build does not produce.
if codesign --force --deep --sign - "$bundle" && codesign --verify --strict "$bundle"; then
    echo "Ad-hoc signed $bundle"
else
    echo "warning: ad-hoc signing failed; the bundle still runs after clearing the quarantine attribute" >&2
fi

echo "Assembled $bundle (version $version, build $build_number)"
