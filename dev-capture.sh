#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

echo "Building SnackaCapture..."
cd src/SnackaCapture
swift build -c release
echo "SnackaCapture build complete"
cd ..

