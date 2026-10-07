#!/bin/sh
# Prepares the integration test: Jellyfin's directories under $ROOT with the plugin
# installed and pointed at the fake hanime.tv, oppai.stream, Hentai Haven and Pornhub
# (fake-hanime.py), and that server's media: an HLS stream per video, an MP4 file and a
# cover image. The plugin writes its library to $ROOT/library.
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

mkdir -p "$ROOT/config/plugins/Adult Media_0.1.0.0" "$ROOT/config/plugins/configurations" \
         "$ROOT/cache" "$ROOT/tmp" "$ROOT/logs" "$ROOT/media/images"
cp "$PLUGIN_DLL" "$ROOT/config/plugins/Adult Media_0.1.0.0/"

# An older version from before the rename to Adult Media, which Jellyfin keeps as another
# plugin: the plugin deletes it on start (here without its DLL, so that it does not load)
mkdir -p "$ROOT/config/plugins/hanime.tv_0.0.5.0"
cat > "$ROOT/config/plugins/hanime.tv_0.0.5.0/meta.json" <<JSON
{"guid":"1029189a-8a81-4419-8b08-78eb68071a0d","name":"hanime.tv","version":"0.0.5.0","targetAbi":"10.11.0.0","status":"Active","autoUpdate":true,"assemblies":[]}
JSON

# No users are selected yet: check.py creates them and selects them. The hanime.tv settings
# are those of version 0.1, which kept them at the top level: the plugin moves them into
# its Hentai section on start
cat > "$ROOT/config/plugins/configurations/Jellyfin.Plugin.HAnimeTV.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <AllowedUsers />
  <EnforceAccess>true</EnforceAccess>
  <HiddenTags />
  <LibraryPath>$ROOT/library</LibraryPath>
  <StreamBaseUrl>${STREAM_BASE_URL:-http://jellyfin:8096}</StreamBaseUrl>
  <SearchUrl>$FAKE_HANIME/api/v11/search_hvs</SearchUrl>
  <HandshakeUrl>$FAKE_HANIME/api/v11/handshake</HandshakeUrl>
  <LoginUrl>$FAKE_HANIME/rapi/v7/sessions</LoginUrl>
  <StreamHost>$FAKE_HANIME</StreamHost>
  <Hentai>
    <HentaiHavenUrl>$FAKE_HANIME/haven</HentaiHavenUrl>
    <OppaiStreamUrl>$FAKE_HANIME/oppai</OppaiStreamUrl>
  </Hentai>
  <Pornhub>
    <ApiUrl>$FAKE_HANIME/ph/webmasters</ApiUrl>
    <SiteUrl>$FAKE_HANIME/ph</SiteUrl>
  </Pornhub>
</PluginConfiguration>
XML

# 30 seconds of H.264 and AAC in 2-second HLS segments, like hanime.tv's streams
for slug in test-show-1 test-show-2 hidden-video ph-hls1; do
  mkdir -p "$ROOT/media/hls/$slug"
  $FFMPEG -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1280x720:rate=24 -f lavfi -i sine=frequency=440 \
    -t 30 -c:v libx264 -preset ultrafast -g 48 -c:a aac -shortest \
    -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_filename "$ROOT/media/hls/$slug/segment%03d.ts" \
    "$ROOT/media/hls/$slug/index.m3u8"
done
# Pornhub's and oppai.stream's MP4 files: the index first, so players can start before the end
mkdir -p "$ROOT/media/media"
$FFMPEG -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=854x480:rate=24 -f lavfi -i sine=frequency=440 \
  -t 30 -c:v libx264 -preset ultrafast -c:a aac -shortest -movflags +faststart "$ROOT/media/media/ph-mp4.mp4"
$FFMPEG -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=400x560 -frames:v 1 "$ROOT/media/images/cover.jpg"

# Jellyfin and the fake server may run as different users
chmod -R a+rwX "$ROOT"
echo "Prepared $ROOT"
