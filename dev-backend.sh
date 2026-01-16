#!/bin/bash

# Development backend startup script - starts only the server
# Usage: ./dev-backend.sh

# Load environment variables from .env file if it exists
if [ -f .env ]; then
    set -a
    source .env
    set +a
fi

SERVER_PORT="5117"
SERVER_URL="http://localhost:$SERVER_PORT"
SERVER_PROJECT="src/Snacka.Server/Snacka.Server.csproj"

# Get local IP for network access
LOCAL_IP=$(ifconfig 2>/dev/null | grep 'inet ' | grep -v '127.0.0.1' | head -1 | awk '{print $2}' || hostname -I 2>/dev/null | awk '{print $1}')

echo "=== Snacka Backend Startup ==="
echo ""

# Kill any existing Snacka server processes
echo "Stopping any existing Snacka server processes..."
pkill -f "dotnet.*Snacka.Server" 2>/dev/null
sleep 1

# Build server
echo "Building server..."
dotnet build "$SERVER_PROJECT" --verbosity quiet
if [ $? -ne 0 ]; then
    echo "Server build failed!"
    exit 1
fi

echo "Build complete."
echo ""

# Start server (bind to all interfaces for network access)
echo "Starting server..."
dotnet run --project "$SERVER_PROJECT" --no-build --urls "http://0.0.0.0:$SERVER_PORT" &
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
echo "=== Backend started ==="
echo "Server: PID $SERVER_PID"
echo ""
echo "Local:   $SERVER_URL"
if [ -n "$LOCAL_IP" ]; then
    echo "Network: http://$LOCAL_IP:$SERVER_PORT"
fi
echo ""
echo "Press Ctrl+C to stop"
echo ""

# Cleanup on exit
cleanup() {
    echo ""
    echo "Shutting down server..."
    kill $SERVER_PID 2>/dev/null
    exit 0
}

trap cleanup SIGINT SIGTERM

# Wait for server process
wait $SERVER_PID
