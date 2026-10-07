#!/usr/bin/env python3
"""End-to-end check of the plugin against a running Jellyfin and fake-hanime.py.

Expects Jellyfin prepared by prepare.sh. Creates users, selects one in the plugin's
settings and checks, through Jellyfin's API as each user, that:

- the plugin loads and its channel exists,
- only the selected user sees the channel; Jellyfin itself refuses its videos to everyone
  else (by id and for playback), administrators included,
- the users' policies follow the selection, also after an administrator grants all
  channels again and for users created later,
- the folders list the catalog, signed the way hanime.tv expects, and hidden genres
  disappear from them,
- a video plays: the streams come from the sealed handshake, are probed, and Jellyfin's
  remux of the HLS stream returns segments (ffmpeg got the plugin's User-Agent),
- the plugin logs no errors.

Environment: JELLYFIN_URL (default http://jellyfin:8096), LOG_DIR (fake-hanime.py's
request log), JELLYFIN_LOG_DIR (optional, for the error check). Exits non-zero on failure.
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
JELLYFIN_LOG_DIR = os.environ.get("JELLYFIN_LOG_DIR")
PLUGIN_ID = "1029189A-8A81-4419-8B08-78EB68071A0D"
failures = []

# Enough for Jellyfin to remux H.264/AAC into HLS
DEVICE_PROFILE = {
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


def has_channel(policy_, channel_id):
    return policy_["EnableAllChannels"] or channel_id in [norm(c) for c in policy_.get("EnabledChannels") or []]


def update_config(admin, **changes):
    status, config = admin.call("GET", f"/Plugins/{PLUGIN_ID}/Configuration")
    config.update(changes)
    status, _ = admin.call("POST", f"/Plugins/{PLUGIN_ID}/Configuration", config)
    return status in (200, 204)


def channels(session):
    status, result = session.call("GET", f"/Channels?userId={session.user_id}")
    return [c for c in (result or {}).get("Items", []) if c.get("Name") == "hanime.tv"] if status == 200 else []


def children(session, parent_id, extra=""):
    status, result = session.call("GET", f"/Items?userId={session.user_id}&parentId={parent_id}{extra}")
    return result.get("Items", []) if status == 200 and isinstance(result, dict) else None


def main():
    admin = setup()

    print("Plugin", flush=True)
    status, plugins = admin.call("GET", "/Plugins")
    plugin = next((p for p in plugins or [] if norm(p.get("Id", "")) == norm(PLUGIN_ID)), None)
    check(plugin is not None and plugin.get("Status") == "Active", f"the plugin is loaded and active ({plugin and plugin.get('Status')})")
    status, plugin_status = admin.call("GET", "/HanimeTV/Status")
    if not check(status == 200, f"the status API answers ({status})"):
        return
    channel_id = norm(plugin_status["ChannelId"])

    print("Access", flush=True)
    alice_id = create_user(admin, "alice")
    bob_id = create_user(admin, "bob")
    check(update_config(admin, AllowedUsers=[alice_id]), "alice is selected in the settings")
    wait_for("the policies to follow the selection",
             lambda: not has_channel(policy(admin, bob_id), channel_id) and not has_channel(policy(admin, admin.user_id), channel_id))
    check(has_channel(policy(admin, alice_id), channel_id), "alice's policy grants the channel")
    bob_policy = policy(admin, bob_id)
    check(not has_channel(bob_policy, channel_id), "bob's policy does not")
    check(not has_channel(policy(admin, admin.user_id), channel_id), "nor the administrator's, who is not selected")

    alice = Session("alice").login("alice", "alice")
    bob = Session("bob").login("bob", "bob")
    check(len(channels(alice)) == 1, "alice sees the channel")
    check(not channels(bob), "bob does not")
    check(not channels(admin), "nor does the administrator")

    print("Folders", flush=True)
    root = children(alice, channel_id) or []
    names = sorted(i["Name"] for i in root)
    check(names == ["Genres", "Most liked", "Most viewed", "New releases", "Recently uploaded", "Series A–Z", "Studios"],
          f"the channel lists its folders ({names})")
    latest = next((i for i in root if i["Name"] == "Recently uploaded"), None)
    # Jellyfin sorts by name unless asked otherwise; the upload time is the date added
    videos = children(alice, latest["Id"], "&sortBy=DateCreated&sortOrder=Descending") if latest else []
    check([v["Name"] for v in videos or []] == ["Hidden Video", "Test Show 2", "Test Show 1"], f"sorted by date added, the newest uploads come first ({[v['Name'] for v in videos or []]})")
    check(any(e["kind"] == "catalog" for e in fake_log()) and not any(e.get("reason") == "bad app2 signature" for e in fake_log()),
          "the catalog was requested with a valid signature")
    video = next((v for v in videos or [] if v["Name"] == "Test Show 1"), None)
    if not check(video is not None, "the video is listed"):
        return
    check(video.get("SeriesName") == "Test Show" and video.get("OfficialRating") == "XXX", "it is an adults-only episode of its series")

    status, _ = bob.call("GET", f"/Items/{video['Id']}?userId={bob.user_id}")
    check(status == 404, f"bob cannot open the video by id ({status})")
    check(not children(bob, latest["Id"]), "nor list the channel's folders")
    check(len(children(alice, latest["Id"]) or []) == 3, "which does not empty them for alice")
    status, _ = bob.call("POST", f"/Items/{video['Id']}/PlaybackInfo?userId={bob.user_id}", {"DeviceProfile": DEVICE_PROFILE})
    check(status in (403, 404), f"nor play it ({status})")

    print("Playback", flush=True)
    status, info = alice.call("POST", f"/Items/{video['Id']}/PlaybackInfo?userId={alice.user_id}",
                              {"DeviceProfile": DEVICE_PROFILE, "UserId": alice.user_id}, timeout=120)
    sources = (info or {}).get("MediaSources", []) if status == 200 else []
    check(len(sources) == 1, f"one stream for a guest, no premium one ({[s.get('Name') for s in sources]}, {status} {info if status != 200 else ''})")
    if not sources:
        return
    source = sources[0]
    check(source.get("Path", "").endswith("/hls/test-show-1/index.m3u8"), f"the stream comes from the handshake ({source.get('Path')})")
    check(any(e["kind"] == "handshake" and e["slug"] == "test-show-1" for e in fake_log()), "the handshake was signed and sealed correctly")
    runtime = (source.get("RunTimeTicks") or 0) / 10_000_000
    check(25 <= runtime <= 35, f"the stream was probed: {runtime:.1f}s")
    check(not source.get("SupportsDirectPlay") and source.get("TranscodingUrl"), "Jellyfin remuxes or transcodes it")
    transcoding_url = source.get("TranscodingUrl", "")
    status, master = alice.call("GET", transcoding_url, raw=True, timeout=120)
    check(status == 200 and b"#EXTM3U" in master, f"the HLS master playlist is served ({status})")
    if status == 200:
        variant = next(line for line in master.decode().splitlines() if line and not line.startswith("#"))
        variant_url = urllib.parse.urljoin(BASE + transcoding_url, variant)[len(BASE):]
        status, playlist = alice.call("GET", variant_url, raw=True, timeout=120)
        segment = next((line for line in playlist.decode(errors="replace").splitlines() if line and not line.startswith("#")), None) if status == 200 else None
        if check(segment is not None, f"the variant playlist lists segments ({status})"):
            segment_url = urllib.parse.urljoin(BASE + variant_url, segment)[len(BASE):]
            status, data = alice.call("GET", segment_url, raw=True, timeout=180)
            check(status == 200 and len(data) > 10000, f"the first segment is served ({status}, {len(data) if status == 200 else data})")
    hls = [e for e in fake_log("hls") if "test-show-1" in e["path"]]
    check(hls and all("Mozilla" in e["user_agent"] for e in hls), "ffmpeg read the stream with the plugin's User-Agent")
    check(not any(e.get("reason") == "no browser User-Agent" for e in fake_log()), "and was never refused")
    alice.call("DELETE", f"/Videos/ActiveEncodings?deviceId=integration-alice&playSessionId={info.get('PlaySessionId', '')}")

    print("Settings", flush=True)
    check(update_config(admin, AllowedUsers=[alice_id], HiddenTags=["skipme"]), "the genre skipme is hidden")
    videos = children(alice, latest["Id"], "&sortBy=DateCreated&sortOrder=Descending") or []
    check([v["Name"] for v in videos] == ["Test Show 2", "Test Show 1"], f"and its video is gone ({[v['Name'] for v in videos]})")

    print("Enforcement", flush=True)
    bob_policy = policy(admin, bob_id)
    bob_policy["EnableAllChannels"] = True
    admin.call("POST", f"/Users/{bob_id}/Policy", bob_policy)
    check(has_channel(policy(admin, bob_id), channel_id), "an administrator grants bob all channels")
    status, result = admin.call("POST", "/HanimeTV/SyncAccess")
    check(status == 200 and "bob" in (result or {}).get("Changed", []), f"enforcing takes the channel away again ({result})")
    check(not has_channel(policy(admin, bob_id), channel_id), "bob's policy no longer grants it")

    carol_id = create_user(admin, "carol")
    wait_for("the new user's policy", lambda: not has_channel(policy(admin, carol_id), channel_id), timeout=30)
    check(not has_channel(policy(admin, carol_id), channel_id), "a new user does not get the channel")

    check(update_config(admin, AllowedUsers=[]), "alice is no longer selected")
    wait_for("alice's policy", lambda: not has_channel(policy(admin, alice_id), channel_id))
    check(not channels(alice), "and no longer sees the channel")
    status, _ = alice.call("GET", f"/Items/{video['Id']}?userId={alice.user_id}")
    check(status == 404, f"nor its videos ({status})")

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
