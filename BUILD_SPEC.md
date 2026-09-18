# WARDOGS Mortar Assistant — MVP build specification

Revision: 3 • Research checked: 16 September 2026 • Guarded-capture policy reconciled: 18 September 2026

## 1. Objective and execution contract

Implement the smallest maintainable Windows/C# utility that supports:

**Set mortar with Ctrl+middle-click → hover a map target → Shift+middle-click → read visible coordinates → display range, compass bearing, and a source-backed L81 elevation setting.**

Prioritize correct results and explicit failures, then reproducible deployment, usability, and measured performance. Visual polish is not a deliverable. A failure message is preferable to a plausible but incorrect solution.

Read existing repository instructions and inspect the working tree before changing anything. Reuse sound existing code. Begin implementation after a short preflight; do not generate another competing specification or a speculative roadmap. This file is the product specification, not a replacement for higher-priority agent or repository instructions.

Maintain one short `docs/VALIDATION.md`: decisions, executed checks, actual results, blockers, and the next actionable step. Do not maintain duplicate planning documents. For every new dependency or alternative implementation, identify the observed failure it addresses and the test that will prove the change useful.

Start with one OCR engine, one capture backend, and at most two preprocessing recipes. After two distinct unsuccessful fixes to the same blocker, report evidence and the missing input rather than repeatedly trying similar changes. Continue independent work when useful, but do not label a blocked gate as passed. Do not repeatedly retest working milestones without a relevant code change.

Use one implementation owner. Optional reviewer agents may inspect narrowly scoped changes; do not create parallel competing implementations, recursive agent trees, or mandatory repeated research passes.

## 2. Evidence, assumptions, and permissions

The user's screenshots show these labels, in different visual positions:

- A, hovering: `x101.53`, `y107.77`.
- B, after a middle-click ping: `x101.66`, `y110.10`.

These are human-readable fixture expectations, not evidence that an OCR engine already recognizes them. Verify which actual image files are available. Do not assume both attachments survived with distinct filenames, fabricate missing fixtures, or assume a resolution from the chat preview. Keep user screenshots and crops local unless their inclusion in the repository is explicitly approved.

The current Schaulers page uses 100 m grid squares, describes interpolated sight readings, and gives an approximate L81 range of 120–700 m. The inspected Apollyon data instead configures L81 limits of 132–684 m and includes samples beyond those limits. These sources are not interchangeable authorities. [S1–S3]

Unproven until tested: OS cursor alignment with the game map point, label offsets near edges, capture timing after a ping, OCR accuracy, display-mode compatibility, and the firing data's agreement with the user's game build.

Do not claim developer approval, anti-cheat compatibility, or compliance merely because the utility uses screenshots. The published WARDOGS terms restrict unauthorized third-party software; permission for this exact workflow has not been established. Revision 3 uses always-live **guarded input observation**, with no Enable/F10 barrier: only explicit user capture gestures in an allowed foreground window can capture pixels. This technical policy is not permission for live use. Obtain explicit publisher/developer approval before starting SITREP alongside the game or performing Gate E; offline image tests and local non-game desktop checks do not require game access. `docs/VALIDATION.md` is authoritative for executed evidence and remaining permission-gated checks. [S4]

Never read/write game memory, inject code, hook the renderer, modify game files, inspect game network traffic, synthesize game input, automate aiming/firing, or bypass/hide from anti-cheat. Do not request administrator privileges or disable security software as a compatibility fix. Foreground window identity and ordinary public window metadata are permitted technical inputs; that is not authorization from the game publisher.

## 3. Supported baseline and dependencies

For a new repository use C#, .NET 10 LTS, WPF, Windows 11 x64, borderless/windowed gameplay, and SDR as the initial support baseline to validate. Existing supported targets may be retained when changing them adds unnecessary work. Other Windows versions, HDR, and exclusive fullscreen are unverified unless actually tested—not promised compatibility. .NET 10 is currently an LTS release. [S5]

Select an actual installed or officially available stable SDK. Pin its full SDK version in `global.json`, disable prereleases, and use the same SDK in CI. For reproducibility with lock files, use an exact SDK policy and intentional updates. Pin package versions, commit NuGet lock files, and use locked restore in verification. Do not confuse a runtime patch number with an SDK version. [S6–S7]

