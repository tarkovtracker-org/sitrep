# SITREP — WARDOGS companion app (mortar solver trial)

Windows/C# helper: Ctrl+middle-click sets the mortar origin, Shift+middle-click captures a target from the visible map coordinates, and the overlay shows range, bearing, and source-backed L81 MIL (uncorrected table).

## Controls
- **Ctrl + Middle click**: capture origin (mortar position). **Shift + Middle click**: capture target. **F9**: clear (acts only while the game or the SITREP control window is in the foreground, so F9 in another application never discards your origin). F8/F7 are keyboard aliases for origin/target. A plain middle click is the game's own ping and never triggers SITREP (the button state is only polled to detect the Ctrl/Shift chord edge; nothing is consumed or synthesized).
- Always live, guarded: there is no Enable/F10 step. Capture requires a non-SITREP foreground window matching the configured executable substring or optional title fallback; explicit desktop test mode instead allows other foreground windows. Unknown/nonmatching identity blocks capture. Alt-tabbing away hides the locked overlay and drops pending work; held keys are reseeded on focus changes. With known client bounds, clicks outside the centered map square reject before capture (`OUTSIDE MAP AREA`); desktop test mode skips this map guard. Unknown bounds still cannot bypass the capture backend's client/monitor bounds checks.
- Input is sampled independently of UI/capture work; the chord modifiers, foreground and cursor anchor are frozen at the sampled edge. Plain MMB and Ctrl+Shift+MMB do not trigger capture; adding modifiers while MMB is held does not trigger. F9 clears older queued gestures. Keep the cursor still through the second snapshot (scheduled after 50 ms); movement or a >500 ms dispatch/retry delay rejects with `MOVED—TRY AGAIN`. Clipped ROIs or overlap with any SITREP window reject rather than OCR partial/self-generated labels.
- Control window (single, resizable, optionally always-on-top): game status pill (`IN GAME` / `GAME RUNNING` / `GAME NOT FOUND`), current status and solution line, hotkey guide, overlay lock/reset, and auto-saved settings (game executable name, desktop test mode, keep-on-top, debug captures). Minimizing hides it only after successful tray registration; otherwise it stays on the taskbar. The icon re-registers after Explorer restarts; failed recovery makes the window accessible again. The tray menu offers Open, Lock/Unlock overlay, Exit. Closing exits.
- Overlay HUD (over the game): status, large elevation, range, bearing, origin/target, hotkey legend. Click-through while locked. **Unlock overlay** in the control window or tray menu, drag it anywhere, then double-click it to lock and save the position.

## Baseline
- .NET 10, WPF, Windows 11 x64, borderless/windowed, SDR. Other modes unverified.

## Run
1. `pwsh -ExecutionPolicy Bypass -File scripts/setup-model.ps1`
2. `dotnet build Sitrep.slnx -c Release`
3. `src/Sitrep.Desktop/bin/Release/net10.0-windows/win-x64/Sitrep.exe`
- The control window is the only window you interact with; the overlay HUD appears over the game automatically while the game is in the foreground and is click-through so it cannot hold buttons. Closing the control window (or tray → Exit) exits everything.
- Config: %LocalAppData%/Sitrep/config.json — settings in the control window save immediately; you can also edit it by hand. `GameProcessName` (default `wardogs`) is matched case-insensitively as a substring of the foreground window's executable name; the Settings card shows the last non-SITREP foreground app to help you find the right name. `ForegroundTitleContains` remains as an optional title-match fallback. Overlay position: OverlayLeft/OverlayTop (`null`/absent = default top-right; negative values from monitors left of or above the primary are kept; Reset restores the default). Debug captures: %LocalAppData%/Sitrep/captures (50 records max: each is one PNG plus matching JSON, "Open folder" button).

