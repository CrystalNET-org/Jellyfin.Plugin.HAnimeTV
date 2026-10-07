#!/bin/sh
# Prepares the integration test: Jellyfin's directories under $ROOT with the plugin
# installed and pointed at the fake hanime.tv (fake-hanime.py), and that server's media:
# an HLS stream per video and a cover image. The plugin writes its library to $ROOT/library.
#
#   ROOT=/path PLUGIN_DLL=…/Jellyfin.Plugin.HAnimeTV.dll sh prepare.sh
#
# FFMPEG runs the ffmpeg that writes the media; FAKE_HANIME is the fake server's address
# as Jellyfin sees it (default http://fake-hanime:8080), STREAM_BASE_URL Jellyfin's own
# (default http://jellyfin:8096).
set -eu
: "${ROOT:?ROOT must be set}" "${PLUGIN_DLL:?PLUGIN_DLL must be set}"
FFMPEG=${FFMPEG:-/usr/lib/jellyfin-ffmpeg/ffmpeg}
FAKE_HANIME=${FAKE_HANIME:-http://fake-hanime:8080}

mkdir -p "$ROOT/config/plugins/hanime.tv_0.0.0.0" "$ROOT/config/plugins/configurations" \
         "$ROOT/cache" "$ROOT/tmp" "$ROOT/logs" "$ROOT/media/images"
cp "$PLUGIN_DLL" "$ROOT/config/plugins/hanime.tv_0.0.0.0/"

# No users are selected yet: check.py creates them and selects one
cat > "$ROOT/config/plugins/configurations/Jellyfin.Plugin.HAnimeTV.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <EnforceAccess>true</EnforceAccess>
  <LibraryPath>$ROOT/library</LibraryPath>
  <StreamBaseUrl>${STREAM_BASE_URL:-http://jellyfin:8096}</StreamBaseUrl>
  <SearchUrl>$FAKE_HANIME/api/v11/search_hvs</SearchUrl>
  <HandshakeUrl>$FAKE_HANIME/api/v11/handshake</HandshakeUrl>
  <LoginUrl>$FAKE_HANIME/rapi/v7/sessions</LoginUrl>
  <StreamHost>$FAKE_HANIME</StreamHost>
</PluginConfiguration>
XML

# 30 seconds of H.264 and AAC in 2-second HLS segments, like hanime.tv's streams
for slug in test-show-1 test-show-2 hidden-video; do
  mkdir -p "$ROOT/media/hls/$slug"
  $FFMPEG -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1280x720:rate=24 -f lavfi -i sine=frequency=440 \
    -t 30 -c:v libx264 -preset ultrafast -g 48 -c:a aac -shortest \
    -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_filename "$ROOT/media/hls/$slug/segment%03d.ts" \
    "$ROOT/media/hls/$slug/index.m3u8"
done
$FFMPEG -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=400x560 -frames:v 1 "$ROOT/media/images/cover.jpg"

# Jellyfin and the fake server may run as different users
chmod -R a+rwX "$ROOT"
echo "Prepared $ROOT"
