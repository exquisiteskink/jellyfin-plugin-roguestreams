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
| Open live streams | every stream in Jellyfin's open-stream map: name, liveStreamId, source device/tuner, consumer count, viewers, recordings, cache file size |
| Tuner leases | live HDHomeRun `/status.json` (optional): channel, name, target IP, bitrate |
| Tuner settings | auto-detect HDHomeRun devices, confirm/test a URL, or disable tuner leases |
| Watching now | live TV sessions with user, device, client, channel, and source tuner |
| Recordings | in-progress DVR timers |

Every listed stream has a **Close** button. If a viewer or recording is attached,
the UI warns before closing.

## Source device recognition

Each open stream is matched by Jellyfin's `TunerHostId` against the configured
Live TV tuner hosts. The UI shows the tuner kind and name, for example:

- **HDHomeRun** — physical/network HDHomeRun tuner
- **M3U tuner** — M3U/IPTV playlist tuner
- any other registered Jellyfin tuner host (NextPVR, TVHeadend, Hauppauge, etc.)
  using the display name that host reports to Jellyfin

If a stream has a tuner id that is not in the Live TV configuration, it is marked
**not in Live TV config**. If Jellyfin gives no tuner id at all, the plugin shows
the internal stream type instead.

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
mkdir -p ./artifact ./repo
jprm plugin build . --version 1.3.0.0 --output ./artifact
jprm repo init ./repo/manifest.json
jprm repo add ./repo ./artifact/*.zip \
  --url "https://exquisiteskink.github.io/jellyfin-plugin-roguestreams"
```

## Configuration

Open Dashboard → Plugins → Rogue Streams. The **HDHomeRun tuner settings**
section can auto-detect tuners with Jellyfin's own discovery, test the URL, and
save the one you confirm. Nothing is written to the plugin configuration until
you press **Save tuner setting**.

`plugins/configurations/Jellyfin.Plugin.RogueStreams.xml` (created on first save):

| Key | Default | Meaning |
| --- | --- | --- |
| `TunerUrl` | empty | HDHomeRun base URL used for `/status.json`. Empty disables tuner lease lookup. Closing does not need this. |
| `GraceSeconds` | `20` | unused; closing is always manual |

There is no built-in device IP. Each installation detects or enters its own
tuner URL; the value is stored only in that server's configuration XML.

## Compatibility

- Jellyfin 12.4.0 (`targetAbi` 12.4.0.0), `net10.0`.
- HDHomeRun `/status.json` is optional context. M3U and other tuners still list
  and close Jellyfin's open live streams.

## Licence

GPL-3.0.
