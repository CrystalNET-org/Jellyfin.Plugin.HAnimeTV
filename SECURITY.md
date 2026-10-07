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

- **Who sees the channel.** Only the users selected in the settings see the channel, its
  folders and its videos; a new installation selects nobody. With *Enforce through the users'
  channel access* (on by default), the channel access in every user's policy follows the
  selection, so Jellyfin itself refuses the videos to everyone else, also by id and for
  playback. Users who are not selected cannot list the channel's folders, also not by id.
  Because Jellyfin reports no changes to a user's access, an administrator granting a user all
  channels in the dashboard takes effect until the plugin's next check (at most 15 minutes,
  scheduled task *Enforce hanime.tv channel access*).
- **Parental controls.** The channel and its videos are rated `XXX`, so users with a parental
  rating limit do not see them even if selected.
- **The plugin's API** (status, test, applying access) is available to administrators only.
- **The hanime.tv account** (optional) is stored in the plugin's settings file on the server,
  like other plugin settings, and is shown to administrators on the settings page. It is only
  sent to the configured login URL.

Streams are fetched by Jellyfin's ffmpeg from hanime.tv (or the configured stream host) and
served to clients through Jellyfin; clients never contact hanime.tv for playback. Cover
images are downloaded by Jellyfin from hanime.tv's CDN.
