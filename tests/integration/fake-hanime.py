#!/usr/bin/env python3
"""Stands in for hanime.tv, oppai.stream, Hentai Haven and Pornhub in the integration test.

Serves what the plugin uses, and checks its requests the way the sites do:

- GET  /api/v11/search_hvs   hanime.tv's catalog; needs a valid app2 signature
- POST /api/v11/handshake    a video's streams; needs a valid web2 signature and a sealed
                             token, answers with the sources sealed in the X-Token header
- GET  /haven/, /haven/?page=N  Hentai Haven's list of every episode, newest first
- GET  /haven/watch/<slug>-episode-<n>/  its episode pages, with the player's frame
- GET  /nhplayer/v/<id>/     the video host's player page: its server links carry the video's
                             address ("address|expiry|signature" in base64)
- GET  /nhplayer/player.php  the server's player (names no address here)
- GET  /nhplayer/media/<name>.mp4  its MP4 files, with ranges; need the player page as Referer
- GET  /oppai/actions/search.php  oppai.stream's search: every episode, newest first
- GET  /oppai/watch?e=…      oppai.stream's episode pages, with "availableres" and subtitles
- GET  /oppai/media/…, /oppai/subs/…  its MP4 files (with ranges) and subtitles; need its Referer
- GET  /ph/webmasters/...    Pornhub's webmasters API (search, categories)
- GET  /ph/view_video.php    Pornhub's video pages with their flashvars; need the age cookies
- GET  /ph/video/get_media   Pornhub's list of MP4 files
- GET  /hls/<name>/...       the HLS streams prepared by prepare.sh; need the plugin's
                             browser User-Agent, which Jellyfin must pass on to ffmpeg, and
                             for Pornhub ("ph-…") Pornhub's Origin and Referer (412 without,
                             as Pornhub's CDN)
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


# Hentai Haven, newest first over two pages: "Test Show" 3 (oppai.stream has it too and wins),
# "Haven Only" 1 (only here) and "Test Show" 2 (hanime.tv has it too and wins)
HAVEN_PAGES = [[("test-show", "Test Show", 3), ("haven-only", "Haven Only", 1)], [("test-show", "Test Show", 2)]]
HAVEN_EPISODES = {f"{slug}-episode-{n}": (title, n) for page in HAVEN_PAGES for slug, title, n in page}
NHPLAYER = f"{BASE}/nhplayer"


# oppai.stream, newest first: "Test Show" 3 (Hentai Haven has it too: oppai.stream wins) and 4,
# and a series of its own. Episode 3 has English subtitles
OPPAI_EPISODES = [("Test Show", 4), ("Oppai Only", 1), ("Test Show", 3)]
SUBTITLE = "WEBVTT\n\n00:00:01.000 --> 00:00:05.000\nHello from oppai.stream\n"


def oppai_search():
    # As actions/search.php answers the site's search page: cards whose title is split into
    # the series and the episode's number
    cards = "".join(f"""
      <div class="in-grid episode-shown" idgt="{i}" folder="{name}" ep="{n}" name="{name}">
        <div class="in-main-gr" just-check="1"><a href="{OPPAI}/watch?e={name} {n}&amp;f={n}">
          <div class="cover-img"><img class="cover-img-in" src="{BASE}/images/cover.jpg"></div>
          <div class="wrap-ep-info"><object><h6 class="gray extra-line">By <a href="{OPPAI}/search?studio=Nur" class="gray">Nur</a></h6></object><h5 class="white bold title-ep"><font class="title inline">{name}</font> <font class="ep inline">{n}</font></h5></div>
        </a></div>
      </div>""" for i, (name, n) in enumerate(OPPAI_EPISODES))
    return f'<div style="position:absolute;opacity:0;" id="amount-full" amo="{len(OPPAI_EPISODES)}"></div>{cards}'


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


def haven_home(page):
    cards = "".join(f"""<a class="a_item" href="/haven/watch/{slug}-episode-{n}/"><div class="v_item"><div class="video_cover">"""
                    f"""<img alt="{title} Episode {n}" loading="lazy" class="lazy" src="/images/cover.jpg"/><div class="card_badges">"""
                    f"""<span class="card_badge">EP {n}</span></div></div><div class="video_title">{title} Episode {n}</div></div></a>"""
                    for slug, title, n in HAVEN_PAGES[page - 1])
    pages = "".join(f'<li class="page-item"><a class="page-link" href="/haven/?page={p}">{p}</a></li>' for p in range(1, len(HAVEN_PAGES) + 1))
    return f'<html><body><div class="sub_overview">{cards}</div><ul class="pagination">{pages}</ul></body></html>'


def haven_episode(slug):
    title, n = HAVEN_EPISODES[slug]
    return f"""<html><body><div class="video"><div class="left">
      <div class="watch-ad-top"><iframe name="spot_id_1" src="//ads.invalid/get/1"></iframe></div>
      <div class="player"><iframe allowfullscreen="" loading="lazy" src="{NHPLAYER}/v/{slug}/"></iframe></div>
      <div class="info_top"><h1 class="video_title">{title} Episode {n}</h1><div class="buttons"><div class="button_item like"><span class="ln">12</span></div></div></div>
      <div class="info_bottom"><div class="cover"><img alt="cover" class="lazy" src="/images/cover.jpg"/></div><div class="r_info_b"><div class="flex_wrap">
        <div class="r_item half"><span>Brand</span><span class="sub_r"><a href="/haven/brand/haven-studio/">Haven Studio</a></span></div>
        <div class="r_item half"><span>Series</span><span class="sub_r"><a href="/haven/series/{slug}/">{title}</a></span></div>
        <div class="r_item half"><span>Release Date</span><span class="sub_r">2024-03-0{n}</span></div>
        <div class="r_item half"><span>Upload Date</span><span class="sub_r">2024-04-0{n}</span></div></div></div></div>
      <div class="video_tags"><a href="/haven/genre/vanilla/">vanilla</a><div class="video_description"><p>From Hentai Haven.</p></div></div>
      </div></div></body></html>"""


def nhplayer_page(slug):
    vid = base64.b64encode(f"{NHPLAYER}/media/{slug}.mp4|1791477583|cb4b317cdc15fee3".encode()).decode()
    return f"""<html><body><div class="frame"><header class="header"><div class="servers"><ul>
      <li data-id="/nhplayer/player.php?vid={vid}&i=aW1n&type=">Main Player</li></ul></div></header>
      <iframe src="" allowFullScreen="true"></iframe></div></body></html>"""


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
        if path == "/haven/":
            page = int(query.get("page", ["1"])[0])
            if not 1 <= page <= len(HAVEN_PAGES):
                self.log(status=404)
                return self.reply(404, b"<html>Not found</html>", html)
            self.log(status=200, kind="haven-list", page=page)
            return self.reply(200, haven_home(page).encode(), html)
        if path.startswith("/haven/watch/"):
            slug = path[len("/haven/watch/"):].strip("/")
            if slug not in HAVEN_EPISODES:
                self.log(status=404)
                return self.reply(404, b"<html>Not found</html>", html)
            self.log(status=200, kind="haven-episode", slug=slug)
            return self.reply(200, haven_episode(slug).encode(), html)
        if path.startswith("/nhplayer/v/"):
            slug = path[len("/nhplayer/v/"):].strip("/")
            if slug not in HAVEN_EPISODES or not self.headers.get("Referer", "").startswith(f"{HAVEN}/watch/"):
                return self.deny("no Hentai Haven Referer")
            self.log(status=200, kind="nhplayer-page", slug=slug)
            return self.reply(200, nhplayer_page(slug).encode(), html)
        if path == "/nhplayer/player.php":
            self.log(status=200, kind="nhplayer-server")
            return self.reply(200, b'<html><body><div id="player"></div><script src="/nhplayer/app.js"></script></body></html>', html)
        if path.startswith("/nhplayer/media/"):
            if not self.headers.get("Referer", "").startswith(f"{NHPLAYER}/v/"):
                return self.deny("no nhplayer Referer")
            self.log(status=206 if self.headers.get("Range") else 200, kind="haven-mp4", range=self.headers.get("Range", ""))
            return self.reply_file(os.path.join(MEDIA_DIR, "media", "ph-mp4.mp4"), "video/mp4")

        # oppai.stream
        if path == "/oppai/actions/search.php":
            self.log(status=200, kind="oppai-search", ajax=self.headers.get("X-Requested-With", ""), ibt=query.get("ibt", [""])[0])
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
