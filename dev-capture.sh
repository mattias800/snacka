#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

echo "Building MiscordCapture..."
cd src/MiscordCapture
swift build -c release
echo "MiscordCapture build complete"
cd ..

