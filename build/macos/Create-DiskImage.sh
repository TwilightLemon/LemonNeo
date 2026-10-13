#!/usr/bin/env bash
# Create a compressed macOS disk image containing the app and an Applications shortcut.
#
# Usage: Create-DiskImage.sh <app-bundle> <dmg-path> [volume-name]
set -euo pipefail

if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then
    echo "usage: $(basename "$0") <app-bundle> <dmg-path> [volume-name]" >&2
    exit 2
fi

app_bundle=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
dmg_path=$2
volume_name=${3:-Lemon Neo}

if [ ! -d "$app_bundle" ] || [ ! -f "$app_bundle/Contents/Info.plist" ]; then
    echo "error: $app_bundle is not an app bundle" >&2
    exit 1
fi

dmg_parent=$(dirname "$dmg_path")
mkdir -p "$dmg_parent"
dmg_parent=$(cd "$dmg_parent" && pwd)
dmg_path="$dmg_parent/$(basename "$dmg_path")"

staging_dir=$(mktemp -d "${TMPDIR:-/tmp}/lemonneo-dmg.XXXXXX")
cleanup() {
    rm -rf "$staging_dir"
}
trap cleanup EXIT

# Use the product's display name in Finder, independently of the CI working bundle name.
display_name=$(plutil -extract CFBundleDisplayName raw "$app_bundle/Contents/Info.plist")
ditto "$app_bundle" "$staging_dir/$display_name.app"
ln -s /Applications "$staging_dir/Applications"

hdiutil create \
    -volname "$volume_name" \
    -srcfolder "$staging_dir" \
    -format UDZO \
    -ov \
    "$dmg_path"

hdiutil verify "$dmg_path"
echo "Created $dmg_path"