Default OCR candidate: the `Tesseract` .NET wrapper with one English LSTM model. Verify its current package compatibility in a tiny Windows smoke test before integrating it. Do not assume NuGet installation alone supplies every runtime dependency: its documentation identifies native Visual C++ runtime and language-data requirements. Record native architecture, model origin/version/hash, and notices. Use the official model source; a pinned `tessdata_fast` English model is a reasonable first candidate. [S8–S9]

Do not choose `Windows.Media.Ocr` as a supposedly dependency-free unpackaged fallback: Microsoft's supported desktop usage requires package identity. Do not introduce MSIX solely to dodge an uninvestigated Tesseract failure. [S10]

Keep model/native binaries out of ordinary generated-source commits. A short, idempotent setup script may retrieve a pinned model from its official source and verify its checksum. Include the resulting model and required redistributable application files in the published folder. Do not download dependencies at application runtime. Disclose any separately required Visual C++ runtime.

## 4. Minimal user interface and controls

Provide one control window (status, hotkey guide, overlay lock, settings) that exits the whole application when closed and disposes its workers/handles, even if the overlay is still open. Minimizing it may hide it only after native `Shell_NotifyIcon` registration succeeds; otherwise retain taskbar access. Re-register after `TaskbarCreated` and reveal a hidden window if recovery fails. No additional UI frameworks. Provide a separate small, non-activating, click-through gameplay overlay that may be temporarily unlocked for repositioning and re-locked in place.

Defaults (revision 3; the earlier F8/F7/F10 scheme is superseded):

