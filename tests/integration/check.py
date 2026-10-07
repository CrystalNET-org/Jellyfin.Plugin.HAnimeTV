#!/usr/bin/env python3
"""End-to-end check of the plugin against a running Jellyfin and fake-hanime.py.

Expects Jellyfin prepared by prepare.sh, with settings of version 0.1 (hanime.tv only).
Creates users, selects them in the plugin's settings and checks, through Jellyfin's API as
each user, that:

- the plugin loads, moves the old settings into its Hentai provider, merges the catalogs
  of hanime.tv (signed the way hanime.tv expects), oppai.stream and Hentai Haven into .strm,
  NFO and subtitle files (an episode several have from the first of them) and creates the
  shows library,
- Jellyfin's scan turns them into series and episodes with the NFO's metadata,
- only the selected user sees the library; Jellyfin itself refuses its videos to everyone
  else (by id and for playback), administrators included,
- the users' policies follow the selection, also after an administrator grants all
  libraries again and for users created later,
- an episode plays: through Jellyfin's remux and directly from the stream link, as browsers
  do; the stream comes from the sealed handshake and every request reaches hanime.tv with
  the plugin's headers; links without the token or with forged URLs are refused,
- an oppai.stream episode plays as an MP4 file with ranges, with its subtitles next to it,
- a Hentai Haven episode plays through its player's API, with the site's headers,
- hidden genres disappear from the library,
- Pornhub, as a channel, shows only for its selected user and plays (HLS, and MP4 files
  with ranges) with the headers Pornhub's CDN needs,
- the hentai provider switched to channel mode leaves the library and shows as a channel,
- the plugin logs no errors.

Environment: JELLYFIN_URL (default http://jellyfin:8096), LOG_DIR (fake-hanime.py's
request log), LIBRARY_DIR (the plugin's library folder), JELLYFIN_LOG_DIR (optional, for
the error check). Exits non-zero on failure.
"""
import base64
import glob
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = os.environ.get("JELLYFIN_URL", "http://jellyfin:8096").rstrip("/")
LOG_DIR = os.environ["LOG_DIR"]
LIBRARY_DIR = os.environ["LIBRARY_DIR"]
JELLYFIN_LOG_DIR = os.environ.get("JELLYFIN_LOG_DIR")
PLUGIN_ID = "1029189A-8A81-4419-8B08-78EB68071A0D"
failures = []

