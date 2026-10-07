# Security

## Reporting a vulnerability

Please report security problems privately through
[GitHub's vulnerability reporting](https://github.com/CrystalNET-org/Jellyfin.Plugin.HAnimeTV/security/advisories/new),
not as a public issue. Include what is affected, how to reproduce it, and the Jellyfin and
plugin versions.

You will get an answer as soon as possible. Fixes are released as a new plugin version and
announced in a security advisory.

## Supported versions

Only the latest release receives security fixes.

## What the plugin protects

- **Who sees the library.** Only the users selected in the settings see the hanime.tv library
  and its series and episodes; a new installation selects nobody. With *Enforce through the
  users' library access* (on by default), the library access in every user's policy follows the
  selection, so Jellyfin itself hides the library from everyone else, also by id and for
  playback. Because Jellyfin reports no changes to a user's access, an administrator granting
  a user all libraries in the dashboard takes effect until the plugin's next check (at most 15
  minutes, scheduled task *Enforce hanime.tv library access*).
- **Parental controls.** The series and episodes are rated `XXX`, so users with a parental
  rating limit do not see them even if selected.
- **Stream links.** The library's `.strm` files point at the plugin's stream endpoint, which
  players use without a Jellyfin login (Jellyfin's ffmpeg has none). It requires the plugin's
  random stream token, and only fetches URLs taken from hanime.tv's answers, each signed with
  that token, so it is no open proxy. Users who can see the library can read the token; with it,
  anyone can stream hanime.tv's videos through the server, nothing else.
- **The library folder.** The plugin only writes to an empty folder or one it created (marked
  by a `.hanime-tv-library` file), as it deletes what it did not write there.
- **The plugin's API** (status, test, sync, applying access) is available to administrators only.
- **The hanime.tv account** (optional) is stored in the plugin's settings file on the server,
  like other plugin settings, and is shown to administrators on the settings page. It is only
  sent to the configured login URL.

Streams are fetched from hanime.tv (or the configured stream host) by the plugin and served to
players through Jellyfin; clients never contact hanime.tv for playback. Cover images are
downloaded by Jellyfin from hanime.tv's CDN.