| Control | Action |
|---|---|
| Ctrl + middle mouse | Capture/re-establish mortar origin |
| Shift + middle mouse | Capture target |
| F9 | Clear origin, target, and solution (only while the game or SITREP's control window is in the foreground) |
| F8 / F7 | Keyboard aliases for origin / target |

An unmodified middle click is the game's own ping and never triggers SITREP capture. Both triggers use the same capture pipeline; there is no second calculator implementation. Input observation starts enabled, but capture requires a non-SITREP foreground window matching the configured executable substring or optional title fallback, or explicit desktop test mode. Unknown/nonmatching identity blocks capture. When client bounds are known, clicks outside the centered map square reject as `OUTSIDE MAP AREA`; desktop test mode skips this map guard. Unknown bounds do not bypass the capture backend's client/monitor bounds checks. Normal game mouse/keyboard input must not be consumed or synthesized.

Keep a small local JSON configuration only for proven necessities: game process name, optional foreground title match, cursor-relative capture offsets/scale, overlay position, desktop test mode, and debug mode. Ship documented defaults. No calibration or keybinding GUI is required.

Identify the real foreground game window by its executable name (`GameProcessName`, default `wardogs`, user-editable and shown alongside the last observed foreground executable so a wrong default is obvious) or a user-configured title match. Unknown window identity must leave capture disabled, not silently remove the guard. Recheck the foreground HWND before capture and before publishing a result. A separately explicit desktop test mode may target a local test window; it must not bypass guards in normal operation.

Show weapon/data profile, range in metres, azimuth in degrees, elevation in game MIL, origin, target, and concise status. Mark table-based output as such. Keep confirmed origin in memory only; the user must reset it after relocating, dying, or changing maps. Do not attempt automatic session/map/origin detection beyond recognizing that the attached game window closed.

## 5. State and concurrency invariants

Use a small explicit state model and immutable request/result records, not an event-bus framework.

Every capture request owns its sequence number, role (origin/target), origin revision, foreground HWND, event timestamp, cursor anchor, ROI, and captured pixels. Never combine X from one frame with Y from another.

With no usable confirmed origin, target triggers show `SET MORTAR (CTRL+MMB)`; they do not enqueue OCR or supersede a pending origin capture.

Immediately invalidate the active firing solution when a new origin/target request begins. During processing show `READING`, not an old solution presented as current.

A successful origin capture establishes the origin and clears the previous target. An origin capture failure leaves no *usable* newly confirmed origin: require a successful origin capture before targeting. Previous values may remain diagnostic-only, never silently active after an attempted origin change.

A target failure preserves the confirmed origin but invalidates the active target/solution. It must show `TARGET OCR FAILED` or a specific reason; old numbers must not look actionable. A valid out-of-range target may show range/bearing but must show `OUT OF RANGE` instead of elevation.

F9, disable, game-window loss/closure, and origin replacement invalidate outstanding work. On foreground loss, hide the overlay and discard pending results; keep the confirmed origin but require a fresh target after returning. Game-window closure clears all positions. A completion from an earlier generation must never restore cleared or superseded state.

Keep one OCR worker/engine instance and at most one pending newest request. Replace and dispose superseded queued captures. Do not run overlapping OCR calls on the same engine or build an unbounded queue. Native OCR need not be forcibly interrupted; discard its result when its request is obsolete. Process OCR off the WPF UI thread and marshal only finished state to the dispatcher. [S11]

## 6. Input, capture, and localization

Use high-bit `GetAsyncKeyState` polling plus explicit up-to-down edge detection; never its unreliable low-bit press-history flag. A dedicated sampling thread polls approximately every 10 ms independently of synchronous UI/capture/OCR work. Freeze modifiers, foreground identity, timestamp and cursor with each edge; dispatch through a bounded mailbox. Plain MMB and Ctrl+Shift+MMB do not capture; modifier changes while MMB is held do not create new edges. F8/F7 are aliases, with origin taking precedence for simultaneous origin/target triggers; F9 supersedes captures from the same sample and clears older queued work. Reseed on enable/focus transitions; focus round-trips invalidate pending work even if the UI was blocked. Reject moved or excessively delayed anchors, never retarget a queued gesture to the later cursor position. [S12]

Polling can miss a press that begins and ends between samples. Measure missed/duplicate triggers under game load. Only if demonstrated, replace that input component with documented Raw Input/`WM_INPUT` background observation; do not introduce a global mouse hook. Do not request input capture or suppress legacy messages. No second concurrent input implementation. [S13]

Begin with `Graphics.CopyFromScreen` for a small screen region. Microsoft documents it as copying screen pixels; it does not promise WARDOGS compatibility. If captured pixels are black, stale, or wrong, diagnose before changing OCR. [S14]

Use PerMonitorV2 awareness and explicitly distinguish physical screen pixels, game client coordinates, image pixels, and WPF device-independent units. Account for monitor origins, including negative coordinates. Intersect capture bounds with the visible game client and relevant monitor; account for crop offsets. A clipped label is a failure, not a reason to parse a partial value. [S15]

First prove that `GetCursorPos` tracks the map point used for the displayed labels. The API returns the Windows cursor position; that alone does not establish the game's coordinate-label anchor. Use diagnostic captures to establish actual offsets. [S16]

Start with one cursor-relative ROI sized from the supplied original-resolution image, roughly 300–400 by 150–250 physical pixels as a tunable hypothesis. Verify top/bottom/right offsets and edge behavior. Keep ROI construction in one named method with tests. Scale and DPI are not interchangeable; validate the user's game UI scale rather than assuming it follows Windows DPI.

Capture pixels as close to the triggering event as practical, before queueing expensive OCR. Freeze that event's anchor; never take a delayed screenshot around wherever the cursor has since moved.

Allow one bounded retry, initially around 40–60 ms after the first snapshot. Prefer collecting the second small snapshot before expensive recognition when timing evidence requires it. Recheck cursor movement and foreground state; if the labels can no longer be associated with the original point, return `MOVED—TRY AGAIN`. Conflicting valid results must be rejected, not resolved by taking the first one. No continuous video or rolling buffer in the MVP.

Do not infer map-open state by counting map-key presses. Require both coordinate labels with the expected local spatial relationship; include non-map screens as negative tests. Do not reconstruct the map, OCR grid labels, or detect the ping popup as the primary signal.

Keep the overlay outside the coordinate capture region. Reject a capture whose ROI overlaps this application's windows unless an explicitly tested method removes that contamination. Otherwise the OCR could read the assistant's own coordinates.

If the cursor hypothesis fails, allow one targeted fallback experiment using a configured map-panel ROI and local X/Y-label detection. Do not broaden into whole-screen OCR or map geometry. If ordinary capture fails in the supported baseline, report the evidence before adding one alternative backend. Exclusive-fullscreen support alone is not a reason to add it.

## 7. OCR acceptance and diagnostics

Use one shared recognition pipeline for image replay and live capture. Reuse the initialized engine; dispose images/pages deterministically. Start with sparse-text segmentation for the two separated labels or two tight label crops when their locations are proven. Bound word/token positions relative to the captured anchor.

Try modest upscaling, grayscale/contrast, and suitable polarity/padding. Tesseract's guidance discusses dark text on light backgrounds, appropriate borders, and segmentation choices; indiscriminate thresholding is not an accuracy guarantee. Use a second thresholded recipe only when it improves measured fixtures without adding false accepts. No OpenCV, model training, or cloud OCR for the initial implementation. [S17]

Parse labelled X and Y independently, with invariant culture and full token boundaries. Initially require the observed two fractional digits, explicit decimal separator, and 1–3 integer digits; do not assume all coordinates have 2–3 integer digits. Support line/order/whitespace changes. Reject partial numeric matches, missing axes, duplicate/conflicting axes, unsupported signs, non-finite values, and wrong decimal precision. Apply actual verified map bounds when available; never invent bounds from the two sample positions.

Whitespace and decimal-comma normalization are acceptable after token isolation. Do not globally replace arbitrary letters with digits, insert a missing decimal, or accept `x10166` as `x101.66`. Initially reject ambiguous letter/digit substitutions. Add a specific correction only with real failing fixtures, explicit tests, and corroborating recognition. Never repair a coordinate to make its range fit the weapon or resemble the previous target.

A character whitelist and engine confidence are hints, not proof. Preserve raw text and, when available, token boxes/confidence for diagnosis. Never average disagreeing coordinates or join partial results from different captures. If confidence is shown, identify it as an engine score, not an estimated probability of correctness.

Debug mode, off by default, may store only user-triggered ROI images and small structured records: request/role, cursor/ROI/DPI, timings, raw text, parsed tokens, rejection reason, and source profile. Store under local application data with at most 50 image/metadata pairs (100 files), collision-resistant names, serialized writes and whole-record retention. Roll back failed writes and repair interrupted-write remnants on subsequent successful saves; diagnostics are best effort on locked/unwritable storage. Do not capture unrelated windows. No continuous screenshots, network upload, or telemetry. Gitignore local fixture/capture/log directories and exclude private images from CI artifacts.

## 8. Coordinate math and ballistic data

For the supported 100 m/grid profile, with east-positive X and north-positive Y:

```
dx = (target.X - origin.X) * 100
dy = (target.Y - origin.Y) * 100
range = sqrt(dx*dx + dy*dy)
azimuth = normalize_degrees(atan2(dx, dy) * 180 / pi)
```

Keep full precision until display. Zero range has undefined bearing and no valid firing solution. Handle non-finite inputs and normalize bearing to [0,360), including display rounding. The current reference material supports the 100 m conversion and compass conventions; verify the selected in-game profile. Do not generalize this to unverified maps. [S1, S18]

Required independent fixture:

```
Origin: 101.53, 107.77
Target: 101.66, 110.10
Delta:  13 m east, 233 m north
Range:  233.36237914454 m
Bearing: 3.19344927328 degrees
```

Use a 1e-6 tolerance for these pure numerical tests, not for OCR correctness.

Support only L81. Prefer the inspected Apollyon L81 profile after confirming the specific data's provenance and license; pin its source revision/hash. Treat Schaulers as a separate comparison, not an automatically identical source. Use another reference only for a documented data or permission problem. Record source URL, retrieval date, license/attribution, coordinate scale, units, permitted in-game weapon limits, and interpolation behavior in the local data profile or its provenance note. Verify whether the particular copied data is covered by the stated license; a repository license does not cover every third-party asset. Do not import game imagery or unrelated assets. [S2–S3, S19]

Do not invent a physics model, convert from real-world mortar data, or reuse earlier illustrative MIL numbers. Interpolate only inside both the selected table's coverage and the weapon's permitted limits. Validate finite samples, strictly increasing range for this single trajectory, duplicate ranges, and valid units. Reject corrupt data; never silently clamp/extrapolate or average conflicting reference sources.

As inspected on the research date, Apollyon's L81 data includes 229 m → 760 MIL and 239 m → 750 MIL. Linear interpolation of those samples at the numerical fixture's range gives approximately 755.63762085546 MIL. That is a source-specific calculation, not a verified Schaulers result or an in-game measurement. Recheck and pin the source before using it as a golden fixture. [S2]

Compare at least five in-range points, including interval interiors and near-boundary points, against the selected external reference. Record expected/actual/error and rounding. Also test both exact weapon limits and just outside them. When comparing Schaulers with another source, document disagreements; do not require contradictory sources to match. Golden expectations must not be generated by the production code under test.

Without sufficiently sourced data, range/bearing can work but elevation must show `TABLE UNVERIFIED/UNAVAILABLE`; the complete firing-solution gate remains blocked. Never ship placeholder MIL as real output. No terrain/height correction in MVP: describe the result as an uncorrected table solution, not a guaranteed impact point.

## 9. Overlay and deployment

Use a layered, click-through, non-activating topmost overlay. Apply native styles after its handle exists; preserve existing style bits and inspect failures. WPF `IsHitTestVisible=false` alone is not the cross-process input contract. Microsoft's layered-window documentation describes mouse pass-through with `WS_EX_TRANSPARENT`; activation must be handled separately. Verify actual mouse pass-through over the visible overlay. [S20–S21]

Use ordinary readable text, a simple background, and explicit units. No animation, fade logic, themes, custom fonts, icons, or elaborate view-model framework. Code-behind is acceptable for view-only behavior; calculation/parsing/state logic must remain testable outside the view.

Publish a `win-x64` self-contained **folder** and ZIP it for local handoff. Do not add an installer, single-file bundling, trimming, AOT, signing infrastructure, or an updater. Self-contained .NET publishing supplies the .NET runtime, not every third-party native prerequisite; test the actual published directory, not only `dotnet run`. [S8, S22]

Use `AppContext.BaseDirectory` for bundled read-only data and a writable local application-data directory for settings/logs. Test launching from an unrelated working directory. Detect missing models/native dependencies/config/data early and show one actionable startup error. No silent bootstrap downloads or administrator requirement.

## 10. Repository structure and verification

For a new repository, three small projects are justified to keep pure tests runnable without Windows:

```
src/WardogsMortar.Core/       # models, parser, math, interpolation, state rules
src/WardogsMortar.Desktop/    # WPF, Win32 input/capture, OCR adapter, diagnostics
tests/WardogsMortar.Tests/   # pure deterministic tests; no game dependency
scripts/                     # minimal setup/verify/publish commands
docs/VALIDATION.md
```

Use `net10.0` for Core/Tests and `net10.0-windows` with x64 publishing for Desktop. A sound existing two-project layout may be retained. Do not create projects for every feature, interface every class, or add dependency injection/service-location frameworks. One small fakeable capture/OCR boundary is acceptable where it makes race tests practical.

Enable nullable analysis, use standard .NET analyzers and a small `.editorconfig`, and check changed files for formatting. Do not impose arbitrary test-coverage or file-count targets. Do not silence warnings indiscriminately.

Create one Windows GitHub Actions verification job: explicit SDK, locked restore, Release build, tests, publish, and packaged OCR self-test. Follow supported official action documentation, use minimal permissions, and pin action revisions. No deployment workflow or platform matrix. Creation of the workflow file does not mean CI has run. [S23]

Respect existing changes. Work on an appropriate feature branch when available. Never reset/clean/stash other work, force-push, rewrite history, alter repository visibility/protection, publish a release, or upload user images without authorization. Make logical commits only when repository instructions/authorization permit. Do not add a project license on the owner's behalf; retain dependency and data notices.

Ignore `bin`, `obj`, IDE caches, local config, downloaded caches, output ZIPs, logs, and private samples. Do not ignore lock files or required small source/provenance files. Remove abandoned experiments. A concise `AGENTS.md` may point to this spec and verified commands without duplicating it.

## 11. Build gates and tests

**Gate A — Preflight and feasibility.** Inspect repository, SDK/OS, available screenshots, and sources. On Windows, prove the OCR engine/model loads and an unprocessed cursor ROI contains the displayed labels. Test a minimal overlay for focus/click-through compatibility early; this is not UI styling. When Windows or game access is absent, record those tests as pending and perform independent portable/static work.

**Gate B — Deterministic behavior.** Prove parser, math, table validation/interpolation, and state invalidation. Include cardinal/intercardinal bearings, reciprocal bearings, zero distance, the numerical fixture, non-finite inputs, exact endpoints, invalid tables, and conflicting/partial OCR tokens. Test clear/disable/origin-change/new-target while an old OCR completion is delayed. No earlier result may become current.

**Gate C — Image replay.** Implement a minimal `--diagnose-image` mode with explicit image path, anchor/ROI metadata, and report path, plus nonzero failure exit codes. It must use the real live recognition pipeline. Recognize A and B when their actual files are available. Include negative images/text: no coordinates, grid-only numbers, popup-only text, missing/extra digits, cropped decimals, conflicting pairs, non-map UI, and overlay contamination. Synthetic fixtures prove limited cases, not game-font accuracy. Do not block on a large dataset; additional user captures expand testing later.

**Gate D — Integrated trial build.** Wire input → captured request → OCR → state → math/table → overlay. Add `--self-test` to exercise packaged native OCR/model loading with a small synthetic label image; identify it as a dependency smoke test. Run clean verification and launch the published executable. Exercise input/focus behavior in a local desktop test window when the game is unavailable. Supply a trial artifact and report remaining real-game checks honestly.

**Gate E — User game validation.** After the user resolves permission concerns, validate on the actual Windows/display/game configuration. Record it. Check original labels against both origin and target captures, Ctrl/Shift+MMB delivery with the game's own ping still passing through, map edges/pan/zoom and the OUTSIDE MAP AREA guard, moving the cursor immediately after clicking, rapid targets, F9 during processing, alt-tab, overlay unlock/drag/lock, tray minimize/restore, and game exit. Do not add unrelated features while waiting.

Initial trial acceptance targets—not promised performance:

- Thirty deliberate target captures: at least 29 correct accepts, zero accepted wrong coordinates, all other attempts explicit rejections.
- Negative fixtures produce no accepted coordinate pair; available positive fixture labels match exactly to their displayed hundredths.
- Mathematical tests meet stated tolerance; reference elevation outputs match within documented display rounding.
- No stolen focus, blocked/doubled game pings, or resurrected stale results.
- Aim for warm end-to-end p95 at or below 500 ms and below 1% mean process CPU while enabled but idle on the recorded machine. Measure cold-start separately. Report memory behavior over a short repeated-capture session and investigate persistent growth, not normal GC fluctuations.

These sample counts do not establish a general false-positive rate. Failure of an accuracy/state criterion requires a focused fix or an explicit blocker; a missed performance target requires measurement, not speculative optimization.

## 12. Handoff and stopping rule

Use three distinct completion labels:

**Implemented:** code exists; any missing build/runtime evidence is named.

**Ready for user trial:** clean Windows build, deterministic tests, available image tests, packaged smoke test, and source-backed calculation checks have actually passed. No game compatibility or permission claim is implied.

**Game-validated:** the real-game checklist has been executed and its results/configuration are recorded. This still is not publisher approval or a guarantee against future game changes.

Deliver the small source tree, reproducible setup/verify/publish commands, local ZIP when built, notices/provenance, and concise README. README covers controls, supported baseline, startup/exit, source/data limits, local-only processing, permission uncertainty, and exact diagnostic locations.

Final report: commit/working-tree status; build/test commands and actual counts; artifact path; startup requirements; controls; reference comparisons; measured versus untested items; blockers; and the short next user test. Never fabricate runs, screenshots, test counts, timing, or approval.

Once the trial artifact and its evidence are ready, stop. No cosmetic pass, second weapon, map system, cloud service, telemetry, collaboration, plugin system, automatic updating, custom installer, input automation, or unsolicited roadmap. Await actual test results before expanding scope.

## Source index

These links support the referenced facts, not live game compatibility. Recheck mutable sources and record immutable revisions for any reused code/data.

- S1: Schaulers calculator — https://schaulers.com/tools/wardogs-mortar-calculator
- S2: Apollyon weapon data — https://raw.githubusercontent.com/apollyon-sys/wardogs-calculator/main/data/weapons.json
- S3: Apollyon feature/weapon documentation — https://raw.githubusercontent.com/apollyon-sys/wardogs-calculator/main/docs/features.md
- S4: WARDOGS EULA/addendum — https://www.wardogs.com/eula
- S5: .NET support — https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- S6: SDK selection — https://learn.microsoft.com/en-us/dotnet/core/tools/global-json
- S7: NuGet locking — https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies
- S8: Tesseract wrapper/dependencies — https://github.com/charlesw/tesseract
- S9: Official fast models — https://github.com/tesseract-ocr/tessdata_fast
- S10: Windows OCR supported desktop usage — https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr
- S11: WPF threading — https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model
- S12: Input-state API — https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate
- S13: Raw Input — https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input
- S14: Screen copying — https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.copyfromscreen
- S15: DPI model — https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows
- S16: Cursor position — https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcursorpos
- S17: OCR quality/segmentation — https://tesseract-ocr.github.io/tessdoc/ImproveQuality.html
- S18: Map scale — https://raw.githubusercontent.com/apollyon-sys/wardogs-calculator/main/docs/maps.md
- S19: Source/asset license scope — https://raw.githubusercontent.com/apollyon-sys/wardogs-calculator/main/docs/legal.md
- S20: Layered-window input — https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows
- S21: Window styles — https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles
- S22: .NET deployment — https://learn.microsoft.com/en-us/dotnet/core/deploying/
- S23: GitHub .NET verification — https://docs.github.com/en/actions/tutorials/build-and-test-code/net