# Enough for Jellyfin to remux H.264/AAC into HLS
REMUX_PROFILE = {
    "MaxStreamingBitrate": 120000000,
    "DirectPlayProfiles": [{"Container": "mp4", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac"}],
    "TranscodingProfiles": [{"Container": "ts", "Type": "Video", "VideoCodec": "h264", "AudioCodec": "aac",
                             "Context": "Streaming", "Protocol": "hls", "MaxAudioChannels": "2",
                             "MinSegments": 1, "BreakOnNonKeyFrames": True}],
    "ContainerProfiles": [], "CodecProfiles": [], "SubtitleProfiles": [],
}


class Session:
    def __init__(self, device):
        self.device = device
        self.token = None
        self.user_id = None

    def auth(self):
        header = f'MediaBrowser Client="integration", Device="{self.device}", DeviceId="integration-{self.device}", Version="1.0"'
        return header + (f', Token="{self.token}"' if self.token else "")

    def call(self, method, path, body=None, raw=False, timeout=60):
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(BASE + path, data=data, method=method,
                                         headers={"Content-Type": "application/json", "Authorization": self.auth()})
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                payload = response.read()
                return response.status, payload if raw else (json.loads(payload) if payload else None)
        except urllib.error.HTTPError as e:
            return e.code, e.read()[:500]
        except Exception as e:  # connection refused while starting, timeouts
            return -1, str(e)

    def login(self, name, password):
        status, auth = self.call("POST", "/Users/AuthenticateByName", {"Username": name, "Pw": password})
        if status != 200:
            sys.exit(f"Login of {name} failed: {status} {auth}")
        self.token = auth["AccessToken"]
        self.user_id = auth["User"]["Id"]
        return self


def check(condition, message):
    print(("  ok    " if condition else "  FAIL  ") + message, flush=True)
    if not condition:
        failures.append(message)
    return condition


def wait_for(description, predicate, timeout=120, interval=2):
    deadline = time.time() + timeout
    while time.time() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(interval)
    failures.append(f"Timed out waiting for {description}")
    print(f"  FAIL  timed out waiting for {description}", flush=True)
    return None


def fake_log(kind=None):
    try:
        with open(os.path.join(LOG_DIR, "fake-hanime.log")) as log:
            entries = [json.loads(line) for line in log if line.strip()]
    except FileNotFoundError:
        return []
    return [e for e in entries if kind is None or e.get("kind") == kind]


def norm(guid):
    return guid.replace("-", "").lower()


def setup():
    admin = Session("admin")

    def started():
        status, info = admin.call("GET", "/System/Info/Public")
        return info if status == 200 and isinstance(info, dict) and info.get("Version") else None

    info = wait_for("Jellyfin to start", started, timeout=300)
    if not info:
        sys.exit(1)
    print(f"Jellyfin {info.get('Version')}", flush=True)
    if not info.get("StartupWizardCompleted"):
        admin.call("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
        admin.call("GET", "/Startup/User")
        admin.call("POST", "/Startup/User", {"Name": "admin", "Password": "admin"})
        admin.call("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
        admin.call("POST", "/Startup/Complete")
    wait_for("the admin login", lambda: admin.call("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})[0] == 200, timeout=60)
    return admin.login("admin", "admin")


def create_user(admin, name):
    status, user = admin.call("POST", "/Users/New", {"Name": name, "Password": name})
    if status != 200:
        sys.exit(f"Could not create {name}: {status} {user}")
    return user["Id"]


def policy(admin, user_id):
    return admin.call("GET", f"/Users/{user_id}")[1]["Policy"]


def has_library(policy_, library_id):
    return policy_["EnableAllFolders"] or library_id in [norm(f) for f in policy_.get("EnabledFolders") or []]


def update_config(admin, hentai=None, pornhub=None, **changes):
    """Changes the plugin's settings: top-level ones, and those of its providers."""
    status, config = admin.call("GET", f"/Plugins/{PLUGIN_ID}/Configuration")
    config.update(changes)
    config["Hentai"].update(hentai or {})
    config["Pornhub"].update(pornhub or {})
    status, _ = admin.call("POST", f"/Plugins/{PLUGIN_ID}/Configuration", config)
    return status in (200, 204)


def has_channel(policy_, channel_id):
    return policy_["EnableAllChannels"] or channel_id in [norm(c) for c in policy_.get("EnabledChannels") or []]


def channels(session):
    status, result = session.call("GET", f"/Channels?userId={session.user_id}")
    return {c["Name"]: c for c in (result or {}).get("Items", [])} if status == 200 and isinstance(result, dict) else {}


def channel_items(session, channel_id, folder_id=None):
    path = f"/Channels/{channel_id}/Items?userId={session.user_id}&fields=MediaSources,Overview"
    status, result = session.call("GET", path + (f"&folderId={folder_id}" if folder_id else ""), timeout=120)
    return (result or {}).get("Items", []) if status == 200 and isinstance(result, dict) else []


def playback_info(session, item_id):
    status, info = session.call("POST", f"/Items/{item_id}/PlaybackInfo?userId={session.user_id}",
                                {"DeviceProfile": REMUX_PROFILE, "UserId": session.user_id}, timeout=120)
    return status, (((info or {}).get("MediaSources") or [{}])[0] if status == 200 else {}), info


def plugin_status(admin):
    return admin.call("GET", "/HanimeTV/Status")[1] or {}


def views(session):
    status, result = session.call("GET", f"/UserViews?userId={session.user_id}")
    return [v.get("Name") for v in (result or {}).get("Items", [])] if status == 200 else []


def view_ids(session):
    status, result = session.call("GET", f"/UserViews?userId={session.user_id}")
    return [norm(v.get("Id", "")) for v in (result or {}).get("Items", [])] if status == 200 else []


def series(session):
    status, result = session.call("GET", f"/Items?userId={session.user_id}&includeItemTypes=Series&recursive=true&sortBy=SortName")
    return {i["Name"]: i for i in (result or {}).get("Items", [])} if status == 200 and isinstance(result, dict) else {}


def episodes(session, series_id):
    status, result = session.call("GET", f"/Shows/{series_id}/Episodes?userId={session.user_id}&fields=Overview,Genres,Studios,DateCreated,Path,MediaSources")
    return (result or {}).get("Items", []) if status == 200 and isinstance(result, dict) else []


def get(session, path, timeout=120, headers=None):
    """GET with the session's login, or without one for an absolute URL (as players do)."""
    if path.startswith("http"):
        try:
            with urllib.request.urlopen(urllib.request.Request(path, headers={"User-Agent": "Lavf/61", **(headers or {})}), timeout=timeout) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as e:
            return e.code, e.read()[:300]
    return session.call("GET", path, raw=True, timeout=timeout)


def play_hls(session, url, description, anonymous=False):
    """Fetches an HLS stream like a player: playlists down to the first segment. Anonymous
    players (Jellyfin's ffmpeg, browsers following a stream link) send no Jellyfin login."""
    url = urllib.parse.urljoin(BASE + "/", url)
    for _ in range(4):
        status, body = get(session, url if anonymous else url[len(BASE):])
        if status != 200:
            check(False, f"{description}: {url[len(BASE):][:70]}… is served ({status} {body[:200]})")
            return
        if b"#EXTM3U" not in body[:20]:
            check(len(body) > 10000, f"{description}: the playlists lead to a segment, which is served ({len(body)} bytes)")
            return
        entry = next((line for line in body.decode(errors="replace").splitlines() if line and not line.startswith("#")), None)
        if entry is None:
            check(False, f"{description}: the playlist lists something to play")
            return
        url = urllib.parse.urljoin(url, entry)
    check(False, f"{description}: no segment found")


def main():
    admin = setup()

    print("Plugin and library", flush=True)
    status, plugins = admin.call("GET", "/Plugins")
    plugin = next((p for p in plugins or [] if norm(p.get("Id", "")) == norm(PLUGIN_ID)), None)
    check(plugin is not None and plugin.get("Status") == "Active", f"the plugin is loaded and active ({plugin and plugin.get('Status')})")
    check(plugin is not None and plugin.get("Name") == "Adult Media", f"as Adult Media ({plugin and plugin.get('Name')})")
    config = admin.call("GET", f"/Plugins/{PLUGIN_ID}/Configuration")[1] or {}
    hentai_config = config.get("Hentai") or {}
    check(hentai_config.get("LibraryPath") == LIBRARY_DIR and hentai_config.get("SearchUrl", "").endswith("/api/v11/search_hvs")
          and hentai_config.get("Mode") == "Library" and "SearchUrl" not in config,
          f"the settings of version 0.1 moved into the Hentai provider ({hentai_config.get('LibraryPath')}, {hentai_config.get('Mode')})")
    state = wait_for("the first sync", lambda: (lambda s: s if s.get("LibraryId") and (s.get("LastReport") or {}).get("Episodes") else None)(plugin_status(admin).get("Hentai") or {}), timeout=180)
    if not state:
        print(json.dumps(plugin_status(admin), indent=1)[:2000])
        return
    library_id = norm(state["LibraryId"])
    check(state["LastReport"]["Series"] == 4 and state["LastReport"]["Episodes"] == 7,
          f"the merged catalog makes 4 series with 7 episodes ({state['LastReport']})")
    check(any(e.get("kind") == "catalog" for e in fake_log()) and not any(e.get("reason") == "bad app2 signature" for e in fake_log()),
          "hanime.tv's catalog was requested with a valid signature")
    check({e.get("page") for e in fake_log("haven-list")} == {1, 2}
          and {e.get("slug") for e in fake_log("haven-episode")} == {"test-show-episode-3", "haven-only-episode-1", "test-show-episode-2"},
          "Hentai Haven's list pages and episode pages were read")
    show_dir = os.path.join(LIBRARY_DIR, "Test Show", "Season 01")
    strm = os.path.join(show_dir, "Test Show S01E01.strm")
    check(os.path.isfile(strm) and os.path.isfile(strm[:-5] + ".nfo") and os.path.isfile(os.path.join(LIBRARY_DIR, "Test Show", "tvshow.nfo")),
          "the files are written: Test Show/Season 01/Test Show S01E01.strm with its NFO, and tvshow.nfo")
    stream_link = open(strm).read().strip() if os.path.isfile(strm) else ""
    check(stream_link.startswith("http://jellyfin:8096/HanimeTV/Stream/test-show-1/index.m3u8?token="), f"the .strm file holds the stream link ({stream_link[:70]}…)")
    second = os.path.join(show_dir, "Test Show S01E02.strm")
    check(os.path.isfile(second) and "/HanimeTV/Stream/test-show-2/" in open(second).read(), "the episode both sites have comes from hanime.tv")
    third = os.path.join(show_dir, "Test Show S01E03.strm")
    oppai_link = open(third).read().strip() if os.path.isfile(third) else ""
    check(oppai_link.startswith("http://jellyfin:8096/HanimeTV/OppaiStream/") and "/video.mp4?token=" in oppai_link,
          f"the episode oppai.stream and Hentai Haven have comes from oppai.stream, as a file ({oppai_link[:70]}…)")
    subtitle = os.path.join(show_dir, "Test Show S01E03.en.vtt")
    check(os.path.isfile(subtitle) and "Hello from oppai.stream" in open(subtitle).read(), "with its subtitles next to it")
    check(os.path.isfile(os.path.join(show_dir, "Test Show S01E04.strm")), "the episode only oppai.stream has joins the series")
    haven_strm = os.path.join(LIBRARY_DIR, "Haven Only", "Season 01", "Haven Only S01E01.strm")
    haven_link = open(haven_strm).read().strip() if os.path.isfile(haven_strm) else ""
    check(haven_link.startswith("http://jellyfin:8096/HanimeTV/HentaiHaven/") and "/video.mp4?token=" in haven_link,
          f"as do Hentai Haven's own series, as files ({haven_link[:70]}…)")
    check(os.path.isfile(os.path.join(LIBRARY_DIR, "Oppai Only", "Season 01", "Oppai Only S01E01.strm")), "and oppai.stream's")
    status, folders = admin.call("GET", "/Library/VirtualFolders")
    library = next((f for f in folders or [] if norm(f.get("ItemId", "")) == library_id), {})
    check(library.get("CollectionType") == "tvshows" and library.get("Name") == "Hentai", f"the shows library Hentai was created ({library.get('Name')}, {library.get('CollectionType')})")

    print("Access", flush=True)
    alice_id = create_user(admin, "alice")
    bob_id = create_user(admin, "bob")
    check(update_config(admin, hentai={"AllowedUsers": [alice_id]}), "alice is selected for hentai")
    wait_for("the policies to follow the selection",
             lambda: has_library(policy(admin, alice_id), library_id) and not has_library(policy(admin, bob_id), library_id)
             and not has_library(policy(admin, admin.user_id), library_id))
    check(has_library(policy(admin, alice_id), library_id), "alice's policy grants the library")
    check(not has_library(policy(admin, bob_id), library_id), "bob's policy does not")
    check(not has_library(policy(admin, admin.user_id), library_id), "nor the administrator's, who is not selected")

    alice = Session("alice").login("alice", "alice")
    bob = Session("bob").login("bob", "bob")
    check("Hentai" in views(alice), f"alice sees the library ({views(alice)})")
    check("Hentai" not in views(bob), "bob does not")
    check("Hentai" not in views(admin), "nor does the administrator")

    print("Series and episodes", flush=True)
    expected_shows = {"Test Show", "Hidden Video", "Haven Only", "Oppai Only"}
    shows = wait_for("the scan", lambda: (lambda s: s if expected_shows <= set(s) else None)(series(alice)), timeout=180) or {}
    check(set(shows) == expected_shows, f"the series are in the library ({sorted(shows)})")
    show = shows.get("Test Show")
    if not show:
        return
    status, show_item = alice.call("GET", f"/Items/{show['Id']}?userId={alice.user_id}")
    check(show_item.get("OfficialRating") == "XXX" and "Test Studio" in [s["Name"] for s in show_item.get("Studios", [])],
          f"the series has the NFO's metadata ({show_item.get('OfficialRating')}, {[s['Name'] for s in show_item.get('Studios', [])]})")
    check(show_item.get("ImageTags", {}).get("Primary") is not None, "and its cover")
    eps = episodes(alice, show["Id"])
    check([(e.get("IndexNumber"), e.get("Name")) for e in eps] == [(1, "Test Show 1"), (2, "Test Show 2"), (3, "Test Show 3"), (4, "Test Show 4")],
          f"its episodes from both sites are numbered ({[(e.get('IndexNumber'), e.get('Name')) for e in eps]})")
    episode = eps[0] if eps else None
    if not episode:
        return
    check(episode.get("Overview") == "The first episode." and "HD" in episode.get("Genres", []) and episode.get("PremiereDate", "").startswith("2023-07-22"),
          f"the episode has the NFO's metadata ({episode.get('Overview')!r}, {episode.get('Genres')}, {episode.get('PremiereDate')})")
    check(episode.get("DateCreated", "").startswith("2023-11-14"), f"its date added is the upload time ({episode.get('DateCreated')})")
    oppai_episode = eps[2] if len(eps) > 2 else {}
    check(oppai_episode.get("Overview") == "Test Show from oppai.stream." and "Oppai Studio" in [s["Name"] for s in oppai_episode.get("Studios", [])],
          f"the oppai.stream episode has the site's metadata ({oppai_episode.get('Overview')!r}, {[s['Name'] for s in oppai_episode.get('Studios', [])]})")
    haven_episode = next(iter(episodes(alice, shows["Haven Only"]["Id"])), {}) if "Haven Only" in shows else {}
    check(haven_episode.get("Overview") == "From Hentai Haven." and "Haven Studio" in [s["Name"] for s in haven_episode.get("Studios", [])]
          and haven_episode.get("PremiereDate", "").startswith("2024-03-01"),
          f"the Hentai Haven episode has the site's metadata ({haven_episode.get('Overview')!r}, {haven_episode.get('PremiereDate')})")
    hidden = next(iter(episodes(alice, shows["Hidden Video"]["Id"])), {})

    status, _ = bob.call("GET", f"/Items/{episode['Id']}?userId={bob.user_id}")
    check(status == 404, f"bob cannot open the episode by id ({status})")
    check(not series(bob), "nor list the series")
    status, _ = bob.call("POST", f"/Items/{episode['Id']}/PlaybackInfo?userId={bob.user_id}", {"DeviceProfile": REMUX_PROFILE})
    check(status in (403, 404), f"nor play it ({status})")

    print("Playback through Jellyfin", flush=True)
    status, source, info = playback_info(alice, episode["Id"])
    check(status == 200 and source.get("Path", "").startswith("http://jellyfin:8096/HanimeTV/Stream/test-show-1/"), f"the episode plays from its stream link ({status}, {source.get('Path', '')[:60]})")
    runtime = (source.get("RunTimeTicks") or 0) / 10_000_000
    check(25 <= runtime <= 35, f"Jellyfin probed the stream: {runtime:.1f}s")
    check(any(e.get("kind") == "handshake" and e.get("slug") == "test-show-1" for e in fake_log()), "the handshake was signed and sealed correctly")
    if source.get("TranscodingUrl"):
        play_hls(alice, source["TranscodingUrl"], "Jellyfin's remux")
        alice.call("DELETE", f"/Videos/ActiveEncodings?deviceId=integration-alice&playSessionId={info.get('PlaySessionId', '')}")
    else:
        check(False, "Jellyfin offers to remux the stream")

    print("Playback in a browser", flush=True)
    # Jellyfin's web client plays a remote source without required headers from its Path
    # when the browser can play it, so the stream link must work on its own
    check(source.get("IsRemote") and source.get("Protocol") == "Http" and not source.get("RequiredHttpHeaders"),
          "a browser that plays the episode directly gets the stream link")
    play_hls(alice, source.get("Path") or stream_link, "the stream link, without a login", anonymous=True)
    hls = [e for e in fake_log("hls") if "test-show-1" in e["path"]]
    check(hls and all("Mozilla" in e["user_agent"] and e["referer"] == "https://hanime.tv/" for e in hls),
          "every request reached hanime.tv with the plugin's headers")
    check(not any(e.get("reason") == "no browser User-Agent" for e in fake_log()), "and none was refused")

    print("oppai.stream", flush=True)
    if oppai_episode:
        status, source, info = playback_info(alice, oppai_episode["Id"])
        path = source.get("Path", "")
        check(status == 200 and path.startswith("http://jellyfin:8096/HanimeTV/OppaiStream/") and "/video.mp4?" in path,
              f"the oppai.stream episode plays from its stream link ({status}, {path[:60]})")
        runtime = (source.get("RunTimeTicks") or 0) / 10_000_000
        check(25 <= runtime <= 35, f"Jellyfin probed its file: {runtime:.1f}s")
        subtitles = [s for s in source.get("MediaStreams") or [] if s.get("Type") == "Subtitle"]
        check(subtitles and subtitles[0].get("IsExternal") and subtitles[0].get("Language") in ("en", "eng"),
              f"Jellyfin found its subtitles ({[(s.get('Language'), s.get('Codec'), s.get('IsExternal')) for s in subtitles]})")
        status, body = get(alice, path, headers={"Range": "bytes=0-99"}) if path else (0, b"")
        check(status == 206 and len(body) == 100 and body[4:8] == b"ftyp", f"its stream link serves ranges, without a login ({status}, {len(body)} bytes)")
        mp4 = fake_log("oppai-mp4")
        check(any(e.get("range") == "bytes=0-99" for e in mp4) and all(e["referer"].startswith("http://fake-hanime:8080/oppai") for e in mp4),
              "every request reached oppai.stream with its Referer and the ranges")
        check(not any(e.get("reason") == "no oppai.stream Referer" for e in fake_log()), "and none was refused")

    print("Hentai Haven", flush=True)
    if haven_episode:
        status, source, info = playback_info(alice, haven_episode["Id"])
        path = source.get("Path", "")
        check(status == 200 and path.startswith("http://jellyfin:8096/HanimeTV/HentaiHaven/") and "/video.mp4?" in path,
              f"the Hentai Haven episode plays from its stream link ({status}, {path[:60]})")
        runtime = (source.get("RunTimeTicks") or 0) / 10_000_000
        check(25 <= runtime <= 35, f"Jellyfin probed its file: {runtime:.1f}s")
        check(any(e.get("slug") == "haven-only-episode-1" for e in fake_log("nhplayer-page")), "through the episode's player at the video host")
        status, body = get(alice, path, headers={"Range": "bytes=0-99"}) if path else (0, b"")
        check(status == 206 and len(body) == 100 and body[4:8] == b"ftyp", f"its stream link serves ranges, without a login ({status}, {len(body)} bytes)")
        mp4 = fake_log("haven-mp4")
        check(mp4 and any(e.get("range") == "bytes=0-99" for e in mp4) and all(e["referer"].startswith("http://fake-hanime:8080/nhplayer/v/") for e in mp4),
              "every request reached the video host with its player as Referer, and the ranges")
        check(not any(e.get("reason") in ("no Hentai Haven Referer", "no nhplayer Referer") for e in fake_log()), "and none was refused")

    print("Stream links", flush=True)
    base_link = stream_link.split("?")[0]
    check(get(alice, base_link)[0] == 403, "a link without the token is refused")
    check(get(alice, base_link + "?token=wrong")[0] == 403, "a link with a wrong token is refused")
    token = stream_link.split("token=")[1]
    forged = base_link.replace("index.m3u8", "proxy") + f"/{token}/0123456789abcdef0123456789abcdef/aHR0cDovL2V4YW1wbGUuY29t/x.ts"
    check(get(alice, forged)[0] == 403, "a proxy link to a URL the plugin did not sign is refused")
    elsewhere = "http://jellyfin:8096/HanimeTV/HentaiHaven/" + base64.urlsafe_b64encode(b"http://example.com/watch/x/").decode().rstrip("=") + "/index.m3u8?token=" + token
    check(get(alice, elsewhere)[0] == 502, "a Hentai Haven link to another site is refused")

    print("Settings", flush=True)
    check(update_config(admin, hentai={"AllowedUsers": [alice_id], "HiddenTags": ["skipme"]}), "the genre skipme is hidden")
    wait_for("the hidden video's files to go", lambda: not os.path.exists(os.path.join(LIBRARY_DIR, "Hidden Video")), timeout=60)
    check(not os.path.exists(os.path.join(LIBRARY_DIR, "Hidden Video")), "its files are removed")
    gone = wait_for("the scan to remove it", lambda: "Hidden Video" not in series(alice), timeout=120)
    check(bool(gone), f"and its series is gone from the library ({sorted(series(alice))})")
    if hidden:
        check(alice.call("GET", f"/Items/{hidden['Id']}?userId={alice.user_id}")[0] == 404, "with its episode")

    print("Enforcement", flush=True)
    bob_policy = policy(admin, bob_id)
    bob_policy["EnableAllFolders"] = True
    admin.call("POST", f"/Users/{bob_id}/Policy", bob_policy)
    check(has_library(policy(admin, bob_id), library_id), "an administrator grants bob all libraries")
    status, result = admin.call("POST", "/HanimeTV/SyncAccess")
    check(status == 200 and "bob" in (result or {}).get("Changed", []), f"enforcing takes the library away again ({result})")
    check(not has_library(policy(admin, bob_id), library_id), "bob's policy no longer grants it")
    other = [norm(f["ItemId"]) for f in admin.call("GET", "/Library/VirtualFolders")[1] if norm(f["ItemId"]) != library_id]
    check(set(other) <= {norm(f) for f in policy(admin, bob_id).get("EnabledFolders") or []}, "while keeping all other libraries")

    carol_id = create_user(admin, "carol")
    wait_for("the new user's policy", lambda: not has_library(policy(admin, carol_id), library_id), timeout=30)
    check(not has_library(policy(admin, carol_id), library_id), "a new user does not get the library")

    print("Pornhub", flush=True)
    surfaces = {(s["Name"], s["IsChannel"]): norm(s["Id"]) for s in plugin_status(admin).get("Surfaces", [])}
    pornhub_id = surfaces.get(("Pornhub", True))
    hentai_channel_id = surfaces.get(("Hentai", True))
    check(pornhub_id and hentai_channel_id, f"the plugin has its channels ({sorted(surfaces)})")
    check(update_config(admin, pornhub={"Mode": "Channel", "AllowedUsers": [bob_id]}), "Pornhub is a channel for bob")
    wait_for("the channel policies", lambda: has_channel(policy(admin, bob_id), pornhub_id) and not has_channel(policy(admin, alice_id), pornhub_id))
    check(has_channel(policy(admin, bob_id), pornhub_id), "bob's policy grants the Pornhub channel")
    check(not has_channel(policy(admin, alice_id), pornhub_id) and not has_channel(policy(admin, admin.user_id), pornhub_id), "alice's and the administrator's do not")
    check(not has_channel(policy(admin, alice_id), hentai_channel_id), "nor the hentai channel, which is in library mode")
    channel = wait_for("bob's channel list", lambda: channels(bob).get("Pornhub"), timeout=60) or {}
    check("Pornhub" not in channels(alice) and "Pornhub" not in channels(admin), f"only bob sees it ({sorted(channels(alice))})")
    root = channel_items(bob, channel.get("Id", "")) if channel else []
    newest = next((f for f in root if f.get("Name") == "Newest"), None)
    check(newest is not None, f"its folders are listed ({[f.get('Name') for f in root]})")
    videos = {v["Name"]: v for v in channel_items(bob, channel["Id"], newest["Id"])} if newest else {}
    check(set(videos) == {"Pornhub HLS Video", "Pornhub MP4 Video"}, f"Newest lists the videos ({sorted(videos)})")
    hls_video = videos.get("Pornhub HLS Video")
    if hls_video:
        check(alice.call("GET", f"/Items/{hls_video['Id']}?userId={alice.user_id}")[0] in (403, 404), "alice cannot open its videos")
        status, source, _ = playback_info(bob, hls_video["Id"])
        check(status == 200 and source.get("Path", "").startswith("http://jellyfin:8096/HanimeTV/Pornhub/phhls1/index.m3u8"),
              f"an HLS video plays from its stream link ({status}, {source.get('Path', '')[:60]})")
        check(source.get("Container") == "hls" and 25 <= (source.get("RunTimeTicks") or 0) / 10_000_000 <= 35, f"probed as HLS ({source.get('Container')}, {source.get('RunTimeTicks')})")
        play_hls(bob, source.get("Path", ""), "its stream link, without a login", anonymous=True)
        hls = [e for e in fake_log("hls") if "ph-hls1" in e["path"]]
        check(hls and all(e.get("origin") == "http://fake-hanime:8080/ph" for e in hls), "every request reached Pornhub with its Origin and Referer")
    mp4_video = videos.get("Pornhub MP4 Video")
    if mp4_video:
        status, source, _ = playback_info(bob, mp4_video["Id"])
        path = source.get("Path", "")
        check(status == 200 and path.startswith("http://jellyfin:8096/HanimeTV/Pornhub/phmp4/video.mp4") and source.get("Container") == "mp4",
              f"an MP4-only video plays as a file ({status}, {path[:60]}, {source.get('Container')})")
        status, body = get(bob, path, headers={"Range": "bytes=0-99"}) if path else (0, b"")
        check(status == 206 and len(body) == 100 and body[4:8] == b"ftyp", f"with ranges, for seeking ({status}, {len(body)} bytes)")
        check(any(e.get("range") == "bytes=0-99" for e in fake_log("mp4")), "passed on to Pornhub")
    check(not any(e.get("reason") in ("no Pornhub headers", "no age cookies") for e in fake_log()), "no Pornhub request was refused")

    print("Hentai as a channel", flush=True)
    check(update_config(admin, hentai={"Mode": "Channel"}), "hentai is switched to channel mode")
    wait_for("alice's policies", lambda: has_channel(policy(admin, alice_id), hentai_channel_id) and not has_library(policy(admin, alice_id), library_id))
    # Channels are views too: the library is told apart by its id
    check(not has_library(policy(admin, alice_id), library_id) and library_id not in view_ids(alice), "alice loses the library")
    check(has_channel(policy(admin, alice_id), hentai_channel_id), "and gets the hentai channel")
    check(not has_channel(policy(admin, bob_id), hentai_channel_id), "bob does not")
    channel = wait_for("alice's channel list", lambda: channels(alice).get("Hentai"), timeout=60) or {}
    folders_ = {f["Name"]: f for f in channel_items(alice, channel["Id"])} if channel else {}
    letters = {f["Name"]: f for f in channel_items(alice, channel["Id"], folders_["Series A–Z"]["Id"])} if "Series A–Z" in folders_ else {}
    t = letters.get("T (1)")
    check(t is not None, f"its series are listed by letter ({sorted(letters)})")
    shows_ = channel_items(alice, channel["Id"], t["Id"]) if t else []
    check([s.get("Name") for s in shows_] == ["Test Show"], f"with one folder per series ({[s.get('Name') for s in shows_]})")
    eps = channel_items(alice, channel["Id"], shows_[0]["Id"]) if shows_ else []
    check([e.get("Name") for e in eps] == ["Test Show 1", "Test Show 2", "Test Show 3", "Test Show 4"], f"holding the episodes of all sites ({[e.get('Name') for e in eps]})")
    if len(eps) == 4:
        status, source, _ = playback_info(alice, eps[2]["Id"])
        check(status == 200 and source.get("Path", "").startswith("http://jellyfin:8096/HanimeTV/OppaiStream/") and source.get("Container") == "mp4",
              f"the oppai.stream episode plays from the channel as a file ({status}, {source.get('Path', '')[:60]}, {source.get('Container')})")
        subtitles = [s for s in source.get("MediaStreams") or [] if s.get("Type") == "Subtitle"]
        url = (subtitles[0].get("DeliveryUrl") or subtitles[0].get("Path") or "") if subtitles else ""
        check(subtitles and subtitles[0].get("IsExternal") and url.startswith("http://jellyfin:8096/HanimeTV/OppaiStream/"),
              f"with its subtitles, through the plugin ({[(s.get('Language'), s.get('DeliveryMethod'), (s.get('DeliveryUrl') or '')[:50]) for s in subtitles]})")
        if url:
            status, body = get(alice, url)
            check(status == 200 and b"Hello from oppai.stream" in body, f"which serves them without a login ({status})")

    check(update_config(admin, hentai={"AllowedUsers": []}), "alice is no longer selected")
    wait_for("alice's policy", lambda: not has_channel(policy(admin, alice_id), hentai_channel_id))
    check("Hentai" not in channels(alice), "and no longer sees the channel")
    if eps:
        status, _ = alice.call("GET", f"/Items/{eps[0]['Id']}?userId={alice.user_id}")
        check(status in (403, 404), f"nor its videos ({status})")

    if JELLYFIN_LOG_DIR:
        print("Log", flush=True)
        errors = []
        for path in glob.glob(os.path.join(JELLYFIN_LOG_DIR, "*.log")):
            with open(path, errors="replace") as log:
                errors += [line.strip() for line in log
                           if any(word in line.lower() for word in ("hanime", "adult media", "hentai", "pornhub")) and ("[ERR]" in line or "[FTL]" in line)]
        check(not errors, "the plugin logged no errors" + ("".join("\n        " + e for e in errors[:10])))


if __name__ == "__main__":
    main()
    if failures:
        print(f"\n{len(failures)} check(s) failed:", flush=True)
        for failure in failures:
            print("  - " + failure)
        sys.exit(1)
    print("\nAll checks passed", flush=True)
