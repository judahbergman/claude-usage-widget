#!/bin/bash
# Builds "Claude Usage.app" (Apple Silicon + Intel) and zips it into dist/ClaudeUsage.zip.
# Usage: ./build.sh [version] [build-number]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
VERSION="${1:-$(tr -d '[:space:]' < "$ROOT/VERSION")}"
BUILD="${2:-1}"
OUT="$ROOT/dist"
APP="$OUT/Claude Usage.app"

rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

for ARCH in arm64 x86_64; do
  echo "==> Compiling for $ARCH"
  swiftc -O -parse-as-library -target "$ARCH-apple-macos12.0" \
    "$ROOT/Sources/ClaudeUsage.swift" -o "$OUT/ClaudeUsage-$ARCH"
done
lipo -create -output "$APP/Contents/MacOS/ClaudeUsage" "$OUT/ClaudeUsage-arm64" "$OUT/ClaudeUsage-x86_64"
rm "$OUT/ClaudeUsage-arm64" "$OUT/ClaudeUsage-x86_64"

cp "$ROOT/Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD/" "$ROOT/Resources/Info.plist" > "$APP/Contents/Info.plist"
codesign --force --sign - "$APP"

(cd "$OUT" && ditto -c -k --keepParent "Claude Usage.app" ClaudeUsage.zip)
echo "==> Built Claude Usage $VERSION ($BUILD): $OUT/ClaudeUsage.zip"
