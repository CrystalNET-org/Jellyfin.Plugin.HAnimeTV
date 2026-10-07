# hanime.tv for Jellyfin

![hanime.tv](images/thumb.png)

Browse and play [hanime.tv](https://hanime.tv) in Jellyfin, as a channel that only the users
you select can see. Its videos are sorted into categories, genres, studios and series, and
play through Jellyfin like any other video, on every Jellyfin client.

## Features

- **Only for selected users:** the channel is invisible to everyone else, administrators
  included. Jellyfin itself enforces it: users who are not selected cannot open, list or play
  the channel's videos, also not by id. A new installation selects nobody.
- **Browse by category:** recently uploaded, new releases, most viewed, most liked, series A–Z,
  genres and studios, with covers, descriptions, genres, studios, ratings and release dates.
- **Plays everywhere:** Jellyfin remuxes hanime.tv's HLS streams (or transcodes them, for
  clients that need it), so clients never contact hanime.tv.
- **Filters:** hide genres (e.g. `loli, shota, scat`) and censored videos everywhere.
- **Parental controls:** the channel and its videos are rated `XXX`.
- **Optional account:** guests get up to 720p; a premium hanime.tv account adds 1080p.
- **Updates through the plugin catalog.**

## Screenshots

The settings page, with a passing test of hanime.tv's catalog and streams:

![Settings page with the status and a passing test](images/settings.png)

The user selection, with the access Jellyfin grants each user:

![User selection: alice and carol are selected and have access, admin and bob do not](images/users.png)

## Requirements

- **Jellyfin 12.1 or newer** on any platform.
- hanime.tv reachable from the Jellyfin server. hanime.tv refuses many datacenter and VPN
  addresses (HTTP 403 in *Test*); a server at home usually works. Otherwise, point the
  endpoints under *Advanced* at a relay.

## Installation

1. In Jellyfin, open *Dashboard → Plugins → Manage Repositories* and add this repository:
   ```
   https://raw.githubusercontent.com/CrystalNET-org/Jellyfin.Plugin.HAnimeTV/main/manifest.json
   ```
2. Install **hanime.tv** from the catalog and restart Jellyfin.
3. Open **hanime.tv** in the dashboard sidebar, below *Plugins*, and click **Test**.
4. Under **Users**, tick the users who may see the channel and save.

The selected users find **hanime.tv** among Jellyfin's channels.

To install without the catalog, extract a
[release](https://github.com/CrystalNET-org/Jellyfin.Plugin.HAnimeTV/releases) zip into a
folder in Jellyfin's `plugins` directory.

## Settings

| Section | Setting | Default | Meaning |
| --- | --- | --- | --- |
| Users | users | nobody | Who sees the channel and can play its videos. |
| Users | Enforce through the users' channel access | on | See *How access is enforced*. |
| Catalog | Hidden genres | none | hanime.tv tags whose videos are never shown, separated by commas. |
| Catalog | Hide censored videos | off | |
| Catalog | Videos per folder | 200 | Jellyfin reads a folder's videos at once and keeps an entry for each. Series always list all episodes. |
| Catalog | Catalog refresh | 6 hours | How long the catalog is kept before it is downloaded again. |
| Account | Email, password | empty | Optional hanime.tv account; premium adds 1080p. After a failed login, videos play as a guest and the login is retried after 15 minutes. |
| Advanced | Catalog, handshake and login URL, stream host | hanime.tv | To use a relay. |

**Test** checks the settings as entered, before saving them: it downloads the catalog and asks
for the streams of the newest video.

## How access is enforced

The plugin's user selection is enforced twice:

- **The channel** answers only selected users: it is missing from everyone else's channel
  lists, home screen and *Latest* row, and its folders refuse to list.
- **Jellyfin's user policies:** with *Enforce through the users' channel access* on, the
  channel access in each user's policy (*Dashboard → Users → user → Access*) follows the
  selection. Jellyfin then refuses the channel's videos to the others wherever they come from,
  e.g. a shared link, and refuses to play them.

The policies are brought in line on startup, when the settings are saved, when a user is
created, and every 15 minutes (scheduled task *Enforce hanime.tv channel access*); the *Users*
section also has **Apply access now**. A user who is not selected but had *Enable access to all
channels* keeps every other channel: they are listed one by one instead. Jellyfin reports no
changes to a user's access, so if an administrator grants such a user all channels again, the
plugin takes the channel away again at its next check.

Turning enforcement off leaves the policies as they are.

## Troubleshooting

- **Test fails with HTTP 403:** hanime.tv refuses the server's address, which is common for
  datacenters and VPNs. Run Jellyfin from another network or use a relay (*Advanced*).
- **A folder is empty:** the catalog could not be downloaded; Jellyfin's log says why (search for
  `hanime.tv`). Folders are kept by Jellyfin for 3 hours; changing a catalog setting refreshes
  them.
- **Playback fails:** click **Test**: hanime.tv may have changed its stream API. Jellyfin's log
  shows the streams found for each played video (`hanime.tv: 1 streams for …`) and ffmpeg's
  errors.
- **Videos are sorted by name:** Jellyfin sorts channel folders by name; sort by *Date added*
  for the upload date or by *Release date*.
- **A user still sees the channel:** check the *Users* section; the right column shows what
  Jellyfin's policy grants. Click **Apply access now**.

## Disclaimer

This plugin is not affiliated with hanime.tv. It uses hanime.tv's unofficial web API, which
can change at any time. The content is for adults only; you are responsible for complying with
the laws of your country and hanime.tv's terms.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for building, tests and releases, and
[SECURITY.md](SECURITY.md) for reporting security problems.