## Verification and local trial artifact
- `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1` — public clean-clone gate: checksum-pinned model setup, locked restore, zero-warning Release build, tests, locked self-contained publish, packaged self-test/integration checks, failure-path probes, and all five committed negative images.
- `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1 -PrivateFixtures` — additionally requires local `MapCords.png` (crop `900,640,300,180`, expected x101.53 y107.77) and `MapPing.png` (crop `900,300,300,160`, expected x101.66 y110.10). Private screenshots are never uploaded by CI.
- Artifact: `out/Sitrep-win-x64/Sitrep.exe` and `out/Sitrep-win-x64.zip`. Launch from the complete extracted folder; Visual C++ x64 runtime must already be installed. No installer/runtime downloads occur in the app.
- Actual results and remaining display/game checks: [docs/VALIDATION.md](docs/VALIDATION.md). Synthetic OCR success is dependency evidence, not proof of game-font accuracy.

## Testing in game
1. Confirm explicit publisher/developer permission **before running SITREP alongside the game**. Input observation starts automatically; foreground guards are not permission. Gate E remains incomplete.
2. Open the game in borderless/windowed (SDR), open its map.
3. Start the app. The header pill should read `GAME RUNNING`, and `IN GAME` once the game window is in the foreground. If it stays at `GAME NOT FOUND`, type the executable name shown as "Last seen app" into *Game executable name* while the game is focused.
4. Stand at the mortar, hover its map position and press Ctrl + middle click (overlay shows ORIGIN SET—AWAITING TARGET).
5. Hover a target on the map, press Shift + middle click. Overlay should show the same numbers as the map labels plus range/bearing/MIL.
6. Try F9 (clears), alt-tab out and back (overlay hides while unfocused, then shows WINDOW LOST until a fresh target; old solution is gone), a click in the gutter outside the map (`OUTSIDE MAP AREA`), and Unlock overlay → drag → double-click to lock. Game-window closure clears the origin too.

## Diagnostics
`Sitrep.exe` is a GUI-subsystem process. Do not trust direct PowerShell invocation to wait or set `$LASTEXITCODE`; use the same helper as CI (it redirects output and checks the actual process exit):

```powershell
. ./scripts/common.ps1
$exe = (Resolve-Path 'out/Sitrep-win-x64/Sitrep.exe').Path
Invoke-PackagedCheck $exe @('--self-test')
# Negative fixture (committed, exit 2):
Invoke-PackagedCheck $exe @('--diagnose-image', (Resolve-Path 'tests/fixtures-neg/conflict.png').Path,
  '--report', "$PWD/out/conflict.json") -ExpectedExit 2
# Or local user fixture if supplied (gitignored MapCords.png, exit 0):
# Invoke-PackagedCheck $exe @('--diagnose-image', (Resolve-Path 'MapCords.png').Path,
#   '--crop', '900,640,300,180', '--report', "$PWD/out/origin.json")
```

Self-test is dependency smoke. Image diagnosis shares live OCR; exit 0 means accepted, 2 means OCR/dependency rejection, 1 means invocation/file/error failure. Reports preserve raw recipe text and engine confidence (not a correctness probability). Debug mode stores at most 50 complete PNG/JSON records (100 files) locally, with unique names and serialized writes across SITREP instances. Retention removes pairs together; failed writes are rolled back and later saves repair interrupted-write remnants. Locked/unwritable storage remains best effort and never changes the OCR result. The old unbounded `records.log` is retired.

Missing config uses defaults without writing a file. Invalid/unreadable config is preserved and produces one actionable startup error; repair or rename `%LocalAppData%/Sitrep/config.json`. Settings saves validated values atomically.

## Limits
- L81 only, 132–684 m, linear table, no terrain correction. Outside limits shows OUT OF RANGE. Missing/corrupt bundled model or table prevents startup with one error. The state layer suppresses MIL if a table is unavailable. Failures never show plausible MIL.
- The overlay includes origin, target, and the uncorrected-table marker. No claim of real-game validation or public redistribution clearance: see [third-party notices](THIRD-PARTY-NOTICES.md) and [data provenance](src/Sitrep.Core/Data/PROVENANCE.md).
- Local-only processing, no network/telemetry. Publisher permission unresolved — get explicit approval before in-game use. No memory read, injection, or input synthesis.
