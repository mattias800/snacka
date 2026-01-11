#!/bin/bash
set -e

# Create DMG installer from .app bundle
# Usage: ./create-dmg.sh <app-bundle-dir> <output-dmg> <volume-name>
#
# Example: ./create-dmg.sh ./output/Snacka.app ./Snacka-0.1.0-arm64.dmg "Snacka"

APP_BUNDLE="${1:?Usage: $0 <app-bundle-dir> <output-dmg> <volume-name>}"
OUTPUT_DMG="${2:?Usage: $0 <app-bundle-dir> <output-dmg> <volume-name>}"
VOLUME_NAME="${3:-Snacka}"

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
TEMP_DMG="/tmp/snacka-temp.dmg"
MOUNT_POINT="/tmp/snacka-dmg-mount"

echo "Creating DMG installer..."
echo "  App bundle: $APP_BUNDLE"
echo "  Output: $OUTPUT_DMG"
echo "  Volume name: $VOLUME_NAME"

# Verify app bundle exists
if [ ! -d "$APP_BUNDLE" ]; then
    echo "Error: App bundle not found at $APP_BUNDLE"
    exit 1
fi

# Clean up any previous attempts
rm -f "$TEMP_DMG" "$OUTPUT_DMG"
rm -rf "$MOUNT_POINT"

# Calculate size needed (app size + 50MB buffer)
APP_SIZE=$(du -sm "$APP_BUNDLE" | cut -f1)
DMG_SIZE=$((APP_SIZE + 50))

echo "Creating temporary DMG (${DMG_SIZE}MB)..."

# Create temporary DMG
hdiutil create -size "${DMG_SIZE}m" -fs HFS+ -volname "$VOLUME_NAME" "$TEMP_DMG"

# Mount the DMG
echo "Mounting temporary DMG..."
mkdir -p "$MOUNT_POINT"
hdiutil attach "$TEMP_DMG" -mountpoint "$MOUNT_POINT" -nobrowse

# Copy the app bundle
echo "Copying app bundle..."
cp -R "$APP_BUNDLE" "$MOUNT_POINT/"

# Create symlink to Applications folder for drag-and-drop install
ln -s /Applications "$MOUNT_POINT/Applications"

# Create a simple background instructions file
cat > "$MOUNT_POINT/.background_readme.txt" << 'EOF'
Drag Snacka to Applications to install.

Requirements:
- macOS 11.0 (Big Sur) or later
- VLC media player (for audio playback): https://www.videolan.org/vlc/
EOF

# Set custom icon positions using AppleScript (optional, makes it prettier)
# This sets up the Finder window to show nicely when opened
echo "Configuring DMG window..."
osascript << EOF
tell application "Finder"
    tell disk "$VOLUME_NAME"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set bounds of container window to {400, 100, 900, 400}
        set viewOptions to the icon view options of container window
        set arrangement of viewOptions to not arranged
        set icon size of viewOptions to 72
        set position of item "Snacka.app" of container window to {125, 150}
        set position of item "Applications" of container window to {375, 150}
        close
        open
        update without registering applications
        delay 2
    end tell
end tell
EOF

# Unmount
echo "Unmounting..."
hdiutil detach "$MOUNT_POINT" -force

# Convert to compressed DMG
echo "Creating compressed DMG..."
hdiutil convert "$TEMP_DMG" -format UDZO -imagekey zlib-level=9 -o "$OUTPUT_DMG"

# Clean up
rm -f "$TEMP_DMG"
rm -rf "$MOUNT_POINT"

# If notarization credentials are available, notarize the DMG
# This is prepared for future use
if [ -n "$APPLE_ID" ] && [ -n "$APPLE_APP_PASSWORD" ] && [ -n "$APPLE_TEAM_ID" ]; then
    echo "Submitting DMG for notarization..."

    xcrun notarytool submit "$OUTPUT_DMG" \
        --apple-id "$APPLE_ID" \
        --password "$APPLE_APP_PASSWORD" \
        --team-id "$APPLE_TEAM_ID" \
        --wait

    echo "Stapling notarization ticket..."
    xcrun stapler staple "$OUTPUT_DMG"

    echo "Notarization complete"
else
    echo "Apple notarization credentials not set, skipping notarization"
    echo "To notarize, set: APPLE_ID, APPLE_APP_PASSWORD, APPLE_TEAM_ID"
fi

echo ""
echo "DMG created: $OUTPUT_DMG"
ls -lh "$OUTPUT_DMG"
