# YARG Event Mode (YAQ)

This fork of [YARG](https://github.com/YARC-Official/YARG) adds **YAQ Event Mode**: the game is driven by the local [YAQ](../yaq) queue app. Guests browse songs and join from their phones; YARG only shows the ready (difficulty select) and score screens.

## Launch

1. Start YAQ (`cd ~/Projects/yaq && npm start`).
2. Open Unity and enable **Settings → Experimental → YAQ stream**, **or** launch a built binary with event mode:

```bash
# Recommended
./YARG -event-mode -yaq-url "ws://127.0.0.1:3000/ws?role=yarg"

# Aliases / shortcuts
./YARG -yaq-event
./YARG -yaq-url "ws://127.0.0.1:3000/ws?role=yarg"   # URL alone also enables event mode
```

| Flag | Effect |
|------|--------|
| `-event-mode` / `-yaq-event` | Force Event Mode on at launch (connects to YAQ) |
| `-yaq-url <url>` | Override bridge URL (default `ws://127.0.0.1:3000/ws?role=yarg`); also enables Event Mode |

The Experimental settings toggle connects/disconnects without restarting. Launch flags still force the stream on.

## Behavior

- **Event Mode uses `EventScene`.** Launching with `-event-mode` / `-yaq-event` / `-yaq-url`, or with **YAQ stream** enabled, boots into that scene instead of the main menu.
- The idle HUD lives on Event Scene (current song, album art, players, next song, join QR). It is not drawn over the main menu, difficulty select, score, or gameplay.
- Admin launches the on-deck set in YAQ → YARG loads the normal Menu scene only for Difficulty Select, then gameplay / score as usual.
- After the score screen Continue → back to Event Scene for the next group.
- **Exit Event Mode** (`eventmode.exit`) leaves Event Scene and loads the normal Menu scene. The WebSocket stays connected so YAQ can re-enter later.
- **Enter Event Mode** (`eventmode.enter`) loads Event Scene again.
- **Hot mic** (flag `hotMic`) keeps vocal monitoring up for host announcements.
- Song library is pushed to YAQ via `library.sync` after scan completes (authoritative hashes).

## Event flags (YAQ → YARG)

Pushed as `{ type: "settings.update", flags }` on connect and when admin saves. YARG replies with `settings.ack`. Defaults:

| Flag | Default | Effect |
|------|---------|--------|
| `hotMic` | `true` | Force vocal monitoring for host talkback |
| `showUpNextHud` | `true` | Show idle OnGUI up-next / ready HUD |
| `skipMainMenu` | `true` | Hide main menu chrome when Menu Scene is used for Difficulty Select |
| `openDifficultySelect` | `true` | Open Difficulty Select on `set.prepare` / `set.launch` |
| `addTestBots` | `false` | Seat unused venue slots (`bass_01`, `drums_01`, `mic_01`, …) as bots for parts the song actually has. Does not create extra YARG profiles. |

Toggle these under **YAQ Admin → YARG event flags**.

## Enter / exit Event Mode from YAQ

While YARG is connected, Admin → **Enter Event Mode** / **Exit Event Mode** (or `POST /api/admin/yarg/event-mode` with `{ "enabled": true|false }`).

- **Exit** unloads Event Scene and returns to the normal Menu scene (menus/HUD/hot mic/set launch off) but **keeps the WebSocket** so YAQ can re-enter later.
- **Enter** loads Event Scene, resumes Event Mode behaviors, and re-syncs the library.
- Fully disconnecting still uses YARG’s Experimental **YAQ stream** toggle (or quitting the game).

## Message catalog

| Type | Direction | Purpose |
|------|-----------|---------|
| `hello` | both | Handshake |
| `library.sync` | YARG → YAQ | Authoritative song list |
| `library.request` | YAQ → YARG | Ask YARG to re-push library |
| `queue.preview` | YAQ → YARG | On-deck song + players for HUD |
| `set.prepare` | YAQ → YARG | Apply players, set current song, ready |
| `set.launch` | YAQ → YARG | Ensure Difficulty Select is open |
| `state` | YARG → YAQ | `idle` / `ready` / `playing` / `score` |
| `ready` | YARG → YAQ | Prepare acknowledged |
| `song.ended` | YARG → YAQ | Set complete + score payload |
| `settings.update` | YAQ → YARG | Event flags |
| `settings.ack` / `settings.report` | YARG → YAQ | Flag confirmation |
| `eventmode.enter` / `eventmode.exit` | YAQ → YARG | Resume / suspend Event Mode (bridge stays connected) |
| `eventmode.state` | YARG → YAQ | `{ enabled, suspended }` after enter/exit |
| `error` | YARG → YAQ | e.g. `song_not_found`, `eventmode_suspended` |

## Files

- `Assets/Scenes/EventScene.unity` — Event Mode idle scene (camera + HUD host)
- `Assets/Script/YAQ/EventMode.cs` — flags + `IsActive` + `HubScene`
- `Assets/Script/YAQ/YaqBridgeClient.cs` — WebSocket client
- `Assets/Script/YAQ/EventModeController.cs` — bridge handlers, hot mic, set launch
- `Assets/Script/YAQ/EventModeScene.cs` — scene-local idle HUD

## Notes

- Bind controllers to venue slots (`guitar_01`, `bass_01`, `drums_01`, `mic_01`, …) after **Exit Event Mode**. Re-entering reuses those profiles by name/Guid; guests only rename the slot for the song.
- Event Mode does not write YARG local scores or replays. Guest scores stay in YAQ.
- YAQ folder-scan hashes are provisional until YARG syncs; prefer launching YARG Event before guests queue when possible.
- LGPL-3.0 (same as upstream YARG). Keep this fork clearly marked as modified for events.

## Linux (Fedora) inotify

Unity Play can fail if `FileSystemWatcher` hits `fs.inotify.max_user_instances` (default 128). YARG now logs and continues without the watcher. To raise the limit (needs sudo):

```bash
sudo sysctl -w fs.inotify.max_user_instances=1024
echo 'fs.inotify.max_user_instances=1024' | sudo tee /etc/sysctl.d/99-inotify-instances.conf
```
