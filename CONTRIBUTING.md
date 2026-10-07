# Contributing to the hanime.tv plugin

Bug reports, ideas and pull requests are welcome.

- **Bugs:** open an [issue](https://github.com/CrystalNET-org/Jellyfin.Plugin.HAnimeTV/issues)
  with the steps to reproduce, the Jellyfin and plugin versions, the result of *Test* and the
  relevant lines from Jellyfin's log (search for `hanime.tv`).
- **Pull requests:** against `main`. Keep them focused, and update the README when behaviour
  or settings change.

## Repository layout

```
Jellyfin.Plugin.HAnimeTV/
├── Jellyfin.Plugin.HAnimeTV/
│   ├── Plugin.cs                    # plugin entry, settings page registration
│   ├── PluginServiceRegistrator.cs  # registers the library sync and the access enforcement
│   ├── Hanime/                      # hanime.tv client: catalog, stream handshake, login, signatures
│   ├── Library/                     # writes the .strm/NFO files, creates and scans the library
│   ├── Streaming/                   # HLS proxy: rewrites hanime.tv's playlists, signs their URLs
│   ├── Access/                      # enforces the user selection through Jellyfin's user policies
│   ├── Controllers/                 # settings page API (status, test, sync, access) and stream endpoint
│   ├── Configuration/               # plugin settings
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

The sync (`Library/LibrarySync.cs`, on startup, after saving the settings and every 6 hours)
groups the catalog into series by name (`LibraryLayout`), writes a `.strm` and an NFO file per
episode and a `tvshow.nfo` per series (`LibraryWriter`, only what changed), creates the shows
library if needed, applies the user selection to the users' policies (`Access/`) and has
Jellyfin scan the library.

The `.strm` files hold `<server>/HanimeTV/Stream/<slug>/index.m3u8?token=…`
(`Controllers/StreamController.cs`): it does the handshake (cached for 10 minutes) and serves
the stream's playlist with every URI rewritten to `proxy/<token>/<signature>/<url>/<file name>`
(`Streaming/HlsProxy.cs`), which fetches it from hanime.tv with the browser headers. Links end
with the upstream file's name because ffmpeg only reads HLS segments whose URLs end with a media
extension; they are relative so they work behind any address or base URL.

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
12.1 and 12.2 images against `tests/integration/fake-hanime.py`, which stands in for hanime.tv: it checks
the plugin's signatures and sealed handshake like hanime.tv and serves real HLS streams.
`tests/integration/check.py` then creates users and checks through Jellyfin's API that the
plugin writes the files and creates the library, that Jellyfin's scan makes series and episodes
with the NFO metadata, that only the selected user sees, lists and plays them, administrators
included, that the users' policies follow the selection (also after an administrator re-grants
all libraries and for new users), that an episode plays through Jellyfin's remux and from its
stream link without a login (as browsers do), that stream links without the token or with
forged URLs are refused, that hidden genres leave the library, and that the plugin logs no errors.
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

Push a tag such as `0.1.0`. CI builds the plugin as version `0.1.0.0`, publishes
`hanime.tv_0.1.0.0.zip` (the DLL, `meta.json` and the plugin image) as a GitHub release, and
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
