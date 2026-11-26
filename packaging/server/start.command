#!/bin/sh
# Starts the Snacka server from this folder. Everything it stores (database, uploads, keys)
# goes in ./data; back up that folder to back up the server.
cd "$(dirname "$0")" || exit 1
export SNACKA_DATA="${SNACKA_DATA:-$PWD/data}"
exec ./Snacka.Server --urls "${SNACKA_URLS:-http://0.0.0.0:5117}" "$@"
