#!/usr/bin/env python3
"""Stands in for hanime.tv, oppai.stream, Hentai Haven and Pornhub in the integration test.

Serves what the plugin uses, and checks its requests the way the sites do:

- GET  /api/v11/search_hvs   hanime.tv's catalog; needs a valid app2 signature
- POST /api/v11/handshake    a video's streams; needs a valid web2 signature and a sealed
                             token, answers with the sources sealed in the X-Token header
- GET  /haven/...            Hentai Haven (WordPress with the Madara theme): the list of
                             series, series and episode pages, the player page with its keys
- POST /haven/wp-content/plugins/player-logic/api.php
                             the player's API: the streams for the keys of the player page
- GET  /oppai/actions/search.php  oppai.stream's search: every episode, newest first
- GET  /oppai/watch?e=…      oppai.stream's episode pages, with "availableres" and subtitles
- GET  /oppai/media/…, /oppai/subs/…  its MP4 files (with ranges) and subtitles; need its Referer
- GET  /ph/webmasters/...    Pornhub's webmasters API (search, categories)
- GET  /ph/view_video.php    Pornhub's video pages with their flashvars; need the age cookies
- GET  /ph/video/get_media   Pornhub's list of MP4 files
- GET  /hls/<name>/...       the HLS streams prepared by prepare.sh; need the plugin's
                             browser User-Agent, which Jellyfin must pass on to ffmpeg, and
                             for Hentai Haven ("haven-…") its Referer, for Pornhub ("ph-…")
                             Pornhub's Origin and Referer (412 without, as Pornhub's CDN)
- GET  /media/<name>.mp4     MP4 files, with ranges; Pornhub's headers as above
- GET  /images/<name>        cover images

Every request is appended to $LOG_DIR/fake-hanime.log as a JSON line for check.py.

Environment: MEDIA_DIR (with hls/ and images/ from prepare.sh), LOG_DIR, PORT (8080).
Needs the cryptography package.
"""
import base64
import hashlib
import json
import os
import re
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

MEDIA_DIR = os.environ["MEDIA_DIR"]
LOG = os.path.join(os.environ["LOG_DIR"], "fake-hanime.log")
PORT = int(os.environ.get("PORT", "8080"))
KEY = hashlib.sha256(b"htv-insecure-handshake-v1").digest()
AAD = b"htv-insecure-v1"
BASE = f"http://fake-hanime:{PORT}"
HAVEN = f"{BASE}/haven"
PORNHUB = f"{BASE}/ph"
OPPAI = f"{BASE}/oppai"

# Two series episodes and a video tagged "skipme", which check.py hides
CATALOG = [
    {"id": 1, "name": "Test Show 1", "slug": "test-show-1", "brand": "Test Studio", "tags": ["hd", "plot"],
     "description": "<p>The first episode.</p>", "views": 300, "likes": 30, "dislikes": 3,
     "created_at_unix": 1700000000, "released_at_unix": 1690000000,
     "cover_url": f"{BASE}/images/cover.jpg", "poster_url": f"{BASE}/images/cover.jpg"},
    {"id": 2, "name": "Test Show 2", "slug": "test-show-2", "brand": "Test Studio", "tags": ["hd"],
     "description": "<p>The second episode.</p>", "views": 200, "likes": 20, "dislikes": 2,
     "created_at_unix": 1700100000, "released_at_unix": 1690100000,
     "cover_url": f"{BASE}/images/cover.jpg", "poster_url": f"{BASE}/images/cover.jpg"},
    {"id": 3, "name": "Hidden Video", "slug": "hidden-video", "brand": "Other Studio", "tags": ["skipme", "censored"],
     "description": "Tagged to be hidden.", "views": 100, "likes": 1, "dislikes": 0,
     "created_at_unix": 1700200000, "released_at_unix": 1690200000,
     "cover_url": f"{BASE}/images/cover.jpg", "poster_url": f"{BASE}/images/cover.jpg"},
]


# Hentai Haven: "Test Show" episodes 2 (also on hanime.tv, which wins) and 3 (only here), and
# a series of its own. The list's second page does not exist
HAVEN_SERIES = {
    "test-show": {"title": "Test Show", "episodes": [2, 3], "media": {2: "test-show-2", 3: "haven-test-show-3"}},
    "haven-only": {"title": "Haven Only", "episodes": [1], "media": {1: "haven-only-1"}},
}
HAVEN_KEYS = {}  # en -> media, as the player page hands them out

