# Contributing to the Adult Media plugin

Bug reports, ideas and pull requests are welcome.

- **Bugs:** open an [issue](https://github.com/CrystalNET-org/Jellyfin.Plugin.HAnimeTV/issues)
  with the steps to reproduce, the Jellyfin and plugin versions, the result of *Test* and the
  relevant lines from Jellyfin's log (search for `hanime.tv`, `oppai.stream`, `Hentai Haven`, `Pornhub` and
  `Adult Media`).
- **Pull requests:** against `main`. Keep them focused, and update the README when behaviour
  or settings change.

## Repository layout

```
Jellyfin.Plugin.HAnimeTV/
├── Jellyfin.Plugin.HAnimeTV/
│   ├── Plugin.cs                    # plugin entry, settings page registration
│   ├── PluginServiceRegistrator.cs  # registers the clients, channels, library sync and access enforcement
│   ├── Hanime/                      # hanime.tv client: catalog, stream handshake, login, signatures
│   ├── OppaiStream/                 # oppai.stream client: search, episode pages, streams, subtitles
│   ├── HentaiHaven/                 # Hentai Haven client: series list and pages, the player's API
│   ├── Hentai/                      # the merged hentai catalog and its videos
│   ├── Pornhub/                     # Pornhub client: webmasters API, video pages
│   ├── Channels/                    # the providers as channels
│   ├── Library/                     # writes the .strm/NFO files, creates and scans the library
│   ├── Streaming/                   # stream links, HLS proxy: rewrites playlists, signs their URLs
│   ├── Access/                      # enforces the user selections through Jellyfin's user policies
│   ├── Controllers/                 # settings page API (status, test, sync, access) and stream endpoints
│   ├── Configuration/               # plugin settings, a section per provider
│   ├── Images/                      # the channels' images
│   └── Web/config.html              # settings page
├── Jellyfin.Plugin.HAnimeTV.Tests/  # unit tests
├── tests/integration/               # end-to-end test in the real Jellyfin image (CI)
├── images/                          # catalog image and README screenshots
├── manifest.json                    # plugin repository file for Jellyfin
├── scripts/
│   ├── next-release-tag.sh          # computes the next patch tag
│   └── update_manifest.py           # adds a release to manifest.json
├── .woodpecker/                     # CI pipelines
└── renovate.json                    # dependency updates
```

## How it works

The plugin has two providers, each shown as a channel or (hentai only) a shows library, for
the users selected for it (`Configuration/ProviderSettings.cs`):

- **Hentai** merges the catalogs of hanime.tv, oppai.stream and Hentai Haven
  (`Hentai/HentaiCatalog.cs`): series are matched by name without case, spaces and punctuation,
  and an episode several sites have comes from the first of them in that order.
- **Pornhub** is a channel over Pornhub's public webmasters API.

### hanime.tv

hanime.tv has no public API; the plugin does what its site does:

- **Catalog:** `GET /api/v11/search_hvs` returns all videos at once (under `data`, next to the
  site's ads). It needs the app signature (`X-Signature-Version: app2`, `X-Claim`,
  `X-Signature`).
- **Streams:** `POST /api/v11/handshake` with a token sealed in hanime.tv's "insecure message"
  envelope (AES-256-GCM, key and associated data are fixed labels) and the web signature
  (`X-Signature-Version: web2`, `X-Time`, `X-Signature`). The answer's `X-Token` header holds the
  HLS sources, sealed the same way. Their links expire and need a browser's `User-Agent` and
  `Referer`.
- **Login** (optional): `POST /rapi/v7/sessions` with the app signature; the session token is
  sent with the handshake and unlocks premium sources.

All of that is in `Hanime/`; when hanime.tv changes something, that is where to look.

### oppai.stream

