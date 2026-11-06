# Changelog

All notable changes to this project are documented here. Jellyfin shows the
`changelog` from `build.yaml` on each catalog version.

## 1.2.0.0

- Add HDHomeRun tuner settings with auto-detect, URL test, and explicit save
- Remove the built-in device-specific tuner URL; each installation confirms its own

## 1.1.4.0

- Load the dashboard page as a Jellyfin plugin view (`pluginConfigurationPage` + `Rogue Streams.js`)
- Inline scripts were ignored, so the stream table never filled

## 1.1.3.0

- Publish a per-version changelog so the Jellyfin catalog can offer updates
- Keep previous versions in the plugin repository manifest instead of replacing it
- Show Rogue Streams under the dashboard Server menu
- Fix empty stream list while Live TV is playing
- List and close streams with CloseLiveStream; do not kill ffmpeg

## 1.1.2.0

- Enumerate open live-stream map keys without plugin load-context type checks
- Include session LiveStreamIds so a web Live TV session appears
- Call Status through ApiClient

## 1.1.1.0

- Register the config page under the plugin name
- Enable the page in the dashboard Server menu

## 1.1.0.0

- Replace ffmpeg pid kills with IMediaSourceManager.CloseLiveStream
- Show open live streams, sessions, and in-progress recordings
- Closing is always manual

## 1.0.0.0

- Initial watchdog that listed ffmpeg Live TV transcodes and HDHomeRun leases
