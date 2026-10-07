#!/usr/bin/env python3
"""Stands in for hanime.tv in the integration test.

Serves what the plugin uses, and checks its requests the way hanime.tv does:

- GET  /api/v11/search_hvs   the catalog; needs a valid app2 signature
- POST /api/v11/handshake    a video's streams; needs a valid web2 signature and a sealed
                             token, answers with the sources sealed in the X-Token header
- GET  /hls/<slug>/...       the HLS streams prepared by prepare.sh; needs the plugin's
                             browser User-Agent, which Jellyfin must pass on to ffmpeg
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
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

MEDIA_DIR = os.environ["MEDIA_DIR"]
LOG = os.path.join(os.environ["LOG_DIR"], "fake-hanime.log")
PORT = int(os.environ.get("PORT", "8080"))
KEY = hashlib.sha256(b"htv-insecure-handshake-v1").digest()
AAD = b"htv-insecure-v1"
BASE = f"http://fake-hanime:{PORT}"

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
            file = os.path.realpath(os.path.join(MEDIA_DIR, path.lstrip("/")))
            if not file.startswith(os.path.realpath(MEDIA_DIR) + os.sep) or not os.path.isfile(file):
                self.log(status=404)
                return self.reply(404)
            types = {".m3u8": "application/vnd.apple.mpegurl", ".ts": "video/mp2t", ".jpg": "image/jpeg"}
            with open(file, "rb") as f:
                body = f.read()
            self.log(status=200, kind="hls" if path.startswith("/hls/") else "image")
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
    print(f"fake hanime.tv on port {PORT}", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
