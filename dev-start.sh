#!/bin/bash

# Development startup script - starts server and two clients with different test accounts
# Usage: ./dev-start.sh

# Set VLC environment variables for audio playback on macOS
if [[ "$OSTYPE" == "darwin"* ]]; then
    export VLC_PLUGIN_PATH="/Applications/VLC.app/Contents/MacOS/plugins"
    export DYLD_LIBRARY_PATH="/Applications/VLC.app/Contents/MacOS/lib:$DYLD_LIBRARY_PATH"
fi

SERVER_URL="http://localhost:5117"
CLIENT_PROJECT="src/Snacka.Client/Snacka.Client.csproj"
SERVER_PROJECT="src/Snacka.Server/Snacka.Server.csproj"
DB_FILE="src/Snacka.Server/snacka.db"

# Test accounts (will be auto-registered if they don't exist)
USER1_EMAIL="alice@test.com"
USER1_PASSWORD="password123"
USER2_EMAIL="bob@test.com"
USER2_PASSWORD="password123"

echo "=== Snacka Development Startup ==="
echo ""

# Kill any existing Snacka processes
echo "Stopping any existing Snacka processes..."
pkill -f "dotnet.*Snacka" 2>/dev/null
sleep 1

# Build projects first
echo "Building projects..."
dotnet build "$SERVER_PROJECT" --verbosity quiet
if [ $? -ne 0 ]; then
    echo "Server build failed!"
    exit 1
fi

dotnet build "$CLIENT_PROJECT" --verbosity quiet
if [ $? -ne 0 ]; then
    echo "Client build failed!"
    exit 1
fi

echo "Build complete."
echo ""

# Start server in background
echo "Starting server..."
dotnet run --project "$SERVER_PROJECT" --no-build &
SERVER_PID=$!
echo "Server PID: $SERVER_PID"

# Wait for server to be ready
echo "Waiting for server to start..."
for i in {1..30}; do
    if curl -s "$SERVER_URL/api/health" > /dev/null 2>&1; then
        echo "Server is ready!"
        break
    fi
    sleep 0.5
done


# Check if server started successfully
if ! curl -s "$SERVER_URL/api/health" > /dev/null 2>&1; then
    echo "Server failed to start!"
    kill $SERVER_PID 2>/dev/null
    exit 1
fi

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
echo "=== All processes started ==="
echo "Server:   PID $SERVER_PID"
echo "Client 1: PID $CLIENT1_PID (Alice)"
echo "Client 2: PID $CLIENT2_PID (Bob)"
echo ""
echo "Press Ctrl+C to stop all processes"
echo ""

# Wait for any process to exit, then cleanup
cleanup() {
    echo ""
    echo "Shutting down..."
    kill $CLIENT1_PID $CLIENT2_PID $SERVER_PID 2>/dev/null
    exit 0
}

trap cleanup SIGINT SIGTERM

# Wait for all processes
wait
