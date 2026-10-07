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
│   ├── PluginServiceRegistrator.cs  # registers the channel and the access enforcement
│   ├── Hanime/                      # hanime.tv client: catalog, stream handshake, login, signatures
│   ├── Channels/                    # the channel and its folders
│   ├── Access/                      # enforces the user selection through Jellyfin's user policies
│   ├── Controllers/                 # API for the settings page (status, test, apply access)
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

## How it talks to hanime.tv

hanime.tv has no public API; the plugin does what its site does:

- **Catalog:** `GET /api/v11/search_hvs` returns all videos at once. It needs the app signature
  (`X-Signature-Version: app2`, `X-Claim`, `X-Signature`). The plugin keeps it in memory and
  builds the folders from it.
- **Streams:** `POST /api/v11/handshake` with a token sealed in hanime.tv's "insecure message"
  envelope (AES-256-GCM, key and associated data are fixed labels) and the web signature
  (`X-Signature-Version: web2`, `X-Time`, `X-Signature`). The answer's `X-Token` header holds the
  HLS sources, sealed the same way. Stream URLs expire, so they are asked for on playback and
  never stored; Jellyfin probes them for their codecs and duration.
- **Login** (optional): `POST /rapi/v7/sessions` with the app signature; the session token is
  sent with the handshake and unlocks premium sources.

All of it is in `Hanime/`; when hanime.tv changes something, that is where to look.

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
`tests/integration/check.py` then creates users and checks through Jellyfin's API that only the
selected user sees, lists and plays the channel's videos, administrators included, that the
users' policies follow the selection (also after an administrator re-grants all channels and for
new users), that a video plays through Jellyfin's remux, and that the plugin logs no errors.
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
contains the same image for the installed plugin's page, and the plugin embeds it as the
channel's image.
