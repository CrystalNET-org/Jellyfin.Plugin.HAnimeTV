#!/usr/bin/env python3
"""End-to-end check of the plugin against a running Jellyfin and fake-hanime.py.

Expects Jellyfin prepared by prepare.sh. Creates users, selects one in the plugin's
settings and checks, through Jellyfin's API as each user, that:

- the plugin loads, writes the catalog as .strm and NFO files (signed the way hanime.tv
  expects) and creates the shows library,
- Jellyfin's scan turns them into series and episodes with the NFO's metadata,
- only the selected user sees the library; Jellyfin itself refuses its videos to everyone
  else (by id and for playback), administrators included,
- the users' policies follow the selection, also after an administrator grants all
  libraries again and for users created later,
- an episode plays: through Jellyfin's remux and directly from the stream link, as browsers
  do; the stream comes from the sealed handshake and every request reaches hanime.tv with
  the plugin's headers; links without the token or with forged URLs are refused,
- hidden genres disappear from the library,
- the plugin logs no errors.

Environment: JELLYFIN_URL (default http://jellyfin:8096), LOG_DIR (fake-hanime.py's
request log), LIBRARY_DIR (the plugin's library folder), JELLYFIN_LOG_DIR (optional, for
the error check). Exits non-zero on failure.
"""
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


def update_config(admin, **changes):
    status, config = admin.call("GET", f"/Plugins/{PLUGIN_ID}/Configuration")
    config.update(changes)
    status, _ = admin.call("POST", f"/Plugins/{PLUGIN_ID}/Configuration", config)
    return status in (200, 204)


def plugin_status(admin):
    return admin.call("GET", "/HanimeTV/Status")[1] or {}


def views(session):
    status, result = session.call("GET", f"/UserViews?userId={session.user_id}")
    return [v.get("Name") for v in (result or {}).get("Items", [])] if status == 200 else []


def series(session):
    status, result = session.call("GET", f"/Items?userId={session.user_id}&includeItemTypes=Series&recursive=true&sortBy=SortName")
    return {i["Name"]: i for i in (result or {}).get("Items", [])} if status == 200 and isinstance(result, dict) else {}


def episodes(session, series_id):
    status, result = session.call("GET", f"/Shows/{series_id}/Episodes?userId={session.user_id}&fields=Overview,Genres,Studios,DateCreated,Path,MediaSources")
    return (result or {}).get("Items", []) if status == 200 and isinstance(result, dict) else []


