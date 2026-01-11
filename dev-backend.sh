#!/bin/bash

# Development backend startup script - starts only the server
# Usage: ./dev-backend.sh

# Load environment variables from .env file if it exists
if [ -f .env ]; then
    set -a
    source .env
    set +a
fi

SERVER_URL="http://localhost:5117"
SERVER_PROJECT="src/Snacka.Server/Snacka.Server.csproj"

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

# Start server
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
echo "=== Backend started ==="
echo "Server: PID $SERVER_PID"
echo "URL: $SERVER_URL"
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
