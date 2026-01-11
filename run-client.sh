#!/bin/bash
# Launcher script for Snacka Client with VLC support on macOS

# Set VLC environment variables before starting .NET
if [[ "$OSTYPE" == "darwin"* ]]; then
    export VLC_PLUGIN_PATH="/Applications/VLC.app/Contents/MacOS/plugins"
    export DYLD_LIBRARY_PATH="/Applications/VLC.app/Contents/MacOS/lib:$DYLD_LIBRARY_PATH"
fi

# Run the client with any passed arguments
dotnet run --project src/Snacka.Client/Snacka.Client.csproj -- "$@"
