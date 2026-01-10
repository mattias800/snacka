#!/bin/bash

# Development Alice client startup script
# Usage: ./dev-alice.sh
# Note: Run dev-backend.sh first

# Set VLC environment variables for audio playback on macOS
if [[ "$OSTYPE" == "darwin"* ]]; then
    export VLC_PLUGIN_PATH="/Applications/VLC.app/Contents/MacOS/plugins"
    export DYLD_LIBRARY_PATH="/Applications/VLC.app/Contents/MacOS/lib:$DYLD_LIBRARY_PATH"
fi

SERVER_URL="http://localhost:5117"
CLIENT_PROJECT="src/Miscord.Client/Miscord.Client.csproj"

# Test account (will be auto-registered if it doesn't exist)
EMAIL="alice@test.com"
PASSWORD="password123"

echo "=== Miscord Alice Client Startup ==="
echo ""

# Check if server is running
echo "Checking if server is running..."
if ! curl -s "$SERVER_URL/api/health" > /dev/null 2>&1; then
    echo "Server is not running at $SERVER_URL"
    echo "Please start the backend first with: ./dev-backend.sh"
    exit 1
fi
echo "Server is ready!"
echo ""

# Build client
echo "Building client..."
dotnet build "$CLIENT_PROJECT" --verbosity quiet
if [ $? -ne 0 ]; then
    echo "Client build failed!"
    exit 1
fi

echo "Build complete."
echo ""

# Start Alice client
echo "Starting Alice client..."
dotnet run --project "$CLIENT_PROJECT" --no-build -- \
    --server "$SERVER_URL" \
    --email "$EMAIL" \
    --password "$PASSWORD" \
    --title "Miscord - Alice" \
    --profile alice
