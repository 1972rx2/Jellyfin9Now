# Jellyfin9Now

9Now Plugin for Jellyfin — early Jellyfin 12.4 test build with Nine TV device-code sign-in and 9Now branding.

An early Jellyfin 12.4 channel plugin for browsing 9Now with Nine's TV device-code sign-in.

## Included in this build

- Sign in at `9now.com.au/activate` from the Jellyfin dashboard. The plugin stores the returned access and refresh tokens in Jellyfin's local plugin configuration.
- Browse Featured rails, Shows, Categories, seasons, episodes, and clips.
- Browse region-specific Live TV channels.
- Resolve live HLS stream URLs and clear video HLS sources when available.
- Show the 9Now logo on the channel tile and plugin dashboard.
- Mark episodes flagged as DRM-protected by Nine with a thumbnail badge and `[DRM]` title prefix.
- Refresh an expired access token using Nine's refresh-token endpoint.

Playback is limited to clear HLS sources. Videos marked DRM-protected by 9Now are not decrypted by this plugin. Ads and other provider stream behavior are left as returned by 9Now. The live-region setting defaults to New South Wales.

This repository provides a third-party Jellyfin plugin feed for an early Jellyfin 12.4 test build. It is not affiliated with Nine Entertainment or Jellyfin. Nine documents TV sign-in through a one-time activation code at [9now.com.au/activate](https://help.9now.com.au/hc/en-au/articles/34314882301081-How-do-I-log-in-to-9Now-from-my-Smart-TV). The plugin's API integration follows the public [SlyGuy 9Now add-on](https://github.com/matthuisman/slyguy.addons/tree/master/slyguy.9now) device-login, catalogue, and stream-resolution flow; provider endpoints may change without notice.

Treat the Jellyfin server and its configuration backups as sensitive because they contain the local 9Now account tokens.

## Install from the Jellyfin catalog

In Jellyfin, open **Dashboard → Plugins → Repositories**, add a repository named `9Now`, and use this URL:

`https://raw.githubusercontent.com/1972rx2/Jellyfin9Now/main/manifest.json`

Then open **Catalog**, install **9Now**, and restart Jellyfin. This build targets Jellyfin 12.4 (`targetAbi` `12.4.0.0`).

## Manual install

1. Extract `Jellyfin.Plugin.NineNow.dll` from `Jellyfin.Plugin.NineNow-0.1.4-jellyfin12.4-test.zip` into a new `NineNow` directory under Jellyfin's plugins directory.
2. Restart Jellyfin.
3. In the dashboard, open **Plugins → 9Now**, select **Sign in to 9Now**, then enter the displayed code at `https://9now.com.au/activate` using your own Nine account.
4. Wait for the dashboard to confirm sign-in, choose your Live TV region if needed, and save.
5. Open the **9Now** channel.

## Build from source

From the `Jellyfin9Now` directory, run `dotnet restore Jellyfin.Plugin.NineNow/Jellyfin.Plugin.NineNow.csproj` and then `dotnet build Jellyfin.Plugin.NineNow/Jellyfin.Plugin.NineNow.csproj --no-restore`. The project targets .NET 10 and Jellyfin 12.4.
