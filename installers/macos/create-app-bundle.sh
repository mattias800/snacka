#!/bin/bash
set -e

# Create macOS .app bundle from published .NET application
# Usage: ./create-app-bundle.sh <publish-dir> <output-dir> <version> [architecture]
#
# Example: ./create-app-bundle.sh ./publish ./output 0.1.0 arm64

PUBLISH_DIR="${1:?Usage: $0 <publish-dir> <output-dir> <version> [architecture]}"
OUTPUT_DIR="${2:?Usage: $0 <publish-dir> <output-dir> <version> [architecture]}"
VERSION="${3:?Usage: $0 <publish-dir> <output-dir> <version> [architecture]}"
ARCH="${4:-arm64}"

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
APP_NAME="Snacka"
APP_BUNDLE="$OUTPUT_DIR/$APP_NAME.app"
BUILD_NUMBER="${GITHUB_RUN_NUMBER:-1}"

echo "Creating $APP_NAME.app bundle..."
echo "  Publish dir: $PUBLISH_DIR"
echo "  Output dir: $OUTPUT_DIR"
echo "  Version: $VERSION"
echo "  Architecture: $ARCH"

# Clean and create directory structure
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Copy Info.plist and substitute version
sed -e "s/\${VERSION}/$VERSION/g" \
    -e "s/\${BUILD_NUMBER}/$BUILD_NUMBER/g" \
    "$SCRIPT_DIR/Info.plist" > "$APP_BUNDLE/Contents/Info.plist"

# Copy the launcher script
cp "$SCRIPT_DIR/Snacka" "$APP_BUNDLE/Contents/MacOS/Snacka"
chmod +x "$APP_BUNDLE/Contents/MacOS/Snacka"

# Copy all published files to MacOS directory
cp -R "$PUBLISH_DIR/"* "$APP_BUNDLE/Contents/MacOS/"

# Ensure the main executable is executable
chmod +x "$APP_BUNDLE/Contents/MacOS/Snacka.Client"

# Copy icon if it exists
if [ -f "$SCRIPT_DIR/AppIcon.icns" ]; then
    cp "$SCRIPT_DIR/AppIcon.icns" "$APP_BUNDLE/Contents/Resources/"
fi

# Create PkgInfo file
echo "APPL????" > "$APP_BUNDLE/Contents/PkgInfo"

echo "App bundle created: $APP_BUNDLE"

# If code signing certificate is available, sign the app
# This is prepared for future use
if [ -n "$CODESIGN_IDENTITY" ]; then
    echo "Signing app bundle with identity: $CODESIGN_IDENTITY"

    # Sign all dylibs and executables first
    find "$APP_BUNDLE" -type f \( -name "*.dylib" -o -perm +111 \) -exec \
        codesign --force --options runtime --timestamp \
        --entitlements "$SCRIPT_DIR/Snacka.entitlements" \
        --sign "$CODESIGN_IDENTITY" {} \;

    # Sign the app bundle itself
    codesign --force --options runtime --timestamp \
        --entitlements "$SCRIPT_DIR/Snacka.entitlements" \
        --sign "$CODESIGN_IDENTITY" \
        "$APP_BUNDLE"

    echo "Code signing complete"
else
    echo "CODESIGN_IDENTITY not set, skipping code signing"
    echo "To sign: export CODESIGN_IDENTITY='Developer ID Application: Your Name (TEAMID)'"
fi

echo "Done!"
