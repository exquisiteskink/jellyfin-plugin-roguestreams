# Rogue Streams — a Jellyfin Live TV watchdog plugin

Finds the Live TV transcodes that nobody is watching and kills them one at a time, from
the Jellyfin dashboard.

## The problem it solves

When a channel change or a crashed client leaves an orphaned ffmpeg running against the
HDHomeRun, the tuner stays leased to the server with no viewer attached. Jellyfin's
**Dashboard → Active Sessions** cannot show these: the session object is gone, and the
ghost is a process holding a shared stream consumer, not a session. The result is a
tuner that looks in use, a channel change that hangs, and transcode segments piling up
on disk.

`pkill -f ffmpeg` fixes it by killing every stream, including the one someone is
watching. This plugin targets only the orphan.

## What it shows

| Panel | Content |
| --- | --- |
| Transcodes | every ffmpeg process on a tuner URL: channel, pid, uptime, and whether a session or recording accounts for it |
| Tuner leases | live `/status.json` per tuner: channel, name, target IP, bitrate |
| Watching now | live TV sessions with user, device, channel, play method |

Orphans are marked **nobody** and get a **Kill** button.

## How it decides

1. Reads `VctNumber`/`VctName`/`TargetIP`/`NetworkRate` from the tuner's `/status.json`.
2. Reads live sessions from `ISessionManager`, taking `NowPlayingItem.ChannelNumber`.
3. Reads each ffmpeg's `/proc/<pid>/cmdline` and extracts the channel from the tuner URL
   (`.../native/v8.1` → `8.1`). This is Jellyfin's numbering, which is why the process is
   matched on its own URL and never on the tuner's channel number — those disagree
   (`VctNumber` `19.10` is the channel Jellyfin calls `19.1`).
4. A transcode whose channel has no session and no in-progress recording is an orphan.

### Safety rails

- `POST /RogueStreams/Kill/{pid}` **recomputes the orphan set before acting** and refuses a
  pid whose channel is protected.
- If a live session is playing but Jellyfin will not report its channel, the plugin
  refuses to kill anything rather than guess.
- Killing releases the tuner: the lease dies with the process reading it.
- Administrator role required on both endpoints.

## Install

### From the plugin repository (after CI publishes it)

Dashboard → Plugins → Repositories → add:

```
https://<owner>.github.io/<repo>/manifest.json
```

then install **Rogue Streams** from the catalog. The workflow publishes that manifest to the
`gh-pages` branch on the first `v*` tag.

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
jprm plugin build . --version 1.0.0.0 --output-dir ./artifact
jprm repo add ./artifact/*.zip --output-dir ./repo
```

## Configuration

`plugins/configurations/Jellyfin.Plugin.RogueStreams.xml` (created on first save):

| Key | Default | Meaning |
| --- | --- | --- |
| `TunerUrl` | `` | HDHomeRun base URL used for `/status.json` |
| `GraceSeconds` | `20` | reserved; the UI reports uptime so you decide when a stream is stale |

## Compatibility

- Jellyfin 12.4.0 (`targetAbi` 12.4.0.0), `net10.0`.
- HDHomeRun tuners: the `/status.json` fields the plugin reads are HDHomeRun-specific.
  M3U and other tuner types will show transcodes but no lease panel.

## Licence

GPL-3.0.
