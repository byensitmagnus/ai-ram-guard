#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="${1:-1.4.0}"
OUTPUT="${2:-$ROOT/out/mac}"
case "$VERSION" in *[!0-9.]*|'') echo "Version must contain digits and dots" >&2; exit 1;; esac
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
STAGE="$(mktemp -d "$OUTPUT/.mac-package.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
APP="$STAGE/AI RAM Guard.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$STAGE/icon.iconset"

cd "$ROOT/macos"
for ARCH in arm64 x86_64; do
  swift build -c release --arch "$ARCH" --scratch-path ".build/$ARCH"
  BIN="$(swift build -c release --arch "$ARCH" --scratch-path ".build/$ARCH" --show-bin-path)"
  cp "$BIN/AiRamGuardMac" "$STAGE/AiRamGuardMac-$ARCH"
done
lipo -create "$STAGE/AiRamGuardMac-arm64" "$STAGE/AiRamGuardMac-x86_64" -output "$APP/Contents/MacOS/AiRamGuardMac"
lipo -verify_arch arm64 x86_64 "$APP/Contents/MacOS/AiRamGuardMac"
chmod +x "$APP/Contents/MacOS/AiRamGuardMac"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>AI RAM Guard</string>
<key>CFBundleDisplayName</key><string>AI RAM Guard</string>
<key>CFBundleIdentifier</key><string>dk.byensit.airamguard</string>
<key>CFBundleExecutable</key><string>AiRamGuardMac</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$VERSION</string>
<key>CFBundleVersion</key><string>$VERSION</string>
<key>CFBundleIconFile</key><string>icon</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>LSUIElement</key><true/>
<key>NSHighResolutionCapable</key><true/>
<key>NSHumanReadableCopyright</key><string>© 2026 Magnus Steinmeier Olsen / Byens IT</string>
</dict></plist>
PLIST
plutil -lint "$APP/Contents/Info.plist"
for SIZE in 16 32 128 256 512; do
  sips -z "$SIZE" "$SIZE" "$ROOT/assets/logo.png" --out "$STAGE/icon.iconset/icon_${SIZE}x${SIZE}.png" >/dev/null
  DOUBLE=$((SIZE * 2))
  sips -z "$DOUBLE" "$DOUBLE" "$ROOT/assets/logo.png" --out "$STAGE/icon.iconset/icon_${SIZE}x${SIZE}@2x.png" >/dev/null
done
iconutil -c icns "$STAGE/icon.iconset" -o "$APP/Contents/Resources/icon.icns"

if [ -n "${MAC_SIGN_IDENTITY:-}" ]; then
  codesign --force --options runtime --timestamp --sign "$MAC_SIGN_IDENTITY" "$APP"
else
  codesign --force --timestamp=none --sign - "$APP"
  echo "Mac preview is ad-hoc signed. Developer ID notarization has not been configured."
fi
codesign --verify --deep --strict "$APP"

if [ -n "${MAC_NOTARY_PROFILE:-}" ]; then
  if [ -z "${MAC_SIGN_IDENTITY:-}" ]; then echo "Notarization requires a Developer ID identity" >&2; exit 1; fi
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$STAGE/notary.zip"
  xcrun notarytool submit "$STAGE/notary.zip" --keychain-profile "$MAC_NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  xcrun stapler validate "$APP"
  spctl --assess --type execute "$APP"
fi

# Retain the exact signed app for CI UI smoke checks and for local installation.
ditto "$APP" "$OUTPUT/AI RAM Guard.app"

# Verify the actual bundled binary before it becomes a download.
"$APP/Contents/MacOS/AiRamGuardMac" --scan-json > "$OUTPUT/mac-scan-smoke.json"
python3 - "$OUTPUT/mac-scan-smoke.json" <<'PY'
import json, sys
with open(sys.argv[1]) as f:
    report = json.load(f)
assert report['totalMemory'] > 0
assert isinstance(report['agents'], list)
PY
mkdir -p "$STAGE/dmg"
ditto "$APP" "$STAGE/dmg/AI RAM Guard.app"
ln -s /Applications "$STAGE/dmg/Applications"
cp "$ROOT/macos/README.md" "$STAGE/dmg/Read me first.txt"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUTPUT/AiRamGuard-mac-universal.zip"
hdiutil create -volname "AI RAM Guard" -srcfolder "$STAGE/dmg" -ov -format UDZO "$OUTPUT/AiRamGuard-mac-universal.dmg"
shasum -a 256 "$OUTPUT/AiRamGuard-mac-universal.dmg" "$OUTPUT/AiRamGuard-mac-universal.zip" | sed "s|$OUTPUT/||" > "$OUTPUT/SHA256SUMS-mac.txt"
echo "Mac packages ready: $OUTPUT/AiRamGuard-mac-universal.dmg"