# oppai.stream, newest first: "Test Show" 3 (Hentai Haven has it too: oppai.stream wins) and 4,
# and a series of its own. Episode 3 has English subtitles
OPPAI_EPISODES = [("Test Show", 4), ("Oppai Only", 1), ("Test Show", 3)]
SUBTITLE = "WEBVTT\n\n00:00:01.000 --> 00:00:05.000\nHello from oppai.stream\n"


def oppai_search():
    cards = "".join(f"""
      <div class="episode-shown"><div class="in"><a href="#" exur="{OPPAI}/watch?e={name} {n}&f={n}">
        <img class="cover-img-in" src="{BASE}/images/cover.jpg"><div class="title-ep">{name} {n}</div></a></div></div>""" for name, n in OPPAI_EPISODES)
    return f"<html><body>{cards}</body></html>"


def oppai_episode(name, n):
    track = f'<track kind="captions" src="{OPPAI}/subs/{name.replace(" ", "-")}-{n}.vtt" srclang="en" label="English">' if n == 3 else ""
    res = json.dumps({"720": f"{OPPAI}/media/oppai.mp4?e={n}", "1080": f"{OPPAI}/media/oppai.mp4?e={n}&q=1080"})
    return f"""<html><body>
      <div class="episode-info"><h1>{name} Ep {n}</h1><h6><a class="red" href="#">Oppai Studio</a></h6></div>
      <video id="episode" poster="{BASE}/images/cover.jpg">{track}</video>
      <div class="description">{name} from oppai.stream. Watch {name} on Oppai Stream</div>
      <div class="tags"><a href="#">Uncensored</a><a href="#">HD</a></div>
      <script>var availableres = {res};</script></body></html>"""


PORNHUB_VIDEOS = [
    {"video_id": "phhls1", "title": "Pornhub HLS Video", "duration": "0:30", "views": "100", "rating": "90",
     "publish_date": "2026-01-02 03:04:05", "default_thumb": f"{BASE}/images/cover.jpg",
     "categories": [{"category": "Amateur"}], "tags": [{"tag_name": "test"}], "pornstars": [{"pornstar_name": "Someone"}]},
    {"video_id": "phmp4", "title": "Pornhub MP4 Video", "duration": "0:30", "views": "50", "rating": "80",
     "publish_date": "2026-01-01 03:04:05", "default_thumb": f"{BASE}/images/cover.jpg",
     "categories": [{"category": "HD"}], "tags": [], "pornstars": []},
]


def haven_listing():
    items = "".join(f"""
      <div class="row c-tabs-item__content">
        <div class="tab-thumb"><a href="{HAVEN}/watch/{slug}/" title="{s['title']}"><img data-src="{BASE}/images/cover.jpg"></a></div>
        <div class="tab-summary"><div class="post-title"><h3 class="h4"><a href="{HAVEN}/watch/{slug}/">{s['title']}</a></h3></div></div>
        <div class="tab-meta"><span class="font-meta chapter"><a href="{HAVEN}/watch/{slug}/episode-{s['episodes'][-1]}/">Episode {s['episodes'][-1]}</a></span></div>
      </div>""" for slug, s in HAVEN_SERIES.items())
    return f"<html><body><div class=\"c-tabs-item\">{items}</div></body></html>"


def haven_series(slug):
    s = HAVEN_SERIES[slug]
    episodes = "".join(f"""
      <li class="wp-manga-chapter"><a href="{HAVEN}/watch/{slug}/episode-{n}/">Episode {n}</a>
        <span class="chapter-release-date"><i>March {n}, 2024</i></span></li>""" for n in reversed(s["episodes"]))
    return f"""<html><head><meta property="og:title" content="{s['title']} - Hentai Haven"></head><body>
      <div class="post-title"><h1>{s['title']}</h1></div>
      <div class="summary_image"><a href="#"><img data-src="{BASE}/images/cover.jpg"></a></div>
      <div class="post-content_item"><div class="summary-heading"><h5>Studio</h5></div><div class="summary-content"><a href="#">Haven Studio</a></div></div>
      <div class="post-content_item"><div class="summary-heading"><h5>Genre(s)</h5></div><div class="summary-content"><div class="genres-content"><a href="#">Vanilla</a></div></div></div>
      <div class="description-summary"><div class="summary__content"><p>From Hentai Haven.</p></div></div>
      <ul class="main version-chap">{episodes}</ul></body></html>"""


