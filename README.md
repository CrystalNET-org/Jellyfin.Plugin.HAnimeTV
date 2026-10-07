# Adult Media for Jellyfin

![Adult Media](images/thumb.png)

Adult media providers in Jellyfin, each only for the users you select:

- **Hentai**: [hanime.tv](https://hanime.tv), [oppai.stream](https://oppai.stream) and
  [Hentai Haven](https://hentaihaven.co) merged into one catalog, as a **shows library** (series
  and episodes with descriptions, genres, studios, dates, images and subtitles) or as a
  **channel**.
- **Pornhub**: featured, newest, most viewed and top rated videos, categories and your own
  searches, as a **channel**.

Everything plays through Jellyfin on every client.

## Features

- **Library or channel, per provider:** the hentai provider works as a real shows library
  (Next Up, Continue Watching, search, favorites, metadata editing like any other show) or as a
  channel browsed by folders; switch in the settings.
- **One hentai catalog from three sites:** series with the same name are one series, whichever
  site they come from; an episode several sites have comes from hanime.tv, else oppai.stream,
  else Hentai Haven. Each site can be turned off.
- **Subtitles and 4K from oppai.stream:** its subtitles are saved next to the episodes (or
  offered by the channel), and its streams play in up to 4K.
- **Only for selected users, per provider:** a provider's library or channel is invisible to
  everyone else, administrators included, through Jellyfin's own channel and library access.
  A new installation selects nobody.
- **Stays current:** new uploads appear and removed videos disappear on every sync (every 6
  hours, or on demand); channels read the sites when they are browsed.
- **Plays everywhere:** the plugin serves the sites' streams through Jellyfin with the headers
  they need, so clients never contact them.
- **Filters:** leave out genres (e.g. `loli, shota, scat`), censored videos, or Pornhub
  categories.
- **Parental controls:** everything is rated `XXX`; the channels are marked as adult.
- **Optional hanime.tv account:** guests get up to 720p; a premium account adds 1080p.
- **Updates through the plugin catalog.**

## Screenshots

The settings page, with each provider's state:

![Settings page with the providers' status](images/settings.png)

A provider's settings, with its user selection and the access Jellyfin grants each user:

![The hentai provider's settings and users](images/users.png)

## Requirements

- **Jellyfin 12.1 or newer** on any platform.
- The sites reachable from the Jellyfin server:
  - hanime.tv refuses many datacenter and VPN addresses (HTTP 403 in *Test*); a server at home
    usually works. Otherwise, point its endpoints under *Advanced* at a relay.
  - oppai.stream and Hentai Haven sit behind Cloudflare, which may answer servers it takes for
    bots with a check, which *Test* reports. A [FlareSolverr](https://github.com/FlareSolverr/FlareSolverr)
    gets past it (see *Troubleshooting*).
  - Pornhub blocks some countries and may refuse servers it takes for bots (HTTP 403 or 451 in
    *Test*).

## Installation

1. In Jellyfin, open *Dashboard → Plugins → Manage Repositories* and add this repository:
   ```
   https://raw.githubusercontent.com/CrystalNET-org/Jellyfin.Plugin.HAnimeTV/main/manifest.json
   ```
2. Install **Adult Media** from the catalog (category *Anime*) and restart Jellyfin.
3. Open **Adult Media** in the dashboard sidebar, below *Plugins*.
4. Under **Streams & access**, check *Jellyfin address for the stream links* (see below).
5. Under **Hentai**, choose library or channel, tick the users who may see it, click **Test**,
   and save. Under **Pornhub**, choose *Channel* and tick its users.

In library mode, the plugin then writes the library's files, creates the **Hentai** shows
library and has Jellyfin scan it. The first sync reads every oppai.stream and Hentai Haven
episode page once, which takes a few minutes (for Hentai Haven, about half an hour); later syncs
only read new episodes. With all three
catalogs (several thousand episodes) the first scan takes a few minutes too.

To install without the catalog, extract a
[release](https://github.com/CrystalNET-org/Jellyfin.Plugin.HAnimeTV/releases) zip into a
folder in Jellyfin's `plugins` directory.

## How it works

**Library mode:** on every sync the plugin reads both hentai catalogs, merges them and writes
them into a folder, laid out like any shows library:

```
hanime.tv/
└── Ishuzoku Reviewers/
    ├── tvshow.nfo                        series: title, description, genres, studio, cover
    └── Season 01/
        ├── Ishuzoku Reviewers S01E01.strm    the episode's stream link
        ├── Ishuzoku Reviewers S01E01.nfo     episode: title, description, dates, rating, image
        └── …
```

hanime.tv's videos are grouped into series by their names ("Title 2" is episode 2 of "Title");
oppai.stream and Hentai Haven name their series and number their episodes. Series are matched
by name, ignoring case, spaces and punctuation. Only changed files are written, so Jellyfin
rescans only what changed.

**Channel mode:** the channel lists recently uploaded and new releases, most viewed and most
liked, series A–Z, genres and studios, built from the same merged catalog. The Pornhub channel
lists featured, newest, most viewed (week, month, all time) and top rated videos, the categories
and your searches, from Pornhub's public webmasters API.

**Playback:** a `.strm` file or a channel video holds a link to the plugin on your Jellyfin
server, not to the site, because the sites' stream links expire and need a browser's headers.
When a video plays, the plugin asks the site for a fresh stream (hanime.tv's handshake, Hentai
Haven's player, Pornhub's video page) and serves it through Jellyfin. Browsers that can play
HLS play that link themselves; other clients get it remuxed or transcoded by Jellyfin.
Pornhub videos that only have MP4 files are served as files, with ranges for seeking.

## Settings

| Section | Setting | Default | Meaning |
| --- | --- | --- | --- |
| Hentai | Show as | Shows library | Library, channel, or off. |
| Hentai | Sources | all | hanime.tv, oppai.stream and Hentai Haven, with the addresses of the last two. |
| Hentai | FlareSolverr address | empty | Optional, e.g. `http://flaresolverr:8191`: reads the pages Cloudflare refuses. |
| Hentai | Users | nobody | Who sees the library or channel and can play its videos. |
| Hentai | Hidden genres | none | Genres whose videos are left out, separated by commas. |
| Hentai | Leave out censored videos | off | |
| Hentai | Catalog refresh | 6 hours | How long the catalogs are kept before they are read again. |
| Hentai | Videos per channel folder | 200 | Channel only. |
| Hentai library | Folder | `hanime.tv` in Jellyfin's data directory | Where the files are written. Use an empty folder: the plugin replaces everything in it. |
| Hentai library | Create the Jellyfin library | on | Creates a shows library for the folder if there is none. |
| Hentai library | Library name | Hentai | |
| hanime.tv account | Email, password | empty | Optional; premium adds 1080p. After a failed login, videos play as a guest and the login is retried after 15 minutes. |
| Pornhub | Show as | Off | Channel or off. |
| Pornhub | Users | nobody | Who sees the channel. |
| Pornhub | Searches | none | Searches shown as folders of their own, e.g. performers. |
| Pornhub | Hidden categories | none | Categories whose videos are left out. |
| Pornhub | Videos per folder | 200 | |
| Streams & access | Jellyfin address for the stream links | this page's address | See below. |
| Streams & access | Enforce through the users' channel and library access | on | See *How access is enforced*. |
| Advanced | hanime.tv URLs, Pornhub API and site URL | the sites | To use a relay. |

**Jellyfin address for the stream links:** the address in the `.strm` files and the channels'
videos. Jellyfin's ffmpeg, and every ffmpeg worker, starts streams from it, and browsers fetch
these links themselves, so it must be the address your clients open Jellyfin with, e.g.
`https://jellyfin.example.com` (an `http://` address on an `https://` site is blocked by
browsers), and reachable from all ffmpeg workers. The settings page fills in its own address;
save to use it. After changing it, the next sync rewrites the links. Left empty, the links use
Jellyfin's guess of its own address, which the *Status* section flags.

**In Kubernetes**, use the ingress address. Jellyfin's guess is the pod's IP, which changes with
every restart and which ffmpeg workers outside the cluster cannot reach (ffmpeg fails with
`Connection to tcp://10.244.…:8096 failed: Connection timed out`). Through the ingress, every
worker, inside or outside the cluster, reaches the same address as the browsers.

**Test** checks a provider's settings as entered, before saving them: it reads each source's
catalog (for oppai.stream and Hentai Haven, the newest entries) and asks for the streams of the
newest video.

The library the plugin creates reads only the plugin's NFO files: no internet metadata
providers, no trickplay or chapter images (which would run ffmpeg over every stream). You can
change that under *Dashboard → Libraries*, e.g. to add the AniDB plugin's metadata.

## How access is enforced

With *Enforce through the users' channel and library access* on, the channel and library access
in each user's policy (*Dashboard → Users → user → Access*) follows the providers' selections:
a provider's library or channel, whichever its mode, for its selected users, and nobody else.
Jellyfin itself then hides them and their videos from everyone else, also by id and for
playback. A provider that is off, or the library of a provider in channel mode, is granted to
nobody.

The policies are brought in line after every sync, when the settings are saved, when a user is
created, and every 15 minutes (scheduled task *Enforce Adult Media access*); the *Streams &
access* section also has **Apply access now**. A user who is not selected but had *Enable access
to all channels* or *libraries* keeps every other one: they are listed one by one instead.
Jellyfin reports no changes to a user's access, so if an administrator grants such a user all
of them again, the plugin takes the provider away again at its next check.

Turning enforcement off leaves the policies as they are; the channels still only list their
content for their selected users.

The stream links carry a secret token of the plugin. It only allows streaming the providers'
videos through your server, and users who can see a provider can read it.

## Troubleshooting

- **Test fails with HTTP 403:** the site refuses the server's address, which is common for
  datacenters and VPNs. Run Jellyfin from another network, or for hanime.tv use a relay
  (*Advanced*).
- **oppai.stream or Hentai Haven answers with a bot check:** Cloudflare does not let the server
  in. Hentai Haven's episode pages are read at most two per second to avoid it; when it comes
  anyway, the sync stops reading them, keeps what it has and reads on at the next sync, and
  episodes play through the player saved during the sync without the site. If the check stays,
  run [FlareSolverr](https://github.com/FlareSolverr/FlareSolverr) (e.g. the
  `ghcr.io/flaresolverr/flaresolverr` image) and enter its address under *Hentai → Sources*.
  It opens the refused pages in a browser; the cookie it gets from Cloudflare then lets Jellyfin
  in directly, if FlareSolverr's requests come from the same public address as Jellyfin's
  (e.g. in the same cluster). Otherwise turn the site off: the library keeps the other sites'
  videos.
- **Pornhub answers HTTP 451:** Pornhub is not available in the server's country.
- **The library is empty:** look at the *Status* section: the last sync's error, each source's
  state, or whether the Jellyfin library exists. Jellyfin's log has details (search for
  `hanime.tv`, `oppai.stream`, `Hentai Haven` or `Pornhub`).
- **Playback fails in the browser but works in apps:** the stream links' address is not
  reachable from the browser, or is `http://` while Jellyfin is opened with `https://`. Set
  *Jellyfin address for the stream links* to the address you open Jellyfin with.
- **ffmpeg fails with `Connection to tcp://…:8096 failed`:** the ffmpeg workers cannot reach the
  stream links' address, e.g. a pod IP. Set *Jellyfin address for the stream links* (see
  *Settings*, *In Kubernetes*).
- **Playback fails everywhere:** click **Test**: the site may have changed how it hands out
  streams.
- **An episode is in the wrong series, or a series is split in two:** series come from the
  episode names, and the sites may spell a title differently. Edit the episode's metadata in
  Jellyfin, or report the title.
- **Both *hanime.tv* and *Adult Media* are listed under *Plugins*, and *Status* does not
  load:** see *Upgrading from 0.1.x*.
- **Durations show only after playing:** Jellyfin reads a `.strm` episode's duration when it is
  first played, not during scans, so that scans don't hit the sites thousands of times.

## Upgrading from 0.1.x

The plugin was called *hanime.tv*. It keeps its id, so it updates in place: its settings move
into the *Hentai* provider, which stays a shows library for the same users, in the same folder
and Jellyfin library, now with oppai.stream's and Hentai Haven's videos added (turn them off
under *Hentai* to keep hanime.tv only). Pornhub is off until you turn it on.

Jellyfin treats the old and the new name as two plugins, so after the update from 0.1.x it may
load both: *hanime.tv* and *Adult Media* are listed under *Plugins*, and the *Status* section does
not load. Since 0.3.1 the plugin deletes the old version when it starts; restart Jellyfin once
more to unload it. With 0.3.0, delete the `hanime.tv_0.1.…` folder in Jellyfin's `plugins`
directory and restart. Then check each provider's users under *Adult Media* and save: the old
version's settings page may have cleared them.

## Disclaimer

This plugin is not affiliated with hanime.tv, oppai.stream, Hentai Haven or Pornhub. It uses their unofficial
web interfaces and Pornhub's public webmasters API, which can change at any time. The content is
for adults only; you are responsible for complying with the laws of your country and the sites'
terms.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, tests and releases, and
[SECURITY.md](SECURITY.md) for reporting security problems.
