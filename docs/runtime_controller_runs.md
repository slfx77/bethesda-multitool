# Xbox controller runs

Controller scenarios use the instrumented Xenia bridge and the existing runtime CLI.
CLI26 passed 427 controller checks; CLI27 passed 13 launcher checks and the actual JSON/private-log control. July boot/connect was observed; live button delivery remains pending. [CLI27 / 13 launcher checks](../artifacts/prototype-runtime/xenia/gamepad-binding-001/managed27-validation-001.json) · [Actual output/closure control](../artifacts/prototype-runtime/xenia/gamepad-binding-001/output-control-001/revalidation-001/validation.json). [427 controller checks](../artifacts/prototype-runtime/xenia/gamepad-binding-001/managed-validation-002.json) · [42 native fixtures](../artifacts/prototype-runtime/xenia/gamepad-binding-001/native-fixtures-004/validation.json) · [Native005](../artifacts/prototype-runtime/xenia/gamepad-binding-001/full-build-005/validation.json).

Prepare candidate file identities, then a fresh profile containing copied configuration,
saves and the exact scenario:

```text
runtime guest-prepare --emulator XENIA.exe --guest GAME.xex --data DATA --output guest.json
runtime guest-prepare-isolated --guest-manifest guest.json --game GAME_ROOT --config CONFIG.toml --saves SAVES --scenario scenario.json --output NEW_RUN
```

Use physical directories. A nonempty save directory requires `--initial-save` with
the selected relative path. An empty directory starts without a save. Optional
`--game-config` copies a per-title configuration. Every run uses a new output directory.

A single Start press:

```json
{
  "actions": [
    { "kind": "gamepad-pulse", "gamepad": {
      "slot": 0, "buttons": 16, "holdMilliseconds": 100,
      "deadlineMilliseconds": 1000
    } }
  ],
  "observeMilliseconds": 3000
}
```

Supported buttons are D-pad, Start, A and B. Hold duration is 20–500 ms; the
deadline must exceed the hold and cannot exceed 2000 ms. Opposing D-pad directions
are rejected. The owned game window must be foreground.

Launch with a bounded lifetime. Use the emitted process ID from a second terminal:

```text
runtime guest-launch --profile NEW_RUN/runtime-guest-profile.json --seconds 300
runtime run --pid PID --profile NEW_RUN/runtime-guest-profile.json --scenario scenario.json --output NEW_TRACE.ndjson
runtime import NEW_TRACE.ndjson --gamepad NEW_CONTROLLER_REPORT.json
```

The launch command requests normal window closure when its lifetime expires or it
is cancelled. Its receipt records the process identity, closure result and original
file verification. `StillRunning` retains the process ID for follow-up.

Before actions, the bridge verifies the running process, executable, effective
configuration, writable routes and pinned files. This binding has a separate
120-second deadline. Scenario timing starts after binding.

Each pulse links its request, binding, guest-observed press and release, and terminal
result. `Observed` requires the matching states, neutral analog inputs, packet and
timing checks, and no event loss. The report preserves source locations and diagnostics.
Import's normal summary describes trace transport; the controller report carries
the pulse validation results.

Stop, cancellation, a missing release or invalid receipt prevents subsequent scenario
actions. Capture/run progress goes to stderr. `guest-launch` emits a JSON progress stream on stdout; child output and the engine log stay in the private run directory. [Historical CLI26 output failure](../artifacts/prototype-runtime/xenia/gamepad-binding-001/live-checks-001/july/validation.json).

Source and validation receipts are retained under
[`gamepad-binding-001`](../artifacts/prototype-runtime/xenia/gamepad-binding-001/).