def b64url(data):
    return base64.urlsafe_b64encode(data).decode().rstrip("=")


def from_b64url(text):
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


def seal(payload):
    iv = os.urandom(12)
    sealed = AESGCM(KEY).encrypt(iv, json.dumps(payload).encode(), AAD)
    envelope = {"v": 1, "alg": "AES-256-GCM", "iv": b64url(iv), "tag": b64url(sealed[-16:]), "data": b64url(sealed[:-16])}
    return b64url(json.dumps(envelope).encode())


def open_token(token):
    envelope = json.loads(from_b64url(token))
    plain = AESGCM(KEY).decrypt(from_b64url(envelope["iv"]), from_b64url(envelope["data"]) + from_b64url(envelope["tag"]), AAD)
    return json.loads(plain)


def recent(header):
    try:
        return abs(time.time() - int(header)) < 120
    except (TypeError, ValueError):
        return False


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log(self, **entry):
        entry.update(method=self.command, path=self.path, user_agent=self.headers.get("User-Agent", ""),
                     referer=self.headers.get("Referer", ""), time=time.time())
        with open(LOG, "a") as log:
            log.write(json.dumps(entry) + "\n")

    def reply_file(self, file, content_type):
        """A file, with a range if one is asked for, as CDNs serve them."""
        size = os.path.getsize(file)
        start, end = 0, size - 1
        requested = self.headers.get("Range", "")
        if requested.startswith("bytes="):
            first, _, last = requested[6:].partition("-")
            start = int(first) if first else size - int(last)
            end = int(last) if first and last else size - 1
        with open(file, "rb") as f:
            f.seek(start)
            body = f.read(end - start + 1)
        headers = {"Accept-Ranges": "bytes"}
        if requested:
            headers["Content-Range"] = f"bytes {start}-{end}/{size}"
        self.reply(206 if requested else 200, body, content_type, headers)

    def reply(self, status, body=b"", content_type="application/json", headers=None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def deny(self, reason):
        self.log(status=403, reason=reason)
        self.reply(403, json.dumps({"error": reason}).encode())

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        query = urllib.parse.parse_qs(self.path.split("?", 1)[1] if "?" in self.path else "", keep_blank_values=True)
        html = "text/html; charset=utf-8"

        # Hentai Haven
        if path == "/haven/" and query.get("post_type") == ["wp-manga"]:
            self.log(status=200, kind="haven-list")
            return self.reply(200, haven_listing().encode(), html)
        if path.startswith("/haven/watch/"):
            parts = path[len("/haven/watch/"):].strip("/").split("/")
            series = HAVEN_SERIES.get(parts[0])
            if series and len(parts) == 1:
                self.log(status=200, kind="haven-series", slug=parts[0])
                return self.reply(200, haven_series(parts[0]).encode(), html)
            if series and len(parts) == 2 and parts[1].startswith("episode-") and int(parts[1][8:]) in series["episodes"]:
                self.log(status=200, kind="haven-episode")
                player = f"{HAVEN}/wp-content/plugins/player-logic/player.php?data={parts[0]}-{parts[1][8:]}"
                return self.reply(200, f'<html><body><div class="player_logic_item"><iframe src="{player}"></iframe></div></body></html>'.encode(), html,
                                  {"Set-Cookie": "haven_session=1; path=/"})
        if path == "/haven/wp-content/plugins/player-logic/player.php":
            slug, _, number = query.get("data", [""])[0].rpartition("-")
            media = HAVEN_SERIES.get(slug, {}).get("media", {}).get(int(number or 0))
            if not media:
                self.log(status=404)
                return self.reply(404)
            en = b64url(os.urandom(12))
            HAVEN_KEYS[en] = media
            self.log(status=200, kind="haven-player")
            return self.reply(200, f"<html><body><script type=\"text/javascript\">var en = '{en}';\nvar iv = 'iv-{en}';</script></body></html>".encode(), html)

        # oppai.stream
        if path == "/oppai/actions/search.php":
            self.log(status=200, kind="oppai-search")
            return self.reply(200, (oppai_search() if query.get("page") == ["1"] else "<html></html>").encode(), html)
        if path == "/oppai/watch":
            name, _, n = query.get("e", [""])[0].rpartition(" ")
            if (name, int(n or 0)) not in OPPAI_EPISODES or query.get("f") != [n]:
                self.log(status=404)
                return self.reply(404, b"<html>gone</html>", html)
            self.log(status=200, kind="oppai-episode", episode=f"{name} {n}")
            return self.reply(200, oppai_episode(name, int(n)).encode(), html)
        if path.startswith("/oppai/media/") or path.startswith("/oppai/subs/"):
            if not self.headers.get("Referer", "").startswith(OPPAI):
                return self.deny("no oppai.stream Referer")
            if path.startswith("/oppai/subs/"):
                self.log(status=200, kind="oppai-subtitle")
                return self.reply(200, SUBTITLE.encode(), "text/vtt")
            self.log(status=206 if self.headers.get("Range") else 200, kind="oppai-mp4", range=self.headers.get("Range", ""))
            return self.reply_file(os.path.join(MEDIA_DIR, "media", "ph-mp4.mp4"), "video/mp4")

        # Pornhub
        if path == "/ph/webmasters/search":
            self.log(status=200, kind="ph-search")
            if query.get("page") != ["1"]:
                return self.reply(200, json.dumps({"code": "2001", "message": "No Videos found!"}).encode())
            videos = [v for v in PORNHUB_VIDEOS if not query.get("category", [""])[0] or query["category"][0] in [c["category"] for c in v["categories"]]]
            return self.reply(200, json.dumps({"videos": videos}).encode())
        if path == "/ph/webmasters/categories":
            self.log(status=200, kind="ph-categories")
            return self.reply(200, json.dumps({"categories": [{"id": 1, "category": "Amateur"}, {"id": 2, "category": "HD"}]}).encode())
        if path == "/ph/view_video.php":
            if "age_verified=1" not in self.headers.get("Cookie", ""):
                return self.deny("no age cookies")
            viewkey = query.get("viewkey", [""])[0]
            definitions = {
                "phhls1": [{"videoUrl": f"{BASE}/hls/ph-hls1/index.m3u8", "quality": "720", "format": "hls"}],
                "phmp4": [{"videoUrl": f"{PORNHUB}/video/get_media?s=phmp4", "quality": [], "format": "mp4"}],
            }.get(viewkey)
            if definitions is None:
                self.log(status=404)
                return self.reply(404, b"<html>gone</html>", html)
            self.log(status=200, kind="ph-page", viewkey=viewkey)
            flashvars = json.dumps({"mediaDefinitions": definitions})
            return self.reply(200, f"<html><script>var flashvars_1234 = {flashvars};</script></html>".encode(), html)
        if path == "/ph/video/get_media":
            self.log(status=200, kind="ph-media-list")
            return self.reply(200, json.dumps([{"videoUrl": f"{BASE}/media/ph-mp4.mp4", "quality": "480", "format": "mp4"}]).encode())

        if path.startswith("/media/"):
            if self.headers.get("Origin") != PORNHUB or not self.headers.get("Referer", "").startswith(PORNHUB):
                self.log(status=412, reason="no Pornhub headers")
                return self.reply(412)
            file = os.path.realpath(os.path.join(MEDIA_DIR, path.lstrip("/")))
            if not file.startswith(os.path.realpath(MEDIA_DIR) + os.sep) or not os.path.isfile(file):
                self.log(status=404)
                return self.reply(404)
            self.log(status=206 if self.headers.get("Range") else 200, kind="mp4", range=self.headers.get("Range", ""))
            return self.reply_file(file, "video/mp4")
        if path == "/api/v11/search_hvs":
            claim = self.headers.get("X-Claim")
            expected = hashlib.sha256(f"9944822{claim}8{claim}113".encode()).hexdigest()
            if self.headers.get("X-Signature-Version") != "app2" or not recent(claim) or self.headers.get("X-Signature") != expected:
                return self.deny("bad app2 signature")
            self.log(status=200, kind="catalog")
            # As hanime.tv answers: the videos under "data", next to the site's ads
            return self.reply(200, json.dumps({"data": CATALOG, "ads": {"all-nav-link-1": {"href": "https://example.com"}}}).encode())

        if path.startswith("/hls/") or path.startswith("/images/"):
            if path.startswith("/hls/") and "Mozilla" not in self.headers.get("User-Agent", ""):
                return self.deny("no browser User-Agent")
            if path.startswith("/hls/haven-") and not self.headers.get("Referer", "").startswith(HAVEN):
                return self.deny("no Hentai Haven Referer")
            if path.startswith("/hls/ph-") and (self.headers.get("Origin") != PORNHUB or not self.headers.get("Referer", "").startswith(PORNHUB)):
                self.log(status=412, reason="no Pornhub headers")
                return self.reply(412)
            file = os.path.realpath(os.path.join(MEDIA_DIR, path.lstrip("/")))
            if not file.startswith(os.path.realpath(MEDIA_DIR) + os.sep) or not os.path.isfile(file):
                self.log(status=404)
                return self.reply(404)
            types = {".m3u8": "application/vnd.apple.mpegurl", ".ts": "video/mp2t", ".jpg": "image/jpeg"}
            with open(file, "rb") as f:
                body = f.read()
            self.log(status=200, kind="hls" if path.startswith("/hls/") else "image", origin=self.headers.get("Origin", ""))
            return self.reply(200, body, types.get(os.path.splitext(file)[1], "application/octet-stream"))

        self.log(status=404)
        self.reply(404)

    do_HEAD = do_GET

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length)
        if self.path.split("?", 1)[0] == "/haven/wp-content/plugins/player-logic/api.php":
            # multipart/form-data with action, a (the player page's en) and b (its iv)
            fields = dict(re.findall(rb'name="?([^";\r\n]+)"?\r\n(?:[^\r\n]+\r\n)*\r\n(.*?)\r\n--', body, re.S))
            en = fields.get(b"a", b"").decode()
            if fields.get(b"action") != b"zarat_get_data_player_ajax" or en not in HAVEN_KEYS or fields.get(b"b", b"").decode() != "iv-" + en:
                return self.deny("bad player keys")
            media = HAVEN_KEYS[en]
            self.log(status=200, kind="haven-api", media=media, cookie=self.headers.get("Cookie", ""))
            sources = [{"src": f"{BASE}/hls/{media}/index.m3u8", "type": "application/x-mpegURL", "label": "720p"}]
            return self.reply(200, json.dumps({"status": True, "data": {"sources": sources}}).encode())
        if self.path.split("?", 1)[0] != "/api/v11/handshake":
            self.log(status=404)
            return self.reply(404)

        stime = self.headers.get("X-Time")
        expected = hashlib.sha256(f"{stime},Xkdi29,https://hanime.tv,mn2,{stime}".encode()).hexdigest()
        if self.headers.get("X-Signature-Version") != "web2" or not recent(stime) or self.headers.get("X-Signature") != expected:
            return self.deny("bad web2 signature")
        try:
            token = open_token(json.loads(body)["token"])
        except Exception as e:  # noqa: BLE001 - any malformed token is refused
            return self.deny(f"bad token: {e}")
        slug = token.get("slug")
        if token.get("directive") != "htv_player_handshake" or not recent(token.get("timestamp_unix")) \
                or not os.path.isdir(os.path.join(MEDIA_DIR, "hls", str(slug))):
            return self.deny("bad handshake")

        self.log(status=200, kind="handshake", slug=slug)
        sources = {"sources": [
            {"kind": "normal", "src": f"/hls/{slug}/index.m3u8", "label": "720p"},
            # Only for accounts: never offered to the guest
            {"kind": "premium", "src": f"/hls/{slug}/premium.m3u8", "height": 1080},
        ]}
        self.reply(200, b"{}", headers={"X-Token": seal(sources)})

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    print(f"fake hanime.tv, Hentai Haven and Pornhub on port {PORT}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
