#!/usr/bin/env bash
# Assemble LemonNeo.Desktop.app from a `dotnet publish` output directory.
#
# The host executable is placed in Contents/MacOS. The rest of the publish output lives in
# Contents/Resources/app, with compatibility symlinks next to the host so .NET and LemonNeo
# can still find their assemblies and `plugins/` directory. Keeping ordinary data out of
# Contents/MacOS is required for macOS code signing. Must run on macOS: ditto, plutil,
# file, and codesign are used.
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
runtime_dir="$bundle/Contents/Resources/app"
mkdir -p "$bundle/Contents/MacOS" "$runtime_dir"
ditto "$publish_dir" "$runtime_dir"
mv "$runtime_dir/$executable" "$bundle/Contents/MacOS/$executable"

# Contents/MacOS is a code-only directory. codesign treats every regular file and dotted
# directory below it as nested code, so putting .NET assemblies and LemonNeo.Plugin.*
# directories there makes --deep fail with "bundle format unrecognized". Relative symlinks
# preserve the runtime's next-to-the-executable lookup without putting those files there.
while IFS= read -r -d '' runtime_item; do
    name=${runtime_item##*/}
    ln -s "../Resources/app/$name" "$bundle/Contents/MacOS/$name"
done < <(find "$runtime_dir" -mindepth 1 -maxdepth 1 -print0)

cp "$icon_source" "$bundle/Contents/Resources/App.icns"
printf 'APPL????' > "$bundle/Contents/PkgInfo"
sed -e "s/@@VERSION@@/$version/g" -e "s/@@BUILD@@/$build_number/g" \
    "$plist_template" > "$bundle/Contents/Info.plist"

plutil -lint "$bundle/Contents/Info.plist"
chmod +x "$bundle/Contents/MacOS/$executable"

# Extended attributes picked up from restored packages break the signature seal.
xattr -cr "$bundle" || true

# Do not use `codesign --deep` for signing. Sign nested Mach-O code and app bundles first,
# then seal the outer bundle. `--deep` remains useful for the final recursive verification.
sign_bundle() {
    while IFS= read -r -d '' candidate; do
        case $(file -b "$candidate") in
            *Mach-O*) codesign --force --sign - "$candidate" || return 1 ;;
        esac
    done < <(find "$runtime_dir" -type f -print0)

    while IFS= read -r -d '' nested_app; do
        codesign --force --sign - "$nested_app" || return 1
    done < <(find "$runtime_dir" -depth -type d -name '*.app' -print0)

    codesign --force --sign - "$bundle" || return 1
    codesign --verify --deep --strict "$bundle"
}

# An ad-hoc signature is enough for the app to run from this machine. Downloading it still
# needs a Developer ID signature and notarization, which this build does not produce.
if sign_bundle; then
    echo "Ad-hoc signed $bundle"
else
    echo "warning: ad-hoc signing failed; the bundle still runs after clearing the quarantine attribute" >&2
fi

echo "Assembled $bundle (version $version, build $build_number)"
