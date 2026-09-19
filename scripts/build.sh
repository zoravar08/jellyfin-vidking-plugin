#!/bin/bash
# Build script for Jellyfin VidKing Integration plugin
# Requires: .NET 10 SDK at /usr/share/dotnet/dotnet10
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SOURCE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)/src"
BUILD_DIR="$SCRIPT_DIR/build"
RELEASE_DIR="$SCRIPT_DIR/release"
DOTNET="/usr/share/dotnet/dotnet10/dotnet"

echo "=== Jellyfin VidKing Plugin Build ==="
echo "Source: $SOURCE_DIR"
echo "Output: $RELEASE_DIR"

# Clean previous build
rm -rf "$BUILD_DIR" "$RELEASE_DIR"
mkdir -p "$BUILD_DIR" "$RELEASE_DIR"

# Build
echo ""
echo "--- Building plugin ---"
export PATH="/usr/share/dotnet/dotnet10:$PATH"
cd "$SOURCE_DIR"
"$DOTNET" build -c Release -o "$BUILD_DIR" 2>&1 | tail -5

if [ ! -f "$BUILD_DIR/Jellyfin.Plugin.VidKing.dll" ]; then
    echo "ERROR: Build failed — DLL not found"
    exit 1
fi

# Copy Python extractor
echo ""
echo "--- Packaging ---"
cp "$SOURCE_DIR/VKingStreamExtractor.py" "$BUILD_DIR/"
cp "$SOURCE_DIR/meta.json" "$BUILD_DIR/"

# Create release ZIP
VERSION=$(python3 -c "import json; print(json.load(open('$SOURCE_DIR/meta.json'))['version'])")
PLUGIN_NAME="Jellyfin.Plugin.VidKing"
ZIP="$RELEASE_DIR/${PLUGIN_NAME}_$VERSION.zip"

cd "$BUILD_DIR"
zip -q -r "$ZIP" .
echo "Created: $ZIP ($(du -h "$ZIP" | cut -f1))"
echo ""
echo "=== Build complete ==="
echo "Files:"
ls -la "$BUILD_DIR/"