oppai.stream is read the way its [Aniyomi extension](https://github.com/Kohi-den/extensions-source/tree/main/src/en/oppaistream)
reads it (`OppaiStream/`):

- **Catalog:** `actions/search.php?text=&order=uploaded&page=N&limit=36&genres=&blacklist=&studio=&ibt=0&swa=0`
  lists every episode, newest first, as cards (`div.episode-shown`) linking to the episode's
  page (`watch?e=<name>…`), the title as `<h5 class="title-ep"><font class="title">Name</font>
  <font class="ep">N</font></h5>`. It is asked for as the site's search page (`search.php?a=recent`)
  asks for it: every parameter, `X-Requested-With: XMLHttpRequest`, that page as Referer.
  Each episode's page is read once (again after 30 days) and kept on disk
  (`<data>/adult-media/oppaistream.json`): title (`Name Ep N`), description, tags, studio,
  subtitle tracks. The site shows no upload dates; episodes count as uploaded when the plugin
  first lists them.
- **Streams:** the page's `var availableres = {"720": "…", "1080": "…", "4k": "…"}`: MP4 files,
  but 4K as WebM, which few clients play without transcoding 4K, so the best MP4 comes first.
  Served through the plugin as `video.mp4` with ranges.
- **Subtitles:** the page's `<track>`s. The sync saves them next to the episode
  (`Show S01E01.en.vtt`), downloading each once; the channel offers them as external streams
  through the plugin.

### Hentai Haven

hentaihaven.co is a Next.js site whose videos are on a separate host, nhplayer (`HentaiHaven/`):

- **Catalog:** the home page lists every episode, newest first, 40 per page (`/?page=N`, the
  last page from its pagination), as cards (`a.a_item`) linking to `/watch/<series>-episode-<n>/`.
  Each episode's page is read once (again after 30 days) and kept on disk
  (`<data>/adult-media/hentaihaven.json`): title (`h1.video_title`), the details
  (`div.r_item`: series, brand, release and upload date), genres (`div.video_tags`),
  description, cover, and its landscape image (in the list of the series' episodes).
- **Streams:** the episode page's player (`div.player iframe`, `https://nhplayer.com/v/<id>/`)
  lists its servers as `data-id="/player.php?vid=…"`, where `vid` is base64 of
  "address|expiry|signature": the address is the MP4 file. A server whose player page names
  stream addresses itself is preferred. The video host gets the player's page as Referer.
  The player's address is kept with the episode, so playback does not read the site.
- **Cloudflare:** the site answers clients that read it fast with a bot check (`cf-mitigated:
  challenge`, a "Just a moment..." page). Its pages are read at most two per second
  (`SiteHttp`); a crawl stops at the first check and the next sync reads on. With a FlareSolverr
  address set, a refused page is read through FlareSolverr's `/v1` `request.get`, and its
  `cf_clearance` cookie and browser are used for the requests that follow (oppai.stream too).

If oppai.stream or Hentai Haven cannot be read and was never read before, the sync leaves it
out rather than failing; once read, its last catalog is used instead.

### Pornhub

The catalog comes from the webmasters API (`/webmasters/search`, `/webmasters/categories`); the
streams from the video page's `flashvars` (`mediaDefinitions`), as yt-dlp reads them, with the
age cookies. Its CDN needs `Origin` and `Referer`, which Jellyfin does not pass on to ffmpeg, so
the streams go through the plugin like the others. Videos with only MP4 files are served as
files, with ranges.

### Library and streams

The sync (`Library/LibrarySync.cs`, on startup, after saving the settings and every 6 hours)
groups the merged catalog into series (`LibraryLayout`), writes a `.strm` and an NFO file per
episode and a `tvshow.nfo` per series (`LibraryWriter`, only what changed), creates the shows
library if needed, applies the user selections to the users' policies (`Access/`) and has
Jellyfin scan the library.

The `.strm` files and channel videos hold `<server>/HanimeTV/<source>/<id>/index.m3u8?token=…`
(`Controllers/StreamControllers.cs`, with `Stream` for hanime.tv, `HentaiHaven` and `Pornhub`):
it asks the source for the stream (cached for 10 minutes) and serves its playlist with every URI
rewritten to `proxy/<token>/<signature>/<url>/<file name>` (`Streaming/HlsProxy.cs`), which
fetches it with the source's headers. Links end with the upstream file's name because ffmpeg
only reads HLS segments whose URLs end with a media extension; they are relative so they work
behind any address or base URL. A source's file instead of a playlist is served as
`video.mp4`, with ranges.

## Building

Needs the .NET 10 SDK.

```bash
dotnet build -c Release Jellyfin.Plugin.HAnimeTV/Jellyfin.Plugin.HAnimeTV.csproj
```

The output is `Jellyfin.Plugin.HAnimeTV/bin/Release/net10.0/Jellyfin.Plugin.HAnimeTV.dll`. Copy
it into a folder in Jellyfin's `plugins` directory and restart Jellyfin to try it.

Unit tests:

```bash
dotnet test Jellyfin.Plugin.HAnimeTV.Tests
```

The integration test (`.woodpecker/integration.yaml`) runs the plugin in the official Jellyfin
12.1 and 12.2 images against `tests/integration/fake-hanime.py`, which stands in for hanime.tv,
oppai.stream, Hentai Haven and Pornhub: it checks the plugin's signatures, sealed handshake, player keys and
headers like the sites do and serves real HLS streams and an MP4 file. The plugin starts with
settings of version 0.1. `tests/integration/check.py` then creates users and checks through
Jellyfin's API that the settings move into the hentai provider, that the plugin merges both
catalogs into files and creates the library, that Jellyfin's scan makes series and episodes
with the NFO metadata, that only the selected user sees, lists and plays them, administrators
included, that the users' policies follow the selection (also after an administrator re-grants
all libraries and for new users), that episodes of both sites play through Jellyfin's remux and
from their stream links without a login (as browsers do), that stream links without the token or
with forged URLs are refused, that hidden genres leave the library, that the Pornhub channel
shows only for its user and plays (HLS, and MP4 with ranges), that the hentai provider in
channel mode leaves the library for a channel, and that the plugin logs no errors.
Releases depend on it. To run it elsewhere, follow the steps of the pipeline: `prepare.sh`,
`fake-hanime.py` (needs the `cryptography` package) reachable as `fake-hanime:8080`,
`start-jellyfin.sh` reachable as `jellyfin:8096`, then `check.py`; all of them must see `ROOT`
at the same path.

The plugin builds against the Jellyfin 12.1 packages, the oldest supported version, so one
build runs on 12.1 and newer: a plugin built against newer packages fails to load on an older
server (`Could not load file or assembly 'MediaBrowser.Controller, Version=…'`). Raise them
together with `TARGET_ABI` in `release.yaml` and the first image in `integration.yaml`.

## CI

The pipelines in `.woodpecker/` run on [Woodpecker CI](https://woodpecker-ci.org/):

| Pipeline | Runs on | Does |
| --- | --- | --- |
| `build.yaml` | pushes to `main`, pull requests, manual | Builds the plugin and runs the unit tests |
| `integration.yaml` | pushes to `main`, pull requests, manual, tags | End-to-end test in Jellyfin 12.1 and 12.2 (see above) |
| `auto_release.yaml` | pushes to `main` that change the `.csproj` | Tags a patch release, after the build and the integration test succeeded |
| `release.yaml` | tags | Builds the release zip, publishes the GitHub release and updates `manifest.json` |
| `renovate.yaml` | cron, manual | Runs Renovate |

## Releases

Push a tag such as `0.2.0`. CI builds the plugin as version `0.2.0.0`, publishes
`hanime.tv_0.2.0.0.zip` (named after the plugin's first name) (the DLL, `meta.json` and the plugin image) as a GitHub release, and
adds the release to `manifest.json` on `main`, with the commit titles since the previous release
as its changelog. That commit is marked `[CI SKIP]`. Jellyfin then offers the update in its
plugin catalog.

Jellyfin package updates are released automatically:

1. Renovate opens PRs for Jellyfin package updates within 12.1 and merges patch updates once
   the build passes.
2. On `main`, once the build and the integration test succeeded, `auto_release.yaml` pushes the
   next patch tag if the Jellyfin packages differ from the latest release
   (`scripts/next-release-tag.sh`).

The catalog shows `images/thumb.png` through `imageUrl` in `manifest.json`; the release zip
contains the same image for the installed plugin's page.
