# Live PC New Vegas control

One running game and one `runtime live` process serve console commands, investigation scripts, timed keyboard/mouse sequences, snapshots, and save resets. Trace capture is optional. Xbox and GUI integration are separate work.

Investigation scope (2026-10-02): automated gameplay captures stay short and isolated, with one specific observation and a bounded deadline. Dead Money and Honest Hearts gameplay checks are reserved for the user's manual review. Static analysis of their records and scripts can continue.

## Start once

Prepare through the existing `runtime prepare-isolated` command with `--reuse-installation`, `--save-file`, and `--live-control`. The latter sets private background/windowed input settings and disables xNVSE's compiled-source cache. Start `runtime live-launch --profile ...` and keep that controller running. Later launches reuse the same profile, saves, and installation; `--bridge PATH` stages an updated bridge DLL while the game is stopped.

```powershell
btool runtime live --pid GAME_PID --session nv
```

In another terminal:

```powershell
btool runtime send --session nv --request tools/NvseRuntimeBridge/examples/live/status.json
btool runtime send --session nv --request tools/NvseRuntimeBridge/examples/live/baseline.json --wait
btool runtime send --session nv --request tools/NvseRuntimeBridge/examples/live/move-look.json --wait
btool runtime send --session nv --request tools/NvseRuntimeBridge/examples/live/reset.json --wait
```

Run baseline creation after loading the neutral starting state. It creates a unique named `.fos` and `.nvse` pair, reports their paths, and pins that save for this game process. F5 uses the profile's separate quicksave slot. Live launch sets `SLocalSavePath` to a profile-specific virtual directory mapped to private `documents/Saves`; existing profiles are upgraded without moving saves. A real directory at that virtual path prevents launch. Reset cancels work, releases input, restores owned settings, loads the pinned save, and checks the player's cell and pose. Its deadline is 30 seconds.

The launch-state file records `engineExitCode`. Use that field to assess game shutdown; the controller's exit code reports the launcher operation.

## Requests

| `op` | Fields | Result |
| --- | --- | --- |
| `status` | — | Active job, scripts, baseline, owned settings, input diagnostics |
| `console` | `command` | Engine execution status |
| `eval` | `expression` | Typed engine value |
| `setting.read` | `name` | Numeric GMST identity, value, and ownership |
| `setting.set` | `name`, `value` | Verified GMST write; retains its original value |
| `snapshot` | `fields` | Selected player position, rotation, cell, health, disabled state, frame, or `message.menu` |
| `baseline` | `name` | Save job and pinned save pair |
| `reset` | — | Verified setting restoration, load job, and restored pose |
| `script.load` | `name`, `file` or `source` | New compiled revision |
| `script.run` | `name`, optional `intervalMs`, `count` | One call or repeated calls |
| `script.stop` | Optional `name` or `jobId` | Stops scheduled script calls |
| `script.status` | — | Script revisions and call counts |
| `sequence.run` | `sequence` | Native input sequence job |
| `job.status` | `jobId` | Current or retained terminal result |
| `job.stop` | Optional `jobId` | Priority Stop; omission stops current work |
| `jobs` | — | Broker's recent jobs |
| `session.status` | — | Connection identity and log status |
| `session.refresh` | — | Refresh loaded plugin order and changed plugin hashes |

Long jobs return `status: running` and a string `jobId` immediately. `--wait` prints progress and the terminal result. Other clients can read or stop the job meanwhile. One state-changing job runs at a time. A replacement for the currently repeating script is allowed between calls.

Console execution status reports dispatch; use `eval` or `snapshot` to verify its outcome. During an active job, evaluations accept simple player getters, menu queries, plugin identity queries, and numeric literals.

Use `setting.set` for numeric game settings restored by reset or disconnect. Up to 16 settings retain their first observed value across repeated writes. Restoration verifies raw readback before loading the baseline; a failure preserves ownership and appears in `status` for retry. Example: [set-run-speed.json](../tools/NvseRuntimeBridge/examples/live/set-run-speed.json).

## Edit and run `.gek` scripts

`script.load` resolves its `.gek` file relative to the request JSON file. Edit the file and send the same request again. Compilation failure retains the previous revision. Diagnostics include source checks and a bounded source excerpt when xNVSE supplies no text. Example files are in [examples/live](../tools/NvseRuntimeBridge/examples/live).

Functions take no arguments and may return a value with `SetFunctionValue`. Without `intervalMs`, `script.run` calls once. With an interval and no count, it repeats until stopped. Revisions are compiled, swapped, and retired on the game thread.

Investigation functions are short synchronous calls. Loops, callbacks, nested function/lambda ownership, and load/save/quit operations are rejected; scheduling and reset belong to the bridge. Stop prevents further calls.

## Timed input

```json
{"op":"sequence.run","sequence":{"version":1,"steps":[
  {"type":"input","durationMs":2000,"keys":["W","LSHIFT"]},
  {"type":"input","durationMs":500,"mouse":{"dx":120,"dy":0}},
  {"type":"input","durationMs":100,"keys":["E"]},
  {"type":"wait","durationMs":100},
  {"type":"snapshot","fields":["player.position","player.rotation"]}
]}}
```

Each input step replaces the held state. Keys use uppercase names; mouse buttons use `left`, `right`, `middle`, `x1`, `x2`, or `button6`–`button8`. Relative mouse deltas are distributed over the duration. Completion, Stop, reset, and disconnect release input. Sequences allow up to 128 steps and one hour total. Retained snapshots are capped at 48 KiB; overflow returns `snapshot-result-limit` with the earlier snapshots.

Input is local to the game process. Physical typing is suppressed while backgrounded or automated; ordinary foreground input returns when idle. `input.backgroundPollingObserved` reports actual game-loop and keyboard/mouse polling in the background. Before/after positions measure movement; durations do not guarantee distance.

Background movement and turning are verified with VS Code focused. The tested message menu requires foreground focus for Enter selection. Switching away can open the pause menu; check `MenuMode` and dismiss it before running a movement sequence.

## Evidence and verification

Current measured results and remaining checks are in the [PC validation report](runtime_live_validation.md).

The broker records identity once and compact request/results in a new log, capped at 64 MiB. Source and sequence bodies are represented by hashes. `session.status.loggingFull` reports the cap. No game-installation copies, screenshots, or full traces are generated per command.

The opt-in [verification driver](../tools/NvseRuntimeBridge/examples/live/verify_live.py) runs against an already connected session:

```powershell
python tools/NvseRuntimeBridge/examples/live/verify_live.py --session nv --phase control --output control.json
python tools/NvseRuntimeBridge/examples/live/verify_live.py --session nv --phase input --output input.json
python tools/NvseRuntimeBridge/examples/live/verify_live.py --session nv --phase scripts --output scripts.json
```

Load a neutral save before `control`; keep VS Code foreground for `input`. `scripts` replaces a running function 100 times, checks its returned values and owned allocations, rejects a broken revision, and verifies Stop.

`load-hvradio.json` and `run-hvradio.json` exercise retail `HVRadioMsg` (`FalloutNV.esm:000AF687`), the control for the article's removed repair choice. The function sets Explosives to 40 and opens the message. `message-menu.json` evaluates `MenuMode 1001`; capture the visible choices, focus the game, use `choose-first.json`, inspect again, and reset. The stored retail message has three buttons; the November prototype has four. Runtime observations apply to the tested retail build. The separate `message.menu` snapshot currently decodes single-button messages.
