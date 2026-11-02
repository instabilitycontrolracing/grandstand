# ApexHUD-Native Architectural Decisions

This file is append-only. Every meaningful architectural choice gets a
dated entry here so future sessions know not just WHAT we chose but WHY.
If we ever revisit a decision, we ADD a new entry rather than editing
the old one. The trail of thinking matters as much as the current state.

---

## 2026-05-27 â€” Choose WPF over MAUI / Avalonia / WinUI 3

**Context:** Need a Windows desktop UI framework that supports transparent,
click-through, always-on-top windows with hardware-accelerated rendering.

**Considered:**
- **WPF** â€” Mature, native, what SimHub uses, supports transparency well,
  excellent SkiaSharp integration.
- **WinUI 3** â€” Microsoft's newer framework. Less mature, harder to do
  click-through transparency, smaller ecosystem.
- **Avalonia** â€” Cross-platform XAML. Mature now, but cross-platform is
  not a goal and we'd take on dependencies we don't need.
- **MAUI** â€” Microsoft's cross-platform framework. Heavier, mobile-first
  mindset, not the right tool.

**Decision:** WPF.

**Reasons:**
1. Proven for this exact use case (SimHub does it well)
2. Best-in-class window transparency support on Windows
3. SkiaSharp.Views.WPF gives us a hardware-accelerated drawing surface inside any window
4. .NET 8 LTS support window goes to November 2026, .NET 10 LTS will follow
5. No cross-platform overhead

---

## 2026-05-27 â€” Bypass SimHub for time-critical data

**Context:** ApexHUD-Web reads everything through SimHub's HTTP endpoints.
This adds 50-300 ms of latency per data point and ties us to SimHub's
polling cadence.

**Decision:** Use IRSDKSharp to read iRacing's shared memory directly. Keep
SimHub as a secondary source only for properties iRacing's SDK doesn't
expose (CrewChief integration data, etc.).

**Reasons:**
1. iRacing's IRSDK is the source of truth â€” SimHub is a middleman
2. Shared memory access is microsecond-latency
3. We gain access to 60 Hz updates instead of being limited to SimHub's poll rate
4. Removes one dependency from the race-critical path

**Trade-off:** We have to read iRacing's binary header format ourselves
(via IRSDKSharp library). The library is mature and well-tested. Risk: low.

---

## 2026-05-27 â€” Single process, no IPC

**Context:** ApexHUD-Web has 16+ processes. Each one is a potential failure
point and a memory drain.

**Decision:** ApexHUD-Native is a single .NET process that hosts all
overlay windows.

**Reasons:**
1. WPF supports multiple top-level windows in one process
2. Data flows via in-process events, not HTTP/JSON
3. No watchdog needed because there's nothing leaking
4. Restart cost goes from ~5 seconds (16 process spawns) to ~1 second
5. Memory footprint goes from ~13 GB to estimated ~150 MB

---

## 2026-05-27 â€” Personal use exemption for copyrighted assets

**Context:** The current ApexHUD-Web uses Rolex Cosmograph dial graphics,
IMSA series logos, and manufacturer marks. Distribution of these would be
copyright infringement.

**Decision:** Continue using them. This is a personal, non-distributed,
non-commercial system for Angelo and his wife. Fair use applies for
personal display in a home racing setup.

**Hard rule:** If this project is ever released, demonstrated publicly,
streamed, or shared with anyone outside Angelo's household, all copyrighted
assets must be removed or replaced with original work first. This decision
is conditional on the project remaining personal.

---

## 2026-05-27 â€” Claude Code for ongoing work, chat for design discussions

**Context:** This conversation has been working through Desktop Commander,
which is functional but slow for a multi-month software project.

**Decision:** Use Claude Code (already installed on this machine, v2.1.101)
for all code work going forward. Use chat conversations only for design
discussions, architectural decisions, and small surgical fixes.

**Reason:** Claude Code is purpose-built for this kind of work. It has
direct file access, can run builds, can iterate quickly, and doesn't burn
context window on tool ceremony.


---

## 2026-05-27 â€” IRSDK library: IRSDKSharper, not IRSDKSharp

**Context:** Verified NuGet package availability during Session 1.

**Choices on NuGet:**
- `IRSDKSharp` â€” original library. Last update September 2022. Stale.
- `IRSDKSharper` â€” actively maintained fork. v1.1.6 is recent.
- `IRacingSDK` â€” alternative implementation, also active.

**Decision:** Start with `IRSDKSharper`. Falls back to `IRacingSDK` if
we run into issues. Both are pure C# wrappers around iRacing's official
shared-memory protocol.

**Reason:** Active maintenance matters. iRacing makes occasional binary
format changes; a stale library could break silently.

---

## 2026-05-27 — SkiaSharp 2.88.x for WPF rendering, not the newer 3.x

**Context:** Phase 2 needs a hardware-friendly 2D drawing surface inside a
WPF window. `dotnet add package SkiaSharp.Views.WPF` (no version) pulls the
latest stable, **3.119.4**.

**Problem with 3.x:** SkiaSharp.Views.WPF 3.x takes a transitive dependency
on `OpenTK` / `OpenTK.GLWpfControl` (to back its `SKGLElement`). OpenTK 3.x
is a .NET Framework package, so restoring it under `net8.0-windows` emits
`NU1701` ("restored using .NETFramework... may not be fully compatible")
warnings — 9 of them per build. It also drags unused OpenGL native binaries
into our output.

**Considered:**
- **Stay on 3.119.4 + `<NoWarn>NU1701</NoWarn>`** — keeps "latest stable" but
  masks a real (if benign) compatibility note and still ships unused OpenTK +
  native GL binaries.
- **SkiaSharp.Views.WPF 2.88.8** — the mature long-stable line. Depends only
  on SkiaSharp core, `System.Drawing.Common`, `Microsoft.Win32.SystemEvents`.
  No OpenTK. Restores and builds with **0 warnings**. Still provides both
  `SKElement` (software) and `SKGLElement` (ANGLE-backed, no OpenTK) if we
  ever want GL.

**Decision:** Pin **SkiaSharp.Views.WPF 2.88.8**.

**Reasons:**
1. Genuinely clean build (0/0) with no suppression hacks — matches the
   project's established "0 warnings, 0 errors" bar from Session 2.
2. Smaller, leaner output — consistent with the whole reason this rebuild
   exists (150 MB target, not 13 GB).
3. We lose nothing we need: 2.88.x still offers GL via ANGLE if a future
   phase wants it.

**Trade-off:** This is a deliberate deviation from the session brief's
"latest stable" default. The brief pre-authorized tolerating SkiaSharp
warnings; we chose to remove their root cause instead. Revisit if a future
SkiaSharp 3.x WPF release drops the .NET Framework OpenTK dependency.

---

## 2026-05-27 — Overlay renders via SKElement (software), not SKGLElement

**Context:** SkiaSharp.Views.WPF offers two host controls: `SKElement`
(CPU raster, composited by WPF) and `SKGLElement` (OpenGL surface).

**Decision:** Use `SKElement` for the first pass.

**Reasons:**
1. Simpler — no GL context lifetime to manage inside a transparent,
   click-through window.
2. More than sufficient for the current content (a rounded plate, text,
   a dot). The host is an RTX 3080 Ti; CPU raster of a few shapes is free.
3. Keeps the door open: switching to `SKGLElement` later is a localized
   change if profiling ever shows we need GPU raster.

---

## 2026-05-27 — Overlay spans the primary monitor only (for now)

**Context:** Angelo's setup has many monitors. The overlay needs a placement
policy.

**Decision:** The Phase 2 window is positioned at (0,0) sized to
`SystemParameters.PrimaryScreenWidth/Height` — primary monitor only.

**Reason:** Multi-monitor span is a Phase 3+ design problem that deserves
deliberate thought (which monitor hosts which overlay, DPI per display,
iRacing's own window placement). A single full-primary-monitor surface is
the right minimal foundation; we widen it intentionally later.

---

## 2026-05-27 — Port strategy: read-only reference, copy assets, rebuild visuals natively

**Context:** Session 4 inventoried the legacy `../ApexHUD-Web` system (see
`PORT_PLAN.md`) to plan reaching feature parity. ApexHUD-Web is an
HTML/CSS/JS overlay suite rendered in `pywebview`/WebView2 windows, fed by a
Python HTTP server that bulk-polls SimHub (+ the benofficial2 plugin).

**Decision:** Port by **reading** ApexHUD-Web as reference and **copying its
assets** (images, fonts, transition videos) into `ApexHUD-Native/assets/`, then
**re-implementing each overlay's visuals and behavior natively** in WPF +
SkiaSharp. We do not translate HTML/CSS/JS mechanically; we rebuild each overlay
to look and behave like its legacy counterpart using the native stack. The
authoritative list of what to port is whatever `overlay_positions.json` marks
`enabled: true` (8 overlays) — not filenames, and not the stale README.

**Port order (highest-impact, lowest-risk first):** Rolex clock centerpiece →
native data layer → session bar → timing tower → featured card → battle card →
starting grid → supporting callouts (fastest-lap/pit-callout/sponsor) → polish.
Numbered, bounded session prompts live in `.claude/queue/`.

**Reasons:**
1. ApexHUD-Web is Angelo's daily driver and a no-write boundary; read-only
   reference + asset copy respects that while extracting full value.
2. A mechanical HTML→WPF translation would carry over the legacy's accumulated
   cruft (dead branches, disabled elements, the SimHub-shaped data contortions).
   Rebuilding from a clean inventory lets us port only what's actually visible
   and fix known legacy bugs in passing.
3. Copyrighted assets (dials, logos, flags) are reused under the existing
   personal-use exemption (2026-05-27 entry); they are copied, not redrawn.

**Scope notes captured during inventory:** Much of the largest legacy file
(`session-bar-ORIGINAL-v9-21.html`, ~5144 lines) is disabled/hidden in
production (track-map, sectors, battle-strip, leader-chip, the in-bar battle
mode, the rotating chip). The port plan scopes the session bar to its
production-visible elements only. The tower's scene-rotation `.mp4` transitions
and the hybrid/corner/ticker/pit-strategy overlays are deferred (see PORT_PLAN).

---

## 2026-05-27 — Data-layer translation: rebuild SimHub/benofficial2 shapes natively from IRSDKSharper

**Context:** Legacy data flows iRacing → SimHub (+ benofficial2 plugin) →
`server.py` (bulk-fetch + reshape every 250 ms) → a `NewData.*` JSON document →
overlays poll `/api/getgamedata` and read deeply-nested properties. The shapes
overlays depend on (`_leaderboard`, `benofficial2.Standings.ClassNN.RowNN`,
`_sessionState`, `_spectatedCar`, `Highlighted`, gap strings) are partly raw
iRacing data and partly **derived** by SimHub/benofficial2/`server.py`.

**Decision:** Collapse the entire chain into **one in-process C# service** in
`ApexHUD.Native.Data` that reads iRacing shared memory via IRSDKSharper and
exposes an **observable race-state model** mirroring the shapes overlays need.
No HTTP, no JSON, no SimHub, no benofficial2 on the time-critical path
(consistent with the 2026-05-27 "Bypass SimHub" decision). The shared JS engine
modules become C# services/helpers, with two simplifications justified by the
single-process native architecture:

- **Drop `name-cache.js`.** It existed only because benofficial2 exposed ~15
  rotating standings rows; the cache accreted full-field driver info over many
  polls. IRSDKSharper's DriverInfo returns the entire field at once, so the
  cache is unnecessary.
- **Defer `story-engine`/`layout-director`** to the polish phase. Their
  wall-clock-based cross-window sync was a workaround for independent browser
  windows with no shared memory; a single .NET process shares state directly.

**Key algorithms to port (specified in PORT_PLAN "Data layer translation"):**
- SessionState (0–6) → race-phase (pre-race / broadcast / post-race).
- Live-gap = `(progressAhead − progressBehind) × bestLap(behind)` where
  `progress = CarIdxLap + CarIdxLapDistPct` — the F1/IMSA position-math gap,
  smoother than `CarIdxF2Time` and runnable at full 60 Hz natively.
- Per-class leaderboard grouping/ordering, class detection, license/flag/temp
  decoding, track-name resolution.

**Trade-off / risk:** Rebuilding benofficial2's per-class standings + gap
computation from `CarIdx*` arrays + DriverInfo is the single largest piece of
work in the port (its own queued session, `02_build_data_layer.md`) and must be
correct before the tower/grid/featured/battle overlays. `_hybrid` is a SimHub
plugin construct with no confirmed IRSDKSharper equivalent — the hybrid overlay
is deferred until those fields are verified.

---

## 2026-05-27 — Data layer built early (port slot 02), not as a late "wire telemetry" big-bang

**Context:** The Session 4 brief suggested a queue that builds all overlay
visuals against mock data first and wires telemetry in a single late session
("All the visuals exist by now; this session makes them live."). The brief also
explicitly authorized tuning the queue and documenting the change here.

**Decision:** Build the native data layer **second** (`02_build_data_layer.md`,
right after the no-data Rolex clock), so every data-driven overlay from the
session bar onward is developed against **live telemetry**, not mocks.

**Reasons:**
1. The data layer is the keystone — tower, grid, featured, and battle cards all
   depend on the same leaderboard/gap/phase model. Building it once, early, and
   validating each overlay against it avoids a risky late integration where five
   complex overlays meet real telemetry for the first time simultaneously.
2. It matches the project's deliberate-pace, prove-each-step ethos and the
   rollback principle (small surgical steps, known-good checkpoints) far better
   than a big-bang wiring session.
3. Session 2 already built an IRSDKSharper data-layer proof of life; extending
   it into a real service is natural continuity, not a new direction.

**Trade-off:** The clock (session 01) is the only overlay built before live data
exists — fine, since it runs purely off the local PC clock. This is a deliberate
deviation from the brief's suggested ordering, made under the latitude the brief
granted, and surfaced to Angelo in the Session 4 summary so he can object before
session 02 runs.

---

## 2026-05-27 — Font: bundle the local RaycerRay set (not Cormorant + Inter, for now)

**Context:** Port 01 had to pick the project's bundled typeface and establish how
the native app loads a font into SkiaSharp. The brief and `PORT_PLAN.md` both
*recommend* Cormorant Garamond + Inter (the current ApexHUD-Web theme) "to
preserve the established aesthetic." The wrinkle: those are Google-Fonts **CDN**
faces — they exist nowhere locally, so adopting them means a **network download**,
which brushes the standing "stay inside the iRacing directory / ask before leaving
the repo" boundary. The 10 **RaycerRay** broadcast `.ttf` files are already bundled
in `../ApexHUD-Web/shared/fonts/` and are copy-only (zero network, fully in-bounds).

**Considered:**
- **Cormorant Garamond + Inter (download).** Matches the legacy look exactly, but
  requires fetching binary fonts from the web. I asked Angelo to approve the
  boundary-crossing download; he didn't answer, so I will not cross the boundary
  unprompted.
- **RaycerRay set (local copy).** Guaranteed-available, in-bounds, purpose-built
  racing/broadcast fonts. The live web theme abandoned them, but they read as
  "F1 broadcast," which suits this project.

**Decision:** Bundle the **RaycerRay** set (copied into `assets/fonts/`).

**Reasons:**
1. The recommended option's only blocker is acquisition, which needs a boundary
   crossing I wasn't cleared for. The in-bounds choice respects the standing rule.
2. **Zero visual cost this session** — the watch face is text-free, so the font is
   not rendered until the session bar (Port 03). The choice is reversible before it
   ever shows on screen.
3. RaycerRay is broadcast-grade and on-theme.

**Trade-off / revisit:** This is a deliberate deviation from the brief's
recommendation. If Angelo wants the exact legacy serif/sans look, revisit when a
font download (or a manual drop of the `.ttf`/`.otf` files into `assets/fonts/`) is
approved — the loader (`Assets.LoadTypeface`) is family-agnostic, so swapping is a
one-line change per overlay.

---

## 2026-05-27 — Asset layout + loading pattern; reusable drawing helpers live in Overlays

**Context:** Port 01 is the first overlay to load bundled images/fonts and the
first to draw a real broadcast graphic, so it sets conventions the whole port
inherits. The brief asked these reusable choices be recorded.

**Decisions:**
- **Assets live at the repo-root `assets/`** (per `ARCHITECTURE.md`), in
  per-overlay subfolders (`assets/clock/`, shared `assets/fonts/`). The
  `ApexHUD.Native.Overlays` csproj copies what it needs to the build output via
  `Content … Link` items (`PreserveNewest`), so the exe finds them next to itself.
- **One loader, `Assets`** (static helper in Overlays): `Resolve(...)` builds a
  path under `AppContext.BaseDirectory/assets`, `LoadBitmap` decodes an image,
  `LoadTypeface` is `SKTypeface.FromFile` from `assets/fonts/`. This is the single
  place the asset-path convention lives.
- **SkiaSharp drawing helpers live in `ApexHUD.Native.Overlays`, not
  `ApexHUD.Native.Common`.** `WatchRenderer` (draws the watch at any centre+radius
  off a `DateTime`) and `WatchArrive` (the `v46-watch-arrive` entrance easing) are
  rendering code that depends on SkiaSharp; `Common` stays SkiaSharp-free for
  truly framework-neutral types. `WatchRenderer` is deliberately decoupled from the
  window so Port 03 can place the same watch inside the session bar.

**Reasons:** Keeps the runtime asset path trivial (no probing up the tree),
keeps `Common` dependency-light, and makes the watch a reusable unit rather than
window-bound code — matching the brief's "build the reusable patterns here" intent
without inventing premature abstractions.

---

## 2026-05-27 — Render loop: CompositionTarget.Rendering drives a continuous repaint

**Context:** The watch needs a smooth (sub-second) second-hand sweep and a
700 ms entrance animation.

**Decision:** Keep the Phase 2 approach — subscribe to
`CompositionTarget.Rendering` and call `SKElement.InvalidateVisual()` every frame;
the paint handler reads `DateTime.Now` and a `Stopwatch` elapsed time. No timers,
no animation framework.

**Reason:** It is the WPF-native per-frame hook (vsync-paced), already proven in
Phase 2, and trivially correct for a 60 Hz redraw of a few hundred Skia ops on
this hardware. If a future overlay is purely event-driven we can invalidate on
demand instead, but the always-animating watch wants every frame.

---

## 2026-05-27 — Bundle Cormorant Garamond + Inter locally (OFL 1.1), superseding the RaycerRay-only font choice

**Context:** Port 01 provisionally bundled only the local RaycerRay set because
the legacy theme's Cormorant Garamond + Inter were CDN-only and acquiring them
needed a network download I wasn't cleared for (see the 2026-05-27 RaycerRay
entry above). Angelo has since decided: **install the legacy fonts locally.**
Both families are SIL Open Font License 1.1 — free to redistribute, no copyright
concern — so this no longer brushes the copyrighted-asset rules, and the
download was explicitly authorized for this session.

**Decision:** Bundle **Cormorant Garamond** (elegant serif — display/body) and
**Inter** (technical sans — UI/timing) under `assets/fonts/cormorant-garamond/`
and `assets/fonts/inter/`. Four upright weights each (Regular 400, Medium 500,
SemiBold 600, Bold 700) plus matching italics, with each family's canonical
`OFL.txt` alongside the `.ttf` files (OFL redistribution clause). The legacy
used a Google-Fonts CDN; we go local because the rebuild has no network layer
and the license permits redistribution.

**Source deviation (why not the google/fonts repo the brief named):** The
brief pointed at `github.com/google/fonts/.../{inter,cormorantgaramond}` for
static per-weight `.ttf` files. That repo now ships **both families as variable
fonts only** (`Inter[opsz,wght].ttf`, `CormorantGaramond[wght].ttf`). Our pinned
**SkiaSharp.Views.WPF 2.88.8** (a deliberate decision — see the SkiaSharp 2.88.x
entry) loads only a variable font's *default* instance via `SKTypeface.FromFile`
and has no clean public API to select Medium/SemiBold/Bold, so one variable file
cannot back the four discrete weight constants Session 03 needs. We therefore
bundled the **genuine Google production static per-weight TTFs** (the exact files
`fonts.gstatic.com` serves, fetched via google-webfonts-helper). This honors the
brief's intent — those four weights as discrete `SKTypeface` constants — while
working within our SkiaSharp pin. Revisit if we ever move to SkiaSharp 3.x, whose
variable-axis support could let us ship the single variable file instead.

**Naming-quirk note for future sessions:** Google packages these weights so that
each upright weight ≥500 reports its own *family* name in the font's `name`
table (e.g. "Inter SemiBold", "Cormorant Garamond Light SemiBold"), not a shared
"Inter" / "Cormorant Garamond" family with a weight axis. We sidestep this by
loading **by file path** (`Assets.LoadTypeface("inter", "Inter-SemiBold.ttf")`),
which yields exactly that file's outlines regardless of the family label. Do
**not** expect `SKFontManager.MatchFamily("Cormorant Garamond")` to find all four
weights — it won't. Load the file you want.

**Loading pattern:** `Fonts.cs` exposes the eight upright weights as lazily
loaded `SKTypeface` constants (`Fonts.Inter.SemiBold`, etc.), built on the
`Assets.LoadTypeface` helper, which was generalized to accept a sub-folder path.
Italics are bundled on disk but not yet given constants — add them when an
overlay first needs one.

**Relationship to the RaycerRay choice:** RaycerRay stays bundled too; nothing
is removed (append-only, and it may still suit a broadcast-style numeric/timing
context). Which family each overlay actually uses is decided in that overlay's
own port — the session bar (Port 03) is the first to render real text and will
make the first such call.

---

## 2026-05-27 — Port 02: ApexHUD.Native.Data is a class library; a new Harness exe replaces the Session-2 Program.cs

**Context:** Session 2 made `ApexHUD.Native.Data` an exe with a top-level
`Program.cs` that printed lap numbers — a proof-of-life. Session 3 left this
project type open: the question of whether `Data` should revert to a class
library once `Overlays` became the entry point was deferred. Port 02's session
prompt closes that loop because every data-driven overlay now consumes `Data`
through its public types, not a `dotnet run` entry point.

**Decision:** `ApexHUD.Native.Data` is a **class library** (`net8.0-windows`,
no `<OutputType>`). The Session-2 lap-counter is **deleted**; a new
`src/ApexHUD.Native.Harness/` exe (assembly name `apexhud-diag`) takes over
that role, but at the new layer — it consumes `RaceStateService` the same way
Overlays does and prints a one-line snapshot summary + the top of each class
on the leaderboard once a second. That makes it a real integration test of
the data layer, not just an IRSDKSharper smoke test.

**Reasons:**
1. `Overlays` already references `Data` (since Phase 3) and is the actual exe
   the launcher runs; an exe-typed `Data` carried no value and ambient .NET 8
   "ProjectReference to Exe" rules add subtle output-directory weirdness.
2. The harness as its own console project means the data layer can be
   exercised without dragging WPF in. Useful for live-verifying telemetry on
   the rig in a terminal alongside iRacing — no overlay windows to dismiss.
3. The architecture doc's project list grows from 3 to 4 (Common, Data,
   Overlays, Harness). This is a small, deliberate addition that mirrors the
   `tests/ApexHUD.Native.Tests/` slot already shown in `ARCHITECTURE.md`,
   except scoped to live diagnostics rather than unit tests.

**Trade-off:** One more `dotnet build` target. Acceptable because the harness
is a single 80-line `Program.cs` with two references and zero new packages.

---

## 2026-05-27 — Race state is exposed as an immutable snapshot record + event, not INotifyPropertyChanged

**Context:** Port 02's session prompt asked us to decide *how* overlays
observe the race-state model: `INotifyPropertyChanged`-style observable
properties on a long-lived view model, a C# `event` that publishes per-tick,
or a snapshot struct rebuilt each frame.

**Decision:** A single immutable **`RaceSnapshot` record** is rebuilt on every
telemetry tick and published via a `SnapshotUpdated` C# event. Subscribers
hold the latest snapshot reference; their render loop reads it lock-free.

**Reasons:**
1. Per-tick the model changes ~40 fields at once (every `CarIdx*` array, the
   flag bitfield, time, temps, the leaderboard). `INotifyPropertyChanged`
   would fire a flurry of `PropertyChanged` per tick at 60 Hz — heavier than
   handing out one new immutable reference.
2. Overlays render every frame anyway (Port 01 established the
   `CompositionTarget.Rendering` pattern). They don't need granular
   per-property notification; they need the latest consistent picture at
   paint time. A frozen snapshot is exactly that — no torn reads, no
   inconsistency between two fields read mid-update.
3. Records and `with` expressions make per-class interval recomputation a
   clean immutable transform inside the service — no mutation, no defensive
   copies, easy to reason about.
4. Test surface is trivial: future unit tests assert against a `RaceSnapshot`
   value, not a notification stream.

