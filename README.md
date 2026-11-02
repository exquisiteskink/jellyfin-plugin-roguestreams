# Rogue Streams — a Jellyfin Live TV stream closer

Shows every **open Live TV stream** in this Jellyfin process, including ones
Dashboard → Active Sessions cannot see, and lets you close them one at a time.

Nothing is closed automatically.

## The problem it solves

A channel change or a crashed client can leave Jellyfin's `SharedHttpStream`
copying from the tuner to disk with nobody watching. The session is gone, the
tuner stays leased, and the next channel change hangs.

Killing ffmpeg does **not** fix that: it drops the consumer and often *leaves*
the copy loop running. This plugin calls `IMediaSourceManager.CloseLiveStream`,
which is the same path as `POST /LiveStreams/Close`.

## What it shows

| Panel | Content |
| --- | --- |
| Open live streams | every stream in Jellyfin's open-stream map: name, liveStreamId, consumer count, viewers, recordings, cache file size |
| Tuner leases | live HDHomeRun `/status.json` (optional): channel, name, target IP, bitrate |
| Watching now | live TV sessions with user, device, client, channel |
| Recordings | in-progress DVR timers |

Every listed stream has a **Close** button. If a viewer or recording is attached,
the UI warns before closing.

## How close works

1. Reads open streams from Jellyfin (`MediaSourceManager`'s live-stream map).
2. Matches sessions by `PlayState.LiveStreamId`.
3. Matches in-progress timers from `ILiveTvManager`.
4. `POST /RogueStreams/Close` calls `CloseLiveStream` until that id is gone, so a
   leaked consumer count still releases the tuner.

Administrator role is required. Closing a stream that is actually in use will
stop that viewer or recording — that is intentional so you can clear a stuck
tuner.

If Close leaves the stream open, or a tuner stays leased with no open stream,
restart the Jellyfin **process** (a dashboard restart often does not drop
`SharedHttpStream`).

## Install

### From the plugin repository (after CI publishes it)

Dashboard → Plugins → Repositories → **+** → paste:

```
https://exquisiteskink.github.io/jellyfin-plugin-roguestreams/manifest.json
```

Save, then Catalog → **Live TV** → **Rogue Streams** → Install → restart Jellyfin.
Dashboard → Plugins → Rogue Streams is the page.

The manifest lives on the `gh-pages` branch and is rebuilt by CI on every `v*` tag.

### Manually

1. Build (below) or download the release zip.
2. Stop Jellyfin.
3. Unpack so you have `plugins/Rogue Streams/Jellyfin.Plugin.RogueStreams.dll` and
   `plugins/Rogue Streams/meta.json`.
4. Start Jellyfin, then open Dashboard → Plugins → Rogue Streams.

## Build

```bash
dotnet build src/Jellyfin.Plugin.RogueStreams/Jellyfin.Plugin.RogueStreams.csproj -c Release
```

On a slim host without ICU the SDK aborts with `Couldn't find a valid ICU package`;
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` gets past it.

To produce an installable package and manifest locally:

```bash
pip install jprm
jprm plugin build . --version 1.1.0.0 --output-dir ./artifact
jprm repo add ./artifact/*.zip --output-dir ./repo
```

## Configuration

`plugins/configurations/Jellyfin.Plugin.RogueStreams.xml` (created on first save):

| Key | Default | Meaning |
| --- | --- | --- |
| `TunerUrl` | `` | HDHomeRun base URL used for `/status.json`. Closing does not need this. |
| `GraceSeconds` | `20` | unused; closing is always manual |

## Compatibility

- Jellyfin 12.4.0 (`targetAbi` 12.4.0.0), `net10.0`.
- HDHomeRun `/status.json` is optional context. M3U and other tuners still list
  and close Jellyfin's open live streams.

## Licence

GPL-3.0.
