#!/bin/bash

# Development startup script - starts server and two clients with fresh database
# Usage: ./dev-start-clean.sh

SERVER_URL="http://localhost:5117"
CLIENT_PROJECT="src/Miscord.Client/Miscord.Client.csproj"
SERVER_PROJECT="src/Miscord.Server/Miscord.Server.csproj"
DB_FILE="src/Miscord.Server/miscord.db"

# Test accounts (will be auto-registered if they don't exist)
USER1_EMAIL="alice@test.com"
USER1_PASSWORD="password123"
USER2_EMAIL="bob@test.com"
USER2_PASSWORD="password123"

echo "=== Miscord Development Startup (Clean) ===="
echo ""

# Kill any existing Miscord processes
echo "Stopping any existing Miscord processes..."
pkill -f "dotnet.*Miscord" 2>/dev/null
sleep 1

# Remove old database file to start fresh
if [ -f "$DB_FILE" ]; then
    echo "Removing old database..."
    rm "$DB_FILE"
fi

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

# Seed the database with test data
echo "Seeding database..."
bash seed-db.sh "$DB_FILE"

echo ""

# Start first client (Alice)
echo "Starting client 1 (Alice)..."
dotnet run --project "$CLIENT_PROJECT" --no-build -- \
    --server "$SERVER_URL" \
    --email "$USER1_EMAIL" \
    --password "$USER1_PASSWORD" \
    --title "Miscord - Alice" \
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
    --title "Miscord - Bob" \
    --profile bob &
CLIENT2_PID=$!
echo "Client 2 PID: $CLIENT2_PID"

echo ""
echo "=== All processes started ===="
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