**Trade-off:** Subscribers that only care about one field (e.g. "did the
session-state change?") still receive the whole snapshot. Cheap — it's a
reference assignment and a few null checks; the snapshot is read-only.

---

## 2026-05-27 — Publish at full 60 Hz (UpdateInterval = 1), no throttle

**Context:** IRSDKSharper exposes `UpdateInterval` for discarding telemetry
frames. The session prompt asked us to decide the publish rate.

**Decision:** Keep `UpdateInterval = 1` — fire on every telemetry frame.
Overlays receive a fresh `RaceSnapshot` 60 times a second.

**Reasons:**
1. The whole reason this rebuild exists is to escape the legacy chain's HTTP-
   capped ~7 Hz polling and get broadcast-grade smoothness on live gaps and
   the second hand. Throttling here would re-introduce that bottleneck.
2. Snapshot construction is bounded (~64 cars × ~20 fields + grouping). On a
   3080 Ti rig this is microseconds per tick, well under the 16.7 ms budget.
3. Each overlay can still throttle on its own end if it ever needs to (it
   reads `service.Current` whenever it repaints), so this is the maximum
   useful rate — overlays opt down, never up.

---

## 2026-05-27 — Track-name resolution lives in Common; per-row gap math is pure helpers in Common

**Context:** Several legacy JS modules (`track-names.js`, `live-timing.js`,
`battle-mode.js`'s `classifyDriver`, the SessionState→phase map) are pure data
transforms with no I/O. Decision needed: do these live in `Data` next to the
SDK service, or in `Common` as framework-neutral helpers?

**Decision:** They live in `ApexHUD.Native.Common` (`TrackNames`,
`ClassDetection`, `LiveGap`, `LapTimeFormat`, `RacePhaseMap`, `FlagState`).
`Common` stays IRSDKSharper-free and SkiaSharp-free — only Overlays (for
drawing) and Data (for SDK access) carry framework deps.

**Reasons:**
1. Anyone reading the leaderboard model wants to format a gap or resolve a
   track name; centralizing those keeps overlays free of duplicated string
   math.
2. Pure functions are independently testable (the future
   `ApexHUD.Native.Tests` project can hit `LiveGap.Between` without any SDK
   or rendering machinery).
3. Mirrors how the legacy JS organized `shared/` — the same separation
   shipped natively.

**Track-name table scope:** The bundled `TrackNames` table is a working
**subset** of the legacy 70+ entry list, focused on the circuits Angelo
actually runs (Spa, Le Mans, Monza, Daytona, Sebring, Watkins Glen, the
Nürburgrings, etc.). The fallback for an unknown track is title-case + suffix
strip — visually fine for one race; expand the table when it bites.

---

## 2026-05-27 — Port 03: one window hosts the bar plate + the watch; primary-monitor-wide × 230 px

**Context:** The legacy session-bar window is 1936×230 @ (0,−8); the visible
glass plate inside it is 976×92 centred horizontally, with the watch
centrepiece bleeding ~18 px above and below the plate. The Phase 2/01 overlay
window mechanics (transparent, click-through, `WS_EX_TRANSPARENT|WS_EX_LAYERED`,
topmost, no taskbar, Escape closes) were already proven and are reused
verbatim. The question was how to translate the legacy window's hard-coded
1936-wide geometry into a native overlay that runs on whatever the primary
monitor actually is.

**Decision:** One WPF window (`MainWindow`), **primary-monitor-wide × 230 px,
pinned at (0,0)**. The bar plate inside it is fixed to the legacy **976×92**
dimensions and horizontally centred. The watch is centred on the plate at
**radius 64 px** so it overhangs the plate by 18 px top + bottom — matching
the legacy "watch overhang" exactly. The window is taller than the plate so
the watch overhang and its drop-shadow have headroom inside the transparent
window.

**Reasons:**
1. Primary-monitor-wide matches the legacy's *intent* (a strip across the
   top of the screen) without baking the 1936 number that only worked on
   Angelo's specific monitor at the time of authoring.
2. Keeping the **plate at 976×92** preserves every CSS pixel measurement in
   the legacy — fonts, paddings, the 18 px progress strip, the watch alcove
   radius all stay correctly proportioned without rederiving them.
3. Multi-monitor span and per-monitor DPI remain a deliberate Phase 3+
   problem (already scoped in the Phase 2 entry). Primary-only is the
   right minimal foundation; widen intentionally later.

**Trade-off:** On an ultrawide primary monitor the bar plate sits centred
with empty transparent space to either side — which is exactly the legacy
behaviour, so the visual reads identical.

---

## 2026-05-27 — Session bar weather drawn natively (sun/rain), not bundled as assets

**Context:** The legacy session bar's weather icon was an inline SVG built by
JS each tick (`buildSunSVG`/`buildRainSVG`), with three CSS animations layered
on top (`sunRotate 28 s`, `sunCorePulse 2.4 s`, `sunGlowPulse 3.2 s`, and
`rainFall 1.4 s` staggered three drops). Two options for the port: (a) bake
the SVGs as PNGs in `assets/` and animate transforms over them, or (b) draw
the sun rays + core + glow + raindrops natively in SkiaSharp.

**Decision:** **Draw natively.** `SessionBarRenderer.DrawSunIcon` /
`DrawRainIcon` reproduce the legacy SVG geometry directly (12 rays at 30°
spacing, gold core + glow, three offset-phased raindrops) and animate by
sampling `DateTime.UtcNow.TimeOfDay.TotalSeconds` against the legacy periods.

**Reasons:**
1. Zero new bundled assets. The legacy weather was procedural; making it
   procedural here keeps the asset folder honest (assets/ ships only things
   we couldn't reasonably draw).
2. SkiaSharp transforms (rotate, scale) + per-frame `InvalidateVisual` are
   exactly what the CSS keyframes were doing. The C# code is shorter than
   the JS+CSS originals it replaces.
3. Crisp at any future scale factor — vector ops, not a PNG that would
   alias on a higher-DPI monitor.

---

## 2026-05-27 — Progress-bar state machine: gold-fill baseline + checker terminal only (first pass)

**Context:** The legacy progress bar has five states authored on the outer
`.battle-progress-bar` element: `init` (no fill yet), `gold` (the default
during racing — dark base with a gold race-progress fill), `celebration` (the
first 10 seconds after green — entire bar pulses bright green), `alert-*`
(yellow/red/white during those flags — entire bar pulses the flag colour),
and `checker` (terminal — scrolling B/W stripes). The state machine in
`updateProgressState()` decides which one fires on each tick.

**Decision:** Port 03 ships the **`gold` + `checker` states only**. The
`alert-*` states are made redundant by the bar-wide flag overlay (which
already pulses the whole bar in the flag colour); `celebration` is a
one-time entrance animation that adds little after the watch's own arrival
overshoot already plays.

**Reasons:**
1. The flag-state overlay (the `.bar[data-flag-state]` CSS that recolors
   the entire bar) is already ported and already pulses yellow/red/white at
   the legacy periods. Pulsing the progress bar with the same colour at the
   same period would be visually duplicative.
2. Celebration is purely cosmetic — it adds nothing to *understanding* the
   race state — and the watch overshoot + the 700 ms entrance already give
   the launch its "arrived" feel.
3. Checker is genuinely informative (it's the race-finish indicator); kept.
4. Smaller surface, faster to ship, easy to add the rest in the polish pass
   if any read as missing on screen.

**Trade-off:** Some of the legacy's full-bar drama on a green-after-yellow
transition is lost. Revisit in the polish pass if it reads flat.

---

## 2026-05-27 — Letter-spacing emulated by per-glyph layout (SkiaSharp 2.88 has no SKPaint letter-spacing)

**Context:** The session bar's broadcast typography depends heavily on CSS
`letter-spacing` (track-name `1.0px`, phase chip `2.2px`, AIR/TRACK labels
`1.8px`, flag-state `1.9px`, lap counter `1.2px`). SkiaSharp 2.88's `SKPaint`
exposes typeface, size, alignment and stroke, but **not** letter-spacing — the
`SKFont` API that does is in 3.x.

**Decision:** `SessionBarRenderer.DrawTextTracked` / `MeasureTracked` emulate
letter-spacing by measuring each glyph with `paint.GetGlyphWidths(text)` and
drawing the glyphs one at a time, inserting the spacing as a horizontal cursor
advance. Both helpers also honour `SKTextAlign.Right` / `.Center` by computing
the tracked total width and pre-shifting the start cursor.

**Reasons:**
1. Lets the bar keep its broadcast typography faithfully (the letter-spacing
   is the difference between "this looks like a sports-broadcast graphic"
   and "this looks like an admin panel").
2. Drawing per glyph means each character takes its own CSS-equivalent
   advance plus the spacing, identical to the browser's behaviour.
3. Zero new dependencies; no SkiaSharp 3.x migration needed.

**Trade-off:** Per-glyph drawing is a touch slower than a single
`DrawText`. Negligible at the bar's text volume (~6 small labels). Revisit
if we ever port `SessionBarRenderer.DrawTextTracked` to a hot tower row.

---

## 2026-05-27 — Snapshots are pulled per-frame from `App.LatestSnapshot`, not subscribed via the SnapshotUpdated event

**Context:** `RaceStateService.SnapshotUpdated` fires on the IRSDKSharper
telemetry background thread at 60 Hz (Port 02 design). The overlay window's
WPF render thread paints via `CompositionTarget.Rendering`. Two options for
connecting them: (a) subscribe to `SnapshotUpdated` and `Dispatcher.Invoke` the
window to repaint, or (b) have the window's per-frame paint handler simply
read `RaceStateService.Current` directly each frame.

**Decision:** **Option (b)** — `App` exposes `RaceState` as a static and the
window reads `App.LatestSnapshot` on every `OnPaintSurface`. The
`SnapshotUpdated` event is left in place for future overlays that genuinely
*need* per-tick notification (e.g. a pit-callout that has to fire on a state
change), but the bar doesn't need it.

**Reasons:**
1. The bar already repaints every frame for the watch + weather + progress;
   reading the latest snapshot reference at paint time is a single volatile
   read — cheaper than marshalling 60 Hz events across threads.
2. No `Dispatcher.Invoke` storm, no UI-thread contention on the IRSDKSharper
   thread, no risk of dropped events under load.
3. The snapshot is immutable (Port 02 decision), so a window reading the
   reference mid-publish always gets a consistent view — no torn reads.

**Trade-off:** Up to one frame (~16 ms at 60 Hz) of staleness between a
publish and a paint. Indistinguishable on a broadcast overlay.

---

## 2026-05-27 — Canonical broadcast palette lives in Common, GTP picks purple

**Context:** Port 04 needs a class palette + license-badge colors that every
overlay can agree on. The legacy code disagreed in two places: (a) GTP/LMDh —
`theme.css` painted it champagne `#C5A572`, the tower painted it purple
`#C084FC`; (b) license badge backgrounds — `theme.css` used a Material-style
palette (P `#7C00FF`, A `#006EFF`, B `#33CC00`, C `#FFCC00`, D `#FF6600`,
R `#E1251B`), the tower's `.row__lic` defaulted to a deeper print palette
(`#1976D2`, `#388E3C`, `#FBC02D`, `#F57F17`, `#D32F2F`). PORT_PLAN risk #3
flagged the GTP one in particular.

**Decision:** Add `ApexHUD.Native.Common.BroadcastPalette` (a string-keyed
hex-color lookup, kept SkiaSharp-free so it can live alongside `LiveGap`
and friends). Pick the **tower's purple GTP `#C084FC`** and the **theme.css
license palette**. Also add `ApexHUD.Native.Common.ManufacturerBrand.Resolve`
with the legacy aliases (Corvette → Chevrolet, AMR → Aston, AMG → Mercedes,
plus VW → Volkswagen and the "Aston Martin" / "Mercedes-AMG" expansions).

**Reasons:**
1. Purple GTP reads cleaner against the gold leader-position colour than
   another gold hue would — a stripe in the same family as the position
   number is muddier than a contrasting purple stripe. Matches PORT_PLAN's
   recommendation under risk #3.
2. The Material-flavoured license palette (`theme.css`) has higher contrast
   per badge and is what the design system was authored around; only the
   tower's local CSS deviated. Pick the design system over a local override.
3. Living in Common (not Overlays) lets the eventual featured/battle/grid
   overlays share the same palette without depending on the renderer. Stays
   framework-neutral (no SkiaSharp dep) — hex strings parsed at draw time.

**Trade-off:** Hex parsing per draw call has a per-row cost (≈ negligible,
6 chars to a byte triple). Revisit only if a profile says otherwise; the
alternative — an SKColor table in Overlays — would force overlays to depend
on Common+SkiaSharp coupling we don't need yet.

---

## 2026-05-27 — Multiple overlay windows are spawned explicitly by App, no StartupUri

**Context:** Port 04 introduces the second overlay window (the tower) alongside
Port 03's session bar. Until now, `App.xaml` used `StartupUri="MainWindow.xaml"`
to auto-launch the single bar window. Two questions: (a) how does App now own
both windows, (b) what does Escape do.

**Decision:** Remove `StartupUri` from `App.xaml`. `App.OnStartup` explicitly
creates and shows **`MainWindow`** (the session bar) **and** `TimingTowerWindow`,
in addition to starting `RaceStateService`. **Escape on either window calls
`Application.Current.Shutdown()`** — closing one window no longer leaves the
other one orphaned, and pressing Escape anywhere shuts the system down.

**Reasons:**
1. Explicit window construction makes the window topology a single readable
   list in `App.OnStartup` — adding the next overlay (featured / battle /
   grid) is one new `.Show()` call. With `StartupUri` the second window had
   to be created after-the-fact, which obscured ownership.
2. `Application.Current.Shutdown()` mirrors how users actually want to dismiss
   the suite — they want every overlay gone. The Port 03 behaviour where bar-
   Escape only closed the bar window was an artefact of the single-window
   era.
3. No new dependency, no plugin pattern. Just one method that lists the
   windows the suite owns.

**Trade-off:** Each new overlay adds a line to `App.OnStartup`. Acceptable —
the audience is two people and there will be ~6 windows total at v1.

---

## 2026-05-27 — Per-row pulse / arrow state is tracked inside the renderer, not in the snapshot

**Context:** The tower needs three pieces of per-driver state that don't
naturally belong on the immutable `RaceSnapshot`: a **position-change arrow**
(green-up / red-down for ~3.5 s after the position changes), a **lap-completion
pulse** (white inset glow for ~900 ms when `LapsCompleted` ticks over), and a
**personal-best pulse** (green inset glow for ~1.3 s when `BestLapTime`
improves). These are deltas against the *previous* tick, not facts about the
current tick.

**Decision:** `TimingTowerRenderer` owns a `Dictionary<int CarIdx, RowState>`
that records the last position / last `LapsCompleted` / last `BestLapTime`
seen for each car and the wall-clock timestamps of the most recent change.
Every paint, the renderer compares the incoming `Driver` to its stored state,
fires a new pulse-start timestamp if the field changed, and computes the
current pulse fraction from `(now − startedAt) / pulseDuration`. The pulses
self-extinguish; `RaceSnapshot` carries no animation state.

**Reasons:**
1. Animation timing is presentation, not data. Putting "this row pulsed
   green at timestamp T" into the snapshot would pollute the immutable data
   contract and force every consumer to skip past it.
2. Keeping deltas in the renderer means each overlay can pick its own
   thresholds. The featured / battle cards might want a longer or shorter PB
   pulse without touching the tower's behaviour.
3. The state is small (≤ 64 cars × ~40 bytes) and cleared by overlay
   restart, which is the right scope — no need for it to survive the session
   bar window's lifecycle.

**Trade-off:** Two overlays that both want the same pulse logic (a future
featured-card PB highlight, say) will each instantiate their own tracker
dictionary. If three overlays end up duplicating this, lift it into a small
shared `RowDeltaTracker` in `ApexHUD.Native.Common`. Until then, premature.

---

## 2026-05-27 — Manufacturer logo brand aliases live in Common (Corvette→Chevrolet, etc.)

**Context:** iRacing's `CarScreenName` / `CarBrand` strings don't always match
the brand filenames we ship in `assets/logos/manufacturers/*.png`. The legacy
`manufacturerLogoPath()` had a small alias table (Corvette → Chevrolet, AMR →
Aston, AMG → Mercedes).

**Decision:** Add `ApexHUD.Native.Common.ManufacturerBrand.Resolve(brand)` that
applies the legacy aliases plus a handful of common variants ("Aston Martin",
"Mercedes-AMG", "VW") and then falls back to a direct case-insensitive lookup
against the 34 bundled filenames. Returns the slug, or null if no logo
shipped.

**Reasons:**
1. The aliases are race-data semantics ("Corvette is a Chevrolet brand"), not
   rendering. Belongs alongside `ClassDetection` and `TrackNames` in Common.
2. Centralising it means the eventual pit-callout (Port 08) and featured-
   card (Port 05) brand logos use the same resolution as the tower — and fix
   the legacy pit-callout bug (which referenced the wrong global and never
   showed logos) for free.
3. Returning null instead of throwing lets each overlay decide whether to
   draw a placeholder or skip the column. The tower simply skips.

---

## 2026-05-27 — Port 05: featured card binds to RaceSnapshot.CameraTarget; no row scan, no F2Time fallback

**Context:** The legacy featured card had to do real work to identify the
camera-focused driver: it cross-referenced bo2's `Highlighted` row (which
carried identity but not lap times) against a 40-row `Standings.ClassNN.RowNN`
window, then re-scanned the IREP `_leaderboard` to find the *real* overall
position because bo2's `Position` field was just a row index per class. On top
of that, the live gap-to-leader was derived from a `DeltaToPlayer` difference
between the camera target and Row00, and refined every 150 ms via a
`CarIdxF2Time` matching pass (`live-timing.js`).

**Decision:** All of that goes away. `RaceSnapshot.CameraTarget` (Port 02 —
resolved from `CamCarIdx` against IRSDKSharper's `DriverInfo.Drivers`) is the
single source of truth, and it already carries the same per-driver shape the
tower binds to (`Driver` record), including `PositionInClass`, `GapToLeaderSeconds`,
`BestLapTime`, `LastLapTime`, `LapDistPct`, `IRating`, `CarBrand`. The renderer
just reads `snap.CameraTarget ?? snap.Player` and renders the four columns.

**Reasons:**
1. The native data layer already produces the shape the card needs; doing the
   legacy's name-match-and-merge dance natively would be re-inventing work the
   snapshot did once for everyone.
2. Removes the entire `CarIdxF2Time` fallback path — the snapshot's
   `GapToLeaderSeconds` is computed by the LiveGap formula at the full 60 Hz
   publish rate (Port 02), which is strictly better than the legacy 150 ms F2Time
   match-and-correct.
3. No name collisions: `CamCarIdx` → `CarIdx` is unique and survives drivers
   sharing a first name or running with similar abbreviations (a real legacy
   failure mode the row-merge had to guard against).

**Trade-off:** If `CameraTarget` is null (between camera switches, or before
the first session-info tick), we render `Player` as a fallback. The legacy did
the same fallback chain (`hl → playerRow → bulkCarId`), so behaviour matches.

---

## 2026-05-27 — Featured card visibility state machine lives in the renderer; default force-visible

**Context:** The legacy featured card had a real broadcast-style spotlight
state machine — show on early laps (1..3), final laps (≤3 remaining), or for
10 s after a position change, with 8 s min-visible and 5 s hide-hysteresis —
but in production it was bypassed by a `?force=1` URL parameter so the card
stayed on screen permanently. The Port 05 brief asked for both: implement the
state machine *and* keep a simple force toggle.

**Decision:** `FeaturedCardRenderer` owns the visibility state machine
(`isVisible`, `shownAtMs`, `wantHideAtMs`, `lastPositionChangeAtMs`,
`lastFeaturedCarIdx`) and computes per-frame alpha + translateY from it. A
public `ForceVisible` bool defaults to **true** to match the legacy production
behavior. When force-visible, the renderer skips the relevance evaluation and
keeps `isVisible = true`. When toggled off, the full state machine takes over.

**Reasons:**
1. Visibility is presentation, not data — the same reason the tower's
   per-row pulse/arrow state lives in `TimingTowerRenderer` (Port 04 decision).
   Keeping it inside the renderer keeps `RaceSnapshot` free of presentation
   timestamps and means each overlay can pick its own thresholds without
   coupling.
2. Default-on matches the legacy production behavior, so the visual is the
   same as ApexHUD-Web from day one. Flipping `ForceVisible = false` is one
   property assignment when Angelo wants to A/B-test the broadcast spotlight
   rhythm against the always-on rhythm.
3. Animations (420 ms entrance + 420 ms leave, 12 px translate) ride on the
   same `isVisible`/`leaveStartedAtMs` timestamps; no separate state machine
   for "is the card animating right now."

**Trade-off:** With `ForceVisible = true`, the state machine code paths are
compiled-only and have not been exercised at runtime against iRacing. The
math is straightforward (and ported verbatim from the legacy constants), so
this is a low-risk "later" — revisit when Angelo wants the spotlight mode on.

---

## 2026-05-27 — Featured card pinned to bottom-centre of the primary monitor

**Context:** Legacy window placement was hard-coded `(576, 880)` size `768×254`
— absolute coordinates that worked on the specific 1920×1080 monitor the legacy
was authored on. The native rebuild has already chosen primary-monitor-relative
placement for the session bar (Port 03) and the tower (Port 04).

**Decision:** Window is **768×254**, horizontally centred at
`(PrimaryWidth − 768) / 2`, and pinned **24 px above the bottom** of the primary
monitor (`Top = PrimaryHeight − 254 − 24`). The card itself sits 24 px inset
from the left/right and 22 px from the top inside the window, leaving 22 px at
the bottom for the entrance translate + the soft top-bar glow to breathe.

**Reasons:**
1. Mirrors the legacy's intent (lower-third broadcast graphic centred on
   screen) without baking in 1920-wide-only assumptions.
2. The 24 px bottom margin reads as "floating above the screen edge" like a
   real broadcast lower-third — pinning it flush to the bottom would look
   like a permanent UI element rather than a spotlight card.
3. Same primary-monitor scope as every other overlay so far; multi-monitor
   placement remains a deliberate polish-pass decision.

---

## 2026-05-27 — TrackCorners is Spa-only on first pass; lives in Common with the other pure helpers

**Context:** The legacy `shared/track-corners.js` shipped a corner-name lookup
keyed off `LapDistPct`, but the bundled table only covered Spa-Francorchamps
(both layouts). Everywhere else the function returned null and the corner line
faded out. Port 05 needs the same surface so the featured card's corner cue
works at Spa.

**Decision:** Bundle the Spa ranges verbatim into
`ApexHUD.Native.Common.TrackCorners`, keyed by the broadcast track name (the
output of `TrackNames.Resolve` / `ResolveInfo`). Return null for any other
track. Keep the helper in `Common` next to `TrackNames` and `LiveGap` so the
eventual corner-indicator overlay can share the same table without depending
on a renderer.

**Reasons:**
1. The data is a pure (track, pct) → string transform with no I/O or framework
   dependency. Belongs in `Common` for the same reason `TrackNames` does.
2. Adding more tracks is one table append per circuit — the lookup signature
   does not change as the table grows.
3. Returning null on unknown tracks (rather than throwing or returning empty)
   lets the overlay decide whether to draw a placeholder or skip the row.
   The featured card simply skips, matching the legacy fade-out behavior.

**Trade-off:** Drivers running anywhere outside Spa get no corner cue. Same
as the legacy. Expand the table when Angelo verifies LapDistPct ranges for
another circuit (the legacy comment block at the top of the JS file is the
authoring procedure — drive the track, note the entry/exit pcts, add an entry).

---

## 2026-05-27 — Port 06: battle detection is a pure helper in Common; state machine + smoothing + EMA live in the renderer

**Context:** Battle detection (player vs same-class opponent within ~1 s) has
two distinct parts: a data shape (which neighbour is closest? what's the gap?)
and a temporal/visual layer (hysteresis, dwell, smoothing, EMA, entrance
animations). The legacy mashed both into `battle-card.html` + `battle-mode.js`.
Where do they live natively?

**Decision:** Split along the data/presentation seam already established
across Ports 02/04/05:
- **`ApexHUD.Native.Common.BattleDetection`** — pure transform from
  `RaceSnapshot` to a `BattleCandidate` record. Walks the player's class
  group, picks nearest connected same-lap neighbours ahead/behind, reports
  absolute gaps. No thresholds, no animation, no hysteresis — those are
  presentation concerns.
- **`ApexHUD.Native.Overlays.BattleCardRenderer`** — owns the state machine
  (enter 1.0 s, exit 2.0 s, min dwell 4 s, dropout grace 1.5 s, reopen
  cooldown 2.5 s), the gap lerp (18 %/frame), and the EMA-smoothed tension
  meter (α 0.32, full scale 0.045 s). Same pattern as the tower pulse state
  (Port 04) and the featured visibility machine (Port 05).

**Reasons:**
1. Future overlays may want raw detection without the card visuals (a tower
   "battle glow", a story-engine event, a spotter chime); a Common helper
   serves all of them.
2. Centralising the state machine in the renderer means a future port that
   wants different thresholds (a longer-dwell battle for endurance, say) can
   subclass or fork without touching the data layer.
3. Common stays SkiaSharp-free + IRSDKSharper-free, consistent with the
   2026-05-27 Common-vs-Data split.

**Reuse of snapshot's `IntervalSeconds`:** rather than re-walking standings
rows like the legacy did via benofficial2's `DeltaToPlayer`, detection reads
the per-row `IntervalSeconds` Port 02 already computes (gap to car ahead in
class, via the F1/IMSA position-math formula). `player.IntervalSeconds` is
the attack gap; the car-behind's `IntervalSeconds` is the defend gap. One
fewer derived value to recompute, smoother at 60 Hz than the legacy 150 ms
F2Time fallback.

---

## 2026-05-27 — Cross-overlay coordination: a static `App.IsBattleActive` flag, not a pub/sub bus

**Context:** Port 05's featured card has to yield (fade out) when Port 06's
battle card appears so the two lower-third overlays don't fight for the same
broadcast lane. The legacy handled this with a CSS attribute on `<body>` that
both overlays' separate browser windows watched. Natively the two windows
are in the same process and the same dispatcher, so the legacy's wall-clock
sync is unnecessary.

**Decision:** Add a static `App.IsBattleActive` bool. The battle window writes
it each frame after `Draw` (true when the renderer's state machine is
visible or animating). The featured-card renderer reads it each frame and
cross-fades to ~10 % opacity (smoothstep over 280 ms) when it flips. No
events, no observable, no pub/sub.

**Reasons:**
1. Same pattern as `App.LatestSnapshot` — single-process state hangs off
   `App`, every window pulls per frame at paint time, no `Dispatcher.Invoke`.
   Matches Port 03's "pull per frame" decision.
2. The signal is a single bool; an event bus would be heavier infrastructure
   than the problem warrants.
3. Yields to ~10 % opacity rather than full 0 % — the camera target's
   identity is still readable behind a faint card silhouette, which matches
   what a real broadcast does when promoting one graphic over another.

**Trade-off:** Static state. If a second source of "battle is active" ever
appears (the tower's per-row glow, say), they'd both need to OR into the flag
or this would need to become a small `BroadcastBus` service. Cheap to refactor
later; over-engineered to anticipate it now.

---

## 2026-05-27 — Battle card window is its own 1280×340 window above the featured-card lane

**Context:** Port 06 needs a fourth WPF overlay window. The legacy was at
(320, 780) sized 1280×340 on a 1920×1080 monitor — coords baked to a specific
monitor. We've already chosen primary-monitor-relative placement for every
previous overlay (Ports 03 / 04 / 05).

**Decision:** Window is **1280×340**, horizontally centred at
`(PrimaryWidth − 1280) / 2`, placed **12 px above** the featured card window's
top edge. Card itself is 1000×280 inside the window, dead-centred so the
entrance translate (14 px) and the top-bar glow have headroom.

**Reasons:**
1. The featured and battle cards share the broadcast lower-third lane in the
   legacy; placing them adjacent (battle above featured by 12 px) lets the
   featured card's faded silhouette read as a clear backdrop when the battle
   is on top.
2. Primary-monitor-only is consistent with every other Port window's choice
   to date. Multi-monitor placement remains a deliberate polish-pass decision
   (PORT_PLAN risk #8).
3. Same click-through / topmost / no-taskbar / Escape-shuts-down mechanics as
   the other windows — reused verbatim from Port 03.

---

## 2026-05-27 — Deterministic test loop ships behind a `--battle-testloop` launcher flag

**Context:** The Port 06 brief explicitly authorised a deterministic test loop
because a close same-class duel is hard to stage live — and acknowledged that
the legacy itself shipped one (`?testloop=1` URL param). We needed an
equivalent for the native build.

**Decision:** Ship `BattleTestLoop` — an `internal` class that synthesises
three GT3 drivers and cycles **Attack (6.5 s, gap 0.92 → 0.22) → idle (2 s) →
Defend (6.5 s, gap 0.85 → 0.18 → 0.85, exercising wheel-close and recovery) →
idle (2 s)** on a 17 s period. The cycle wakes the state machine through the
same code path live detection uses, so the visuals, smoothing, and hysteresis
are all exercised against a known trajectory. Wired up via a launcher flag:
`--battle-testloop` on the exe → `App.BattleTestLoopRequested = true` →
`BattleCardWindow` constructs the renderer with `TestLoop = new BattleTestLoop()`.

**Reasons:**
1. A live close duel can take a whole race to materialise; an out-of-band
   visual verification path is essential to iterate the renderer without
   waiting for racing.
2. Sharing the renderer code path (same state machine, same smoothing) means
   the test loop verifies the real implementation, not a parallel one.
3. Flag-gated so production launches are unaffected — the renderer's
   `TestLoop` property is null unless explicitly opted in.

**Trade-off:** The test loop uses synthetic driver records with hand-picked
names, numbers, licenses, lap times, and class colours. Live-session shapes
might differ subtly (different ClassColor hex, different LicenceString
format). The test loop verifies layout and animation; live verification is
still needed to confirm production shapes flow through cleanly.

---

## 2026-05-27 — Port 07: expose `WeekendInfo.SeriesID` on the snapshot (smallest data-layer change of the port)

**Context:** The grid eyebrow is supposed to read a broadcast series name
("IMSA SPORTSCAR CHAMPIONSHIP"), not just the track. The legacy resolver
(`shared/series-logos.js → ApexSeriesLogos.getName`) keyed off
`WeekendInfo.SeriesID`. The Port 02 snapshot intentionally only carried
track-name fields (the previous ports — bar, tower, featured, battle — had
no use for SeriesID), so the snapshot couldn't reach the resolver as-shipped.

**Decision:** Add a single nullable `int? SeriesId` field to
`RaceSnapshot`, populated from `weekend.SeriesID` in
`RaceStateService.OnSessionInfo` (carried through `SessionCache`). Keep
the legacy resolver shape — `Common.SeriesNames.ResolveName(int?)` returns
the curated name or null — and let the grid renderer fall back to
`TrackBroadcastName` (uppercased) on a null. Reuses the deliberate
null-fallback ergonomic that `TrackCorners.Resolve` and
`ManufacturerBrand.Resolve` already established.

**Reasons:**
1. The data shape genuinely was missing — every other change in the port
   would otherwise be a renderer-side hack to derive a name from the wrong
   field. One nullable record field is the right size of fix.
2. Falling back to the track name in the renderer keeps overlays free of
   "did we ship a name for this series" branching everywhere — the
   resolver is the only place that knows.
3. `SeriesNames` is intentionally a thin table (one mapped SeriesID at the
   time of porting). The append surface for new IDs is trivial: one
   `switch` arm in two methods. Bundled logo slugs already enumerated so a
   future grid-with-logo or tower-with-series-chip can pick them up.

**Trade-off:** Every snapshot now carries one extra nullable int. Cost is
~8 bytes per snapshot — irrelevant. The wider implication — that other
`WeekendInfo` fields can be added the same way — is intentional; future
overlays should follow this pattern rather than reaching around the data
layer.

---

## 2026-05-27 — Port 07: grid is a single transparent window that paints only when phase-gated; phase fade lives in the renderer

**Context:** Port 07 introduces a fifth WPF overlay window. The grid is the
first overlay whose **visibility is gated by race phase** (visible only when
`Phase == PreRace` or `PostRace`; hidden during broadcast). Two ways to
shape this: (a) the window itself appears/disappears (Show / Hide on the
WPF window) on a phase flip, with a Storyboard for the cross-fade, or
(b) the window is always alive but the renderer paints zero pixels when the
phase doesn't gate, with a 520 ms alpha-and-translate fade owned in the
renderer like the tower's pulses and the featured card's visibility machine.

**Decision:** Option (b). `StartingGridWindow` stays alive for the process
lifetime alongside the bar / tower / featured / battle windows;
`StartingGridRenderer` owns a `phaseFlipAtMs` timestamp + a `wasGated`
bool, computes `fadeAlpha`/`translateY` per frame, and short-circuits the
paint to `return false` once the fade has settled to zero.

**Reasons:**
1. Same pattern as every other animation-bearing renderer in the suite
   (tower pulses Port 04, featured visibility Port 05, battle entry/exit
   Port 06): presentation timing lives next to the pixels, the immutable
   snapshot stays animation-free.
2. WPF Show/Hide of a transparent topmost click-through window has a
   measurable flicker on this rig (the Win32 layered-window machinery
   re-arms its DWM composition). A constant always-alive window with an
   internal alpha avoids that entirely.
3. Cleaner shutdown — only one `Show()` call per window in `App.OnStartup`,
   and Escape on any overlay still calls `Application.Current.Shutdown()`
   verbatim.

**Trade-off:** The window paints a transparent surface every frame even
during the broadcast phase, where the grid is invisible. The cost is one
`canvas.Clear(SKColors.Transparent)` + an early `return false` before any
real drawing — measured in microseconds on this hardware. Revisit if a
future profile flags it.

---

## 2026-05-27 — Port 07: class column order follows the IMSA broadcast preference, not the leader-position-first snapshot order

**Context:** `RaceSnapshot.ClassLeaderboard` is sorted by the **overall
leader's position** within each class (Port 02's choice — the class with
the overall fastest car comes first). For the timing tower that's the right
order: the camera-following class leads visually. But the legacy
**starting grid** authored a fixed IMSA-style class order — GTP, LMDh, LMP,
LMP2, LMP3, GTDP, GTE, GTD, GT3, GT4, OTHER — so a multi-class field reads
left-to-right in the same order every grid, regardless of which class
happens to be on pole.

**Decision:** `StartingGridRenderer.DrawBody` copies the snapshot's
per-class list and re-sorts it by a canonical
`ClassOrder` array (the legacy `CLASS_ORDER` constant, ported verbatim).
Snapshot semantics stay unchanged; the grid simply imposes the broadcast
preference at render time. Unknown classes land after `OTHER`.

**Reasons:**
1. Production grids on real broadcasts always show GTP/Hypercar on the
   left and GT3 on the right — viewers know where to look. Following the
   leader-position order would shuffle the field visually between grids.
2. Single source of truth for class order lives in the grid renderer
   (where it matters); the tower keeps its leader-position-first order
   (where *that* is what matters). Different overlays, different needs.
3. The re-sort is O(n log n) on a tiny n (≤ ~8 classes); trivial cost.

**Trade-off:** Two renderers now disagree on class order. Acceptable —
they're conveying different things, and the snapshot stays the canonical
shape both build from.

---

## 2026-05-27 — Port 07: grid window is centred on the primary monitor, sized to the legacy 1100×830 slot

**Context:** The legacy window was hard-coded `(410, 230)` × `1100×830`
on a 1920×1080 monitor — coords baked to a specific monitor, like every
other legacy overlay. Every previous port adopted primary-monitor-relative
placement (Ports 03 bar, 04 tower, 05 featured, 06 battle).

**Decision:** Window is **1100×830** (the legacy slot dimensions are
preserved so the card's 1068×798 interior — and every pixel measurement
inside — stays correctly proportioned), horizontally **centred** at
`(PrimaryWidth − 1100) / 2`, vertically **centred** at
`max(0, (PrimaryHeight − 830) / 2)`. The card sits 16 px inset on every
side inside the window (mirroring the legacy `.grid-card { margin: 16px }`),
giving the gold accent hairline + drop-shadow headroom.

**Reasons:**
1. Centred is the right intent of "broadcast lower-third tabular graphic"
   — the legacy `(410, 230)` was just centring on a 1920×1080 monitor.
2. Keeping the card at its legacy 1068×798 interior means all the
   typography (eyebrow / title / phase pill / row chips / column widths) is
   left untouched from the source — no rederiving font sizes for a
   resized card.
3. Consistent with every other overlay in the suite: same primary-only
   scope, same Phase 3+ deferral for multi-monitor placement.

**Trade-off:** On an ultrawide monitor the card centres with transparent
margin on both sides. Same as every other overlay; multi-monitor placement
is the deliberate polish-pass decision (PORT_PLAN risk #8).

---

## 2026-05-27 — Port 08: per-tick pit-state diffing on the snapshot, no data-layer change

**Context:** The legacy Pit Callout watched `CarIdxOnPitRoad[]` and
`CarIdxPosition[]` directly via SimHub, comparing each car's pit state to the
previous tick and storing the entry position so the rejoin delta could be
computed at pit-exit. The native data layer already publishes per-`Driver`
`IsInPit` (sourced from `CarIdxOnPitRoad`) and `Position` (from
`CarIdxPosition`) on every snapshot — so the renderer can do the same
transition-detection by simply diffing the `Driver` records it walks each
frame.

**Decision:** `PitCalloutRenderer` owns its own
`Dictionary<int CarIdx, bool prevPit>` and
`Dictionary<int CarIdx, int entryPosition>` keyed on `Driver.CarIdx`, seeded
on the first observed snapshot (no spurious "everyone entered the pit" event
at session join), and diffs incoming snapshots against it every frame. No
`Driver` or `RaceSnapshot` field is added; no new IRSDKSharper datum is
queried.

**Reasons:**
1. The data is already on the snapshot — adding a parallel "pit events" list
   to `RaceSnapshot` would duplicate state every other overlay would have to
   skip. Same principle as the tower's per-row pulse state (Port 04 decision)
   and the featured visibility machine (Port 05) — presentation state lives
   in the renderer that consumes it.
2. The per-frame walk is cheap (≤ ~64 cars × two dictionary lookups). The
   queue holds events while the card animates so back-to-back stops still
   each get a full callout.
3. Keeps the per-snapshot `Driver` record stable. Future overlays that want
   pit *durations* or *strategy* (deferred `pit-strategy` overlay from
   PORT_PLAN) would need richer state; that's their problem when they arrive,
   not a forward-compatibility tax now.

**Trade-off:** The renderer carries its own deltas, so two overlays that both
want pit-entry events (a future spotter chime, say) would each diff
independently. Cheap to refactor into a small `PitEventBus` in Common when
that second consumer actually exists; speculative now.

---

## 2026-05-27 — Port 08: fastest-lap scans every class and labels the setter's class, not the camera

**Context:** The legacy fastest-lap callout (a) only scanned the first class
bucket (`benofficial2.Standings.Class00.Row00..Row19`) and (b) used the
spectated car's `CarId` to *label* the class. In a multi-class race this
mislabels the callout (e.g. an LMP2 best gets a "GTP" header when the camera
is on a GTP car) and never fires for a non-Class00 lap improvement at all.
The session brief flagged both behaviors as bugs to fix in passing.

**Decision:** `FastestLapRenderer.DetectNewBest` walks
`RaceSnapshot.ClassLeaderboard.SelectMany(c => c.Drivers)` — every connected
driver across every class — picks the min `BestLapTime`, and stores the
driver's *own* `ClassName` in the trigger record. The badge meta line reads
"{CLASS} · {BRAND}" of the driver who actually set the lap.

**Reasons:**
1. The native snapshot already exposes every class as a first-class peer
   (Port 02 decision); the legacy single-class restriction was an artefact of
   benofficial2's standings model, not a design choice.
2. Identifying the lap-setter from the same iteration removes the wrong
   `CarId`-lookup path. One pass, one source.
3. Cooldown (4 s), dwell (5.5 s), and first-observation suppression are
   ported verbatim, so the cadence at which callouts fire matches the legacy
   — just without the bugs.

**Trade-off:** A truly improbable double-PB on the exact same tick across two
classes will only show the smaller of the two; the other is silently
absorbed as "session-best advanced." Same drop-overlapping behaviour as the
legacy; queueing fastest-lap events feels heavier than it's worth for an
event this rare.

---

## 2026-05-27 — Port 08: sponsor card restored to deep-glass, no opaque hack

**Context:** The legacy sponsor `.glass` rule was deliberately overridden
with a fully opaque background to dodge a recurring pywebview "white box"
transparency bug — see the inline CSS comment block in
`overlays/sponsor.html` at the SPONSOR-SPECIFIC override. WPF
`AllowsTransparency="True"` + an SkiaSharp canvas drawing alpha-rgba doesn't
have that bug, and the PORT_PLAN explicitly called out reverting the hack as
in-scope for the port.

**Decision:** `SponsorRenderer` paints the same deep-glass base
(`BroadcastPalette.TowerGlassBase` × 0.82 α) every other Port-04+ lower-third
uses, plus the gold 1 px top accent + soft glow. No opaque background, no
backdrop-filter shim.

**Reasons:**
1. Consistency with the rest of the suite — the sponsor card now reads as
   "broadcast lower-third" instead of "solid box."
2. Removes a workaround whose original cause is gone. Carrying a legacy hack
   forward with no live bug to justify it is pure cargo cult.
3. The renderer is otherwise the lightest in the port — config defaults
   match PORT_PLAN §6 (label "PRESENTED BY", url
   "abgeneralcontracting.net", tagline "VISIT OUR WEBSITE FOR A FREE STORM
   REPORT"); ForceVisible is off so the 15 s / 8 s / 75 s cycle runs.

**Trade-off:** If a future iRacing build introduces a WPF transparency
regression similar to the pywebview one, we'd reintroduce the hack the same
way. Currently no such bug; revisit only if it actually breaks.

---

## 2026-05-29 — Port 09 polish slice: scoped to cohesion, not chrome rework

**Context:** Session 09's brief is open-ended: animation timing & easing
cohesion, typography, colour palette settlement, broadcast director, tower
scene transitions, multi-monitor placement, cohesion details, anything flagged
"Pending" in prior journals. With the ship-clean v1 already on disk and no
human eyeball pass yet performed against an active iRacing session, the
deliberate-pace principle pointed at the slice that doesn't require live
verification to be safe.

**Decision:** This session ships a **cohesion + concrete-bug** slice:
1. POLISH_BACKLOG #1 (bar window clips the top of the watch) — fixed.
2. Centralise the easing math the renderers had each re-implemented privately
   into a new `ApexHUD.Native.Overlays.BroadcastTiming` module — see the next
   entry.
3. Unify the deep-glass alpha drift in `StartingGridRenderer` from 0.86 down
   to the canonical 0.82 every other glass card uses.

Out of scope this session (re-deferred, with reason):
- Typography consistency — every overlay already loads Cormorant Garamond +
  Inter via the central `Fonts` constants (Port 00); per-overlay font-size
  choices are intentional and tied to each card's information density.
- Canonical class palette — already resolved in Port 04 (`BroadcastPalette` in
  Common, GTP picks purple). No drift detected.
- Broadcast director (`story-engine` / `layout-director`) — not earning its
  keep yet; tower↔ticker rotation only matters once the ticker exists, and
  PORT_PLAN already lands that post-v1.
- Tower scene `.mp4` transitions, multi-monitor placement — both deferred to
  their own deliberate decisions; out of scope to bundle into the polish pass.
- TrackCorners expansion beyond Spa (POLISH_BACKLOG item) — low priority per
  the backlog's own note ("unless the corner-name callout starts feeling
  missed"); revisit when Angelo races somewhere new and notices.
- Synchronised broadcast-start clock — surveyed and rejected. Only the watch
  has a truly launch-time entrance animation; the other seven renderers
  animate in response to data state changes (visibility flips, phase changes,
  events), so sharing a single launch epoch wouldn't make them visibly more
  in sync.
- Hardcoded inline status colours in pit/battle/fastest (PIT amber, gain
  green) and chrome top-accent treatment unification across glass cards — both
  are real surface area, both are low-risk surgical work for a later polish
  iteration, neither is blocking v1.

**Reasons:** The session prompt explicitly asked for a "coherent, bounded slice"
rather than a sweep. The three items shipped have the highest cohesion payoff
per line of diff: one fixes a concrete clip bug, one removes ~30 lines of
duplicated easing math across six files, one removes a one-token visual drift
that's hard to spot but easy to live with. The deferred items are journaled
rather than punted — Session 10+ has a clear handle on each.

---

## 2026-05-29 — BroadcastTiming centralises easings; durations are documented but not enforced

**Context:** Six renderers each had a private static easing function:
`BattleCardRenderer.EaseOut`, `FeaturedCardRenderer.EaseOut`,
`StartingGridRenderer.EaseOut`, `SponsorRenderer.EaseOutCubic` (all four
identical: `1 − (1−t)³`), and `FastestLapRenderer.EaseOutQuint` +
`PitCalloutRenderer.EaseOutQuint` (both identical: `1 − (1−t)⁵`).
`FeaturedCardRenderer` also had an inline smoothstep `t·t·(3−2t)` for its
battle-card cross-fade. The legacy CSS design system had named these and
their durations: `--ease-out` / `--ease-out-soft` / `--ease-overshoot`, and
`--anim-fast 240ms` / `base 480ms` / `slow 900ms` / `very-slow 1600ms`.

**Decision:** New file `src/ApexHUD.Native.Overlays/BroadcastTiming.cs`:
- Four design-system duration constants (`FastMs`, `BaseMs`, `SlowMs`,
  `VerySlowMs`) — documented as guidance for new animations, not enforced on
  existing ones (the legacy per-overlay entrance timings — watch 700 ms,
  featured 420 ms, battle 920 ms, etc — are deliberate and stay as-is).
- Four easing helpers: `EaseOut`, `EaseOutSoft`, `EaseOutQuint`, `Smoothstep`.
  Each is a clamped polynomial approximation of the CSS cubic-bezier it names,
  inline-callable, allocation-free.
- The six renderers' private easing methods stay in place as one-line forwards
  to `BroadcastTiming.*` — preserves the renderer-local names everyone reads
  at the call sites while the actual math lives once.

`CubicBezier` (Newton-Raphson Bezier solver from Port 01's `WatchArrive`)
stays in `WatchArrive.cs`. The watch overshoot bezier
`cubic-bezier(0.34, 1.4, 0.64, 1)` is bespoke (not a design-system curve),
and the watch is the only consumer; lifting the solver into BroadcastTiming
would be premature abstraction.

**Reasons:**
1. Six identical static methods is the textbook "extract a helper" smell.
   The polish pass is the right time to pay that down.
2. Naming the design-system durations on disk makes them grep-able the next
   time someone authors a new overlay — the legacy CSS variables were a real
   onboarding aid that this codebase had lost.
3. Durations stay non-enforced because the legacy values *aren't* uniform on
   purpose — a 240 ms watch entrance would feel slammed; a 1600 ms pit
   callout would miss its window. Each renderer's existing constant is the
   broadcast-feel it was tuned to.

**Trade-off:** Each renderer still defines its own one-line `EaseOut` /
`EaseOutQuint` forward. Could be deleted in a future pass that just calls
`BroadcastTiming.EaseOut` directly — but the call-site readability is better
with the local name, and one line of forwarding is a fair price.

---

## 2026-05-29 — Bar window: bar plate dropped 20 px so the watch doesn't clip

**Context:** POLISH_BACKLOG item #1. The session-bar window was 230 px tall
at (0, 0) with the bar plate anchored at `BarPlateTop = 14`; the watch sat
centred on the plate (plate height 92 → watch centre y=60) with radius 64 px,
so the watch's top edge reached y = −4 and clipped against the monitor's top
edge. Angelo flagged it from a Session 03 screenshot.

**Decision:** Shift `BarPlateTop` from 14 to 34 (the only changed value).
Watch centre moves to y=80, watch top to y=16 — gives ~16 px of breathing
room above the bezel, which is also enough that the watch's drop-shadow
(`0, 3, sigma 4`) and the entrance blur (≤ 2 px) don't crowd the window
edge.

**Reasons:**
1. The plate moves; the watch alignment to the plate stays the same; nothing
   else needs to change. Smallest possible surgical edit.
2. Window height (230) and width (primary-monitor) are unchanged, so
   click-through, transparency, and the existing 18 px overhang relationship
   between watch and plate are all preserved.
3. The visible bar position drops 20 px on screen, which is closer to the
   legacy `(0, −8)` window placement than the old (0,0) plate-at-14 was —
   the legacy negative offset existed to give the watch breathing room
   above the monitor edge.

**Trade-off:** Total visible chrome (plate top + watch overhang) is now ~20 px
lower from the screen edge. Visually nearly indistinguishable from the
legacy at typical monitor heights; if Angelo wants the bar tighter to the
top of the screen later, drop the bar plate height a touch and lower
`BarPlateTop` proportionally — the relationship is one constant.

---

## 2026-05-29 — Battle-mode choreography: tower↔ticker swap on `App.IsBattleActive`, no Director service

**Context:** Angelo's first live on-rig review (post-Port 09) reshaped what
"battle mode" should look like on screen. The desired end state: when a
same-class battle is live, the screen reads as a clean broadcast composition —
session bar on top (unchanged), one battle card pinned as a true TV lower-third
at the bottom, the timing tower hidden, and a compact horizontally-scrolling
**standings ticker** under the session bar carrying the running order. When no
battle is live, the tower is the standings view and the ticker is hidden. The
queue file `11_port_standings_ticker.md` had pencilled in a `Director` service
to coordinate this (tower↔ticker rotation, wall-clock cycle). With Angelo's
choreography pinned to a single signal (`battle-active = yes/no`), a Director
service would be infrastructure built for a coordination problem this signal
already solves.

**Decision:** Keep `App.IsBattleActive` (Port 06) as the **single coordinator**
for all four windows; no Director service. Update consumers:
- `TimingTowerRenderer` cross-fades to 0 over 220 ms when the flag flips on, and
  back over 220 ms when it flips off. When fully hidden the renderer skips
  painting (no lingering dim).
- New `StandingsTickerRenderer` cross-fades from 0 → 1 over 320 ms on flip-on,
  back to 0 on flip-off, and skips painting when fully hidden.
- `BattleCardWindow` already wrote the flag every frame from `renderer.IsActive`
  (Port 06); kept verbatim.
- `FeaturedCardRenderer` continues to yield to ~10 % when the flag is set
  (Port 06 behaviour, deliberately not touched).
- `SessionBarRenderer` is and remains battle-agnostic — verified by audit, no
  edits needed.

**Reasons:**
1. The single flag is enough. Tower↔ticker rotation on a wall-clock cycle was
   the legacy `layout-director.js` design when there was no shared in-process
   state; with a single .NET process and one static flag every window pulls per
   frame, the two views are guaranteed-coherent without a coordinator object.
2. Speculative infrastructure violates the project's deliberate-pace principle.
   If a future overlay (a narrative `story-engine` event) needs more than one
   coordination signal, OR it into the same flag or promote `App.IsBattleActive`
   into a small `BroadcastBus` then — but only when a second consumer of that
   shape actually exists. (The Port 06 trade-off note already anticipated this.)
3. Cross-fades cap at 320 ms so the swap reads as a deliberate broadcast cut,
   not a snap and not a lingering dim — the exact failure mode Angelo flagged.

**Trade-off:** Tower and ticker animate independently from each other. In the
~220 ms cross-fade window both are partially visible at low alpha. Acceptable —
the alpha is low enough that they read as the same broadcast layer transitioning,
and avoiding it would require sync infrastructure we don't yet need.

---

## 2026-05-29 — Battle card window pinned to the screen bottom (broadcast lower-third), not above the featured card

**Context:** Port 06 placed the battle card 12 px above the featured card window
(`Top = PrimaryHeight − 254 − 24 − 340 − 12`), which on a 1080 p monitor put the
window's top edge near y=450 — mid-screen. Angelo's live review: a battle card
in the middle of the screen reads as overlay clutter, not as a broadcast lower-
third. He wants it anchored to the bottom of the screen like the real-world
graphics that inspired this rebuild.

**Decision:** `BattleCardWindow.Top = PrimaryHeight − WindowHeight − 8`. The
window is 1280×340 with `CardOffsetY ≈ 30` of bottom pad inside, so the card's
bottom edge floats ≈38 px above the screen bottom — true broadcast lower-third
geometry. `BottomMargin = 8` lives next to the other geometry constants so a
future tweak is one line.

**Reasons:**
1. "Lower-third" only reads as lower-third if it sits in the lower third of the
   screen. The Port 06 placement was named "lower-third lane" but was actually
   mid-screen; this is the corrected anchor.
2. The card width/height/content/animations are unchanged. Geometry-only edit;
   the renderer is untouched. Matches Angelo's brief: "this is a position
   change, not a redesign."
3. The featured card stays in its Port 05 slot (`PrimaryHeight − 254 − 24`); it
   no longer pairs visually with the battle card, which is fine — the new
   choreography uses the featured card's existing 10 % yield to step aside,
   not to sit underneath.

**Trade-off:** On an ultrawide or very tall monitor the card sits flush against
the bottom rather than floating over the bottom-third. The 8 px margin is the
seam; raise it if a future polish pass wants more breathing room.

---

## 2026-05-29 — Standings ticker is rendered natively under the session bar; one class at a time, rotating

**Context:** Queue item 11 (the disabled legacy `standings-ticker` overlay)
became blocking once the battle-mode choreography needed a visible standings
view during a battle (because the tower hides). The legacy was 1056×60
@ (432, 160) with a class-label box on the left, a viewport on the right
scrolling driver chips, and the layout director rotating through classes every
15 s.

**Decision:** Build `StandingsTickerRenderer` + `StandingsTickerWindow` natively:
- **Window** 976×60 (matches the legacy bar inside its frame, no outer chrome
  needed in WPF), centred on the primary monitor at `Top = 150` — directly
  below the session bar (the bar plate ends at window-y 126; the watch overhang
  reaches y 144).
- **Layout**: 118 px class-label box on the left (class-color → transparent
  horizontal gradient, "STANDINGS" sub-label + class name in Inter), and a
  clipped chip viewport on the right with soft left/right edge fades.
- **Chips**: P{n} · #NUM · F. LASTNAME · gap. Leader chip gets a faint warm-
  gold wash; in-pit chips dim to 0.45 α with an amber stripe along the bottom.
- **Scroll**: a single horizontal cursor advanced by `nowMs/1000 × 60 px/s`,
  wrapped at the one-copy width; two copies drawn back-to-back for seamless
  loop. No scroll when fewer than `MinChipsToScroll = 4` chips (a short field
  looks worse mid-scroll than centred).
- **Class rotation**: when there's more than one class, advance to the next
  every `ClassCycleMs = 12_000`. Slightly faster than the legacy's 15 s so a
  battle's worth of context cycles in a feel-right cadence.
- **Visibility**: gated entirely by `App.IsBattleActive` (with a 320 ms
  cross-fade owned in the renderer, mirroring the tower's 220 ms fade). The
  renderer paints nothing when fully hidden.

**Reasons:**
1. Reuses Port 02's per-class grouping — `RaceSnapshot.ClassLeaderboard` is the
   same shape the tower (Port 04) consumes. No data-layer change.
2. Reuses `BroadcastPalette.Class`, `BroadcastPalette.Leader`, and the bundled
   `Fonts.Inter.Bold` for the chips — cohesion with every other broadcast card.
3. The legacy used CSS `transform: translateX(-50%)` on a 2× chip strip; the
   native equivalent is a Skia-side cursor + `ClipRect` + double-draw, which is
   trivial at this content volume and avoids needing a `Storyboard`.
4. Class rotation matches the legacy's rotating-class layout in spirit but is
   driven by `nowMs` math, not a separate timer.
5. No Director service. The brief's "broadcast director" was speculative; the
   single `IsBattleActive` flag is the entire coordination this overlay needs
   (see the choreography decision above).

**Trade-off:** Per-frame chip-width measurement allocates a small `Chip[]`
array. ~12 entries × a record-struct on a 60 Hz redraw is negligible on this
hardware; revisit only if a profile flags it. The "first observation" gap of
the first class shown is whichever class is at `ClassLeaderboard[0]` — i.e.
the leader's class. Could be biased to the player's class instead in a future
polish pass.

---

## 2026-05-29 — Session bar stays battle-agnostic (audit-confirmed, no change)

**Context:** Angelo's brief said the bar "currently extends / alters" during a
battle and asked us to find and remove it. Audit:
`SessionBarRenderer` and `MainWindow` do not reference `App.IsBattleActive` or
any battle state; the bar paints identically regardless. The PORT_PLAN §1
scoping cut for Port 03 explicitly omitted the legacy bar's battle-mode
extension ("battle mode, ticker, rotating chip etc. are hidden in production
and intentionally omitted"), so the native bar never inherited the legacy
behavior Angelo remembered.

**Decision:** No code change. Documented here so a future session doesn't go
looking for the same thing. The session bar is and remains battle-agnostic.

**Reasons:**
1. The desired end state ("bar visible, unchanged") already holds.
2. Editing the bar to "remove" a behavior that isn't there would risk
   introducing a regression for no payoff.
3. What Angelo may have observed on screen and attributed to the bar is more
   likely the **featured card's existing 10 % yield** (Port 06 decision) —
   that's the closest thing to "something subtle happening near the top of the
   screen" during a battle. That behaviour is intentional and deliberately
   left in place this session.

**Trade-off:** If Angelo's next live look confirms there genuinely is a bar
behaviour we're not seeing in code, revisit — but expect to find it in a
sibling (featured card, or even monitor-edge composition) rather than the bar
itself.

---

## 2026-05-29 — StartingGrid glass alpha aligned to TowerGlassBase 0.82 (was 0.86)

**Context:** `StartingGridRenderer.BgGlass` was the only glass card painting
its base at hex alpha 219 (~0.86 — the legacy `--surface-base`). Every other
glass card (tower, featured, battle, fastest-lap, sponsor) used 209 (~0.82,
matching `BroadcastPalette.TowerGlassBase` × `theme.css --bg-glass`). Both
values came from the legacy CSS variables — `--surface-base` was a separate
token from `--bg-glass`, used on the standalone grid card — but the grid is
a peer broadcast graphic in the native suite, not a settings dialog or modal,
so the legacy distinction was structurally over-applied.

**Decision:** Change the byte to 209 so the grid matches every other glass
card. Comment updated to record the change.

**Reasons:**
1. Cohesion. When the bar, tower, featured, battle, grid, fastest and sponsor
   all share a screen during pre-race, identical glass alpha is the single
   strongest "these are one broadcast" cue.
2. The two values differ by 4/255 — 1.6 percentage points of opacity.
   Indistinguishable in isolation; subtly off when they sit side by side.
3. The legacy `--surface-base` vs `--bg-glass` split existed to separate
   modal-card chrome from lower-third chrome. The grid is a lower-third in
   the native suite; the right side of that split is `--bg-glass`.

**Trade-off:** None observable. If a future polish pass wants the grid
fractionally more opaque so the text inside reads better against bright iRacing
content, raise the canonical `TowerGlassBase` byte for every glass card at
once — the whole suite drifts together, deliberately.

---

## 2026-05-29 — Canonical driver-name formatter (`NameFormat.Broadcast`)

**Context:** Driver names were being formatted by five different ad-hoc
helpers — `StartingGridRenderer.ShortName`, `StandingsTickerRenderer.ShortName`,
`PitCalloutRenderer.FormatDriverName`, `FastestLapRenderer.FormatDriverName`,
and inline `string.IsNullOrEmpty(d.AbbrevName) ? d.Name : d.AbbrevName` reads
in `TimingTowerRenderer`, `FeaturedCardRenderer`, and `BattleCardRenderer`. The
inline reads in tower/featured/battle rendered iRacing's raw
`AbbrevName` ("Ramorellos, A"), which Angelo flagged in live review as the
wrong shape — he wants every overlay to read "A. RAMORELLOS".

**Decision:** Add `ApexHUD.Native.Common.NameFormat.Broadcast(string?, string?)`
(plus a `Broadcast(Driver)` convenience overload) as the single broadcast name
formatter. Every overlay site routes through it; the five ad-hoc helpers are
removed (or collapsed to one-line forwards). Output shape:
`"{FirstInitial}. {LASTNAME}"` upper-cased.

**Source preference order:** `AbbrevName` first, then `UserName` fallback. The
formatter accepts either iRacing shape:
- `UserName` form `"First Last"` → `"F. LAST"`
- `AbbrevName` form `"Last, F"` (comma-separated) → `"F. LAST"`

**Surname assumption (worth recording):** For the `"First Last"` form, the
**last whitespace-delimited token** is treated as the surname. This is wrong
for names with multi-token surnames — `"Pieter Van der Berg"` resolves to
`"P. BERG"`, not `"P. VAN DER BERG"`. We mitigate by preferring `AbbrevName`
when present, because iRacing authors it with the full surname before the
comma, which sidesteps the assumption. `"Van der Berg, P"` resolves
correctly to `"P. VAN DER BERG"`. The brief explicitly accepted this
trade-off ("use the last whitespace-delimited token … good enough; note any
assumption in DECISIONS.md").

**Reasons:**
1. One formatter means one bug fix when an edge case surfaces, instead of five.
2. Lives in Common alongside `LapTimeFormat`, `LiveGap`, `ClassDetection` —
   the established home for pure broadcast helpers, SkiaSharp-free and
   IRSDKSharper-free.
3. The legacy "PREFIX-Last, First" roster-override prefix (e.g. "GT3-Smith,
   Joe") was duplicated in two of the five sites; centralising kept the
   strip-prefix-before-parse logic exactly once.

---

## 2026-05-29 — Starting grid: one stable rich-row layout per column (was three buckets)

**Context:** `StartingGridRenderer` chose between three row layouts per class
column based on `cls.Drivers.Count`:
- **Normal** (< 18 cars) — 40 px rows, full layout: license chip, number chip,
  name, iRating, manufacturer logo.
- **Dense** (18–25 cars) — 28 px rows, **stripped**: no license, no iRating.
- **UltraDense** (≥ 26 cars) — 22 px rows, same stripped layout.

Angelo's two live screenshots, taken seconds apart in the same session,
showed the GT3 column flipping between the Normal layout ("17 CARS") and
the Dense layout ("18 CARS") frame to frame — the per-frame `Drivers.Count`
was oscillating across the 18-car threshold, dragging the bucket selection
(and therefore the entire visual mode) back and forth. The left column (GTP)
sat below the threshold so it was stable.

**Decision:** Remove the bucket selector entirely. Every class column draws
the rich row layout (the former `Normal` bucket) regardless of field size.
The `Bucket` enum, `ChooseBucket`, and `ForBucket` are deleted; one helper
`RichRow(postRace)` returns the only `RowMetrics` shape.

**Reasons:**
1. The brief was explicit: "The desired target is the FULL detailed row
   (State A: number box + name + iRating + logo) for every class column."
   The rich layout was already implemented; the bucket selector was the
   *only* thing dropping to a stripped form.
2. Determinism beats hysteresis here. Hysteresis would keep three branches
   and a smoothing rule; collapsing to one layout removes the entire class
   of state-dependent visual flips, which is the rollback principle applied
   to the renderer ("no nuclear option, but the smallest surgical change
   that removes the cause, not just the symptom").
3. Large fields don't break — the existing `ClipRect` on `rowsRect` plus
   the `if (top >= rect.Bottom) break;` loop guard already truncated rows
   that didn't fit. A 20+ car field clips the bottom rows; same overflow
   behaviour the legacy CSS had.

**Trade-off:** Class columns with > ~15 cars (the body height fits roughly
that many at 40 px) lose visibility of the tail of the field. Acceptable
per the brief, and the count badge ("18 CARS") still tells the whole story
in the header. If a future session wants more rows visible without
flipping layouts, the right move is to **reduce the row height once** for
all fields (still one deterministic layout, just denser), not reintroduce
threshold-based bucketing.

---

## 2026-05-30 — PresentationState: one coordinator for every overlay's visibility

**Context:** The suite had accreted three independently-authored visibility
rules. The starting grid gated on `Phase ∈ {PreRace, PostRace}` inside its
renderer (Port 07). The tower cross-faded on `App.IsBattleActive` (Session 14).
The featured card had a relevance state machine AND a 10 % battle yield AND
no phase gate, so during pre-green it kept rendering a "PRE-RACE" lower-third
that wasn't supposed to be on screen. The ticker also gated on
`App.IsBattleActive`. Three sources of truth, three failure modes, none of
which agreed about restoration on battle exit — Angelo flagged that only the
battle card transitioned correctly in live use.

**Decision:** Add a single `ApexHUD.Native.Overlays.PresentationState` static
class that owns the entire visibility contract. It exposes three signals:

- `Phase` — read from the latest snapshot every access.
- `Lap1Complete` — a process-wide latch; flips to true the first frame
  `snap.Leader.LapsCompleted >= 1`; resets to false on a transition back to
  `RacePhase.PreRace` (so a session reset starts the latch fresh).
- `IsBattleActive` — written by `BattleCardWindow` from the battle renderer's
  `IsActive` (same pattern as Port 06's `App.IsBattleActive` flag, which is
  removed in favour of this one).

From those three it derives every overlay's visibility:

| Element        | Property            | Predicate |
|----------------|---------------------|-----------|
| Session bar    | `BarVisible`        | `!IsPostRace` |
| Session bar    | `BarFullWidth`      | `IsRacing && IsBattleActive` |
| Starting grid  | `GridVisible`       | `IsPreGreen \|\| IsPostRace` |
| Timing tower   | `TowerVisible`      | `IsRacing && Lap1Complete && !IsBattleActive` |
| Featured card  | `FeaturedVisible`   | `IsRacing && Lap1Complete && !IsBattleActive` |
| Standings ticker | `TickerVisible`   | `IsRacing && IsBattleActive` |
| Battle card    | `BattleCardVisible` | `IsRacing && IsBattleActive` |

Every renderer that previously kept its own gate now reads exactly one
property and cross-fades on the boolean flip. The match between "visible"
and "should be visible" is now a single grep away.

`PresentationState.Tick(snap)` is called once per frame from a single
`CompositionTarget.Rendering` hook in `App.OnStartup` — the only place the
latch is updated, on the WPF render thread, with no dispatcher marshalling.

**Reasons:**
1. The legacy ApexHUD-Web used four separate windows with no shared memory
   and CSS state spread across files; the native rebuild already collapses
   to one process with one snapshot, so one coordinator is the obvious
   shape. The Session 14 decision *not* to build a Director service still
   stands — this is not a service, it's a derived view over already-existing
   state, with no scheduling or wall-clock coordination.
2. The `Lap1Complete` latch is the one piece of cross-frame memory the new
   contract introduces. Computing it on the snapshot (per-row in
   `RaceStateService`) would either pollute every consumer's snapshot with
   "is this the first time we've seen this?" semantics or require keeping
   the prior leader's lap count in the service. The latch lives where it's
   used — at the presentation seam — and resets cleanly on `PreRace`.
3. Each renderer still owns its own cross-fade timing (200–320 ms,
   per-element). The coordinator gives the boolean; the cuts stay broadcast-
   tuned per overlay.

**Trade-off:** `PresentationState` is a static facade. A second source of
"is a battle active" (e.g. a future story-engine narrative event) would need
to OR into the flag here, or this would need to grow into a `BroadcastBus`
instance. Same trade-off the original Port 06 decision noted. Cheap to
refactor; speculative to anticipate.

---

## 2026-05-30 — Battle-mode session-bar expansion, restoration cycles correctness

**Context:** With the coordinator in place, the brief's remaining choreography
items were a session-bar full-width expansion during battle (the bar plate
grows from 976 px centred to the primary-monitor width, restoring on battle
exit), reliable restoration across many battle on/off cycles, and a flush-
attached ticker with the watch overlapping its top edge.

**Decisions made implementing it:**

- **Bar width animation lives in `MainWindow`, not the renderer.** The
  renderer accepts an optional `barWidth` parameter (defaults to the legacy
  `BarWidth` constant so call sites that don't care don't need to pass it),
  and uses an internal `currentBarWidth` field for every helper that
  previously read the constant. The animation — a 400 ms smoothstep between
  `BarWidth` and `screenWidth` keyed off `PresentationState.BarFullWidth` —
  belongs at the window level next to the watch animation and the screen-
  width discovery. Keeping it out of the renderer means a future overlay
  that wants to draw the bar at a fixed width (e.g. a thumbnail) doesn't
  inherit the animation state.
- **Right zone slides outward instead of stretching.** Phase chip / lap
  counter / flag-state width stays at its legacy 408 px span; only `zoneLeft`
  and `zoneRight` recompute relative to `currentBarWidth`. The alternative
  — stretching every right-zone element proportionally — would make the
  phase chip read as a comically-wide chrome bar during battle, which is
  the opposite of the brief's broadcast intent.
- **Watch stays on screen centre across both states.** The window's
  `plateX = (screenWidth − plateWidth) / 2` formulation collapses to 0 when
  the bar is full-screen and to `(screenWidth − 976)/2` when default; in
  both cases `watchCx = plateX + plateWidth/2 = screenWidth/2`. The legacy
  hand-coded `(plateX, plateY) → (0, 0)` could not have done this without
  re-resolving the screen width every frame.
- **Ticker pinned flush under the bar plate; watch overlaps the ticker.**
  Window `Top = 126` on the primary monitor (the plate bottom edge); window
  width = full primary monitor (matches the bar in expanded mode and is
  invisible-transparent outside the renderer's strip in default mode). The
  bar window is shown **last** in `App.OnStartup` so it sits above the
  ticker in z-order — the watch (drawn in the bar window) reaches y=144 in
  screen space, overlapping the top ~18 px of the ticker, which is the
  desired broadcast layering ("the watch face should overlap the top of
  the ticker slightly"). Reordering `Show()` calls is a one-line z-order
  fix without resorting to per-frame `SetWindowPos`.
- **Featured card's 10 % battle yield is removed.** The brief now requires
  full hide during battle and FULL opacity on return; the new outer gate
  on `PresentationState.FeaturedVisible` handles both directions cleanly.
  The old `ComputeBattleYield` method is replaced by `ComputeGateAlpha`.
  Trade-off: the legacy spotlight relevance state machine
  (`ForceVisible = true` bypass) is still in place as an inner gate, but
  during battle the outer gate takes the card to 0 regardless — the inner
  machine's behaviour during battle is now moot.

**Reasons:**
1. Single-knob restoration. Because every consumer reads the same
   `PresentationState.*Visible` property, "many on/off cycles restore
   every element" is enforced by construction: every flip drives every
   renderer's cross-fade through the same boolean transition. There is no
   state in any consumer that can leak.
2. The 400 ms bar-expand cadence matches the 280–320 ms tower/ticker fades
   in feel — long enough to read as a deliberate broadcast move, short
   enough not to drag.
3. Z-order via `Show()` ordering is the cheapest correct fix. The
   alternative — keeping the bar above the ticker via `SetWindowPos`
   per frame — would burn a hot path on a problem the OS already solves
   from the initial show order.

**Trade-off:** The ticker window is now always primary-monitor-wide, even
though the visible strip is centred and ~976 px in default mode (it never
shows in default mode because `TickerVisible` requires `IsBattleActive`,
but the window itself exists and paints transparent pixels). The cost is a
larger transparent-pixel clear per frame, which is microseconds on this
hardware.

---

## 2026-05-30 — Standings ticker scroll: continuous accumulator + fixed gap-column slot

**Context:** Angelo flagged the ticker as "seizurey" — clipping and jumping
back and forth instead of scrolling. Root cause: the renderer computed
scroll offset as `(nowMs / 1000 × ScrollPxPerSec) % oneCopyWidth`, where
`oneCopyWidth` was recomputed every frame from chip widths that depended on
the current gap-text formatting. When a gap rounded from "+0.3" to "+0.4"
between frames, the chip width changed by ~1 px; the modulus then jumped
non-monotonically — that is exactly the "seizurey" visual the brief
described.

**Decision:** Two complementary fixes in `StandingsTickerRenderer.DrawChips`:

1. **Free-running `scrollPx` accumulator advanced by `delta_ms × speed`.**
   Monotonic by construction. Wraps once per frame at the current
   `oneCopyWidth` (so any width change is absorbed by a single bounded
   adjustment, not amplified into a multi-pixel jump). Round to integer
   pixels at draw time so the strip doesn't shimmer between sub-pixel
   positions.
2. **Fixed gap-column slot width (`GapSlotWidth = 56 px`).** The dominant
   source of frame-to-frame chip-width fluctuation was the gap text
   ("+0.3" → "+0.4" changes the measured width by ~1 px every few
   frames). Pinning the gap column at a slot wide enough for "+59:59"
   and "LEADER" removes that source: pos / num / name are stable
   strings, so chip widths now only change on class rotation or a
   driver leave/join. The displayed gap text is **right-aligned within
   the slot** so the chip looks tabular regardless of length.

The two work together: the accumulator handles any remaining
slow-changing width transitions cleanly, and the fixed gap slot removes
the noisy fluctuation that the old modulus would have amplified.

**Reasons:**
1. A cache over the chip array was considered but rejected: the cache key
   `(class name, driver count)` doesn't invalidate on position swaps, so
   a P1↔P2 change between frames would have rendered the wrong driver's
   name in the wrong slot. Pinning the gap column instead of the whole
   chip keeps the live data and removes the jitter at the same time.
2. The accumulator pattern is standard for marquee-style scrollers; the
   `nowMs % width` pattern only works when `width` is constant.
3. Integer rounding of the cursor matches what the legacy CSS effectively
   did with `transform: translateX(...px)` on a sub-pixel-rounded
   compositor — no shimmer, no visible artefacts.

**Trade-off:** Chips with very short gap text leave a small amount of
empty space inside the right-padded gap slot. Reads as deliberate tabular
alignment rather than a layout bug, and is the visual price of stable
chip widths.



---

## 2026-05-30 — Repaint gating + frame-rate-independent timing policy

**Context:** Angelo's live review after Session 17 (visibility state machine)
flagged systemic clunkiness across every transition. Root-cause analysis
identified two architectural drivers, both independent of any individual
renderer:

1. **All nine overlay windows unconditionally called
   `Canvas.InvalidateVisual()` from `CompositionTarget.Rendering` every frame**,
   regardless of whether the window was visible or its renderer was painting
   any pixels. Each window is `AllowsTransparency="True"` + a software
   `SKElement`, so every per-frame composition pass goes through WPF's CPU
   compositor on the UI thread. Nine simultaneous unconditional repaints under
   iRacing load competed for the same UI-thread budget and dropped frames.
2. **Two renderer animations advanced per-frame instead of per-elapsed-time**:
   `BattleCardRenderer`'s gap lerp (`displayGap += diff * 0.18` per frame) and
   tension EMA (`smoothedTension += 0.32 * (rawDelta - smoothedTension)` on a
   raw per-frame `rawDelta`). At anything other than a steady 60 fps the lerp
   converged at the wrong wall-clock pace and the EMA mis-scaled its input.
   The ticker also Math.Round'd a sub-pixel scroll cursor to integer px, which
   produced a 1 px jump roughly every other frame at ~60 px/s velocity.

**Decision:** Adopt two project-wide policies for every overlay going
forward, and apply them retroactively to the existing nine windows.

**Policy 1 — Repaint gating.**
A window only calls `InvalidateVisual()` when it is actually visible per
`PresentationState` (or while a just-flipped visibility transition is still
mid-fade). Once a window is hidden and the fade has elapsed, it goes quiet
and consumes zero frame budget until its gate opens again. The single shared
helper `RepaintGate` (`src/ApexHUD.Native.Overlays/RepaintGate.cs`) encodes
the predicate; each gated window owns a `RepaintGate` field configured with
the renderer's own fade duration plus a 60 ms overhang so the renderer
settles past floating-point epsilon before the window stops repainting.

Five windows are gated under this policy: `TimingTowerWindow`,
`FeaturedCardWindow`, `StandingsTickerWindow`, `StartingGridWindow`,
`SponsorWindow`. Four windows keep their unconditional per-frame paint
deliberately:

- `MainWindow` — the session bar animates a continuous-sweep watch and
  procedurally-drawn weather; it must paint every frame while present.
- `BattleCardWindow` — the renderer publishes
  `PresentationState.IsBattleActive` (which gates `TickerVisible`,
  `TowerVisible`, `FeaturedVisible`, `BarFullWidth`) out of its
  `OnPaintSurface`. Gating it on a flag it itself produces would deadlock.
- `FastestLapWindow` / `PitCalloutWindow` — these detect transitions
  (`BestLapTime` improvements, `IsInPit` edges) by comparing against
  state held from the previous tick. Skipping frames would drop events.

For `SponsorWindow` the renderer exposes `IsActiveOrAnimating` and a cheap
`Tick(snapshot, ms)` method the window calls during the long ~75 s hidden
phase so the cycle still advances without a Skia paint.

**Policy 2 — Frame-rate-independent animation timing.**
Every animation's progress must be a pure function of absolute elapsed
wall-clock time (a `Stopwatch` / `nowMs`), not of accumulated per-frame
deltas and not of an implicit 60 fps step. The existing
`BroadcastTiming.EaseOut / EaseOutSoft / EaseOutQuint / Smoothstep` family
already encodes the eased curves; renderers feed them
`(nowMs - eventStartedAtMs) / durationMs` and that is the entire contract.
The `BattleCardRenderer` smoothing was rewritten in this session to honour
that contract:

- The gap lerp uses an exponent-scaled per-step weight
  `1 - (1 - 0.18)^(dtSeconds * 60)` so the wall-clock convergence rate is
  identical at 30 fps, 45 fps and 60 fps (the legacy 60 fps constant is
  reproduced exactly when `dtSeconds = 1/60`).
- The tension input `rawDelta` is normalised by `1 / (dtSeconds * 60)` so
  it expresses a "60 fps equivalent delta", keeping
  `TensionFullScale = 0.045 s` valid as a per-60-fps-step unit.
- The tension EMA weight is exponent-scaled the same way.

The ticker scroll was already a `delta * pxPerSec` accumulator (correct);
the `Math.Round` on the draw cursor was removed in favour of full
sub-pixel positioning, and the per-frame chip-width re-measurement was
replaced by a layout cache keyed on `(currentClassIdx, drivers-by-position
CarIdx list)`.

**Reasons:**
1. Three of the nine windows are visible at any one time during a typical
   broadcast layout (bar + tower + featured, or bar + ticker + battle).
   Gating six of the remaining out of the every-frame loop is the single
   largest throughput win available without switching off
   `AllowsTransparency` or moving to GPU rendering. Of those six, five
   actually go quiet by gate; the sixth (battle card) keeps painting for a
   producer-side reason called out above.
2. Tying every animation to wall-clock time means a momentary frame drop
   under iRacing load shifts the rendering one frame, not the entire animation
   timeline. Motion stays correctly paced; nothing "speeds up" to catch up.
3. Both policies are renderer-local edits: no architectural rework, no GPU
   path, no window consolidation. Consistent with the "low-risk, surgical"
   Rollback principle in `CLAUDE.md`.

**Trade-off / what this does not fix:** The deeper throughput ceiling is
that all nine windows are CPU-rasterised software-transparent layered
windows. Even with five gated quiet, the visible three still go through
the same CPU compositor. If the gated improvements are insufficient under
real iRacing load, the next architectural step is one of (a) switch to
`SKGLElement` (DirectComposition-backed GPU surface) for the active
windows, or (b) consolidate all overlays into one full-screen transparent
canvas with a single render loop. Both are larger changes and were left
out of this session deliberately — flagged here so a future session can
pick them up if Angelo reports the broadcast still doesn't feel smooth.

---

## 2026-05-30 — Battle activation: opening-lap force + 2.0s same-class rule with hysteresis

**Context:** Port 06's battle detection only fired when a same-class opponent
was within ~1 s (wheel-to-wheel). Angelo's live review after Session 18:
battle mode wasn't coming on at the green flag / start of lap 1, even
though the bunched formation is structurally a battle for everyone, and it
also wasn't reliably triggering for closely-following cars at typical race
gaps. He wants the start to *always* read as battle mode and a 2-second
rule thereafter.

**Decision:** Replace the single 1.00 s / 2.00 s threshold pair in
`BattleCardRenderer.EvaluateLive` with two OR'd activation sources:

1. **Opening-lap force.** From green flag (`Phase == Broadcast` in a race
   session) until `PresentationState.Lap1Complete` flips true, force battle
   ON regardless of gaps. Picks the nearest same-class rival (ahead
   preferred when both exist; closer wins ties). If the player has no
   same-class peer at all, no sensible pair → no force, fall through to
   the 2.0 s path (which also returns None) so the card stays hidden.

2. **2.0 s same-class rule with hysteresis.** Enter at gap ≤ 2.0 s; once
   on, stay on until no same-class neighbour is within 2.3 s. Min visible
   dwell once on: 1.5 s. Dropout grace 1.5 s (a brief blip can't strobe
   the card off either).

Constants live in `BattleCardRenderer` (one place, easy to tune):

| Constant            | New     | Was     |
|---------------------|---------|---------|
| `EnterThresholdS`   | 2.00    | 1.00    |
| `ExitThresholdS`    | 2.30    | 2.00    |
| `MinDwellMs`        | 1500    | 4000    |
| `DropoutGraceMs`    | 1500    | 1500    |
| `ReopenCooldownMs`  | 2500    | 2500    |

**Reasons:**
1. The bunched start is a battle for everyone by definition; forcing the
   card on matches broadcast intent and Angelo's mental model of the race
   start. The 2.0 s rule afterwards covers the typical "loose duel" the
   1 s wheel-to-wheel threshold was missing.
2. Routing the opening-lap force through the existing renderer state
   machine (rather than a parallel detector or a `PresentationState`
   override) means the smoothing, hysteresis, dwell and dropout-grace
   already implemented handle the transition out cleanly when the leader
   crosses the line on lap 1. No second source of truth, no `PresentationState`
   plumbing changes — `IsBattleActive` is still produced by
   `BattleCardWindow` from `renderer.IsActive`, and the bar-expand /
   ticker / tower-hide / featured-hide choreography (Sessions 14 + 16)
   stays driven by exactly the same signal it always was.
3. `Lap1Complete` is the snapshot-derived latch Session 16 already
   introduced for the visibility coordinator; the activation rule
   genuinely needs the same edge, so reusing it is the right shape.

**Trade-off:** During the opening lap the displayed gap reads honestly
(e.g. "4.50 SEC AHEAD") even though that wouldn't have qualified as a
"battle" under the legacy threshold. The brief explicitly accepts this:
the card's *presence* during the bunched start is the signal Angelo
wanted; the gap number is honest about distance to the nearest rival.
Edge case: a player who is solo in their class during the opening lap
still sees no card — there is no sensible pair to render.

---

## 2026-05-30 — Standings ticker entries fill ~80% of the strip's vertical height

**Context:** After Angelo's live review, the standings ticker text was
reading too small — entries (position / car number / name / gap) sat as a
thin centred band in the 44 px strip, occupying roughly the middle third
(~33%) of its vertical height with broad empty margins above and below.
Far from the broadcast-prominent feel the ticker should land.

**Decision:** Scale the entry typography ~2.1× so the entries' line-box
(ascent + descent extent for Inter Bold ≈ 1.20 × `TextSize`) fills
~76–80% of the existing 44 px strip, with a small balanced ~5 px
margin top and bottom. Keep the strip height at 44 — the brief
preferred enlarging text within the existing strip — and let the
common `baseline = ChipHeight/2 − (ascent+descent)/2` centring keep
the now-bigger entries vertically centred.

**Final sizes (was → now):**

| Element                | Was (px) | Now (px) | Line-box ÷ 44 |
|---                     |---:|---:|---:|
| Pos number (Inter Bold)| 13 | 28 | ~76% |
| Car number             | 11 | 24 | ~65% |
| Driver name            | 12 | 26 | ~71% |
| Gap / "LEADER"         | 12 | 24 | ~65% |
| STANDINGS subtitle     |  8 | 11 | (class box, modest 1.4×) |
| Class name (left box)  | 15 | 22 | (class box, 1.5× to stay a peer label) |

Horizontal spacing was bumped modestly so the bigger entries breathe
without breaking the tabular feel that keeps the marquee smooth:
`ChipPad` 14 → 20, `ChipGap` 8 → 12, `GapSlotWidth` 56 → 100 (room
for "+59:59" / "LEADER" at the new 24 px). `ClassBoxWidth` 118 → 150
to hold the 22 px class label with its existing 0.08 em tracking.
Letter-spacing remains the same `K × size` formula at each call site
(`0.02 × pos`, `0.08 × num`, `0.04 × name`, `0.04 × gap`) so the
visual rhythm scales with the size — the constants now reference
the new `PosTextSize` / `NumTextSize` / `NameTextSize` / `GapTextSize`
fields rather than the old `13` / `11` / `12` literals.

**Reasons:**
1. Inter Bold's line-box ≈ 1.20 × `TextSize`; at 28 px that puts the
   pos line-box at 33.6 / 44 = 76% of the strip — close to the 80%
   target and centred with ~5 px even margin, matching the brief's
   "~10% margin top and bottom".
2. Pos > name > num/gap keeps the broadcast hierarchy the smaller
   sizes already had (pos was the largest at 13), now amplified.
   Common baseline (`baseline` computed once off `posPaint`'s
   metrics and shared across all four label paints) keeps the
   different sizes sitting on the same tabular line.
3. Strip height untouched: the watch overhang from the session bar
   still bleeds into the top ~18 px of the ticker (Session 16's
   intentional layering); growing the strip would have pushed the
   overlap off and required a window-Y change. The brief explicitly
   prefers enlarging the text first.

**Trade-off / what we kept:**
- Scroll speed (`ScrollPxPerSec = 60`), rotation cadence, fade
  timings, the delta-based scroll accumulator that fixed the
  "seizurey" jitter in Session 18, and the cached chip layout
  rebuild rule are all untouched. Only typography + the horizontal
  pad/gap/gap-slot constants changed.
- Chips are roughly 2.1× wider as a result (driven by the wider
  text + GapSlotWidth bump); at typical 12–18 driver fields the
  scroll loop is still well above the `MinChipsToScroll = 4`
  threshold and the marquee keeps continuous motion.

---

## 2026-05-30 — Battle card gap: guard invalid/early-race values to placeholder

**Context:** Session 19 forced battle mode ON during the opening lap (before
the leader completes lap 1). In that window `BattleDetection.Compute`
commonly returns `GapAheadSeconds = null` / `GapBehindSeconds = null` because
each driver's `IntervalSeconds` is `<= 0` until at least one full lap is on
the books. The opening-lap branch in `BattleCardRenderer.EvaluateLive`
substituted `?? double.MaxValue` (or `?? 0`); `displayGap` then became
`double.MaxValue`, and `g.ToString("0.00")` produced the runaway
"00000000…" string Angelo saw spanning the gap column in live race-start
footage.

**Decision:** Treat "no displayable gap" as a first-class state through the
full pipeline, modeled on the BEST LAP field's existing placeholder treatment.

- `EvaluateLive` opening-lap force: when the chosen opponent's gap is null,
  pass `double.NaN` through `gapF` (no sentinel substitution). The state
  machine still sees `gap.HasValue == true` so the card stays visible.
- `UpdateSmoothing`: a non-finite or negative `targetGap` parks `displayGap`
  at `NaN`, zeroes `smoothedTension`, and re-arms the snap path so the next
  valid gap lands cleanly without lerp lag from the previous invalid frame.
- `DrawGapColumn`: a `gapValid = double.IsFinite(displayGap) && displayGap >= 0`
  guard drives the big-number text (em-dash on invalid), the wheel-close halo
  (suppressed on invalid), and the `SEC` label (suppressed on invalid). The
  trend block + tension meter degrade to STEADY/empty automatically since
  `smoothedTension == 0` while the gap is invalid.

**Reasons:**
1. The runaway zeros were a single rendering artefact of `double.MaxValue`
   flowing through a fixed-point formatter — the same path produces clean
   output for any finite value. Fixing the source (NaN, not MaxValue) plus
   one finite-and-non-negative guard at the renderer covers every code path
   that can reach the gap text.
2. Matching BEST LAP's em-dash treatment keeps the card visually coherent
   during the opening-lap force window — both labelled "no data yet" fields
   now read the same way instead of one showing "—" and the other a wall of
   digits.
3. The placeholder is also a defense-in-depth guard against any future caller
   that might pass NaN/Infinity into smoothing (network glitches, paused
   replays, etc.) without each producer having to special-case.

**Trade-off:** Between the moment battle mode is forced on and the first
frame with a real gap, the gap column shows "—" with the trend at STEADY
and the tension meter empty. That is the intended placeholder behaviour and
exactly mirrors how BEST LAP looks on lap 1.

---

## 2026-05-30 — Timing tower: one value column, session-aware semantics, 10 s valid-lap flash

**Context:** The legacy tower row carried two trailing value columns —
`ColInterval` (64 px) and `ColGap` (82 px) — sourced from
`Driver.IntervalSeconds` and `Driver.GapToLeaderSeconds` respectively. In
practice the two readings carried more information than the row needed:
race viewers cared about gap-to-ahead-in-class; quali viewers wanted a
fastest-lap reading and a delta-to-ahead-on-pace, neither of which the
legacy row exposed. The double-column layout also crowded the name field
(`120 px` of 1fr at default tower width). Angelo asked for the foundation
step before any class cycling / video-transition / logo work: one value
column, session-type-aware semantics, plus a flash that surfaces the
just-completed lap time on a valid lap.

**Decision:** Collapse the two value columns into a single right-aligned
`ColValue = 92 px` column. Donate the freed 54 px to the 1fr name column.
Compute the value at draw time using three precedence rules:

1. **PIT badge** — unchanged amber "PIT" tag when the driver is on pit road.
2. **Valid-lap flash** — for 10 s after a row's driver completes a valid
   lap (no in-laps, no out-laps), the column shows that driver's
   just-completed lap time in `m:ss.mmm`. Each row's flash is independent.
3. **Session-type default:**
   - **Race** (`LooksLikeRace(SessionTypeName)` true): class leader → gold
     `LEADER` tag (existing treatment); others → `Driver.IntervalSeconds`
     formatted as a broadcast gap, with `LapDifference > 0` preserving the
     legacy "+1L" / "+2L" amber-bold treatment.
   - **Practice / Qualifying** (everything else): class leader → their
     `BestLapTime` rendered `m:ss.mmm` in gold; others → delta between
     their own fastest lap and the fastest lap of the driver directly
     ahead in class, formatted as `+0.000`. A driver with no valid
     fastest yet (own or ahead) renders an em-dash.

**Validity for the flash:** a per-`RowState` `WasInPitDuringLap` flag is
OR-ed with `Driver.IsInPit` every frame and reset when `LapsCompleted`
increments. At the increment frame, if the flag was false the lap is
valid → `FlashUntilMs = now + 10_000` and `FlashLapTime = LastLapTime`;
if true, no flash is started. This single mechanic rejects both in-laps
(pit-road touched somewhere during the lap) and out-laps (pit-road
observed at the start of the lap before the driver exits). First-sight
seeding initialises the flag from `IsInPit` so a row first observed mid
pit-stop doesn't fire a spurious flash on the subsequent crossing.

**Reasons:**
1. One column reads cleaner at row scale and removes the "two numbers,
   pick which one you care about" cognitive load. Race viewers want one
   gap; quali viewers want one time or one delta — never both columns at
   once. The 92 px width comfortably holds `1:23.456` at the existing
   15 px SemiBold treatment and `LEADER` at 11 px Bold with the legacy
   tracking; the freed 54 px makes the name field breathe noticeably,
   reducing ellipsis cases on tracks/series with longer driver names.
2. Routing the flash through the existing `UpdateRowState` lap-pulse path
   means the new behaviour reuses the same edge (`LapsCompleted` strictly
   incremented) the legacy white pulse already keyed off — no second
   detector, no replay-edge headaches. The flash and the PB pulse
   coexist: a valid lap that is also a new personal best paints the row
   green (PB pulse) while the value column reads the lap time (flash).
3. The single boolean `WasInPitDuringLap` collapses in-lap and out-lap
   detection into one rule. iRacing's `IsInPit` is true throughout the
   pit-road event regardless of direction, so any touch during a lap
   marks that lap invalid. The first-sight seeding edge-case (mid-stop
   start) is the only thing we can't otherwise infer; initialising from
   `IsInPit` covers it without a wider state-machine.
4. `LooksLikeRace` is shared in spirit with `BattleCardRenderer`'s
   identical substring helper; for now we keep a local copy in the tower
   renderer rather than promote it to a `Common` static — two callers
   doesn't yet justify a new module, and substring-matching `"race"`
   isn't likely to evolve.

**Trade-offs:**
- We lose the explicit gap-to-leader readout in race rows. The brief is
  deliberate: gap-to-ahead-in-class is the broadcast-relevant number for
  every row except the leader, and the standings ticker still carries
  the full leader-anchored hierarchy for viewers who want it.
- During the flash, the leader's row no longer reads `LEADER` for 10 s.
  This is the spec — the lap-time payload is more interesting than the
  static tag while it's fresh.
- The quali-mode "ahead in class on fastest" math assumes the snapshot
  orders `cls.Drivers` by class position, which in non-race sessions is
  iRacing's best-lap ordering. If the ordering ever shifts (e.g.
  qualifying-grid mode), the delta math will need a separate sort.
- This session deliberately did NOT take on class cycling, video
  transitions, or logos. Those are queued as the planned follow-up so
  Angelo can iterate on this foundation in live.

---

## 2026-05-30 — Timing tower scene rotation (Phase 1): SceneDirector + branded
splash, video seam reserved for Phase 2

**Context:** The tower has, since Port 04, painted the whole field at once.
Angelo's session brief asks the tower to behave like a TV broadcast — cycle
through focused boards (OVERALL → GTP → LMP2 → GT3 → loop, dropping any class
not present in the session), with a branded splash transition between each.
Phase 1 is scope-bounded: scene cycling, per-class boards, animated (non-video)
splash. Real `.mp4` playback is Phase 2 and explicitly out of scope. The
architecture must reserve a clean seam so Phase 2 only swaps the splash
*background* — every other part of the system (the scene swap timing, the
centred composite, the visibility coordination) stays identical.

**Decision (Phase 1 architecture):**

- **`SceneDirector` static coordinator** (sibling to
  `PresentationState`, lives in `ApexHUD.Native.Overlays`). Owns the
  rotation. Each tick it:
  1. Reads `PresentationState.TowerVisible`. If false, resets to OVERALL and
     halts — never carry a half-played splash across a battle takeover.
  2. Rebuilds the active scene list from the current snapshot: OVERALL always
     first, then any of `GTP / LMP2 / GT3` whose name appears in
     `RaceSnapshot.ClassLeaderboard`. Class priority lives in a single ordered
     `FocusClassPriority` array — adding a future class (e.g. LMP3) is a
     one-line append.
  3. Single-scene case (only OVERALL present, no focus class) short-circuits:
     never starts a splash. Nothing to rotate to.
  4. Holds the current scene for `SceneHoldMs = 20_000`, then begins a splash
     whose `pendingScene` is the next entry in the list.
  5. Activates the next scene at the splash's **opaque midpoint**
     (`splashStartedAtMs + splashTotal/2`) — when the splash is fully covering
     the lane, the rebuild is invisible.
  6. Splash ends at `splashTotal` (or `SplashFailsafeMs = 30_000`, whichever
     hits first — the failsafe is NOT a normal cap, only a hard floor against
     a Phase-2 video decoder stall).
- **Per-scene splash duration** lives in `CurrentSplashDurationMs`. Phase 1
  defaults all scenes to `DefaultSplashTotalMs = 1200` (a tasteful stand-in).
  Phase 2 sets it per-scene from the video file's length; the rest of the
  state machine doesn't change.
- **`TimingTowerRenderer` focus filter**: purely additive. Before the existing
  `foreach (var cls in snapshot.ClassLeaderboard)` loop, the renderer reads
  `SceneDirector.CurrentScene`. On a class-focus scene it `continue`s past any
  class whose name doesn't match — every other code path (class divider, row
  drawing, pulses, gates, dropout hold) is untouched. The freed vertical
  space is left empty for Phase 1 — same `RowHeight`, same row design — so
  the diff stays the smallest possible thing that exposes the new behaviour.
- **`TimingTowerSplashRenderer` + `TimingTowerSplashWindow`**: a transparent,
  click-through, topmost overlay matching the established
  `WS_EX_TRANSPARENT | WS_EX_LAYERED` pattern of every other window in
  `App.OnStartup`. Window geometry matches the tower lane exactly (432 ×
  primary-monitor height @ (0,0)). The renderer:
  - Draws a **class-coloured background** (deep-glass base + radial bloom +
    slow vertical sweep, all in SkiaSharp). This is the Phase-2 swap point.
  - Draws a **centred vertical composite** over the background: iRacing
    wordmark, series logo (resolved via
    `SeriesNames.ResolveLogoSlug(snap.SeriesId)` — gracefully omitted on
    unknown series), then the class name big at the bottom in the class
    colour (warm-ivory for OVERALL).
  - Reads `SceneDirector.SplashAlpha(nowMs)` for the fade and
    `SceneDirector.SplashScene` for the announced class. Renders nothing when
    no splash is active.
- **Window z-order**: `TimingTowerSplashWindow` is shown immediately after
  `TimingTowerWindow` in `App.OnStartup`, so it sits above the tower in the
  WPF z-order. `MainWindow` (the session bar) is still shown last, so the
  watch + clock continue to sit above the splash where they overlap — the
  splash never hides the chrome at the top of the screen.
- **Per-frame ticking**: `App.OnRenderingFrame` now calls
  `SceneDirector.Tick(snap)` right after `PresentationState.Tick(snap)`, so
  the rotation advances on the same WPF render-thread cadence every other
  overlay reads from. Splash window uses the existing `RepaintGate` pattern,
  gated on `SceneDirector.SplashActive`, with `SplashFadeMs (250)` as the
  buffer — when the splash is fully done, the window goes quiet.

**Phase 2 video seam (recorded explicitly so the next session inherits the
shape, not the freedom to redesign):**

1. The `DrawBackground` method in `TimingTowerSplashRenderer` is the *only*
   thing Phase 2 replaces. The centred composite stays identical — Phase 2
   keeps painting the iRacing wordmark, series logo, and class name on top of
   the (now video) background, with the same fade alpha applied to the layer
   as a whole.
2. Per-scene splash duration is already per-scene
   (`CurrentSplashDurationMs`), with no upper cap beyond the failsafe. Phase
   2 sets it to the video length when the splash starts (`Tick` already
   exposes the writable field internally — Phase 2 will set it from the
   chosen video's container duration at the moment it begins the splash).
3. The scene swap fires at `splashStartedAtMs + splashTotal/2`. For Phase 2
   that lands at the video's midpoint, where the chosen video should be at
   its peak opacity (the class identification frame). Authors of class videos
   should design their visuals so the most class-readable frame is at the
   midpoint, with quieter fade-in / fade-out frames at each end.
4. Transparent video over a Skia surface in WPF is the hard part of Phase 2
   and is intentionally NOT prototyped here — the seam guarantees Phase 2 can
   experiment with that decoder choice freely without breaking the rest of
   the suite.
5. Class videos exist at `../ApexHUD-Web/assets/transitions/`
   (`OVERALL.mp4`, `GTP.mp4`, `GT3.mp4`). `LMP2` has no clip yet — Phase 2
   either renders the Phase 1 animated background for LMP2 or commissions /
   styles its own clip.

**Reasons:**
1. Pull-don't-push matches every other coordination point in the suite
   (`PresentationState`, `App.LatestSnapshot`) — `SceneDirector` is a static
   facade, not a service, with no event subscriptions, no dispatcher
   marshalling, and no per-tick allocations beyond the rebuilt scene list.
2. The additive focus filter is the smallest possible diff to the renderer
   that still delivers the per-class boards. Rewriting the row/column code
   to fit each class differently would carry real regression risk for a
   foundation-step session that explicitly forbids breaking the working
   tower. We can grow per-board layout later from this seam.
3. Splash-as-its-own-window mirrors every other overlay's mechanics
   verbatim. The alternative — drawing the splash from inside the tower
   window — would force the tower to know about z-order and would couple two
   render paths whose lifetimes Phase 2 will want to manage independently.
4. The per-scene `SplashDurationMs` + opaque-midpoint scene swap is the
   single rule that lets Phase 1 and Phase 2 share the same code path. The
   alternative (separate code paths for animated vs video splashes) would
   guarantee Phase 2 has to re-derive every timing rule from scratch.
5. Pause-and-reset on `!TowerVisible` is the cleanest interaction with
   `PresentationState`'s battle-takeover choreography — no second source of
   truth, and the tower-returns-from-battle case always starts on OVERALL,
   which matches Angelo's broadcast mental model ("when the battle ends, you
   come back to the overall standings, not mid-rotation").

**Trade-offs:**
- During the splash's fade-in / fade-out window (~250 ms each side) the tower
  is partially visible behind the splash. Acceptable — broadcast-feel
  transition, not a hard cut, and any rebuild jitter still falls inside the
  opaque region in the middle.
- A class that disconnects mid-cycle (all GTP cars retire while OVERALL is
  on-screen, say) silently disappears from the rotation on the next tick.
  Reasonable — a class with zero present cars has no board to show.
- The freed vertical space on per-class boards is empty for Phase 1; rows
  don't get taller and the lane reads with white space at the bottom. A
  future polish session can grow `RowHeight` when focused, or add a class
  championship side-panel, without touching the rotation code itself.
- The Phase 1 animated background is intentionally restrained — a soft
  class-coloured bloom + vertical sweep, not a logo-heavy explosion. The
  visual impact lives in the centred composite (wordmark + series + class
  name). Phase 2 video will carry whatever drama the clip itself has; the
  composite stays the constant cohesive layer.

---

## 2026-05-30 — Timing tower scene rotation (Phase 2): real footage as pre-extracted frame sequences

**Context:** Phase 1 (2026-05-30 entry above) built the whole scene-rotation
system with a deliberately-reserved seam: `TimingTowerSplashRenderer.DrawBackground`
is the only method that needed to change, and `CurrentSplashDurationMs` is
already per-scene. Phase 2's job was to replace the animated splash background
with the real transition footage and drive each splash's duration from the clip
length — nothing else.

**Decision (Phase 2, Option A — confirmed with Angelo): play the footage as
pre-extracted JPEG frame sequences, decoded on demand.**

- SkiaSharp 2.88 cannot decode H.264 and we will not take on a fragile runtime
  video-decode dependency, so the three clips (OVERALL, GTP, GT3 — portrait
  1200×2604, 30 fps, opaque yuv420p, no baked-in branding) are bundled as
  per-frame JPEGs extracted at lane width:
  `assets/transitions/frames/{overall,gtp,gt3}/fNNN.jpg` (83 / 183 / 169
  frames). Counts validated against the Phase-2 preview extraction. They are a
  build output (`Content … CopyToOutputDirectory PreserveNewest`, same pattern
  as `assets/logos`), so they land in `bin/Debug/net8.0-windows/assets/...`.
- New `SceneTransitions` static helper owns frame-count discovery (enumerated
  once at startup), the clip-key mapping (`scene.Label` lower-cased →
  `overall` / `gtp` / `gt3`; a clip "exists" only if its folder was bundled),
  the per-clip duration (`frameCount / 30 × 1000` ms), and a **single-frame
  decode cache**. Only the current ~480 px frame is ever resident — we do not
  pre-decode whole clips into RAM (the low-RAM goal). Decoding one small JPEG
  per 30 fps draw is cheap.
- **Splash duration = clip length** (Angelo's "play in full" rule): when
  `SceneDirector` starts a splash, if the announced scene has a clip it sets
  `currentSplashTotalMs = SceneTransitions.ClipDurationMs(clip)` (OVERALL 2.77 s,
  GTP 6.10 s, GT3 5.63 s); otherwise it keeps `DefaultSplashTotalMs`. The
  ~30 s failsafe stays only as a stall guard. The scene swap still fires at the
  splash's opaque midpoint, unchanged.
- **Renderer decides per scene.** `TimingTowerSplashRenderer.TryDrawVideoFrame`
  loads the frame for `floor(SplashElapsedMs / (1000/30))` (clamped to the last
  frame), cover-fills the lane (preserve aspect, clip overflow), and draws a
  dark radial legibility scrim so the white iRacing wordmark reads over bright
  footage (the GTP clip is a sunset). If no clip exists (LMP2, anything
  unmapped) or a frame fails to load, it falls through to the Phase 1 animated
  background. `SceneDirector.SplashElapsedMs(nowMs)` is the one new accessor
  added to expose splash-elapsed to the renderer.

**Reasons:**
1. Frame sequences sidestep the entire transparent-video-over-Skia problem
   (the part Phase 1's pending note called "the hard part") with zero new
   dependencies and bounded, predictable memory.
2. Driving duration from frame count (not a magic number) makes "play in full"
   automatically correct and keeps Phase 1 and Phase 2 on the same code path —
   the only behavioural difference is which background the renderer draws.
3. The composite (wordmark + series + class name) and the whole rotation /
   battle-pause / opaque-midpoint-swap machinery are untouched, honouring the
   additive-seam constraint. The change is two call-site edits in the renderer,
   one duration line + one accessor in `SceneDirector`, and one new helper.

**Trade-off:** ~12 MB of bundled JPEGs in the repo (fine for a personal repo).
Frames are extracted at 480 px wide; on a lane taller than the frame they
upscale slightly (acceptable broadcast softness). LMP2 still uses the Phase 1
animated splash until Angelo supplies a clip — the renderer already handles a
missing clip gracefully. Re-extraction recipe is in the Phase-2 brief
(`ffmpeg -vf scale=480:-1 -q:v 3`) if frames ever need rebuilding.


---

## Phase 2.1 — smooth transition video via background decode-ahead (built directly, not via the autonomous session)

**Problem.** Phase 2 decoded a JPEG synchronously on the render thread for every
frame (`SceneTransitions.LoadFrame`, single-frame cache, no read-ahead). The GTP
clip — longest (183 frames) and heaviest frames (~42 KB avg, the detailed sunset
footage) — could not decode inside the 30 fps frame budget, so playback was
choppy and the cold start stuttered. A first attempt was queued for the
autonomous session but never built (prompt consumed, no commit landed); this was
built directly instead.

**Two causes found, both fixed:**
1. Synchronous decode on the render thread (the known one).
2. `FilterQuality.High` on the per-frame `DrawBitmap` — high-quality
   mipmap/bicubic resampling of a full-lane image every frame, also on the
   render thread. Wasted work since the frames are already ~lane width.

**Fix.**
1. New `FrameStreamer` (one background worker thread, `BelowNormal` priority)
   decodes frames ahead of playback into a bounded look-ahead window
   (`LookAhead = 30` ≈ 1 s). The render thread calls a non-blocking `GetFrame`
   that returns the ready frame or the nearest earlier buffered frame — it never
   decodes. Frame 0 is decoded synchronously in `Start` so the first paint is
   instant. Frames strictly behind the current index are evicted/disposed, so
   only ~1 s of one clip is ever resident (low-RAM goal preserved). Bitmaps are
   guarded by a lock; software `SKBitmap`s are not thread-affine so worker-decode
   + UI-draw is safe.
2. Per-frame draw dropped from `FilterQuality.High` to `Medium` (bilinear),
   `IsAntialias = false`.

**Lifecycle.** The splash renderer starts the streamer when the clip changes,
and releases it (`ReleaseStreamer`) whenever no splash is active or the scene has
no clip, so nothing decodes between transitions. Disposed with the renderer.

**Kept unchanged (confirmed with Angelo).** 30 fps and the current frame
resolution stay — the splash lane is only ~400 px wide on screen, so the 480 px
frames are already display-native for 1080p; lowering either was explicitly off
the table. Composite, fades, durations, rotation, battle pause/reset, LMP2
animated fallback all untouched. `SceneTransitions.LoadFrame` is now superseded
by the streamer but left in place (harmless) to keep the change surgical.

**Verification.** Clean build 0/0. Threading/perf can't be tested from Session 0
(no live iRacing, no GUI) — Angelo eyeballs. If still choppy after this, the next
suspect is the splash window repaint cadence (`RepaintGate` / `CompositionTarget.
Rendering`), to be instrumented directly rather than guessed.


---

## Phase 2.2 — per-frame render-cost optimization (built directly)

**Context.** After Phase 2.1 moved decode off the render thread, the GTP
transition was still clunky. Deep research (WPF + SkiaSharp) established the
splash is on a *double software path*: `AllowsTransparency=true` forces the
window to be composited in software (Microsoft WPF docs: per-pixel layered
windows get no hardware acceleration), and `SKElement` is itself CPU-only
(renders into a WriteableBitmap). The GPU is sidelined for that window. A full
GPU path would mean abandoning AllowsTransparency for DirectComposition — a real
rearchitecture, deferred. Before that, eliminate per-frame waste (the research's
lower-risk recommendation).

**Three per-frame costs found and removed (video/clip path only):**
1. **`SaveLayer` every frame.** The splash wrapped its whole composite in
   `canvas.SaveLayer` to fade as one layer; SaveLayer allocates a full offscreen
   bitmap every call (Skia docs), in software, even at full opacity — so the
   entire ~6 s GTP clip paid for an offscreen per frame for nothing. Now: only
   use SaveLayer while actually fading (alpha < 0.999); during the full-opacity
   hold (essentially the whole clip) draw directly. Pixel-identical at full
   opacity; the brief fade in/out still uses SaveLayer for a clean cross-fade.
2. **Per-frame scrim shader.** The legibility scrim rebuilt an
   `SKShader.CreateRadialGradient` + paint every frame. The lane geometry is
   constant for the window's life, so it's now built once and cached
   (`EnsureScrim`), rebuilt only if lane size changes.
3. **Per-frame paints.** The frame bitmap, the two logos, and the class-name
   text each allocated a fresh `SKPaint` per frame (SkiaSharp guidance: never
   allocate paints in the render loop). Now reused: `videoFramePaint`,
   `logoPaint`, cached `classNamePaint` (colour set per frame), cached
   `scrimPaint`. Frame draw dropped High→Low filter (frames are ~1:1 with the
   lane: 480x1042 into a 400x1048 lane on the 1080p primary).

**Result.** The steady-state video frame is now: one bitmap blit (reused paint),
one cached-shader rect, two logo blits, one text draw — zero heap allocations,
zero offscreen buffers. Kept 30 fps, current resolution, all visuals. The
animated (LMP2/no-clip) background path is untouched (still uses SaveLayer; it's
rare and was never the problem).

**If still choppy after this:** the software-composite ceiling is real and the
DirectComposition GPU-overlay rearchitecture is justified (its own large
session). Verify build only from Session 0; Angelo eyeballs GTP live.


---

## Session 26 — GPU overlay path validated (the "make it smooth" investigation)

The timing-tower transitions were clunky. After two optimization passes changed
nothing, instrumentation proved the bottleneck is the **present path**, not our
drawing: WPF software-composited transparent windows reach the screen at only
~15 fps over iRacing (draw cost was a fine ~8 ms; render cadence ~59 ms with
spikes; UI-thread probe showed a secondary contention issue too).

A standalone GPU overlay POC (OpenGL + SkiaSharp 3.119.4, presenting on its own
thread, not WPF) was proven to hold **~58 fps at 1920×1080 over a live replay** —
~4× the WPF path, with per-pixel alpha working. Full diagnosis, numbers, and the
staged conversion plan are in `poc/GPU_FINDINGS.md`.

**Decision:** rebuild the overlay window/present layer on a native GPU
transparent-overlay host; keep all existing `SKCanvas` renderers; consolidate
the nine WPF transparent windows into one GPU canvas (also fixes the secondary
UI-thread contention). Staged Phase B→D in the findings doc. 4K validation
deferred until a 4K display is available (path is 4K-ready by design).

This is the biggest architectural change in the project; it does NOT touch the
working WPF overlay until the GPU host is proven in-app (Phase B), per the
rollback discipline.


---

## Phase B — own the native GPU overlay host (`ApexHUD.Native.GpuHost`)

Phase A (Session 26) proved a GPU-composited overlay holds ~58 fps over iRacing
vs the WPF software path's ~15 fps. Phase B builds our OWN native host (rather
than depending on OverlaySharp), because the existing renderers need a raw
`SKCanvas` and this is a tool Angelo relies on.

**What we built.** A new isolated `WinExe`, `src/ApexHUD.Native.GpuHost`, that
references `Common` / `Data` / `Overlays` and owns ~3 small files of interop:
- A native Win32 transparent overlay window
  (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE |
  WS_EX_TOOLWINDOW`, `WS_POPUP`). Per-pixel alpha is the OverlaySharp-validated
  technique: `SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA)` + a DWM
  "sheet of glass" (`DwmExtendFrameIntoClientArea` with -1 margins) over an
  alpha (cAlphaBits=8) pixel format; GL clears to a transparent buffer and
  blends premultiplied, so the GPU composites our canvas onto iRacing.
- A WGL GL context (opengl32 P/Invoke) on the window DC, vsync via
  `wglSwapIntervalEXT(1)`.
- A SkiaSharp GPU surface: `GRGlInterface.Create()` → `GRContext.CreateGl()` →
  `GRBackendRenderTarget(w,h,0,8, fboInfo{fbo, GL_RGBA8})` →
  `SKSurface.Create(..., BottomLeft, Rgba8888)`, exposing a raw `SKCanvas`.
- A dedicated render thread that owns the window + GL + Skia for their whole
  lifetime (created, used, torn down on the one thread), so teardown is fully in
  our control — the POC's `StopOverlayAsync` hang cannot recur.

**DECISION — build on SkiaSharp 2.88.8, NOT 3.119.4 (deviates from the brief).**
The Phase B brief inherited "SkiaSharp 3.119.4" from the POC, but that version
was forced there only by OverlaySharp's dependency. Our renderers compile
against **SkiaSharp 2.88.8** (via `SkiaSharp.Views.WPF 2.88.8`). Handing them a
3.x `SKCanvas` is a different type identity, and 3.x removed
`SKFilterQuality` — which `TimingTowerSplashRenderer` uses — so 3.119.4 would
have forced rewriting the renderers, violating the non-negotiable "reuse the
renderers as-is" constraint. The GL/WGL/DWM interop technique is
version-independent, so we target 2.88.8 and the renderers' canvas type matches
exactly. (Phase D can revisit a unified Skia upgrade if ever needed.)

**DECISION — one tiny additive seam: `App.SnapshotOverride`.** The shared
visibility logic (`PresentationState.TowerVisible`) and scene rotation
(`SceneDirector`) both read `App.LatestSnapshot`, which is hardwired to the WPF
`RaceState` singleton. In the GPU-host process that singleton is never started.
We added a default-null `App.SnapshotOverride` static; `LatestSnapshot` now
resolves `SnapshotOverride ?? RaceState?.Current ?? Disconnected`. The WPF
overlay never sets it, so its behaviour is byte-for-byte unchanged; the host
sets it each frame from its own feed. This is the minimal, inert seam that lets
the renderers be reused verbatim — no renderer or window code changed.

**Result (measured, over a live running iRacing window, 3080 Ti, 1920×1080).**
The host drove the real `TimingTowerRenderer` + `TimingTowerSplashRenderer`
(incl. the GTP video-frame splash) through its raw GPU `SKCanvas` at
**avg 16.8 ms / 59.5 fps, median 16.7 ms, p95 17.8 ms, 0.3% of frames >33 ms**
across a 25 s run that spanned the OVERALL→GTP video transition. Clean teardown,
no hang. Matches the Phase A POC. Per-pixel-alpha appearance over on-track
content is Angelo's live eyeball check; the present cadence is proven.

This does NOT replace the WPF overlay. Phase C consolidates the remaining eight
overlays onto the one GPU canvas and retires the WPF transparent windows.

---

## 2026-05-31 — Phase C: single-canvas compositor with an on-demand render loop; first batch = tower + splash + featured card

**Context:** Phase B (Session 27) proved one native GPU overlay host could drive
the tower + splash renderers at ~60 fps over iRacing. Phase C consolidates: turn
that single-overlay host into a multi-renderer **compositor** that draws several
existing renderers at their screen positions on ONE shared GPU `SKCanvas`,
retiring the per-overlay WPF transparent windows — while keeping the WPF suite as
the instant rollback. The session brief required PORT-AS-IS faithfulness, a
mandatory on-demand (dirty-driven) render loop, no per-frame allocations, and an
even-pacing video path. Renderers reused UNCHANGED.

**Decisions:**

1. **`OverlayCompositor` registry (in `ApexHUD.Native.GpuHost`).** Each overlay is
   an `Entry` = (visibility predicate, screen-positioned paint adapter, animation
   metadata). The predicate is the SAME `PresentationState`/`SceneDirector` gate
   the WPF window read; the adapter translates the shared canvas to the overlay's
   screen rect and calls the existing renderer at its in-window offset. Adding an
   overlay is one `Entry`. The renderers are not touched — only the window/present
   layer changed, exactly as GPU_FINDINGS planned.

2. **On-demand loop is global, not per-overlay (the single most important
   behaviour).** On one shared surface there are no per-overlay retained layers, so
   the decision is global: present the whole canvas only when SOMETHING needs it,
   else sleep (the double-buffered front buffer holds the last composite, so static
   overlays cost ~0 GPU). `FrameNeeded` returns true when a transition splash is
   active, a visibility gate just flipped (its entrance/exit fade is still
   playing), a `Continuous` overlay is visible (e.g. a sweeping watch), or the
   rendered data changed within a ~3.6 s **content-settle window**. The settle
   window exists because renderer-internal animations (the tower's ~3.5 s
   position-change arrow, lap/PB pulses) are *triggered* by a one-frame data change
   but *play out* over seconds; without it they would freeze after one present.
   Verified in the demo log: 15 s of **zero** presents while static, bursting only
   on the transition.

3. **Alloc-free decision path (requirement C).** The dirty check runs every loop
   tick (~125 Hz incl. idle). It uses an FNV-1a numeric hash over exactly the
   fields the registered renderers draw (positions, gaps, laps, best/last lap,
   `LapDistPct`, phase, flags, current scene) — excluding time-derived fields
   (`CapturedAt`, session clocks) that change every tick without changing what the
   overlays show. Indexed `for` loops over the `IReadOnlyList` (not `foreach`,
   which allocates an enumerator); the demo/canned snapshot is built ONCE and
   reused (building it per tick churned the GC and jittered the cadence).

4. **Burst rate capped to ~60 fps (kind to iRacing).** Even while animating, the
   host presents at most ~60 fps (≥15.5 ms between presents) regardless of the
   display's refresh, rather than chasing a 144/240 Hz panel — GPU_FINDINGS' "the
   host must not try to drive the display refresh." A 30 fps clip then shows each
   frame for exactly two presents.

5. **Even-pacing transition video (requirement D), additive.**
   (a) A **presentation clock** advances the splash's video time by one steady
   present interval per present (not wall-clock), so a 30 fps clip lands on a clean
   2-2-2 cadence (the 2-2-3 judder came from wall-clock-vs-vsync drift). The splash
   start/swap/end stay on the real clock via `SceneDirector`, so it can't drift.
   (b) `FrameStreamer` now **pools** decoded frame buffers (decode into a recycled
   `SKBitmap` via `SKCodec.GetPixels`, with a `pinnedIndex` guard so the frame the
   render thread may still be drawing is never recycled, and a fallback to the
   legacy `SKBitmap.Decode` so the picture never regresses) instead of
   allocate/dispose per frame. (c) Look-ahead widened 30→45 frames and the decoder
   worker lifted BelowNormal→Normal so the buffer stays full through a transition.
   (d) `GpuFrameUploader` is the seam to upload the next frame into a REUSED GPU
   texture ahead of draw — minor for the 480 px splash (which still draws its CPU
   bitmap, faithfully) but required-ready for the future full-size sponsor-card
   videos; it is validated once on the live GL context at startup.

6. **First batch = tower + splash + featured card** (not the bar+watch the brief
   listed first). The featured card establishes the gate+fade pattern six of the
   other overlays share, and — critically — it lets the mandatory on-demand IDLE be
   *demonstrated*: its gate can close and it has a clean static state, whereas the
   session bar's watch sweeps continuously and would keep the canvas at animation
   rate forever, making "idle at rest" impossible to show. The bar is therefore
   queued late in PORT_PLAN (after the cached-layer optimisation).

7. **Bounded per-frame work (requirement B), partial.** The tower is skipped while
   the splash is fully opaque (it's occluded — faithful, since it's invisible
   behind the opaque splash). The fuller cached-static-layer optimisation is
   DEFERRED and documented in PORT_PLAN as the scaling path: on the single shared
   thread every visible overlay's per-frame cost is now additive (in WPF they
   painted on separate windows/threads in parallel), so tower+splash held ~60 fps
   but adding the shader-heavy featured card dropped the burst to ~30–45 fps.
   Caching static overlays to a GPU layer and blitting them is how the remaining
   overlays will scale back to 60 fps. Requirement B was scoped "nice-to-have" vs
   the mandatory on-demand present, so the foundation is built and the layer cache
   is the next batch's prerequisite rather than this session's risk.

**Trade-off / status:** The headline on-demand idle is proven and the consolidation
pattern is established and faithful. The multi-overlay burst rate (~30–45 fps when
the heavy featured card co-draws) is the known cost of single-thread compositing
and is addressed by the documented cached-layer work, not by altering the reused
renderers. WPF suite untouched and remains the rollback.

## 2026-10-24 — Gaps come from iRacing's own timing (CarIdxF2Time), per class, computed once per update

**Context:** Port 02 / Port 05 chose the lap-distance x best-lap "position-math" gap over
`CarIdxF2Time` for smoothness. In practice it disagreed with iRacing's own timing screens,
measured everyone against the OVERALL leader (wrong for every non-lead class in multiclass),
and in practice/qualifying it reported on-road distance instead of lap-time differences.
The race tower is now ordered by live track position (RaceStateService `raceLive`).

**Decision (supersedes the "no F2Time" parts of Port 02 / Port 05):**
- `RaceStateService.ClassGaps` fills `IntervalSeconds` and `GapToLeaderSeconds` for each class
  once per telemetry update; overlays only read them.
- Races: interval = F2(car) - F2(car directly ahead in the tower order); class gap = running sum
  of intervals (= F2(car) - F2(class leader) whenever all intervals came from F2).
- Fallbacks per pair: F2 missing (0 except the overall P1, or -1), F2 says the pair is the other
  way round (track-position order moved before iRacing's timing caught up), or a replay ->
  CarIdxEstTime estimate (laps x CarClassEstLapTime + est-time difference) -> the old
  lap-distance x pace estimate.
- Practice / qualifying / warmup: fastest-lap differences (F2, else CarIdxBestLapTime).
- `GapToLeaderSeconds` is now the gap to the CLASS leader (every consumer already treated it so).
- The battle card uses the snapshot interval when its road neighbour is the tower neighbour.

**Trade-off:** F2 updates at iRacing's timing points, so gaps step rather than glide; the battle
card's own gap lerp smooths it there. Unverified live (no iRacing in the build box).

## 2026-10-24 — Car classes from iRacing's CarClassID / CarClassShortName

**Decision:** `ClassDetection.FromIRacing(classId, shortName, rosterName, carName)` is the class
source. Order: roster "PREFIX-" override (unchanged) -> CarClassShortName table -> CarClassID
table -> car-name keyword fallback (the old rules) -> the iRacing short name itself for unknown
classes (so two unknown classes no longer merge into "OTHER"). Output labels are the same set
as before (GTP, LMP2, LMP3, GT3, GTD, GTDP, GT4, GTE, OTHER), so colours and logos are unchanged.
The ID table is deliberately small and must be confirmed from a session log (see the
"classes:" line in the GPU host log).
## 2026-10-24 — Control app completed (Preview/Program, sponsors, data browser, card builder, packs) + replay director

**Control app (plan Angelo agreed: basic tinkering for anyone, full control in Studio, never slower).**
- The page stays a separate lightweight web page. Studio editors live in `control/studio.js`, which is
  only downloaded once Studio mode is on (Lean / Broadcast never load it). Polling runs only while
  the page is open and visible: state 1 Hz, data browser 2 Hz, server caches telemetry / session-info
  reads for 250 ms so several tabs share one read.
- The render engine only ever sees: `ControlState` (version-checked once per tick), the sponsor pack
  (swapped whole, `SponsorPack.Version` drops cached logo bitmaps on the render thread), the custom
  card list (parsed and validated on the server thread, published as an immutable array), and tiny
  queue messages (test card / play card).
- Preview/Program monitors are schematics (boxes), not a second render path. TAKE copies Preview to
  Program. On Air big buttons are deliberate "hot" cuts that change Program directly.
- Sponsors: the Event & sponsors screen edits `sponsors.json` live (validate → write tmp → move →
  swap). Pre-race partner panels pick their list when PreRaceCards is built, so a change there needs
  a restart.
- Custom cards: one JSON file per card in `Documents\iRacing\Grandstand\cards`, max 50, 64 KB each,
  every field clamped; bad files are skipped and reported, never thrown at the render loop. Drawn by a
  small new renderer (`CustomCards`) rather than bolted into StoryCards (2,300 lines, tuned): right
  side under the session bar, never while a story card / outro is up, decisions at 2 Hz, presents only
  while fading or after a value refresh. Off in Lean mode; master switch `customCards`.
- Packs: settings + sponsors + cards in one shareable JSON (`packs\*.json`). A pack never turns the
  camera director on.

**Replay director.** Own BelowNormal thread; sleeps on an event while off (zero work), 4 Hz while on.
Replays and spectating only: if IsOnTrack/IsOnTrackCar and not IsReplayPlaying it sets the setting OFF
and lets go. Single camera owner: a manual camera change backs it off for 20 s. The setting is forced
OFF on every startup. Camera groups come from session info CameraInfo (Blimp/Chopper, TV1-3, Pit Lane,
Chase); cars are switched by CarNumberRaw via `CamSwitchNum`. Incident replay = jump back 4 s
(`ReplaySearchSessionTime`), half-speed slow-mo, then resume at the saved time + elapsed (saved replay)
or `ReplaySearch(ToEnd)` (live). Raw SDK access sits in `RaceStateService.Broadcast.cs` (partial class)
so the upstream data file stays mergeable.

**Watch Replay.** iRacing documents VR/monitor only as a UI choice (status-bar device icon, optional
start-up pop-up); no command-line switch or app.ini key. `tools\watch_replay.ps1` lists replays, starts
the UI and prints the clicks; it edits nothing.

## 2026-10-24 — Bring-your-own clock dial; the personal dial is never shipped

**Decision:** the session-bar watch's dial comes from `ClockDial` (Overlays), not a hard-coded
`assets/clock/dial.png`. `Documents\iRacing\Grandstand\clock.json` = `{"dial", "subdialHands"}`; user
pictures go in `Documents\iRacing\Grandstand\dials\` (created on first run with a README.txt).
Choices: `classic` (assets/clock/dial.png, only if present and > 64 px — the owner's personal dial),
`homage_black|green|ivory|navy|panda` (original artwork, bundled, generated by
`tools/make_homage_dials.py`), `file:<name>`, and `auto` (default) = classic if present, else
**homage_ivory** (owner's pick for the public default).
- Purpose-made dials (classic + homages) keep the 124% cover rect (DialDest, 50±54.56) and their
  registers sit on the existing SubL/SubR/SubB pivots, so subdial hands default on.
- Custom pictures are decoded once at selection (SKCodec scaled decode, centre-cropped square,
  ≤ 1024 px, immutable) and mapped to the r=44 circle's box (6..94) — no 124% factor. Subdial hands
  default off (they'd float over a logo) unless `subdialHands: true`. The Fastest Laps chronograph
  follows the same flag.
- Swap = new `Face` record + `Version` bump; every WatchRenderer compares the int per frame. Old
  bitmaps are left to the GC (they may still be mid-draw on the render thread); swaps are rare.
- Any failure (missing, unreadable, traversal-y name) → auto + one log line. A cream disc is always
  drawn under the image, so the watch can't go blank.
- Control app: `GET /api/dials`, `POST /api/dial` (same X-Grandstand CSRF header as every POST),
  `GET /api/dials/thumb/{classic|builtin/<id>|file/<name>}` — plain file names from the dials folder
  only, png/jpg/jpeg/webp. "Broadcast clock" card on Event & sponsors.

**Publishing hygiene:** `assets/clock/dial.png` (and its copies in tracked `bin/`) are untracked and
git-ignored ("personal dial — never ship"); both csproj files now include `assets/clock/*.png` so a
clone without the personal dial still builds; `installer/grandstand_publish.ps1` deletes
`assets\clock\dial.png` from the release and says so. Note: older commits still contain dial.png —
don't publish this repo's existing history as-is.

**Why:** sponsors, partners and creators want their own face on the broadcast; the Rolex-dial look
is personal-use only and must not travel with a release.

## 2026-10-24 — Public build without third-party footage, photos or sounds; the owner's setup unchanged

**Decision:** same pattern as the clock dial: personal files stay on the owner's PC (untracked, git-ignored
incl. their `bin/` copies, left out by `installer/grandstand_publish.ps1`, whose final check FAILS the publish if
one is in the staged folder or the zip); the code uses them when present and falls back to original content
when absent; a folder under `Documents\iRacing\Grandstand\` lets creators drop in their own. None of these files
was ever tracked in git, so nothing needed `git rm --cached`.

| Asset | Personal (owner's PC) | User folder | Public fallback |
|---|---|---|---|
| Class transition | `assets/transitions/frames/<key>` (IMSA footage) | `transitions\<gt3\|gtp\|lmp2\|gtd\|overall>\` jpg/png, name order, 30 fps | `ProceduralStinger` (drawn live) |
| Car picture | `logos/sponsors/car_*_panel.png`, `car_<name>.png` | `cars\car_<words>[_panel].png/jpg` (word match) | `CarArt` drawn panel — public installs only |
| Sounds | `assets/audio/<name>` | `audio\<name>` | `assets/audio_public/<name>` (`tools/make_sfx.py`, CC0) |
| Sponsors | defaults = his partners when `AB_GC_Logo.png` is installed | `sponsors.json` (unchanged) | neutral defaults: all named partners off, Custom "YOUR BRAND HERE" |

- **Class transition choice** (`ControlSettings.Transitions`, control page Settings → Class transitions):
  `auto` (default) = per class personal frames → user rollers → built-in; `builtin`; `folder` (user rollers where
  present, else built-in); `off` (SceneDirector swaps boards with no splash). `SceneTransitions` keeps immutable
  frame lists per source, swapped whole; `FrameStreamer` keeps the list it started with; frames bigger than the lane
  (user full-HD sequences) are shrunk/centre-cropped to 480x1046 on the worker (owner's 480x1042 footage untouched).
  `GET /api/transitions`, `POST /api/transitions/rescan`.
- **ProceduralStinger**: same trigger points; duration = the footage it replaces (OVERALL 2.8 s, classes 5.6 s);
  the lane is fully covered between the 380 ms wipes, so the midpoint swap stays hidden; the splash renderer's wipe
  clip/edge still frame it (edge in silver for GTP black). Diagonal light sweeps + streaks, chevron flythrough,
  scrolling outlined class name, glass panel punch-in with class logo or name, iRacing wordmark and series logo,
  rails, glint, scanlines, grain, vignette. Vectors in lane units (sharp at 4K via the host's height/1080 scale).
  Paints, unit shaders, chevron path, text blobs, grain/scan images built once; shaders/blobs rebuilt only when the
  class changes (once per splash). Measured: 0 managed bytes on steady frames (4 rebuilds = 20 KB over 1,172 frames).
  If it ever throws, the original Phase 1 splash takes over for the rest of the run.
- **Owner differences, deliberately small:** a class with no personal footage (LMP2) now gets the built-in stinger
  (5.6 s) instead of the 1.2 s Phase 1 splash. Everything else on his PC resolves to his own files first.
- **Publishing:** robocopy also skips the patterns; after copying, `Remove-Personal` deletes matches from the build
  (dotnet publish copies some as Content); `Assert-NoPersonal` checks the stage and the zip; an old zip is deleted
  first so a failed run leaves none. Fonts need a licence (`assets/licenses/OFL-*.txt` added for Inter, Cormorant
  Garamond, Barlow Condensed); **RaycerRay** is left out of releases (licence not confirmed; Outro falls back to
  Barlow Condensed). Logos of other companies stay (small badges, owner accepted the grey area).

**Why:** the public beta must not carry broadcast footage, press photos, the owner's business partners or sounds
of unknown origin, while Angelo's own broadcast keeps looking exactly as he built it.

## 2026-10-24 — Amendment: "preserve mine" for classes without footage

**Decision:** supersedes the "owner differences" bullet above. In Auto, when ANY personal frames folder exists, a
class without personal (or user) frames keeps the original 1.2 s Phase 1 splash exactly as before
(`SceneTransitions.UseLegacySplash`). Only installs with no personal frames anywhere get the built-in stinger in
Auto; "Built-in animated" still forces it everywhere. **Why:** the owner asked that his setup stay exactly as it is.

## 2026-10-24 — The opener draws frames without making garbage (choppy-intro fix, round 2)

**Decision:** every steady frame of the race opener (GridIntro pages, the GT7 globe, the class pages and board
chrome) allocates ~0 managed bytes and creates no Skia objects. Measured with the new `tools/intro_bench`
(the real renderers frame by frame at 60 fps, fabricated assets; no-draw canvas + raster surface).
Rules now used in the opener code (helpers in `Overlays/FrameCache.cs`):
- **No `new SKPaint` per frame** (~1.3 KB managed each, plus SkiaSharp's global handle table): kept paints, `Reset()`
  then set what the old initializer set.
- **Shaders via `ShaderSlot`**: same arguments as the old `SKShader.Create*` call, rebuilt only when a stop colour /
  point / radius changes. A gradient that only MOVES is built around (0,0) and moved with `canvas.Translate`
  (lights, liquid-colour pools, logo sheens, comet, stinger slab/streaks). Drop shadows via `ShadowSlot`.
- **Text via `TextBlobs`** (cached SKTextBlobs; checked byte-identical to `DrawText(string)`); `TextTracked` and the
  globe's spaced captions use positioned blobs with the old per-character advances.
- **`BitmapImages`**: SkiaSharp 2.88's `DrawBitmap` wraps the bitmap in a new SKImage per call; immutable bitmaps get
  one kept image.
- **Never `SKBitmap.SetPixel` in a loop**: it makes a canvas per pixel (~200 B each). The track-logo matte made ~90 MB
  of garbage on the track page's first frame; the globe's grain ~13 MB. Both now write a buffer (same bytes:
  unpremul (r,g,b,a) / all-zero at a=0; premul round(c*a/255)).
- Strings that depend on data are made once per value (names, lap times, gaps, numbers, key strings);
  `TrackMapCache.Resolve` and `GlobeGT.Installed` remember their last answer; no lambdas that capture parameters
  in every-frame methods (a capture allocates its closure on every call, even on the early-return path); no
  `foreach` over `IReadOnlyList` (boxed enumerator); constant gradient stops live in `Stops` (a constant collection
  expression showed ~100 B per use in the bench).
- **GlobeGT**: triangles go straight into kept buffers, sized for the whole flight in the background during
  `Warm` (no large-object allocation mid-beat); lighting / lat-lon only for vertices of on-screen quads (same
  formulas); place-name blobs laid out in `Warm`, a dozen per frame. Left: 4 `SKVertices` per frame (~320 B) —
  SkiaSharp has no way to draw vertex arrays without one.
- **Warm-up before the opener**: `GridIntro.Prewarm` (called by StartingGridRenderer every pre-race frame before
  the parade lap) decodes the logos, keys out the track logo's plate, renders/loads the map, finds the infield —
  on a background task, once per track (`TrackInfo`, locked); `WarmGpu` then draws the pictures 2 px at alpha 1/255
  (same filter quality) so the GPU has them uploaded/mip-mapped. GlobeGT.Warm now gets the screen rect, not the card.
- **Dot-globe fallback (GlobeIntro)**: same treatment; its point buckets are drawn with `sk_canvas_draw_points`
  (count + reused buffer, P/Invoke into libSkiaSharp; falls back to `ToArray` if the entry point is missing).

**Result (1080p, steady frames, no-draw canvas):** globe 338 KB → 0.4 KB/frame and 10.0 → 5.2 ms CPU; class pages
~220 KB → 0.0 KB median (transitions ≤ 19 KB); iRacing/series/track pages 25 → ~0 KB; whole opener 428 MB → 1.2 MB,
gen0 collections during the opener ~28 → 0 (the bench's own snapshot rebuilds account for the odd one left).
Pixels: 12 frames compared before/after, 22 pixels differ in all — 1 level on 6 (text/sheen positions), and the
8-pixel 2x2 warm-up dots at the screen corner (alpha 1/255, invisible on air).
**Why:** Angelo: the intro (iRacing page and globe) is "horribly choppy"; his PC bench showed ~370 KB per globe
frame and several gen0/gen1/gen2 collections inside the 3.6 s beat.

## 2026-10-27 — TV loading splash (concept A "TRACE") in its own host window on the Apollo 4K screen
**Decision:** while iRacing is in its menus or loading a session, the TV shows the approved concept A splash (navy
plate, faint real track outline, a gold comet lapping it as the indeterminate loader, COMING UP / track / hairline /
subtitle, SESSION · CLASSES · LOCAL TIME · TRACK strip, SIM LOADING •••, small GRANDSTAND), and hands off to the game
(comet hurries round, line lights up, dive through the comet's head with a warm flash, plate dissolves).
- **States** (`SplashDirector`, run from GpuHost's existing 250 ms tracking loop): `Off` (no menus app, no sim) /
  `Menus` (window titled exactly "iRacing" or process iRacingUI, no sim) / `Loading` (sim window
  "iRacing.com Simulator" or process iRacingSim64DX11, telemetry not flowing) / `Live` (`RaceStateService.IsSessionLive`
  + sim window). Process names checked at most once a second. Debounce: hand-off after 1 s of continuous Live; the
  splash returns only after 3 s out of Live; Off fades it away. Demo mode: never.
- **Where:** a SECOND `NativeOverlayHost` (own window class "ApexHudGpuSplashWindow", title "Grandstand Splash",
  created hidden, click-through, WS_EX_NOACTIVATE, topmost) bounded to the TV, made the first time the splash is
  needed. The main host follows the sim window and is hidden in exactly these states, so it cannot carry the splash.
  The secondary host skips the main host's global duties (quit hotkey, Spout, IntroWatch, auto light mode) and has
  its own present cap (30 fps idle, 60 during fades / hand-off).
- **Which screen is the TV:** Grandstand had no monitor selection before (everything followed the sim window). Rule:
  the largest NON-primary screen of at least 3840x2160 physical pixels (the process is per-monitor DPI aware). Angelo's
  main monitor (D40f-J09) is primary; Apollo's TV screen is the extra 4K one. No such screen → no splash. The primary
  monitor is never used. The chosen screen and the full screen list are logged ("splash: TV screen = ...").
- **Data:** only real fields - `RaceStateService.Splash` (new `SplashInfo`, captured on every SessionInfo update in
  its own guard, cleared on disconnect so the last sim run's track never shows): TrackDisplayName / TrackConfigName /
  TrackCountry, SessionType of CurrentSessionNum, distinct CarClassShortName (fastest class first),
  WeekendOptions.TimeOfDay, TrackAirTemp (°F/°C per the control app's unit). Wet/dry only from live TrackWetness
  (TrackWeatherType says constant/dynamic weather, not wet/dry, so it is not used). Unknown fields are hidden. Menus
  stage is generic ("STAND BY / NEXT SESSION / WAITING FOR THE SIM") - no last-session track.
- **Main overlay** waits behind the splash until the dissolve (`HoldMainOverlay`, never longer than 6 s after Live).
- **Cost:** plate, outline+glow, title block and info strip are rasterised once per content/size change on a
  background thread and uploaded once (`ToTextureImage`); per frame only image draws with alpha, the sweep, hairline,
  dots and the comet (tail via cached `SKPathMeasure` into one reused `SKPath`, glow in 6 chunks). Measured on Linux
  (raster, 4K): 0 B/frame idle and hand-off once warm. SaveLayer only while the whole splash fades. No sound.
- **Setting:** `loadingSplash` in control.json (Settings → Behaviour → "Loading splash on the TV"), ON by default.
**Why:** Angelo approved concept A (splash_previews/notes.md, 2026-10-27).

## 2026-10-27 — SESSION REVIEW on the TV between live sessions (own .ibt reader, same window as the loading splash)
**Decision:** while no session is live (iRacing closed = `Off`, or in its menus = `Menus`) the TV plays SESSION REVIEW: the
last session's breakdown in the track guide's language (the wide card in the track logo's colour, the 3D terrain map and
the corner-by-corner walk with CornerTheme labels / backdrops / heritage), on a loop. `Off` no longer means "nothing on the
TV" when the review is on (Angelo: "the TV can sit all day and play stuff about how to get better at each part of the track").
- **Data (self-contained C#, no Race Engineer needed):** `Common/IbtFile.cs` reads iRacing's .ibt (112-byte header, 32-byte
  disk sub-header, 144-byte var headers, session YAML, fixed rows; only the needed channels, streamed in 2048-row blocks).
  `Common/SessionReview.cs` (`SessionReviewAnalyzer`): last session in the file with a completed lap; laps from a continuous
  lap position (tows / resets break it); iRacing's own lap time when it matches ours, and laps iRacing threw out (LapLastLapTime
  -1: track limits) never set a best lap or a best piece. The lap is split into CrewChief's named corners
  (`TrackCorners.Corners`) and the straights between them as a gapless partition (gaps < 250 m belong to the corner before),
  so the best pieces add up to a real theoretical lap; tracks without landmarks get "Corner n" from the fastest lap's speed
  trace. Per piece, over clean passes only (no pit lane, caution, off, spin or incident there; not a race's first lap): best /
  typical (median) time, every pass's time, slowest point, brake point (start of the braking zone, never reaching into the
  previous corner), full-throttle point, gear, entry / exit / top speed, speed traces (best + median) with brake / throttle;
  a corner whose slowest point is still under braking at its exit (a chicane split in two) gets no apex numbers. Offs
  (surface off-track > 0.3 s), spins (slip angle > 50° above 8 m/s for 0.25 s) and incident points are placed where they
  started. Tips are rule-based one-liners from those numbers only (`Tip`), thresholds keep them to real differences.
- **Which file:** `GpuHost/SessionReviewLibrary.cs` - newest .ibt in Documents\iRacing\telemetry or E:\RaceEngineer\telemetry
  (and one level of sub-folders) that nobody is writing (opening it with FileShare.Read must succeed) and older than 20 s,
  skipping stubs (no completed lap). Analysed on a BelowNormal background thread (~0.4 s for a 240 MB race file on Linux),
  cached as JSON in Documents\iRacing\Grandstand\session_review (40 newest kept). Scans every 10 s for 5 minutes after a
  live session ends (and until one is found), then every minute; never while live.
- **Show:** `Overlays/SessionReviewShow.cs`. Opener 18 s (best lap big, theoretical / typical / gap, result + start position,
  laps, incidents, top speed, temperatures, every lap's bar), one page per piece 7.6 s (camera flies in like the guide's
  WalkZoom, stretch lit in its accent with the running light, brake / slowest / full-throttle pins, best vs typical numbers,
  every clean pass, tip, speed trace), closer 18 s ("work on these three": biggest typical-minus-best gains, numbered on the
  map, typical lap with them fixed). Loop ~3 min at Le Mans; starts from the opener when it (re)appears after 5 min away or
  for a new session.
- **Cost:** plate (screen + card + footer), line glow / shadow (plane coordinates), terrain texture, logo and inset are
  baked once per review; each page's left column, theme backdrop, chart and pin labels are baked on a background thread one
  page ahead, the page behind is released. Per frame: image draws (map ones through the camera's perspective matrix), the
  outline and the lit stretch transformed into reused paths, a halo, pins. Measured 0 B/frame (0.4 B average) at 4K.
  30 fps. Hidden for a minute -> page pictures released. No TV screen -> nothing drawn.
- **Windows / hand-off:** one splash host window carries both (review under, loading splash on top). SplashDirector:
  Off/Menus -> review (`ShowNow` when the splash is still up, so nothing shows through), splash only when there's no review;
  Loading -> loading splash fades in over the review, the review hides once the splash covers the screen; Live -> hidden
  (3 s debounce coming back, as the splash). TV screen checked every 5 s while iRacing is closed.
- **Setting:** `sessionReview` in control.json (Settings -> Broadcast effects -> "Session review on the TV"), ON by default.
- **Units:** speeds in MPH unless the control app's temperature unit is C; brake / throttle distances in metres (the track's
  boards are metric).
**Why:** Angelo asked for "compelling 4K broadcast splashes for in between sessions with past telemetry breakdowns on them"
with "the identical theme as the track intro".

## 2026-10-27 — Visual review pack (V1-V8, W8-W13): production code, all OFF until approved
**Decision:** every visual idea Angelo picked from the immersion brainstorm is built as production code with a base
and an elevated design, behind its own switch (default OFF) so nothing changes on his TV until he approves it.
- **Where:** `src/ApexHUD.Native.Overlays/VisualPack/` - one renderer per item (`WeatherCard` V1, `NightPalette` V2,
  `BattleRadar` V3, `StrategyBoard` V4, `LiveTag` V5, `ReplayBumper` V6, `StatsCards` V7, `YellowSectorMap` V8,
  `BreakCard` W8, `PipOnboard` W10, `LensFx` W11, `RaceControlCard` W12), the shared look + plumbing in `VpKit`
  (`Vp` palette/easing, `VpLayer` baked layers, `VpText`, `VpStr` / `VpNum` allocation-free numbers, `VpTrack`), and
  `VisualPackDirector` (the data hookup, also W9 / W13 sounds). The compositor registers each graphic as an entry
  (`vp_*`), feeds the director once per tick (`VpContext`: battle, opponent, radio card, wipe event, TV camera) and wraps
  every graphic in the night grade (V2).
- **Settings:** `control.json` → `visuals` (`VisualPackSettings`): one bool per item (all false except
  `lowerThirdSounds`, which follows the existing stingers: on, heard only in replays), `breakMusic`, `pipCamera`, and
  `elevated` (list of item ids using the elevated design). Control app: Settings → "Visual review pack" (switch,
  Base / Elevated, Test).
- **Cost rule:** static parts recorded once per show (`VpLayer`: SKPicture → raster at the screen's real scale →
  `GpuContext.Pin`), moving parts drawn with paints/paths/shaders kept in fields; live numbers via cached strings
  or per-character cached blobs. The layer scale comes from the frame's screen scale (`Vp.LastScale`), never the
  current matrix, so 3D flips / tilts don't re-rasterise. Measured with `tools/visual_pack` at 4K: 0 B/frame on steady
  frames for every graphic (see the review pack's alloc numbers); `VisualPackDirector.Tick` 0 B/tick with all items on.
- **Honesty limits:** iRacing gives no other car's tyres/fuel (V4 = laps since the last stop seen + estimated next stop
  from the class's typical stint, labelled EST.); no second camera picture (W10 = a live "virtual onboard" from
  CarIdxSteer/Gear/RPM + our own speed estimate; the optional camera cut uses the broadcast API in replays only, never
  while the replay director steers); no sun position in the shot and no access to the game's pixels (W11 = light added
  over the picture, flare placed per camera cut); no penalty reasons (W12 = flags and incident-point changes only).
- **Sounds:** `lowerthird_light.wav` (W9) and `timing_beep.wav` (W13) generated by `tools/make_sfx.py` (CC0); both via
  `StorySound.PlayBroadcast` (replays only). W8's bed (`break_bed.wav`, the owner's own music) loops only while the
  break card is up in a replay; `StorySound.PlayBedLoop` / `StopBed`.
**Why:** Angelo (immersion build plan, 2026-10-26): "I love all of these", all V items BUILD > REVIEW; 2026-10-27:
"built, tested, then ... elevate it ... in case we choose to implement every single one".

## 2026-10-27 — Which sounds play when (#309): documented, no gating added
**Decision:** Grandstand's sounds may play live; they must only ever reach the TV, never the headset. That is done by
Windows' per-app routing (Grandstand's output -> the cable that feeds the TV), so every sound uses the app's DEFAULT
endpoint and no code forces a device: `StorySound` = winmm `PlaySound(file, SND_ASYNC | SND_FILENAME | SND_NODEFAULT)`,
`OpenerAudio` = WPF `MediaPlayer` (default device); `GameDuck` only changes Voicemeeter's stream bus gain (B1), never the
headset bus or the default device. Audited 2026-10-27: no other audio path (`ProceduralStinger` is visual only).

| Call | Cues | When it plays |
|---|---|---|
| `OpenerAudio.Play` | grid_opener theme | the pre-race opener on race load (also when a replay reaches it; stops on rewind) |
| `StorySound.Play` | pre-race info cards (pre_*), grid_tick | with the pre-race intro cards / grid board |
| `StorySound.Play` | story cards (story_*), story_exit, story_dq_stamp | when a story card enters / leaves - live or replay |
| `StorySound.Play` | outro_flag / podium / results | post-race outro pages |
| `StorySound.PlayBroadcast` | battle_in, fastest_lap, yellow_flag, pit_callout, featured_in, custom_in, tower_swap, replay_wipe, lowerthird_light, timing_beep | only while a replay plays (`ReplayState.IsReplay`, #207, 2026-10-26) |
| `StorySound.PlayBedLoop` | break_bed (W8) | replay paused 6 s+ with the break card on |
| `StorySound.Play` (Test) | timing_beep, lowerthird_light | control-app Test buttons |

Open question for Angelo: #207 still limits the `PlayBroadcast` stingers to replays (code unchanged). If "stingers can
play live" means those too, that's a one-line change in `StorySound.PlayBroadcast`.
**Why:** Angelo (2026-10-27): stingers can play live; they just go to the TV only.

## 2026-10-27 — No story cards in a battle while driving live (#338)
**Decision:** `StoryDirector.NoCardsInBattleWhenDriving` (control.json `noCardsInBattleWhenDriving`, ON by default): while
`DrivingState.DrivingLive` (in the car, not a replay, at the live end) and a battle is on, no card starts at all (also the fastest-lap / overdue / pit cards that used to
push through), a card already up bows out; when the battle ends, cards older than 60 s are dropped. Replays / spectating
unchanged. **Why:** Angelo no longer wants cards over battles while he drives. Next (#339, not built): mini cards for battles.

## 2026-10-27 — Timing tower first-show warm-up (#44)
**Decision:** the tower's one-off work is done before its first show: `TimingTowerRenderer.Prewarm` (background: decode /
pre-scale every manufacturer + class logo of the field, the iRacing wordmark, the series cap; lock-guarded cache, replaced
series pictures disposed on the render thread), `WarmCode` (background, once per process: a picture-less throwaway tower
draws the field twice on a small raster surface - JIT + Skia glyph cache), then on the render thread while the tower is
hidden and no opener / transition plays: `WarmLayout` (no-draw canvas: this thread's text blobs) and the next frame
`WarmGpu` (one draw at alpha 1/255 through the fade layer + the moving-row blur: textures, glyph atlas, layer, blur
shader). Row / arrow state from the warm draws is thrown away. Re-run when the field's set of brands / classes / series
changes (order-free key), checked every 5 s by the compositor. The gate fade's SaveLayer is bounded to the tower lane +
wings (was the whole 4K screen). Logged: "tower warm-up: ...".
**Measured** (`tools/tower_warm`, 4K raster CPU, synthetic 40-car field, placeholder logos): first painted frame
150-220 ms cold -> 40-70 ms (= an ordinary fade frame) warmed; one-off ~110-180 ms -> ~0. Background warm 135-215 ms,
WarmLayout 4-7 ms, WarmGpu ~ one raster fade frame. GPU numbers must be measured on the PC (`tools/tower_warm/run.ps1`
and the "frame cost" / "tower warm-up" log lines).

## 2026-10-27 — Refined corner themes (#115 / #118), OFF by default
**Decision:** `CornerTheme.Refined` (control.json `cornerRefined`, Settings -> Broadcast effects, default OFF) gives every
theme a finer finish with the same identity, layout and widths: two-layer shadow (contact + ambient), names on whole device
pixels, accent ticks beside the eyebrow, a rule with a lit core and soft glow, radial glows (no blur blobs) behind badges /
the Ford script / Earth, one capsule heritage pill for every theme (same centre as before, text centred on cap height,
accent dots, hairline edge), Michelin plate sheen + bevel + grounded shadow, PlayStation plate lit top edge + floor glow +
a nebula when there's no space picture, Dunlop bridge contact shadow + tapered road line, chip hairlines. Previews:
`tools/corner_preview` (run.sh / run.ps1 - on the PC it reads the real corner_logos).

## 2026-10-27 — Visual pack round 2: reuse the language we already have, place against the real layout, line up with the sim
**Decision:** after Angelo's review ("very generic looking ... check what we already have active"), the pack is rebuilt on
the existing graphics' language (audit: outputs/visual_pack_r2/existing_audit.md) and its approvals are applied.
- **Approved, ON by default:** W8 break card, W11 lens effects, W12 race control card, W13 purple beep. `VisualPackSettings.Rev`
  (0 in older control.json files) switches those four on ONCE (rev -> 2) so his later choices stick. V3 battle radar is not
  approved: forced off and left out of the control app (code kept).
- **Layout bus (`VpLayout`):** the compositor publishes where the live graphics are each tick (session bar plate as drawn -
  its width animates in battle mode -, the watch hanging below it, the ticker, the team-radio card rect and its anchor, the
  battle map, the tower). New cards place themselves from it: V1 hangs under the bar / watch / ticker stack (never over it,
  normal or battle mode); W12 lives in the radio column (radio card's right edge, its anchor, stacked above it when a radio
  card is up, shrinks to fit before overlapping); V5 takes the REPLAY tag's corner unless the bar is full width; V4 / V8
  stay right of the tower and under the stack.
- **#336 local-time watch (`LocalWatch`):** the session bar's `WatchRenderer` (so his `ClockDial` dial) with the real PC time,
  dial + bezel + shadow + caption baked once at the screen's scale (`WatchRenderer.DrawFace`), hands per frame
  (`DrawHandsOnly`); top middle on the break card, the TV session review ("LOCAL TIME") and the loading splash ("REAL TIME" -
  its info strip's LOCAL TIME is the track's). The TV window has its own GL context: `PinToGpu = false` there.
- **#337:** `PresentationState.BumperBattleStyle` (set from the bumper each tick) = battle-mode bar + ticker, tower and
  featured card away, while the replay bumper plays; the bumper entry is drawn under the ticker and the bar.
- **W11 alignment (`SunAlign`):** sun = iRacing's SolarAzimuth / SolarAltitude; flare only on chase / nose / gyro (forward) and
  rear chase / gearbox (backward) cameras; heading = YawNorth for the player live, else the track outline's direction at the
  car's lap position turned to north by a calibration learned from his own driving (sun_align.json) or the terrain fit's
  rotation / mirror; only where the outline turns < 6 deg over +-35 m; per-mount FOV / pitch defaults (70 deg, -4 deg) with
  `visuals.lensFov` / `lensPitch` overrides. Cut = everything off at once, 250 ms settle, 350 ms ease in. Unknown = nothing.
  New snapshot fields: `SolarAltitude`, `SolarAzimuth`, `PlayerYawNorth`, `TrackNorthOffset`.
- **W10:** live sessions only (never a replay / behind live) and it stands down when the chaser's CarIdx steer / gear / RPM
  are all zero or frozen for 2 s. `tools/carIdx_probe` checks the data live on the PC.
- **Sponsored graphics:** new sponsors.json slots WeatherCard (StormExhibit, typeset name or his logo file), StrategyBoard
  (the board's look is MEASURED from the partner's own logo file by `SponsorTheme`: biggest shape, its outline as the border,
  its colour as the theme, it booms out of the logo - Grandstand never draws a company's mark), ReplayBumper, AwardStint /
  AwardLapsLed / AwardConsistent / AwardPhotoFinish.
- **V7** is now `RaceAwards`: story-card family members played one at a time after the outro (fuel gauge / LED board /
  metronome / film strip), data gathered all race (laps led, clean lap times, stints). **V8** is rebuilt on the track
  card's 3D map (perspective plane, terrain, lifted line, lap-walk zoom) in the battle map's plate. **V2**'s grade now
  changes the palette (navy glass, ivory type, amber gold) so the change is visible. **V5** is a BACK TO LIVE plate, the
  REPLAY tag's sibling (one design). **V6** elevated: his watch rewinding in the doors.
**Why:** Angelo's round-1 review, 2026-10-27 (relayed by the main session).

## 2026-10-27 — #340 legibility pass: small grey labels readable from across the room
**Decision:** a size + contrast pass (not a redesign) over every on-air graphic except StoryCards, VisualPack, SessionReviewShow
and LoadingSplash (other sessions own them). Rule: small labels under 13 design px go up 2-4 px to at least ~13 (9.5 -> 12.5,
10-11 -> 13, 12 -> 13.5-14); big numbers / names / headlines unchanged; grey labels to ~75-80 % luminance and >= ~0.9 alpha,
still quieter than the values; letter-spacing trimmed where a label grew so nothing moves or overlaps. Centralised:
`BroadcastPalette.TextTertiary` #9CA3AF -> #C6CBD3; the local `Txt2`/`Txt3`/`Dim` greys (#B9C1CF / #7E8AA0) -> #D3D9E3 / #B4BECE.
Text drawn straight on the video (full-screen onboard, mini-map, compact corner label) gets `TextTracked.DrawHalo` - a soft
dark blur under the glyphs from one cached per-thread paint + blur (0 extra bytes/frame, measured). Full table:
outputs/legibility/audit.md; previews: `tools/legibility_preview` (run.sh, GSBIN = a before or after build).
**Why:** Angelo, request #340: "make all this gray small text everywhere more visible" - he watches the 4K TV from across the room.

## 2026-10-27 — #349 story card layouts: one right-third reflow, four arrangements (setting `cardLayout`)
**Decision:** control.json `cardLayout` = `classic` (default: the right-half card, drawn by the untouched classic code) |
`column` (A) | `halves` (B) | `flip` (C) | `corners` (D), applied through `StoryCards.Layout` (OverlayCompositor, like
`noCardsInBattleWhenDriving`). The non-classic layouts live in `StoryCardsColumn.cs` (StoryCards is now `partial`): every
card kind is reflowed ONCE into a right-third column x 1290..1884 (594 design units, top 172 as before, clear of the centre
third 640..1280 and the session bar) made of two sections - `ColTop` (title / partner + headline numbers) and `ColBottom`
(chart / picture / list / detail) - and the four layouts only arrange them: A = one panel, top + bottom stacked to the foot;
B = top panel, bottom panel 400 tall at the foot, entering 0.55 s later; C = one 420-tall panel that flips (vertical squash) to
page 2 halfway through the card (never before 4.2 s), page dots under it; D = top (<= 320) in the top corner, bottom (300) in
the bottom corner, 0.35 s later. Each panel keeps its card's own entrance, background, shake, flash, FINAL tab and colour-bar
exit; the Michelin tower is split into its screen (top) and a second screen with the leaning MICHELIN pylon running to the
ground (bottom). Type is never made smaller than the classic card's; what doesn't fit is dropped (list in JOURNAL).
`Plate` gained `compact` (default false = classic unchanged): narrower maker badge, no "IN CLASS" tag.
**Why:** Angelo (#349): the right-half card covers part of the centre third where the cars are; he liked four layout ideas
and wants to see the real cards in each before choosing. Classic stays default until he picks; the classic frames are
pixel-identical to the previous build in `tools/storycard_preview` (Your Race differs run-to-run in both builds).

## 2026-10-27 — #393 Mixed story card layout (new default) and the special partner cards
**Decision:** `cardLayout` gains `mixed` and it is the default (control.json default and `StoryCards.Layout`); `column`,
`halves`, `flip`, `corners` and `classic` stay selectable (control app: Settings > Behaviour > Story card layout). In mixed,
regular cards take column -> halves -> flip -> corners in turn: the variation is picked on a showing's first frame (a new
`StoryDirector.StartedMs`) and kept to its end, and the cycle means two regular cards in a row never share one.
SPECIAL cards (`StoryCards.IsSpecial`) always use the tall column with their own treatment, in every non-classic layout:
the Michelin pit-stop tower slot (PitStops) and whatever card the sponsor pack has GO FAST presenting
(`Presenters.For(k) == Partner.GoFast`; Angelo's mapping = Fastest Laps and On the Limit; a public install maps them to the
Custom slot, so they are regular there). GO FAST Fastest Laps is paged: one page per class (slowest class first), the card's
time split evenly with at least 4 s a page; when that needs longer than the card's time, `StoryDirector.ExtendExit` moves the
exit later (once, on the first frame; only ever later, never undoing a bow-out). Every page keeps the GO FAST header; the big
watch sweeps to the next class, the reading cross-fades, the page shows that class's three quickest; page dots under the panel.
**Why:** Angelo (#393) after the #349 previews: variation flavour for the regular cards, partner cards keep one big identity.

## 2026-10-27 — #398 / #399 / #401 / #402 story card fixes (Angelo, after seeing mixed on the TV)
**Decision:**
- **#398 GO FAST 911 back in the column cards, full inner width.** On the Limit: `gofast_911_side.png` sweeps in and parks
  edge to edge above the footer with the green underglow and speed streaks (`Limits911`); the map moves beside the
  MOST TIMES OFF list and the list loses rows for the room (kerb blocks go in that narrow list). Fastest Laps: `gofast_911_front.png`
  spans the inner width behind the watch on every class page; the watch keeps the classic proportion to the car (<= 0.235 of
  its width) so the headlights show, and they flare again as each class page lands. Type unchanged.
- **#399 Trading Paints plates:** the maker badge, P-number and IN CLASS are laid out from measured widths against the
  race-number panel (measured first, `PlateMeasure` with one cached paint): IN CLASS is dropped when it won't fit, then the
  badge narrows (to 36), then only the P-number shrinks (never below 28). Same code for classic and every column variant
  (CarContact, the DQ card, Cleanest). Classic frames that fitted before are pixel-identical.
- **#401 Michelin tower:** the Michelin Man is back on the stem in classic (#216's `false &&` removed). In the column the tower
  is ONE shape (`PitTower`): one screen (header + numbers over the quickest stops) and the leaning pylon leaving its right end
  with an elbow, down to the foot of the column, with the Michelin Man ("michelin man full.png", else "michelin man.png",
  clipped to the pylon) or, without his pictures, the typed word.
- **#402 On the Limit map zoom** (classic + column): the full outline while the offs drop in (to 2.0 s), then an ease-in-out
  (smoothstep) zoom over 1.2 s onto the densest 3-of-40 lap window of offs (`HotWindowCentre`), framed with a margin, max 4.5x,
  kept inside the map box and clipped to it; line widths / dots divided by the zoom so they stay the same on screen; then a
  label with the corner most offs in that window were at (TrackCorners), else the window's corner / straight name. No
  rotation (kept simple). The matrix is a struct, the name is worked out once per hotspot, the label's paints are cached.
**Why:** Angelo #398 ("keep the car in it, that was cool ... bigger, to the boundaries of the width"), #399 ("a two digit Px
gets overlapped"), #401 ("one piece and change it back to the Michelin Man on the stem"), #402 ("start with the full track
outline but then zoom into the part of the track with the most offs").

## 2026-10-27 — Visual pack round 3: free space only (V4, V5, W11)
**Decision:** the pack's graphics never sit on another graphic or in the centre third (#349, x 640..1280 in 1920x1080).
- **`VpLayout` grows into a free-space map:** besides the bar / watch / ticker / radio / battle map / tower it now carries the
  battle card (with its drawn width and UNDER PRESSURE tab), story, featured and custom cards and the onboard treatment
  (= no free space), plus a claim slot per pack card (weather, race control, yellow map, strategy, BACK TO LIVE, PiP; a
  claim lives 250 ms). `Occupied` / `IsFree` / `Clearance` are allocation-free (stackalloc).
- **V4 strategy board ("it can't block the timing tower"):** right third only (x 1296..1896), in the tallest free stretch
  of that column; type sizes unchanged (#340: no shrinking - the old `boardScale` is gone; the tyre-age lap count got
  bigger). To fit: the STOPS and LAST STOP columns are dropped (position, car, driver, tyre-age bar + laps, next stop /
  IN PIT stay), and rows drop to the top N of the class that fit (own car always kept in the last line); fewer than 3 rows
  fit = it fades out. The fuel line drops its "L / LAP" part if too wide; the estimate note has its own line. The elevated
  title is "THE RACE SO FAR" (the long one ran into the lap count). A partner whose mark can't frame a >= 540 px board
  inside the column (e.g. a round mark) gets the house glass in the partner's colour with PRESENTED BY + logo in the
  header (no boom); a mark that can still frames it, with the boom, shockwave and flash kept inside the board's own spot.
  It jumps (never glides) when its spot changes.
- **V5 BACK TO LIVE:** gone 4.05 s after the picture is back at live (cut in at +0.65 s, hold 2.9 s, out 0.4 s; was
  ~5.7 s). Placed only where `IsFree` says (REPLAY-tag corner first, then down the right edge, then right of the tower);
  no free spot when it would show = skipped (logged); if something comes up on it, it jumps to another free spot (160 ms
  fade-in, no glide across graphics) or goes at once.
- **W11 lens effects:** drawn only while the sun sits in the top band (y < 620) of the left / right third and 260 px clear
  of every graphic (none at 150 px); fades out in 160 ms when anything comes near, back in 350 ms. Everything is confined
  to the sun's third and band and has soft holes (a nine-patch soft square, DstOut, in a layer bounded to that third) cut
  round every graphic, the centre third and below the band. Ghost chain cut to three small ghosts within 260 px of the
  sun; streaks 560 / 900 px (were 900 / 1900); glow and starburst smaller; heat haze removed (it lay over the cars).
  With the tower up the left third has no room left, so in practice the flare lives in the top right. His real Le Mans
  race only put the sun there for ~0.3 s, so expect it rarely.
**Why:** Angelo's round-2 verdicts, 2026-10-27 (relayed): V4 "it can't block the timing tower", V5 "can't ever block any
other element and has to disappear after a few seconds of being back at live", W11 "it appears in the way during a lot of
different things" (switched off on his PC until this).

## 2026-10-27 — #410 / #411 / #412 story card tweaks
**Decision:**
- **#410** GO FAST Fastest Laps (column): the head-on 911 behind the watch is twice the inner width; it runs past the padding
  to the panel's own edges (clipped by the panel, and to its section so it never sits over the title). The watch stays on top
  at up to 190 radius; a class page lists its two quickest (was three) when the car is there. Type unchanged.
- **#411** On the Limit: the rumble strip moves from along the top to down the card's LEFT edge, full height, 30 units wide,
  following the slanted edge (straight with square corners), kerb outside, white line + asphalt inside (`KerbLeft`; classic
  and column). `DrawKerb` now takes the canvas scale from the matrix's length, so a turned kerb is built at full resolution.
- **#412 preview:** `StoryCards.SquareCorners` (static, default false = the app is unchanged). When on, the slanted left
  side of the card panels (classic `Slant` / `Glass`, column `SlantCol` / `GlassCol`) and Your Race's chamfered corners become
  plain 90-degree rectangles; the slab and keyline go vertical. Rounded shapes (Weather's radar screen, the Michelin tower),
  the livery stripe cuts and the light streak keep their shapes. The preview tool switches it on (`--slanted` = off).
**Why:** Angelo after seeing the GO FAST / Trading Paints cards live: "twice the size", "the rumble strip on the left",
"maybe we make them 90 degree angle all the way around" (for his OK first).

## 2026-10-27 — Visual pack round 4: V7 / V8 in the right-third column, On the Limit hotspot, #413 top-stack gap
**Decision:**
- **V7 race awards** keep one award at a time, each its own sponsored card and object, but each is now a tall portrait
  card (594 x 872 design px) in the story cards' right-third column (x 1290..1884, top max(172, `VpLayout.CardTop`), foot
  1044) - never in the centre third, never over the tower. Re-laid per award: fuel gauge above the rolling laps + stint
  ribbon; full-width LED board; timing strip over the value with the metronome beside it; the film strip runs top to bottom
  (P1 frame, chequered line, P2 frame; WINNER / BEATEN TO THE LINE added), the margin stamp under it. Slant 46 px (was 70),
  the counter on its own line, BY [partner] after the title (top right when a mark is too wide), labels >= 13 px in
  bright greys (#C6CBD3 / #D3D9E3), foot line wraps at its " · " rather than shrinking. The director only starts an award
  when `VpLayout.IsFree(RaceAwards.Spot())` (new claim slot `SlotAwards`); if anything comes up on the column (radio card,
  battle card / map, another pack card) it exits at once and, if cut before 60 % of its time, plays again afterwards.
- **V8 yellow map** in the right-third column's tallest free stretch (as V4): full plate 594 x 430, or a compact 594 x 280
  plate whenever a battle card is up (it sits under the battle map) or the stretch is short (e.g. above a radio card);
  both sizes baked at show time; no room = not drawn, and its camera starts when first on screen; jumps with a 160 ms
  fade, never glides over graphics. Now also shown during battles (was suppressed). Header: "YELLOW FLAG" at 14 px drawn
  directly (the theme label's own eyebrow scaled to ~7 px), the themed place name under it; cause line 14 px (13 min).
- **On the Limit** (classic + column): the HOTSPOT line and the zoomed map's label come from one `Hotspot()` - the densest
  stretch of offs (the window the map zooms onto), named by `HotName`, with the offs in that window; the busiest single
  corner only when the stretch has no name. Cached per hotspot / count / track.
- **#413 top-stack gap:** `VpLayout.TopGap = 16`, `CardTop = TopStackBottom + 16` (bar plate + watch + ticker when it
  shows). `IsFree` refuses any rect whose top is above `CardTop` (full width); the strategy board's free stretch, the
  weather card (snaps down at once when the ticker comes in, eases back up), race control (never above it while easing),
  the awards, the yellow map and PiP all start at `CardTop` or lower. Type never shrinks; rows / sizes give way instead.
**Why:** Angelo 2026-10-27: "V7 and V8 I love, but now we have to size them according to our new spacing constraints";
the On the Limit label / HOTSPOT mismatch; #413 "almost all of those cards will touch the session bar and the session bar
ticker if it is on".

## 2026-10-27 — #409 running Michelin Man, #412 square corners on, #422 no white + red foot, #424 hotspot themes the card
**Decision:**
- **#412** approved: `StoryCards.SquareCorners` defaults to true in the app.
- **#409** `MichelinRun`: Angelo's frame sequence (Documents\iRacing\Grandstand\corner_logos\michelin_run\*.png, personal,
  never shipped; upright, 580x480, 15 fps, last frame = the finished logo) plays once on a WHITE PANEL at the top of the
  Pit Stops tower's stem (classic + column), then holds the last frame. Upright (Angelo: "zoom it in, don't turn it on its
  side"): the panel is at least 200 wide (wider than the stem where needed, like the real tower's panel, kept inside the
  card), the frame zoomed so its middle ~92% fills it. Without the folder: the Michelin Man picture, then the typed word.
  Memory / thread: nothing loads until the card first asks; a background thread decodes the wanted frame and 3 ahead into a
  6-frame ring (~1.1 MB each) and keeps the last frame for good; the render thread never touches the disk and never waits
  (it shows the nearest earlier frame it has); the ring never evicts a frame inside the window being shown (a replay of the
  card starts at frame 0 while the ring still holds the end of the last showing). Steady state (frozen) allocates nothing.
- **#422** the livery stripe at the foot of every story card (and the race-award cards) is the card's colour only, full
  width: the white + red cuts at the right end are gone.
- **#424** On the Limit (classic + column): when the hotspot (the same place the map label / HOTSPOT line name) has a
  CornerTheme, the battle map's backdrop for it (`BattleMap.Backdrop`) cross-fades over the WHOLE card with the zoom
  (smoothstep, 2.0 -> 3.2 s), under a dark scrim (up to 42%) so the text reads; the GO FAST header, kerb, map and list are
  drawn on top; the map label becomes the themed lettering (`CornerTheme.LabelImage`, cached). Plain places stay GO FAST green.
  The theme is looked up once per hotspot name.
**Why:** Angelo, 2026-10-27 (#409, #412 "I approve", #422 "why is there that white and red thing?", #424 "that corner
theme to change the theme of the card as it zooms in, and the entire card's background").
## 2026-10-27 — #425 V8 yellow map takes the incident place's theme, then alternates with the yellow look
**Decision:** the plate is baked as a base (shadow, glass), two skins and a foot (marshal housings, cause line): the
YELLOW-FLAG skin (amber wash, yellow keyline, YELLOW FLAG + the place in plain letters) and the THEME skin (the battle
map's `Backdrop` for the place's `CornerTheme` - PlayStation space, Michelin blue + yellow edge, Dunlop graphite, Bricks,
Speed, Earth, maker colours - its colour on the edge, the name in the theme's own lettering drawn with
`CornerTheme.DrawLabel`, scaled so the name reads ~28 px and fits between the marshal panels, YELLOW FLAG kept at 14 px).
A place without a theme gets a neutral asphalt theme (maker-plate style, grey) so there is still something to alternate
with. Mix: the theme cross-fades in over the fly-down (1.7 -> 2.8 s, smoothstep), then alternates by a cosine with a
1.6 s period for the rest of the card. The map follows the mix: the ground is tinted (SrcATop) from amber to the theme's
colour, the line's wall / glow from gold to the theme's eye colour; the yellow sector on the line and the marshal panel
LEDs stay yellow throughout. The place is the corner / straight name at the incident's lap % (TrackCorners), falling
back to the callout's place. Both sizes (full / compact) bake all four layers at show time; 0 B/frame.
**Why:** Angelo #425: "as it zooms in, change to the theme of the incident location, then smoothly flash alternate between
the yellow and the theme".
## 2026-10-27 — #339 mini story cards in a battle (PREVIEW, setting `miniCardsInBattle`, OFF by default)
**Decision:** `StoryDirector.MiniCardsInBattle` (control.json `miniCardsInBattle`, default false; control app switch "Mini story
cards in a battle (preview)"). Only while the #338 hold is on (driving live + battle + `noCardsInBattleWhenDriving`), a mini
card - a 560x110 plate with the card's single headline fact, in its own colours, type and background (slant glass + livery,
Weather radar round-rect, Driver of the Day black/gold Cormorant, Your Race HUD chamfer, Michelin blue/yellow), its partner
mark small at the right ("BY [mark]", 13 px verb; Michelin logo on Pit Stops) - plays: 0.4 s in (wipes in from the right),
6 s hold, 0.4 s colour-bar wipe out; first one 8 s into the battle, then at most one every 45 s. It takes the front of the
SAME queue (new fastest lap first, then priority, then age) and consumes it: a story told as a mini is not replayed in full
after the battle (cut off within 2 s -> back to the front of the line). Placement (`StoryCards.MiniRect`, from VpLayout):
right-aligned with the battle map, 14 px under it (or under the bar / ticker if lower) = x 1336..1896, y 416..526 - the only
560-wide spot in the right third that misses the battle map (172..402), the battle card (top ~730 incl. tabs, out to x 1460 /
1600 in the 3-5 car layout) and the session bar / ticker; the tower lane is on the left. The radio card (x 1599..1896, bottom
718, top 718 - 0.9 x height) only clashes with long (3+ line) calls: then the mini waits, or steps out if it is already up.
Also waits for custom cards (which are held while it shows), flags / final laps / pits (same `Blocked` as the full cards),
onboard shots; out at once when the battle ends or a full card starts. No sound. Text >= 13 px, greys #C6CBD3 / #D3D9E3 (#340).
**Why:** Angelo (#339): "mini versions of the cards" for battles, and he wants to see them before anything goes live.

## 2026-10-28 — Per-track broadcast content for IMSA S4 weeks 8-12 (Mexico, Suzuka, Silverstone, Sebring, Monza)
**Decision:** Same per-track content Le Mans has, for the rest of the season, in the existing tables only (no new systems):
`TrackCorners` - CrewChief's trackLandmarksData.json stays the source of every corner POSITION; a new per-track `Renames`
table (exact iRacing track ids) only fixes NAMES - a bare turn number that has a real name (Silverstone Village / Aintree /
Brooklands, Suzuka 200R, Mexico curve 12 Adrián Fernández), CrewChief spellings / short forms (Mansel, Horquilla -> Recorte
Rebaque, Ascari -> Variante Ascari, Kristensen -> Kristensen Corner ...), and drops CrewChief's short markers on straights
(Sebring "Flying Fortress" / "Ullman", Silverstone's un-numbered "brooklands" stretch, which sits on the Wellington Straight
before CrewChief's own turn 6) so those straights read by their real names; back-to-back stretches renamed alike merge (Suzuka
S Curves). `Pretty` keeps Monza's lowercase del / della and spells Maggotts. New `KnownStraights`: Ulmann (fixed - was keyed to
a "Turn 17" CrewChief doesn't have), Flying Fortress, Hamilton (Silverstone pit straight, renamed 2020), Suzuka back straight,
Mexico Recta Jim Clark. `CornerTheme.Heritage`: one sourced line per place (sources in the code comments); places whose source
gives nothing get no line. `TrackStory`: a short description + first race per track (Mexico: no FIRST RACE - not verified).
`TrackNames`: Autódromo Hermanos Rodríguez added (name + mx flag; it was missing).
**Themes:** no new theme kinds and no drawn logos. Text themes only where the name says what the place looks like: Copse
(Silverstone, named for the small woods) and Lesmo (Monza, once the "curve of the oaks") green Earth; Sunset Bend (Sebring, into
the setting sun) orange Earth; Hangar Straight and Ulmann Straight the Speed streaks. Brand-named corners use the EXISTING
user-picture path: Suzuka's Dunlop Curve takes the Dunlop arch only if dunlop.png is in corner_logos (Angelo's Le Mans picture
- note the arch was drawn for Le Mans' bridge); Casio Triangle shows a "casio.png" if he adds one (generic picture branch);
driver-named corners (Ascari, Fangio, Gurney, Kristensen, Mansell ...) show a portrait only if he adds e.g. "fangio.png".
Without the picture every one falls back to the plain gold label, exactly as before. Nothing branded ships.
**Why:** Angelo's season: AHR week 8 (Nov 3), Suzuka 9, Silverstone 10, Sebring 11, Monza 12. Standing rule: no made-up facts
or numbers - every line traces to formula1.com, mexicogp.mx (official AHR curve names), 24h-lemans.com, sportscar365, IMSA,
Wikipedia; disputed ones (Spoon's name, Vale, Stowe, Sebring "Bishop Bend vs Curve", La Presidencial's story) left out.

## 2026-10-28 — Track packs for the other 63 iRacing circuits live in a data file (TrackData/tracks.json)
**Decision:** The six hand-made tracks (Le Mans, Mexico, Suzuka, Silverstone, Sebring, Monza) stay in code exactly as they
are. Everything else comes from `src/ApexHUD.Native.Common/TrackData/tracks.json` (embedded resource, read once by
`TrackPacks`), generated by `tools/track_data_gen/build_tracks.py` from the 63 sourced research files (sc_packs/out,
schema SCHEMA.md). Keys are iRacing's exact track ids (CrewChief irTrackName, `_` -> space, lower case); Sonoma (its id
not confirmed in the research) is matched by the prefix "sonoma". Hand-made tables are always checked first; the pack only
fills in where they have nothing, so the six tracks render byte-identical (checked: preview stops + PNGs before/after).
- **Corner names:** no positions in the file. Each name is pinned to an existing CrewChief landmark by its start metre
  (+/-1 m) and applied while TrackCorners loads the landmarks: rename (official name, parentheses / "Turn N" notes stripped),
  or drop (CrewChief markers that sit on a named straight: Kemmel, Döttinger Höhe, Road America's Moraine Sweep / Kettle
  Bottoms / Thunder Valley / Road America Straight, Mid-Ohio Thunder Valley). Back-to-back stretches with the same name
  merge (Interlagos Senna S, Hungaroring ...). Research names that are only a turn number, an "exit" or a "link" keep
  CrewChief's name. Tracks whose CrewChief entry has no landmarks (Chicago, Zandvoort, Fuji, Motegi, Sandown, St Pete,
  Summit Point, Sonoma, Rockingham, Aragon, Navarra, Phoenix road, Rudskogen) get name / flag / story / first race only.
- **Named straights:** by the corner they follow (`TrackPacks.StraightAfter`, checked before KnownStraights). Generic
  ones (main / front / pit / start-finish) are left to the existing "the main straight".
- **Heritage:** the research line, uppercased, <= 48 chars, exact-name match per track (not substring).
- **Themes:** text only. Earth where the place's own name is water / woods / oaks / sun / rock (curated list in the
  generator; palettes woods, oaks, redearth, sunset + new water, stone in CornerTheme), Speed for the long named straights
  the research flags. No brand / sponsor / driver themes are added: those still change only when Angelo drops his own
  picture into corner_logos (CornerBrand); the Le Mans-only guard for Dunlop / PlayStation / Michelin is untouched.
- **First race:** the first year in the research's first_race text, except Interlagos (text is the first F1 round),
  Phillip Island (the 1928 island road course) and Navarra (a MotoGP showing) - left blank.
- **Track card:** on pack tracks the corner label also shrinks to fit its heritage pill in the column (long 40+ char lines
  overflowed); the six hand-made tracks keep the old fit.
**Why:** Angelo: Le Mans-level content on every track, sourced only, positions from CrewChief. A data file keeps
CornerTheme / TrackCorners readable instead of 1,000+ table lines, and the generator makes a re-run trivial when the
research grows.

## 2026-10-28 — Track packs round 2: every research file, the hand tracks' other layouts, Le Mans parity
**Decision:** (Angelo: "all the tracks, all the intros, everything that has been done with Le Mans")
- `build_tracks.py` runs over all 85 research files (sub-folders ignored) except the two off-road tracks (Lucas Oil
  `wheatland`, Wild West) - 82 packs, 291 track ids. Le Mans has no pack (untouched).
- **Hand-made tracks:** the five IMSA layouts done in code (mexicocity gp, suzuka grandprix, silverstone 2019 gp,
  sebring international, monza full) still get no pack, so they render byte-identical. Their OTHER layouts get a pack
  marked `"hand"`: the hand tables are checked first there (KnownStraights before pack straights, the hand themes before
  pack themes; heritage and story were already hand-first). The generator reads TrackCorners.cs and never renames a
  landmark the hand Renames table renames or whose name the main layout already shows (Monza Combined keeps Curva Grande
  and Parabolica, as on the GP), and adds no heritage / themes for places on the main layout (the hand table left
  Stowe, Vale, Spoon, Bishop out on purpose). So those layouts gain facts plus only what is new to them (Silverstone
  2011's Bridge line, Suzuka West's 200R, Mexico GP Historic's curve names).
- **Layout facts** (km / turns): config -> id from the research's explicit map, then the layout's own name ("gp" = Grand
  Prix), then corner evidence (may be shared by a legacy and a 2026 id of one layout), then the scheduled layout for the
  first id, then the one id left with the one config left. `iracing_length_km` (simracing.wiki) counts as a sourced
  length. Never borrowed from another layout (Zandvoort 2023 Nationaal no longer shows the GP's 4.259 km).
- **Main straight:** a generic front straight's sourced heritage line / Speed flag goes on Grandstand's own "Main
  straight" stop when the research's corner before it is the last corner of the CrewChief lap (Indianapolis bricks,
  Portland's Cottonwood Street, Pocono 3,740 ft).
- **Portrait (new text theme):** places the research flags "portrait" AND whose named_after type is "driver" (71: Senna
  S, Brock's Skyline, Andretti Hairpin...). Champagne / gold, an honour-roll rule (two hairlines + a small diamond),
  battle-map plate in warm graphite with gold hairlines. No photo, no logo; if Angelo has a corner_logos picture for that
  name it still wins. Not on the hand-made tracks (their main layouts must not change).
- **Grid intro:** `GridIntro.HoldsTrackPlate` - the track page stays up behind the grid at every track whose map
  Grandstand has (TrackMapCache), Le Mans exactly as before.
- **Kerb colours hook:** every pack has `"kerbs": []`; a research file's `kerb_colours` ("#RRGGBB" list) goes straight in
  and StoryCards.KerbColours uses it (2+ colours) after the Le Mans / Watkins Glen lines, which are unchanged.
- Heritage that names an old sponsor under CrewChief's newer name (Goodyear Kehre showing "DUNLOP KEHRE ...") is left
  out; corners matched loosely by hyphen / space ("Warsteiner-Kurve" = "Warsteiner Kurve").
**Why:** Le Mans parity audit (outputs/lemans_parity_audit.md): new research files unbuilt, the IMSA tracks' other
layouts empty, backdrop Le Mans-only. Sourced data only; nothing from "unverified"; no drawn logos.

## 2026-10-29 — #517 story cards take the split (corners) layout only while a team radio call is on screen
**Decision:** the corners arrangement (D: top piece in the top-right corner, bottom piece in the bottom-right corner, the
radio card's spot between them) is now THE split layout and is used only while the radio card is up. `corners` leaves the
mixed cycle (column -> halves -> flip); a `cardLayout` of `corners` now means column when there is no radio. The compositor
sets `StoryCards.RadioOn` every tick from `RadioPopupRenderer.IsActive`. `StoryCards.SplitTick`: the radio state has to hold
300 ms before the card follows it (blips never move it); then a 420 ms smoothstep glide - the two split pieces move out of
the full layout's top / bottom sections to their corners while the full layout fades out around them (and back, reversed,
when the radio ends, if the card is still up). The card's clocks are untouched, so no entrance replays. A card that starts
while the radio is already up starts in split (its own corners entrance). In split the top piece stops 14 units above the
radio card's top (followed smoothly, never shorter than 290). Special partner cards (Michelin pit tower, GO FAST) keep the
tall column, as #393; the classic right-half card is unchanged.
**Why:** Angelo (#517): "the split variation of the cards only need to play if there is a radio playing and we need a way
for it to transition to the split version when a radio call is happening".


## 2026-10-29 — Highlight reel, phase 1 (#9)
**Decision:** one click after a session (control app On Air → Highlight reel, or POST /api/reel/start {file}) plays that
session's highlight markers back in iRacing's replay while OBS records them into one video.
- **Plan (pure, `Common/HighlightReel.cs`, tested by tools/reel_check):** markers with a replay time, in time order; a
  marker < 10 s after the one kept before it is the same moment (the more important kind wins: finish > start > pass >
  flag > battle > lost > fastest > yellow > pit, +5 for your own); at most 20 (most important first, then back in time
  order); each starts 6 s early and plays 12 s (pass / lost / battle) or 8 s; a moment that would start before the
  previous one ends carries straight on (no jump back, nothing shown twice).
- **Camera:** the marker's first car (none = your car, then the leader) on TV1 / TV2 / TV3 in turn, Pit Lane for pit
  stops, TV1 for the start and the flag, then Chase, then any TV group; never cockpit / onboard / blimp.
- **Playing:** SDK broadcast messages only - pause, ReplaySearchSessionTime, CamSwitchNum (re-sent until it takes),
  then 1x until the replay clock reaches the end. The end is judged on the replay's SessionTime, not the wall clock.
- **OBS:** StartRecord on the first moment (only if OBS isn't already recording; if it is, the reel adds chapters and
  leaves that recording alone), a CreateRecordChapter per moment, PauseRecord / ResumeRecord around each jump (no frozen
  frames), StopRecord after the last or on Stop. Sent whatever the "Highlights to OBS" switch says - the reel was asked for.
- **Safety:** starts only in a replay (file, IsReplayPlaying, or > 30 frames behind live) with you out of the car;
  stops at once on driving / IsOnTrack (live session) / iRacing gone, after 2 s back at live, or when you take over
  (camera car, camera group, speed / slow-mo changed, or a jump the reel didn't make). While it plays
  `HighlightReel.Playing` stops the highlight log writing markers (the replayed moments would be marked again into the
  very file being played) and pauses the replay director.
- **4K:** the video is whatever OBS captures. A true 4K reel = watch the replay in iRacing's monitor mode on the 4K
  screen (the future "Watch Replay" mode) with OBS capturing it at 3840x2160. The reel does not switch iRacing's mode.
**Why:** Angelo's "I don't want to edit a highlight reel" (#487) - the markers already hold the exact replay moments.


## 2026-10-29 — Viewer cards and themes (#494)
**Decision:** viewer interactions arrive on the existing local control server (POST /api/viewer, same X-Grandstand: 1
header as every other change) from Streamer.bot / StreamElements running on the PC - Grandstand never holds a YouTube or
Twitch login. Only paid messages (Super Chat, tip, bits) and members / subs go on screen; follows never do. Paid messages
ride a strip that slides down from under the normal-width session bar (the standings ticker's look) and scroll through
once; from a configurable amount ($20 / 2000 bits) a banner also covers the tower's cap (series logo) for 6.5 s. Members /
subs get a pre-race style card under the bar. One event at a time from a queue (ViewerCardsRenderer); nothing starts or
stays up during lap 1 of a race, a battle (or the bumper's battle look), an onboard, a replay, a story card, a pre-race /
post-race phase or from the white flag on - an event cut short before 60 % goes back to the front of the queue. Messages:
single line, 140 characters, links dropped, built-in word list + Documents\iRacing\Grandstand\viewer_blocklist.txt
(a hit hides the whole message; a blocked name shows as "A viewer"). Amounts are only ever the event's own text.
Track fund (default OFF): goal text + target in control.json, current amount in fund.json, increased only by real
Super Chats / tips in plain dollars (never test events, never converted currencies).
Themes: `Theme` (Overlays) with Default (draws nothing) and Halloween (warm black wash, ember glow, drifting procedural
embers, cobweb / bat motif, orange accent hairlines). Skins only the glass of the tower, session bar and viewer cards via
`Theme.Skin`; layout and the session bar's design are untouched. control.json `theme` + `themeByDate` (Oct 24-31 =
Halloween), both defaulting to Default / off.
**Why:** Angelo approved the design (#494) - viewers' support on screen without ever covering the racing moments.