def get(session, path, timeout=120):
    """GET with the session's login, or without one for an absolute URL (as players do)."""
    if path.startswith("http"):
        try:
            with urllib.request.urlopen(urllib.request.Request(path, headers={"User-Agent": "Lavf/61"}), timeout=timeout) as response:
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
    state = wait_for("the first sync", lambda: (lambda s: s if s.get("LibraryId") and (s.get("LastReport") or {}).get("Episodes") else None)(plugin_status(admin)), timeout=120)
    if not state:
        print(json.dumps(plugin_status(admin), indent=1)[:2000])
        return
    library_id = norm(state["LibraryId"])
    check(state["LastReport"]["Series"] == 2 and state["LastReport"]["Episodes"] == 3, f"the catalog makes 2 series with 3 episodes ({state['LastReport']})")
    check(any(e["kind"] == "catalog" for e in fake_log()) and not any(e.get("reason") == "bad app2 signature" for e in fake_log()),
          "the catalog was requested with a valid signature")
    strm = os.path.join(LIBRARY_DIR, "Test Show", "Season 01", "Test Show S01E01.strm")
    check(os.path.isfile(strm) and os.path.isfile(strm[:-5] + ".nfo") and os.path.isfile(os.path.join(LIBRARY_DIR, "Test Show", "tvshow.nfo")),
          "the files are written: Test Show/Season 01/Test Show S01E01.strm with its NFO, and tvshow.nfo")
    stream_link = open(strm).read().strip() if os.path.isfile(strm) else ""
    check(stream_link.startswith("http://jellyfin:8096/HanimeTV/Stream/test-show-1/index.m3u8?token="), f"the .strm file holds the stream link ({stream_link[:70]}…)")
    status, folders = admin.call("GET", "/Library/VirtualFolders")
    library = next((f for f in folders or [] if norm(f.get("ItemId", "")) == library_id), {})
    check(library.get("CollectionType") == "tvshows" and library.get("Name") == "hanime.tv", f"the shows library hanime.tv was created ({library.get('Name')}, {library.get('CollectionType')})")

    print("Access", flush=True)
    alice_id = create_user(admin, "alice")
    bob_id = create_user(admin, "bob")
    check(update_config(admin, AllowedUsers=[alice_id]), "alice is selected in the settings")
    wait_for("the policies to follow the selection",
             lambda: has_library(policy(admin, alice_id), library_id) and not has_library(policy(admin, bob_id), library_id)
             and not has_library(policy(admin, admin.user_id), library_id))
    check(has_library(policy(admin, alice_id), library_id), "alice's policy grants the library")
    check(not has_library(policy(admin, bob_id), library_id), "bob's policy does not")
    check(not has_library(policy(admin, admin.user_id), library_id), "nor the administrator's, who is not selected")

    alice = Session("alice").login("alice", "alice")
    bob = Session("bob").login("bob", "bob")
    check("hanime.tv" in views(alice), f"alice sees the library ({views(alice)})")
    check("hanime.tv" not in views(bob), "bob does not")
    check("hanime.tv" not in views(admin), "nor does the administrator")

    print("Series and episodes", flush=True)
    shows = wait_for("the scan", lambda: (lambda s: s if {"Test Show", "Hidden Video"} <= set(s) else None)(series(alice)), timeout=180) or {}
    check(set(shows) == {"Test Show", "Hidden Video"}, f"the series are in the library ({sorted(shows)})")
    show = shows.get("Test Show")
    if not show:
        return
    status, show_item = alice.call("GET", f"/Items/{show['Id']}?userId={alice.user_id}")
    check(show_item.get("OfficialRating") == "XXX" and "Test Studio" in [s["Name"] for s in show_item.get("Studios", [])],
          f"the series has the NFO's metadata ({show_item.get('OfficialRating')}, {[s['Name'] for s in show_item.get('Studios', [])]})")
    check(show_item.get("ImageTags", {}).get("Primary") is not None, "and its cover")
    eps = episodes(alice, show["Id"])
    check([(e.get("IndexNumber"), e.get("Name")) for e in eps] == [(1, "Test Show 1"), (2, "Test Show 2")],
          f"its episodes are numbered ({[(e.get('IndexNumber'), e.get('Name')) for e in eps]})")
    episode = eps[0] if eps else None
    if not episode:
        return
    check(episode.get("Overview") == "The first episode." and "HD" in episode.get("Genres", []) and episode.get("PremiereDate", "").startswith("2023-07-22"),
          f"the episode has the NFO's metadata ({episode.get('Overview')!r}, {episode.get('Genres')}, {episode.get('PremiereDate')})")
    check(episode.get("DateCreated", "").startswith("2023-11-14"), f"its date added is the upload time ({episode.get('DateCreated')})")
    hidden = next(iter(episodes(alice, shows["Hidden Video"]["Id"])), {})

    status, _ = bob.call("GET", f"/Items/{episode['Id']}?userId={bob.user_id}")
    check(status == 404, f"bob cannot open the episode by id ({status})")
    check(not series(bob), "nor list the series")
    status, _ = bob.call("POST", f"/Items/{episode['Id']}/PlaybackInfo?userId={bob.user_id}", {"DeviceProfile": REMUX_PROFILE})
    check(status in (403, 404), f"nor play it ({status})")

    print("Playback through Jellyfin", flush=True)
    status, info = alice.call("POST", f"/Items/{episode['Id']}/PlaybackInfo?userId={alice.user_id}",
                              {"DeviceProfile": REMUX_PROFILE, "UserId": alice.user_id}, timeout=120)
    source = ((info or {}).get("MediaSources") or [{}])[0] if status == 200 else {}
    check(status == 200 and source.get("Path", "").startswith("http://jellyfin:8096/HanimeTV/Stream/test-show-1/"), f"the episode plays from its stream link ({status}, {source.get('Path', '')[:60]})")
    runtime = (source.get("RunTimeTicks") or 0) / 10_000_000
    check(25 <= runtime <= 35, f"Jellyfin probed the stream: {runtime:.1f}s")
    check(any(e["kind"] == "handshake" and e["slug"] == "test-show-1" for e in fake_log()), "the handshake was signed and sealed correctly")
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

    print("Stream links", flush=True)
    base_link = stream_link.split("?")[0]
    check(get(alice, base_link)[0] == 403, "a link without the token is refused")
    check(get(alice, base_link + "?token=wrong")[0] == 403, "a link with a wrong token is refused")
    token = stream_link.split("token=")[1]
    forged = base_link.replace("index.m3u8", "proxy") + f"/{token}/0123456789abcdef0123456789abcdef/aHR0cDovL2V4YW1wbGUuY29t/x.ts"
    check(get(alice, forged)[0] == 403, "a proxy link to a URL the plugin did not sign is refused")

    print("Settings", flush=True)
    check(update_config(admin, AllowedUsers=[alice_id], HiddenTags=["skipme"]), "the genre skipme is hidden")
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

    check(update_config(admin, AllowedUsers=[]), "alice is no longer selected")
    wait_for("alice's policy", lambda: not has_library(policy(admin, alice_id), library_id))
    check("hanime.tv" not in views(alice), "and no longer sees the library")
    status, _ = alice.call("GET", f"/Items/{episode['Id']}?userId={alice.user_id}")
    check(status == 404, f"nor its episodes ({status})")

    if JELLYFIN_LOG_DIR:
        print("Log", flush=True)
        errors = []
        for path in glob.glob(os.path.join(JELLYFIN_LOG_DIR, "*.log")):
            with open(path, errors="replace") as log:
                errors += [line.strip() for line in log if "hanime" in line.lower() and ("[ERR]" in line or "[FTL]" in line)]
        check(not errors, "the plugin logged no errors" + ("".join("\n        " + e for e in errors[:10])))


if __name__ == "__main__":
    main()
    if failures:
        print(f"\n{len(failures)} check(s) failed:", flush=True)
        for failure in failures:
            print("  - " + failure)
        sys.exit(1)
    print("\nAll checks passed", flush=True)
