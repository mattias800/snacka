#!/bin/bash

# Development clients startup script - starts two test clients
# Usage: ./dev-clients.sh
# Note: Run dev-backend.sh first, or use dev-start.sh to run everything together

# Set VLC environment variables for audio playback on macOS
if [[ "$OSTYPE" == "darwin"* ]]; then
    export VLC_PLUGIN_PATH="/Applications/VLC.app/Contents/MacOS/plugins"
    export DYLD_LIBRARY_PATH="/Applications/VLC.app/Contents/MacOS/lib:$DYLD_LIBRARY_PATH"
fi

SERVER_URL="http://localhost:5117"
CLIENT_PROJECT="src/Snacka.Client/Snacka.Client.csproj"

# Test accounts (will be auto-registered if they don't exist)
USER1_EMAIL="alice@test.com"
USER1_PASSWORD="Password123!"
USER2_EMAIL="bob@test.com"
USER2_PASSWORD="Password123!"

echo "=== Snacka Clients Startup ==="
echo ""

# Kill any existing Snacka client processes
echo "Stopping any existing Snacka client processes..."
pkill -f "dotnet.*Snacka.Client" 2>/dev/null
sleep 1

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

# Start first client (Alice)
echo "Starting client 1 (Alice)..."
dotnet run --project "$CLIENT_PROJECT" --no-build -- \
    --server "$SERVER_URL" \
    --email "$USER1_EMAIL" \
    --password "$USER1_PASSWORD" \
    --title "Snacka - Alice" \
    --profile alice &
CLIENT1_PID=$!
echo "Client 1 PID: $CLIENT1_PID"

sleep 3

# Start second client (Bob)
echo "Starting client 2 (Bob)..."
dotnet run --project "$CLIENT_PROJECT" --no-build -- \
    --server "$SERVER_URL" \
    --email "$USER2_EMAIL" \
    --password "$USER2_PASSWORD" \
    --title "Snacka - Bob" \
    --profile bob &
CLIENT2_PID=$!
echo "Client 2 PID: $CLIENT2_PID"

echo ""
echo "=== Clients started ==="
echo "Client 1: PID $CLIENT1_PID (Alice)"
echo "Client 2: PID $CLIENT2_PID (Bob)"
echo ""
echo "Press Ctrl+C to stop all clients"
echo ""

# Cleanup on exit
cleanup() {
    echo ""
    echo "Shutting down clients..."
    kill $CLIENT1_PID $CLIENT2_PID 2>/dev/null
    exit 0
}

trap cleanup SIGINT SIGTERM

# Wait for all client processes
wait
