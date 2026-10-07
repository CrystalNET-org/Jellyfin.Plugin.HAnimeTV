#!/bin/sh
# Starts Jellyfin with all its directories under $ROOT.
set -e
: "${ROOT:?ROOT must be set}"
export JELLYFIN_DATA_DIR="$ROOT/config" JELLYFIN_CONFIG_DIR="$ROOT/config/config" \
       JELLYFIN_LOG_DIR="$ROOT/config/log" JELLYFIN_CACHE_DIR="$ROOT/cache" \
       XDG_CACHE_HOME="$ROOT/cache" TMPDIR="$ROOT/tmp"
exec /jellyfin/jellyfin
