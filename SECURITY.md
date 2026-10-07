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

- **Who sees a provider.** Only the users selected for a provider see its library or channel
  and their videos; a new installation selects nobody. With *Enforce through the users' channel
  and library access* (on by default), the channel and library access in every user's policy
  follows the selections, so Jellyfin itself hides them from everyone else, also by id and for
  playback. Because Jellyfin reports no changes to a user's access, an administrator granting
  a user all channels or libraries in the dashboard takes effect until the plugin's next check
  (at most 15 minutes, scheduled task *Enforce Adult Media access*). The channels also refuse to
  list their content for users who are not selected.
- **Parental controls.** Everything is rated `XXX` and the channels are marked as adult, so
  users with a parental rating limit do not see them even if selected.
- **Stream links.** The `.strm` files and channel videos point at the plugin's stream
  endpoints, which players use without a Jellyfin login (Jellyfin's ffmpeg has none). They
  require the plugin's random stream token, and only fetch URLs taken from the sites' answers,
  each signed with that token, and for Hentai Haven only pages of the configured site, so they
  are no open proxy. Users who can see a provider can read the token; with it, anyone can stream
  the providers' videos through the server, nothing else.
- **The library folder.** The plugin only writes to an empty folder or one it created (marked
  by a `.hanime-tv-library` file), as it deletes what it did not write there.
- **The plugin's API** (status, test, sync, applying access) is available to administrators only.
- **The hanime.tv account** (optional) is stored in the plugin's settings file on the server,
  like other plugin settings, and is shown to administrators on the settings page. It is only
  sent to the configured login URL.

Streams are fetched from the sites (or the configured relays) by the plugin and served to
players through Jellyfin; clients never contact the sites for playback. Cover images are
downloaded by Jellyfin from the sites' CDNs.
