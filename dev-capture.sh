#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

echo "Building SnackaCaptureVideoToolbox..."
cd src/SnackaCaptureVideoToolbox
swift build -c release
echo "SnackaCaptureVideoToolbox build complete"
cd ../..

echo "Building SnackaMetalRenderer..."
cd src/SnackaMetalRenderer
swift build -c release
echo "SnackaMetalRenderer build complete"
cd ../..

echo "All native macOS components built successfully!"
