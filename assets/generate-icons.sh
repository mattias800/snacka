#!/bin/bash
set -e

# Generate platform-specific icons from SVG source
# Usage: ./generate-icons.sh [svg-file]
#
# Prerequisites:
#   - ImageMagick (brew install imagemagick / apt install imagemagick)
#   - librsvg (brew install librsvg / apt install librsvg2-bin) - for better SVG rendering
#   - macOS: iconutil (built-in) for .icns generation
#
# Output:
#   - src/Snacka.Client/snacka.ico          (Windows)
#   - installers/macos/AppIcon.icns         (macOS)
#   - installers/linux/snacka.png           (Linux - 256px)
#   - installers/windows/snacka.ico         (Windows installer)

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PROJECT_ROOT="$( cd "$SCRIPT_DIR/.." && pwd )"
SVG_FILE="${1:-$SCRIPT_DIR/icon.svg}"

echo "=== Snacka Icon Generator ==="
echo "Source: $SVG_FILE"
echo ""

# Check prerequisites
check_command() {
    if ! command -v "$1" &> /dev/null; then
        echo "Error: $1 is required but not installed."
        echo "Install with: $2"
        exit 1
    fi
}

check_command "convert" "brew install imagemagick (macOS) or apt install imagemagick (Linux)"

# Check if rsvg-convert is available (better SVG rendering)
USE_RSVG=false
if command -v rsvg-convert &> /dev/null; then
    USE_RSVG=true
    echo "Using rsvg-convert for high-quality SVG rendering"
else
    echo "Warning: rsvg-convert not found, using ImageMagick (may have lower quality)"
    echo "Install with: brew install librsvg (macOS) or apt install librsvg2-bin (Linux)"
fi
echo ""

# Create temp directory
TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

# Function to convert SVG to PNG at specific size
svg_to_png() {
    local size=$1
    local output=$2

    if [ "$USE_RSVG" = true ]; then
        rsvg-convert -w "$size" -h "$size" "$SVG_FILE" -o "$output"
    else
        convert -background none -density 300 -resize "${size}x${size}" "$SVG_FILE" "$output"
    fi
}

# ============================================
# Generate PNGs at all needed sizes
# ============================================
echo "Generating PNG files at various sizes..."

SIZES=(16 32 48 64 128 256 512 1024)
for size in "${SIZES[@]}"; do
    echo "  ${size}x${size}..."
    svg_to_png "$size" "$TEMP_DIR/icon_${size}.png"
done

# ============================================
# Windows .ico (multi-resolution)
# ============================================
echo ""
echo "Creating Windows .ico files..."

# ICO format supports: 16, 32, 48, 64, 128, 256
convert "$TEMP_DIR/icon_16.png" \
        "$TEMP_DIR/icon_32.png" \
        "$TEMP_DIR/icon_48.png" \
        "$TEMP_DIR/icon_64.png" \
        "$TEMP_DIR/icon_128.png" \
        "$TEMP_DIR/icon_256.png" \
        "$PROJECT_ROOT/src/Snacka.Client/snacka.ico"

cp "$PROJECT_ROOT/src/Snacka.Client/snacka.ico" "$PROJECT_ROOT/installers/windows/snacka.ico"

echo "  Created: src/Snacka.Client/snacka.ico"
echo "  Created: installers/windows/snacka.ico"

# ============================================
# Linux PNG (256px standard)
# ============================================
echo ""
echo "Creating Linux PNG..."

cp "$TEMP_DIR/icon_256.png" "$PROJECT_ROOT/installers/linux/snacka.png"
echo "  Created: installers/linux/snacka.png"

# Also create a 512px version for high-DPI
cp "$TEMP_DIR/icon_512.png" "$PROJECT_ROOT/installers/linux/snacka-512.png"
echo "  Created: installers/linux/snacka-512.png"

# ============================================
# macOS .icns (requires iconutil on macOS)
# ============================================
echo ""
echo "Creating macOS .icns..."

if [[ "$OSTYPE" == "darwin"* ]]; then
    # On macOS, use iconutil
    ICONSET_DIR="$TEMP_DIR/AppIcon.iconset"
    mkdir -p "$ICONSET_DIR"

    # macOS iconset requires specific naming convention
    cp "$TEMP_DIR/icon_16.png" "$ICONSET_DIR/icon_16x16.png"
    cp "$TEMP_DIR/icon_32.png" "$ICONSET_DIR/icon_16x16@2x.png"
    cp "$TEMP_DIR/icon_32.png" "$ICONSET_DIR/icon_32x32.png"
    cp "$TEMP_DIR/icon_64.png" "$ICONSET_DIR/icon_32x32@2x.png"
    cp "$TEMP_DIR/icon_128.png" "$ICONSET_DIR/icon_128x128.png"
    cp "$TEMP_DIR/icon_256.png" "$ICONSET_DIR/icon_128x128@2x.png"
    cp "$TEMP_DIR/icon_256.png" "$ICONSET_DIR/icon_256x256.png"
    cp "$TEMP_DIR/icon_512.png" "$ICONSET_DIR/icon_256x256@2x.png"
    cp "$TEMP_DIR/icon_512.png" "$ICONSET_DIR/icon_512x512.png"
    cp "$TEMP_DIR/icon_1024.png" "$ICONSET_DIR/icon_512x512@2x.png"

    iconutil -c icns "$ICONSET_DIR" -o "$PROJECT_ROOT/installers/macos/AppIcon.icns"
    echo "  Created: installers/macos/AppIcon.icns"
else
    # On Linux, create icns using png2icns if available, otherwise skip
    if command -v png2icns &> /dev/null; then
        png2icns "$PROJECT_ROOT/installers/macos/AppIcon.icns" \
            "$TEMP_DIR/icon_16.png" \
            "$TEMP_DIR/icon_32.png" \
            "$TEMP_DIR/icon_128.png" \
            "$TEMP_DIR/icon_256.png" \
            "$TEMP_DIR/icon_512.png"
        echo "  Created: installers/macos/AppIcon.icns"
    else
        echo "  Skipped: Not on macOS and png2icns not available"
        echo "  Install png2icns or run this script on macOS to generate .icns"

        # Create a placeholder by copying PNG (will be replaced in CI on macOS)
        cp "$TEMP_DIR/icon_512.png" "$PROJECT_ROOT/installers/macos/AppIcon.png"
        echo "  Created placeholder: installers/macos/AppIcon.png"
    fi
fi

# ============================================
# Avalonia app icon (embedded resource)
# ============================================
echo ""
echo "Creating Avalonia app icon..."

# Copy 256px PNG for Avalonia window icon
cp "$TEMP_DIR/icon_256.png" "$PROJECT_ROOT/src/Snacka.Client/Assets/snacka-icon.png"
echo "  Created: src/Snacka.Client/Assets/snacka-icon.png"

# ============================================
# Summary
# ============================================
echo ""
echo "=== Icon Generation Complete ==="
echo ""
echo "Generated files:"
ls -la "$PROJECT_ROOT/src/Snacka.Client/snacka.ico" 2>/dev/null || true
ls -la "$PROJECT_ROOT/src/Snacka.Client/Assets/snacka-icon.png" 2>/dev/null || true
ls -la "$PROJECT_ROOT/installers/windows/snacka.ico" 2>/dev/null || true
ls -la "$PROJECT_ROOT/installers/macos/AppIcon.icns" 2>/dev/null || true
ls -la "$PROJECT_ROOT/installers/linux/snacka.png" 2>/dev/null || true
echo ""
echo "To update the icon, edit assets/icon.svg and run this script again."
