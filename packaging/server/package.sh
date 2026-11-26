#!/usr/bin/env bash
# Builds the self-hosting bundle for one platform (#53): the server as one self-contained
# executable, the web client, LiveKit, start scripts and a README, zipped.
#
#   packaging/server/package.sh <dotnet-rid> <livekit-target> <out-dir>
#
# livekit-target is a release asset suffix (linux_amd64, linux_arm64, windows_amd64), or
# darwin_arm64 / darwin_amd64, which LiveKit doesn't publish: those are built from source, which
# needs Go and cgo, so a Mac.
# Expects the web client to be built already (client/ui/dist).
set -euo pipefail
RID=$1 LIVEKIT=$2 OUT=$3
LIVEKIT_VERSION=1.13.11
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
NAME="snacka-server-$RID"
DIR="$OUT/$NAME"
rm -rf "$DIR" && mkdir -p "$DIR"

dotnet publish "$ROOT/src/Snacka.Server" -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -o "$DIR"
rm -f "$DIR"/appsettings.Development.json
cp -R "$ROOT/client/ui/dist/." "$DIR/wwwroot/"

TMP=$(mktemp -d)
case "$LIVEKIT" in
  darwin_*)
    git clone -q --depth 1 --branch "v$LIVEKIT_VERSION" https://github.com/livekit/livekit.git "$TMP/livekit"
    (cd "$TMP/livekit" && CGO_ENABLED=1 GOOS=darwin GOARCH="${LIVEKIT#darwin_}" go build -trimpath -o "$DIR/livekit-server" ./cmd/server)
    ;;
  windows_*)
    curl -sSfL "https://github.com/livekit/livekit/releases/download/v$LIVEKIT_VERSION/livekit_${LIVEKIT_VERSION}_$LIVEKIT.zip" -o "$TMP/livekit.zip"
    unzip -q -o "$TMP/livekit.zip" livekit-server.exe -d "$DIR"
    ;;
  *)
    curl -sSfL "https://github.com/livekit/livekit/releases/download/v$LIVEKIT_VERSION/livekit_${LIVEKIT_VERSION}_$LIVEKIT.tar.gz" | tar -xz -C "$DIR" livekit-server
    ;;
esac
rm -rf "$TMP"

cp "$ROOT/packaging/server/README.txt" "$DIR/"
if [[ "$RID" == win-* ]]; then
  cp "$ROOT/packaging/server/start.cmd" "$DIR/"
elif [[ "$RID" == osx-* ]]; then
  cp "$ROOT/packaging/server/start.command" "$DIR/"
else
  cp "$ROOT/packaging/server/start.sh" "$DIR/"
fi

(cd "$OUT" && rm -f "$NAME.zip" && zip -qr "$NAME.zip" "$NAME")
echo "$OUT/$NAME.zip"
