#!/bin/bash
set -e

# Create Linux AppImage from published .NET application
# Usage: ./create-appimage.sh <publish-dir> <output-dir> <version>
#
# Example: ./create-appimage.sh ./publish ./output 0.1.0
#
# Prerequisites:
#   - appimagetool (will be downloaded if not present)

PUBLISH_DIR="${1:?Usage: $0 <publish-dir> <output-dir> <version>}"
OUTPUT_DIR="${2:?Usage: $0 <publish-dir> <output-dir> <version>}"
VERSION="${3:?Usage: $0 <publish-dir> <output-dir> <version>}"

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
APP_NAME="Snacka"
APPDIR="$OUTPUT_DIR/${APP_NAME}.AppDir"
ARCH=$(uname -m)

echo "Creating $APP_NAME AppImage..."
echo "  Publish dir: $PUBLISH_DIR"
echo "  Output dir: $OUTPUT_DIR"
echo "  Version: $VERSION"
echo "  Architecture: $ARCH"

# Download appimagetool if not present
APPIMAGETOOL="$OUTPUT_DIR/appimagetool"
if [ ! -f "$APPIMAGETOOL" ]; then
    echo "Downloading appimagetool..."
    APPIMAGETOOL_URL="https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
    curl -L -o "$APPIMAGETOOL" "$APPIMAGETOOL_URL"
    chmod +x "$APPIMAGETOOL"
fi

# Clean and create AppDir structure
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/lib"
mkdir -p "$APPDIR/usr/share/applications"
mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$APPDIR/usr/share/metainfo"

# Copy application files
echo "Copying application files..."
cp -R "$PUBLISH_DIR/"* "$APPDIR/usr/bin/"

# Make main executable executable
chmod +x "$APPDIR/usr/bin/Snacka.Client"

# Copy AppRun script
cp "$SCRIPT_DIR/AppRun" "$APPDIR/AppRun"
chmod +x "$APPDIR/AppRun"

# Copy desktop file
sed "s/Exec=Snacka/Exec=Snacka.Client/" "$SCRIPT_DIR/snacka.desktop" > "$APPDIR/usr/share/applications/snacka.desktop"
cp "$APPDIR/usr/share/applications/snacka.desktop" "$APPDIR/snacka.desktop"

# Copy icon (use placeholder if not present)
if [ -f "$SCRIPT_DIR/snacka.png" ]; then
    cp "$SCRIPT_DIR/snacka.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/snacka.png"
    cp "$SCRIPT_DIR/snacka.png" "$APPDIR/snacka.png"
else
    echo "Warning: snacka.png not found, creating placeholder icon"
    # Create a simple placeholder icon using ImageMagick if available
    if command -v convert &> /dev/null; then
        convert -size 256x256 xc:#5865F2 -fill white -gravity center \
            -pointsize 120 -annotate 0 "S" \
            "$APPDIR/usr/share/icons/hicolor/256x256/apps/snacka.png"
        cp "$APPDIR/usr/share/icons/hicolor/256x256/apps/snacka.png" "$APPDIR/snacka.png"
    else
        # Create minimal 1x1 PNG as fallback
        echo "Warning: ImageMagick not found, AppImage will have no icon"
        touch "$APPDIR/snacka.png"
    fi
fi

# Create AppStream metainfo
cat > "$APPDIR/usr/share/metainfo/com.snacka.client.appdata.xml" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<component type="desktop-application">
  <id>com.snacka.client</id>
  <name>Snacka</name>
  <summary>A self-hosted Discord alternative</summary>
  <metadata_license>MIT</metadata_license>
  <project_license>MIT</project_license>
  <description>
    <p>
      Snacka is a self-hosted communication platform with support for text channels,
      voice chat, video calls, and screen sharing.
    </p>
  </description>
  <url type="homepage">https://github.com/yourusername/snacka</url>
  <launchable type="desktop-id">snacka.desktop</launchable>
  <releases>
    <release version="$VERSION" date="$(date +%Y-%m-%d)"/>
  </releases>
  <content_rating type="oars-1.1"/>
</component>
EOF

# Create the AppImage
echo "Creating AppImage..."
OUTPUT_APPIMAGE="$OUTPUT_DIR/Snacka-$VERSION-$ARCH.AppImage"

# Set architecture for appimagetool
export ARCH="$ARCH"

# Run appimagetool
# Note: On systems where FUSE is not available, we extract and run
if "$APPIMAGETOOL" --version &>/dev/null; then
    "$APPIMAGETOOL" "$APPDIR" "$OUTPUT_APPIMAGE"
else
    # Extract and run if FUSE is not available (common in CI)
    "$APPIMAGETOOL" --appimage-extract
    ./squashfs-root/AppRun "$APPDIR" "$OUTPUT_APPIMAGE"
    rm -rf squashfs-root
fi

# Clean up AppDir
rm -rf "$APPDIR"

echo ""
echo "AppImage created: $OUTPUT_APPIMAGE"
ls -lh "$OUTPUT_APPIMAGE"

echo ""
echo "To run: chmod +x $OUTPUT_APPIMAGE && ./$OUTPUT_APPIMAGE"
