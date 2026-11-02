# ApexHUD-Web → ApexHUD-Native Port Plan

*Produced in Session 4 (2026-05-27) as read-only reconnaissance of the legacy
`../ApexHUD-Web` system. This is the catalog future sessions port from. It is a
living reference — append corrections as you learn more during each port, but
treat the original inventory as the snapshot of the legacy system on this date.*

---

## Summary

ApexHUD-Web is a broadcast-graphics overlay suite for iRacing built as
HTML/CSS/JS pages rendered in transparent, click-through `pywebview`
(WebView2/Edge Chromium) windows. A Python desktop launcher
(`overlay_launcher.py`) opens one frameless always-on-top window per enabled
overlay and applies `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
WS_EX_NOACTIVATE` so clicks fall through to iRacing. A Python HTTP server
(`server.py`, port 8089) bulk-polls **SimHub** (`/api/GetGamedata` +
`/api/GetLeaderboarddata`, with the **benofficial2** plugin supplying
per-driver standings) every **250 ms**, reshapes the result into a stable
`NewData.*` JSON document (including an IREP-style `_leaderboard`, a
benofficial2-style `Standings` tree, an injected `_sessionState`, a
`_spectatedCar`, etc.), and serves it at `/api/getgamedata`. Each overlay polls
that single endpoint at 100–500 ms via `shared/simhub-client.js` and binds
deeply-nested properties into the DOM.

**What we are porting:** the *visual* overlays and their *behavior*, rebuilt
natively in WPF + SkiaSharp.

**What we are deliberately leaving behind:** the entire HTTP/SimHub/benofficial2
data chain. ApexHUD-Native reads iRacing's shared memory directly via
IRSDKSharper (Session 2 already proved this works) and rebuilds the
`NewData`/`_leaderboard`/`Standings` shapes in C# as an in-process observable
race-state model. No HTTP, no JSON, no `server.py`, no SimHub dependency for
the time-critical path. (See `DECISIONS.md`, 2026-05-27 "Bypass SimHub".)

**Scale of the legacy code:** the `overlays/` and `shared/` directories are
buried in `.bak` snapshots and `_experiment.py` scripts. The authoritative
live set is whatever `overlay_positions.json` references with `enabled: true`.
Everything in this plan is keyed to that file, not to filenames alone.

### The one source of truth: `overlay_positions.json`

The launcher reads `overlay_positions.json`. That file — not the README, which
is stale and describes an obsolete 2-overlay OBS-browser-source era — defines
what actually runs. Live (`enabled: true`) overlays and their windows:

| Overlay | File served | Window (WxH @ x,y) | Visibility mode |
|---|---|---|---|
| Session Bar | `session-bar-ORIGINAL-v9-21.html` | 1936×230 @ (0,−8) | force-visible |
| Timing Tower | `timing-tower.html` | 440×1080 @ (0,0) | force-visible |
| Featured Card | `featured-card.html` | 768×254 @ (576,880) | force-visible |
| Battle Card | `battle-card.html` | 1280×340 @ (320,780) | auto-context |
| Fastest Lap | `fastest-lap.html` | 784×174 @ (568,532) | force-visible |
| Sponsor | `sponsor.html` | 440×230 @ (1564,152) | auto (timed cycle) |
| Pit Callout | `pit-callout.html` | 784×144 @ (568,164) | auto-context |
| Starting Grid | `starting-grid.html` | 1100×830 @ (410,230) | force-visible |

Disabled (`enabled: false`) overlays, present but not launched: `pit-strategy`,
`corner-indicator`, `hybrid-battery`, `standings-ticker`.

> ⚠️ The session bar's live file is the confusingly-named
> `session-bar-ORIGINAL-v9-21.html`, **not** `session-bar.html`. The plain
> `session-bar.html` and the `-clean/-minimal/-v3..v5` variants are dev history.
> "ORIGINAL" was a rollback anchor created before a destructive cleanup.

### Design system (shared, applies to every overlay)

- **Aesthetic:** deep-glass panels (`rgba(8,10,16,0.82)` + `backdrop-filter:
  blur(28px) saturate(180%)`), **square corners everywhere** (all radii = 0),
  warm-ivory text (`rgba(252,250,245,0.98)`), champagne-gold accent (`#C5A572`),
  1 px hairline borders, broadcast tabular numerals.
- **Class colors:** GTP/LMDh `#C5A572`/`#C084FC` (champagne/purple — note the
  two source files disagree; tower uses purple `#C084FC`, theme.css uses
  champagne `#C5A572`), GT3 `#E5C66B`/`#FFDA59`, GTD-Pro `#FFB627`, GT4
  `#4ADE80`, GTE `#F87171`, LMP/LMP2 `#60A5FA`/`#6B85B8`, default `#6366F1`.
- **License colors:** P `#7C00FF`, A `#006EFF`, B `#33CC00`, C `#FFCC00`,
  D `#FF6600`, R `#E1251B`.
- **Motion:** easings `--ease-out: cubic-bezier(0.16,1,0.30,1)`,
  `--ease-out-soft: cubic-bezier(0.22,1,0.36,1)`, `--ease-overshoot:
  cubic-bezier(0.34,1.56,0.64,1)`; durations fast 240 ms / base 480 ms /
  slow 900 ms / very-slow 1600 ms.
- **Fonts (current production):** **Cormorant Garamond** (serif display) +
  **Inter** (sans body/timing), loaded from the **Google Fonts CDN** via
  `shared/fonts.css`. ⚠️ There is **no CDN in WPF** — these must be bundled as
  local files, OR we switch to the **10 bundled RaycerRay broadcast `.ttf`
  fonts** in `shared/fonts/` which the *current* theme does **not** use. This is
  an open decision (see Risks).

---

## Inventory of legacy overlays

### 1. Session Bar — `overlays/session-bar-ORIGINAL-v9-21.html` (~5144 lines)

- **Purpose:** Full-width semi-transparent strip pinned to the top of the
  screen with a real-time analog **Rolex-style watch** centerpiece, flanked by
  race context (flag/track/temps/weather and phase/session-timer/lap-counter/
  flag-state), reacting to flag-state colors.
- **Window:** 1936×230 @ (0,−8). Bar itself is a centered glass element
  (~720–976 px wide × 76–92 px) with the watch bleeding above/below.
- **What is ACTUALLY VISIBLE in production (critical scoping finding):**
  - The **centerpiece analog watch** (`.centerpiece`): SVG hands (hour/minute/
    second + three static subdial groups), a fluted-bezel SVG ring, dial slot.
    Drives off **local PC time** via `requestAnimationFrame` (the
    `SessionTimeOfDay` integration was removed — it made the second hand reset).
  - The **flag-state bar coloring**: the whole bar takes `data-flag-state`
    (yellow/red/white/green/blue/black/checkered) and recolors/pulses.
  - The **battle-header zones** (injected at runtime by `buildOnce()`):
    - Left: country **flag** image, **track name**, **air/track temps**,
      animated **weather** SVG (sun rays / rain drops).
    - Right: **phase chip**, **session-time countdown**, **lap counter**,
      **flag-state dot + label**.
  - The **race-progress bar** (`.battle-progress-bar`): gold fill at
    race-elapsed %, with state colors (gold/celebration/alert-yellow/-red/
    -white/checker).
- **What is present but DISABLED/HIDDEN in production (do NOT port in v1):**
  - `.track-map`, `.sector-strip`, `.battle-strip`, `.leader-chip` — all
    `display:none !important` ("tier-hide-4-elements", 2026-05-24). Their data
    loops still run; the DOM is just hidden.
  - **Battle mode** in the bar — hard-disabled (`switchMode()` forces
    `battle`→`session`; "canned by spec").
  - The **rotating chip** (`#rotChip`), **series logo** (double-hidden),
    `.metric--lap`.
  - The **standings ticker row** (`.ticker-row`) — only shows when
    `body[data-battle="true"]`, which never fires in the bar; it's the
    standalone `standings-ticker` overlay's job.
- **Data inputs (visible parts only):** flags `Flag_Red/Checkered/Yellow/White/
  Black/Orange/Blue`; `CurrentLap`, `TotalLaps`, `SessionTimeLeft`,
  `SessionTimeOfDay`, `SessionTypeName`; `AirTemperature`, `RoadTemperature`,
  `TemperatureUnit`, `IsRaining`; `TrackName`, `TrackId`, `TrackPositionPercent`;
  `_sessionState` (via race-phase). Track→country/flag via
  `ApexTrackNames.resolveInfo()`. Series logo via `/api/getproperty/...SeriesID`
  + `Category` (for the hidden series chip — skip).
- **Assets:** `shared/assets/dial.png` (watch face), `shared/assets/watch.png`;
  `/assets/flags/{cc}.png`; fonts. Weather is pure CSS/SVG (no asset).
- **Animations:** `v46-watch-arrive` (700 ms overshoot watch entrance),
  `v46-bar-stretch` (750 ms bar grows from center), staggered content fades;
  flag pulses (yellow 1.6 s / red 1.0–1.2 s); `phasePulseFinalLaps` (1.5 s);
  weather `sunRotate` (28 s), `rainFall` (1.4 s); progress-bar `barCelebrate`/
  `barAlert`/`checkerScroll`. Clock hands via rAF. Many 250 ms `setInterval`
  coordinator loops.
- **Shared imports:** `track-names.js`, `live-timing.js`, `series-logos.js`,
  `simhub-client.js`, `race-phase.js`, `story-engine.js`, `layout-director.js`,
  `battle-mode.js`.
- **Quirks:** `trackLengthEl` is referenced but never declared (throws in demo
  mode). A `TEST_FORCE_VISIBLE = true` leaves the ticker forced visible for 1 h
  (test leftover). Battle-header DOM doesn't exist until the first poll injects
  it.

### 2. Timing Tower — `overlays/timing-tower.html` (~4324 lines) + `shared/tower-scenes.js` (~805 lines)

- **Purpose:** Full-height vertical standings tower down the left edge, showing
  the field grouped by class with positions, car numbers, manufacturer logos,
  license badges, names, and gaps, with leader/player highlighting and
  proximity "battle" glow. `tower-scenes.js` is a scene-rotation engine that
  cycles overall → per-class views behind full-screen video splash transitions.
- **Window:** 440×1080 @ (0,0). Tower element ~400 px wide.
- **Row layout (9-col grid):** `stripe | pos | arrow | num | logo | lic | name |
  interval | gap`. Per-row attrs: `data-leader`, `data-player`, `data-pit`,
  `data-fastest`, `data-connected`, `data-battle-intensity`. Class dividers/
  headers between class sections.
- **Data inputs:** primary is `NewData._leaderboard[]` with fields `Name`,
  `CarNumber`, `Position`, `PositionInClass`, `BestLapTime`, `LastLapTime`,
  `RelativeGapToLeader`, `IsInPit`, `IsConnected`, `LicenceString`, `CarName`,
  `CarClassColor`, `iRating`, `CurrentLap`. Fallback path
  `benofficial2.Standings.ClassNN.RowNN.*` (`Name`, `Number`, `License`,
  `IRating`, `CarBrand`, `InPitLane`, `DeltaToPlayer`, …) plus `_playerClass`,
  `_classes`, `_playerRowIndex`, `Highlighted`, `_spectatedCar`. Top-level
  `SessionTypeName`, `TrackName`, `CurrentLap`, `TotalLaps`, `SessionTimeLeft`,
  `OpponentsCount`, `PlayerName`, `CarId`. Series via `/api/getproperty/...`.
- **Assets:** class-intro transition videos `/assets/transitions/OVERALL.mp4`,
  `GTP.mp4`, `GT3.mp4` (GTDP uses text splash); manufacturer logos
  (`manufacturerLogoPath()`); iRacing wordmark; series logos; fonts.
- **Animations:** tower enter/leave (420 ms); battle "takeover" slide-off-left
  (`translateX(-115%)`); per-row battle glow (threshold 1.0 s / close 0.3 s);
  lap pulse 900 ms; PB pulse 1300 ms; scene system 20 s/scene with 600 ms
  splash cover; live interval updater at 150 ms; visibility hysteresis (hide
  5 s, min-visible 8 s).
- **Shared imports:** `manufacturer-logos.js`, `iracing-brand.js`,
  `series-logos.js`, `live-timing.js`, `name-cache.js`, `track-names.js`,
  `simhub-client.js`, `story-engine.js`, `layout-director.js`, `tower-scenes.js`,
  `sponsor-logos.js`, `battle-mode.js`, `race-phase.js`, `camera-hide.js`.
- **Production scope finding:** runs in `classGrouping = 'main'` mode. The
  header **lap/time number** and **session-progress bar** are hidden; the
  gap-mode rotation has a single mode (no-op); the V34-C reorganizer stands
  down. Scene rotation + class-intro videos are real and used. A large dead
  `if(false)` branch and several `.broken-vNNN.bak` siblings exist.

### 3. Featured Card — `overlays/featured-card.html` (~967 lines)

- **Purpose:** Lower-third card spotlighting the camera-focused driver:
  position, identity (number/license/class chips, name, car), perf (last lap,
  best lap, gap, iRating), manufacturer logo, and current corner name.
- **Window:** 768×254 @ (576,880). Card 720×210, 4-col grid
  `position | identity | perf | brand`.
- **Data inputs:** `benofficial2.Highlighted` (camera target: `Name`, `Number`,
  `CarName`, `License`, `IRating`, `Position`, `DeltaToPlayer`, `CarBrand`),
  `benofficial2._playerRow`, `_playerRowIndex`, `Standings.*`, `_classes`,
  `_leaderboard` (class-relative position), top-level `CarId`, `CarModel`,
  `Position`, `PlayerName`, `CurrentLap`, `TotalLaps`, `TrackName`, `GameRunning`.
  Live gap via `live-timing.js` (`CarIdxF2Time` matching). Corner via
  `LapDistPct` + `track-corners.js`. Camera group via `camera-hide.js`.
- **Assets:** manufacturer logos; fonts. Flags path exists but not rendered.
- **Animations:** card enter/leave 420 ms; `count-bounce` on value change
  (320 ms overshoot); corner cross-fade; live gap update 150 ms. Visibility
  state machine: shows on early laps (1–3), final laps (≤3 remaining), or 10 s
  after a position change; force-visible in production; min-visible 8 s, hide
  hysteresis 5 s.
- **Shared imports:** `track-corners.js`, `manufacturer-logos.js`,
  `live-timing.js`, `simhub-client.js`, `battle-mode.js`, `camera-hide.js`.
- **Quirks:** hides itself when battle-card is active; its class-detection table
  is shorter than battle-mode's (can disagree); a couple of dead CSS rules.

### 4. Battle Card — `overlays/battle-card.html` (~1533 lines)

- **Purpose:** Auto-triggered card when the player has a same-class opponent
  within 1.0 s ahead (attack) or behind (defend): both drivers' identities, a
  large animated gap timer, a closing/opening trend, and a tension meter.
- **Window:** 1280×340 @ (320,780). Card 1000×280, 3-col grid
  `left-driver | gap-timer | right-driver`. States `none/attack/defend` swap
  which side is the player (no 3D flip — a data re-assignment).
- **Data inputs:** `_leaderboard[]` (identity, `PositionInClass`, `IsConnected`,
  `LapsCompleted`), `benofficial2._classes` + `Standings.{class}.Row{NN}.
  DeltaToPlayer` (the gap source), `SessionTypeName`, `PlayerName`,
  `_sessionState`. Camera group via `camera-hide.js`. Gates on
  `body[data-race-phase]`.
- **Assets:** fonts only — **no images** (text-only driver identity).
- **Animations:** staggered entry (~920 ms); gap value lerps toward target
  (~18 %/frame); tension meter EMA (α 0.32); critical pulse <0.50 s (1.4 s),
  wheel-close <0.30 s (0.85 s). State machine: enter 1.0 s, exit 2.0 s
  hysteresis, min dwell 4 s, dropout grace 1.5 s, reopen cooldown 2.5 s.
- **Shared imports:** `battle-mode.js`, `race-phase.js`, `simhub-client.js`,
  `camera-hide.js`.
- **Quirks:** historically the card never triggered because `client.start()`
  was missing (fixed "tier2-startfix"); see `HANDOFF_BATTLE_CARD_INVESTIGATION.md`
  in the legacy root. Debug panel is permanently on (`debug=1` url-param). Gap
  depends on bo2 standings rows — if an opponent isn't in the 40-row window the
  gap can't be computed.

### 5. Fastest Lap — `overlays/fastest-lap.html` (~558 lines)

- **Purpose:** Animated lower-third callout that slides up when a new session-
  best lap is set (driver, number, time, improvement delta), auto-dismissing
  after 5.5 s.
- **Window:** 784×174 @ (568,532), pinned to bottom of its window.
- **Data inputs:** scans `benofficial2.Standings.Class00.Row00..Row19` for the
  best `BestLapTime`; `TrackName` (reset on track change); `CarId` (class label).
- **Animations:** `callout-enter` 720 ms, `callout-exit` 600 ms; aura/badge
  breathe loops; dwell 5500 ms; cooldown 4000 ms.
- **Shared imports:** `simhub-client.js`, `camera-hide.js`.
- **Quirks:** single-class only (`Class00`); uses spectated car's `CarId` for
  the class label (wrong in multi-class); no event queue (drops overlapping).

### 6. Sponsor — `overlays/sponsor.html` (~320 lines)

- **Purpose:** Configurable sponsor card (logo, label, URL, tagline) on a timed
  cycle, visible only while the game is running.
- **Window:** 440×230 @ (1564,152). Card forced **fully opaque** (deliberate
  fix for a pywebview "white box" transparency bug — relevant: WPF won't have
  this bug, so we can restore the glass look).
- **Config (url_params in production):** `logo=AB_GC_Logo`, `label=PRESENTED
  BY`, `url=abgeneralcontracting.net`, `tagline=VISIT OUR WEBSITE FOR A FREE
  STORM REPORT`.
- **Data inputs:** `GameRunning` (gate); `CarId` (unused class color). Logo from
  `window.SPONSOR_LOGOS` baked map (`AB_GC_Logo` → base64 PNG).
- **Animations:** 1200 ms fade/slide; cycle = first delay 15 s, visible 8 s,
  hidden 75 s.
- **Shared imports:** `sponsor-logos.js`, `simhub-client.js`.
- **Note:** essentially static/config-driven; trivial to port.

### 7. Pit Callout — `overlays/pit-callout.html` (~482 lines)

- **Purpose:** Event callout that slides down when any car enters/exits pit
  road, queuing rapid events, 4 s each.
- **Window:** 784×144 @ (568,164). Solid dark card (not glass) with class-color
  left stripe; columns `state-badge | identity | brand-logo`.
- **Data inputs:** `GameRawData.Telemetry.CarIdxOnPitRoad[]` (+ fallbacks),
  `CarIdxPosition[]`, `benofficial2._classes` + `Standings.{class}.Row{NN}`
  (`CarIdx`, `Name`, `Number`, `CarName`, `CarBrand`). Tracks pit-state
  transitions and position delta across a stop.
- **Animations:** `callout-enter` 1000 ms, exit 600 ms; visible 4 s.
- **Shared imports:** `simhub-client.js`, `manufacturer-logos.js`.
- **Quirks:** **manufacturer logos never show** — references
  `window.ApexManufacturerLogos` but the global is `window.MANUFACTURER_LOGOS`
  (name mismatch). `#stateLabel` text never updates (stays "STATUS"). Fix both
  in the port. No camera-hide.

### 8. Starting Grid — `overlays/starting-grid.html` (~864 lines)

- **Purpose:** Pre-race broadcast starting-grid panel (cars grouped by class,
  sorted by grid position), which becomes a **FINAL CLASSIFICATION** table
  after the race. Phase-gated, not event-driven.
- **Window:** 1100×830 @ (410,230). Header (series eyebrow + title + phase
  pill), body (class columns of 9-col rows), footer. Row-density buckets
  (normal/dense/ultra-dense) by car count.
- **Data inputs:** `_leaderboard[]` (`IsConnected`, `Name`, `CarName`,
  `CarNumber`, `PositionInClass`, `LicenceString`, `LicenceColor`, `iRating`,
  `GapToLeaderString`, `LapDifference`, `CurrentLap`); `PlayerName`;
  `WeekendInfo.SeriesID` (eyebrow via `ApexSeriesLogos.getName`); `TrackName`/
  `TrackDisplayName`; `_sessionState`/`SessionTypeName` (via race-phase).
- **Assets:** manufacturer logos, series logos, fonts.
- **Animations:** `.grid-root` fade/slide 520 ms on phase change; pulsing phase
  dot 1.8 s. Full innerHTML rebuild on data change (no per-row anim).
- **Shared imports:** `manufacturer-logos.js`, `series-logos.js`,
  `battle-mode.js` (only `classifyDriver`), `simhub-client.js`, `race-phase.js`.
- **Visibility:** shown only when `data-race-phase` is `pre-race` (STARTING
  GRID) or `post-race` (FINAL CLASSIFICATION).

### Disabled overlays (present, not launched — defer)

- **Pit Strategy** (`pit-strategy.html`, 752×124 @ 584,948): fuel/stint/tire/pit
  strip, contextual show. **Richest telemetry overlay; all fields are standard
  iRacing telemetry. Good v1+ candidate** once the data layer exists.
- **Corner Indicator** (`corner-indicator.html`, 336×76 @ 792,160): names the
  current corner from `LapDistPct` + `track-corners.js`. **Blocker: corner DB
  has only Spa.** Defer until the DB is populated for Angelo's tracks.
- **Hybrid Battery** (`hybrid-battery.html`, 440×140 @ 1480,400): SoC/deploy/
  energy for LMDh/LMH cars. **Blocker: depends on `NewData._hybrid`, a SimHub
  plugin construct.** Defer until hybrid SDK fields are confirmed in IRSDKSharper.
- **Standings Ticker** (`standings-ticker.html`, 1056×60 @ 432,160): horizontal
  scrolling class standings; the only overlay driven by `layout-director.js`.
  Shares the tower's leaderboard data. Reasonable v1+ candidate after the tower.

---

## Data layer translation

The legacy chain (iRacing → SimHub+bo2 → `server.py` reshape → `NewData.*`)
collapses, in the native build, into one C# service that reads IRSDKSharper and
exposes an observable race-state model with the same shapes overlays expect.
The shared JS engine modules (`race-phase`, `live-timing`, `battle-mode`,
`name-cache`, `story-engine`, `layout-director`, `track-names`, `track-corners`,
`*-logos`) become C# services/helpers. Mapping:

| Legacy property (`NewData.*` unless noted) | Native source | Notes |
|---|---|---|
| `Flag_Red/Yellow/White/Checkered/Black/Blue/Orange` | iRacing `SessionFlags` bitfield | Decode the bitfield into booleans. |
| `CurrentLap`, `TotalLaps`, `LapsRemaining` | SDK telemetry / SessionInfo | Lap vs. timed race detection. |
| `SessionTimeLeft`, `SessionTimeOfDay` | SDK `SessionTimeRemain`, `SessionTimeOfDay` | Bar timer; clock uses local PC time anyway. |
| `SessionTypeName` | SDK SessionInfo `SessionType` | "Race"/"Qualify"/"Practice". |
| `_sessionState` | SDK telemetry `SessionState` (enum 0–6) | Drives race-phase. See mapping below. |
| `AirTemperature`, `RoadTemperature`, `TemperatureUnit`, `IsRaining` | SDK WeekendInfo / Weather | Session-bar conditions. |
| `TrackName`, `TrackId`, `TrackDisplayName` | SDK WeekendInfo `TrackName`/`TrackDisplayName` | Resolve to broadcast name (`track-names` → C#). |
| `TrackPositionPercent` / `LapDistPct` | SDK `LapDistPct`, `CarIdxLapDistPct[]` | Corner indicator, live gaps. |
| `PlayerName`, `Position`, `CarId`, `CarModel` | SDK DriverInfo for `DriverCarIdx` | Player identity + class detect. |
| `OpponentsCount` | count of valid `CarIdx` slots | Field size. |
| `_leaderboard[]` (IREP-shaped) | **built natively** from `CarIdx*` arrays + DriverInfo | The core build. Fields per row listed below. |
| `benofficial2.Standings.ClassNN.RowNN.*` | **built natively** (same data, class-bucketed) | Replaces benofficial2 plugin entirely. |
| `_playerClass`, `_classes`, `_playerRow`, `_playerRowIndex` | derived from the native standings | Player's class/row. |
| `Highlighted` / `_spectatedCar` | `CamCarIdx` → DriverInfo lookup | Camera-focused driver (featured card). |
| `DeltaToPlayer`, `GapToLeaderString`, `RelativeGapToLeader`, `Interval` | **computed natively** (live-gap formula) | See `live-timing` port below. |
| `CarIdxOnPitRoad[]`, `CarIdxPosition[]`, `CarIdxLap[]`, `CarIdxBestLapTime[]`, `CarIdxF2Time[]` | SDK telemetry arrays (direct) | Pit callout, live timing. |
| `CarClassColor`, `LicenceString`, `LicenceColor`, `iRating` | SDK DriverInfo | Class color, license badge, iRating. |
| `CamGroupNumber` | SDK telemetry `CamGroupNumber` | Camera-hide (likely unneeded natively — see Risks). |
| `_hybrid.*` (SoC/Deploy/DeployMode/…) | **needs investigation** in IRSDKSharper | Blocks the hybrid overlay only. Defer. |
| `WeekendInfo.SeriesID`, `Category` | SDK WeekendInfo | Series name/logo. |

**Per-row `_leaderboard` fields the overlays consume** (build all of these):
`Name`, `AbbrevName`/`Initials`, `CarNumber`, `CarName`, `CarBrand`,
`ClassColor`/`CarClassColor`, `ClassName`, `LicenceString`, `LicenceColor`,
`Position`, `PositionInClass`, `CurrentLap`, `LapDistPct`, `BestLapTime`,
`LastLapTime`, `DeltaToBest`, `GapToLeaderString`, `RelativeGapToLeader`,
`Interval`, `IsConnected`, `IsInPit`, `iRating`, `LapDifference`.

**SessionState → phase mapping (from `race-phase.js`):** 0 Invalid→broadcast
(safe default), 1 GetInCar / 2 Warmup / 3 ParadeLaps → **pre-race**, 4 Racing →
**broadcast**, 5 Checkered / 6 CoolDown → **post-race**. If `SessionTypeName`
isn't a Race, force **broadcast**.

**Live-gap formula (from `live-timing.js`, replaces benofficial2 deltas):**
`progress(car) = CarIdxLap + CarIdxLapDistPct`; `gapSeconds(ahead, behind) =
(progressAhead − progressBehind) × bestLapTime(behind)` (fall back to ahead's
best lap; return null if behind is ahead — lapped edge). This is the
F1/IMSA-style position-math gap, updated every SDK tick — much smoother than
`CarIdxF2Time` (which only updates at timing-loop crossings). Natively we can run
this at the full 60 Hz.

**name-cache.js** existed only because benofficial2 showed ~15 rotating rows;
the cache accreted full-field driver info over many polls. **Natively this is
unnecessary** — IRSDKSharper's DriverInfo gives the whole field at once. Drop it.

**story-engine.js / layout-director.js** are the broadcast "director": the
director rotates tower↔ticker on a wall-clock cycle (so independent windows
agree with no IPC); the story engine detects narrative events (currently only
`LeadChange`). Natively, single-process in-memory state makes this trivial and
removes the wall-clock-sync hack. Port as a small `Director` service in Phase 3+
(only needed once both tower and ticker exist). Most of story-engine is stubs.

---

## Asset inventory

All under `../ApexHUD-Web/`. Copy into `ApexHUD-Native/assets/` during the
relevant per-overlay session (NOT in this recon session). Read-access to
ApexHUD-Web is permitted for copying.

- **Watch / clock (session 01):** `shared/assets/dial.png`,
  `shared/assets/watch.png` (+ `shared/assets/README.txt` for provenance).
- **Fonts (session 01, used by all):** `shared/fonts/` — 10 RaycerRay `.ttf`
  files (`RaycerRay-F1-Font-Display-Wide.ttf`, `…-F125-Font-SportStyle-{Light,
  Regular,ExtraBold}.ttf`, `…-SportStyleBold-{Regular,Bold}.ttf`,
  `…-Text-Medium.ttf`, `…-Timing-{Medium,Bold,Black}.ttf`). **Plus** decide on
  Cormorant Garamond + Inter (current theme, currently CDN) — see Risks.
- **Manufacturer logos (tower, featured, pit-callout, grid):**
  `assets/logos/manufacturers/` — **34 PNGs**: Acura, Alpine, Aston, Audi, BMW,
  Bentley, Buick, Cadillac, Chevrolet, Dallara, Ferrari, Ford, Holden, Honda,
  Hyundai, Jaguar, Kia, Lamborghini, Lexus, Ligier, Lotus, Maserati, Mazda,
  Mclaren, Mercedes, Nissan, Pontiac, Porsche, Radical, Renault, SkipBarber,
  Subaru, Toyota, Volkswagen. (Brand aliases: Corvette→Chevrolet, AMR→Aston,
  AMG→Mercedes.)
- **Series logos (tower, grid):** `assets/logos/series/` — **10 PNGs** incl.
  `IMSA iRacing Series.png`, `IMSA Sportscar Endurance Challenge.png`,
  `IMSA Michelin Pilot Challenge.png`, `GT3 Fanatec Challenge.png`,
  `Creventic Endurance Series.png`, etc.
- **Country flags (session bar, featured):** `assets/flags/` — **273 PNGs**,
  ISO-3166 alpha-2 names (`us.png`, `gb.png`, `be.png`, …).
- **iRacing wordmark (tower splash, brand):**
  `assets/logos/iracing-brand/iRacing-Inline-Color-White.png` (also baked in
  `shared/iracing-brand.js` and `shared/iRacing-watermark-white.svg`).
- **Sponsor (sponsor overlay):** `assets/logos/sponsors/AB_GC_Logo.png`.
- **Class-intro transition videos (tower scenes — Phase 3+):**
  `assets/transitions/OVERALL.mp4`, `GTP.mp4`, `GT3.mp4` (+ `originals/`).
  ⚠️ Video playback in SkiaSharp/WPF is a non-trivial decision — defer to polish.

---

## Recommended port order

Rationale: start with the **iconic, zero-data, lowest-risk** piece to establish
the SkiaSharp drawing + asset + window patterns, then build the **data-layer
keystone** so every subsequent overlay is developed against *live* telemetry
(not mocks), then port overlays in rough order of visual impact and how
self-contained they are. We deliberately do **not** defer all telemetry wiring
to one late "big-bang" session (see DECISIONS.md, "Data-layer-first port order").

1. **Rolex clock centerpiece** (`01`) — standalone analog watch overlay. No
   telemetry. Establishes everything reusable: SkiaSharp drawing, asset copying
   (`dial.png`/`watch.png`), fonts, the render-loop + click-through window
   pattern (already prototyped in Phase 2). Iconic, self-contained, the brief's
   chosen first port.
2. **Native data layer** (`02`) — the keystone. IRSDKSharper-backed observable
   race-state model + race-phase, class detection, live-gap, track-name
   resolution. Resolves the `Data` exe-vs-library question. Nothing data-driven
   ships before this.
3. **Session bar** (`03`) — highest screen presence; reuses the clock from 01
   and the data layer from 02. Scoped to production-visible elements only.
4. **Timing tower** (`04`) — the signature standings overlay; first heavy
   consumer of the leaderboard model. Scenes/videos deferred to polish.
5. **Featured card** (`05`) — focused-driver lower third; introduces the
   camera-target (`Highlighted`/spectated) path.
6. **Battle card** (`06`) — auto-context card; exercises live-gap + battle
   detection + phase gating.
7. **Starting grid** (`07`) — phase-gated pre-race grid + post-race
   classification; reuses the leaderboard model.
8. **Supporting callouts** (`08`) — fastest-lap + pit-callout + sponsor (three
   small overlays grouped). Fixes the legacy pit-callout logo bug.
9. **Polish pass** (`09`) — animation timing, fonts/kerning, color, cinematic
   transitions (revisit tower scene videos), multi-monitor placement, and
   anything flagged across sessions.

(Disabled overlays — pit-strategy, standings-ticker, corner-indicator,
hybrid-battery — are out of the v1 queue; pit-strategy and standings-ticker are
the strongest follow-ups once v1 lands.)

---

## Risks and open questions

1. **Fonts: bundle which family?** The current look is Cormorant Garamond +
   Inter via Google's CDN — unavailable in WPF. Options: (a) bundle Cormorant
   Garamond + Inter `.ttf`/`.otf` locally to match the current look exactly, or
   (b) adopt the 10 bundled RaycerRay broadcast fonts (more "F1", but the live
   theme abandoned them). **Decide in session 01** and record in DECISIONS.md.
   *Recommendation:* bundle Cormorant Garamond + Inter to preserve the
   established aesthetic; keep RaycerRay available for the polish pass.
2. **Leaderboard/standings build is the hard part.** benofficial2 did real work
   (per-class ordering, gap strings, pit/connection state). Rebuilding it from
   `CarIdx*` arrays + DriverInfo is the bulk of session 02 and must be correct
   before tower/grid/featured/battle. Budget accordingly; consider a focused
   unit test for the gap math and class ordering.
3. **Class-color disagreement in the legacy code** (GTP champagne `#C5A572` in
   theme.css vs. purple `#C084FC` in the tower). Pick one canonical palette in
   the data/theme layer. *Recommendation:* purple GTP `#C084FC` reads better on
   the tower; confirm with Angelo or just match the tower.
4. **Camera-hide may be unnecessary natively.** Legacy overlays faded out on
   onboard cameras because they were OBS/stream graphics. For a personal VR/rig
   HUD this may be undesirable or moot. Decide per-overlay; likely drop it.
5. **Tower scene videos** (`.mp4` class intros). Video decode in WPF/SkiaSharp
   is a real engineering choice (MediaElement layered behind the Skia surface?
   pre-rendered frame sequence?). Out of scope until the polish pass; the tower
   should work fully without scenes first.
6. **Sponsor opacity hack reversal.** The sponsor card was forced opaque to dodge
   a pywebview transparency bug WPF won't have — restore the intended glass look
   when porting.
7. **`_hybrid` telemetry** is a SimHub plugin construct; whether IRSDKSharper
   exposes equivalent SoC/deploy fields is unconfirmed. Blocks only the
   (already-deferred) hybrid overlay.
8. **Multi-monitor placement.** The legacy positions assume a specific monitor
   layout (e.g. 1936-wide bar at y=−8). DECISIONS.md already scopes Phase 2 to
   the primary monitor; revisit placement deliberately in the polish pass.

These are flagged for the sessions that hit them; the two foundational ones
(port strategy, data-layer translation) have DECISIONS.md entries as of
Session 4.

---

# GPU consolidation migration order (Phase C → WPF retirement)

*Appended 2026-05-31 (Session 28, Phase C). This section is the running plan for
moving the nine WPF transparent overlay windows onto the single native GPU canvas
(`ApexHUD.Native.GpuHost` → `OverlayCompositor`). The renderers are reused
UNCHANGED; only the window/present layer changes. See DECISIONS.md (Phase C) and
GPU_FINDINGS.md.*

## What Phase C established (the pattern every later batch follows)

`OverlayCompositor` holds a registry of **entries**, each a triple of:
- a **visibility predicate** (the same `PresentationState` / `SceneDirector` gate
  the WPF window read — e.g. `() => PresentationState.TowerVisible`),
- a **paint adapter** that positions the shared canvas at the overlay's screen
  rect and calls the existing renderer at its in-window offset, and
- animation metadata (`OnsetMs` = entrance/exit fade window to keep presenting
  through; `Continuous` = animates every frame while visible, e.g. a watch).

The host runs an **on-demand loop**: it presents only when `FrameNeeded` returns
true (a splash is active, a visibility gate flipped, a continuous overlay is
visible, or the rendered data changed within a ~3.6 s settle window) and otherwise
sleeps — static overlays cost ~0 GPU. Burst is capped to ~60 fps (never chases a
144/240 Hz panel). Adding an overlay to a batch = appending one `Entry`.

**Batch 1 (done, Session 28):** tower + splash + **featured card**. Proven: idle
at rest (15 s of zero presents in the demo log), bursts on the transition,
even-paced 30 fps splash video, GPU texture-upload seam validated on the live GL
context.

## Remaining migration batches (suggested order)

Order chosen by: (a) overlays that share a lane/gate with what's already migrated,
(b) light per-frame cost first, (c) the always-animating bar last among the
"core" set because it never lets the canvas idle (so migrate it only once the
cached-layer optimisation below exists, or accept it pins the burst rate).

- **Batch 2 — Standings ticker** (`PresentationState.TickerVisible`, full-width
  ×60 @ y=126). Shares the battle-mode gate with the tower (ticker shows when the
  tower hides), so it composes cleanly with Batch 1's gate logic. Light renderer.
- **Batch 3 — Battle card** (`PresentationState.BattleCardVisible`, 1000×280
  centred near the bottom). NOTE: in WPF the battle window also *publishes*
  `PresentationState.IsBattleActive = renderer.IsActive` every frame — the
  compositor must do this each tick (drive the battle renderer, then set the
  flag) so tower/featured/ticker keep switching correctly. This is the one
  cross-overlay signal in the suite; wire it when battle migrates.
- **Batch 4 — Starting grid** (`PresentationState.GridVisible`, 1100×830 centred,
  pre-race + post-race). Static once cross-faded → ideal cached-layer candidate.
- **Batch 5 — Supporting callouts** (fastest-lap, pit-callout, sponsor). Each owns
  an internal state machine (no `PresentationState` gate); register with
  `Visible = () => true` and let the renderer's own alpha/queue decide. Sponsor's
  own `IsActiveOrAnimating` cadence is itself an on-demand pattern.
- **Batch 6 — Session bar + watch** (`MainWindow`). Faithful port needs the
  window-resident composition glue reproduced in the adapter: `ComputeBarWidth`
  (976→full battle expansion), the `WatchArrive` entrance, and the watch
  drop-shadow `SaveLayer`. The watch sweeps continuously, so this entry is
  `Continuous = true` → the canvas never idles while the bar is visible (which is
  most of the time). Migrate it AFTER the cached-layer optimisation, or accept the
  burst runs at the watch's animation rate.

## Required scaling work before/under the later batches

**Cached static layers (requirement B — becomes load-bearing past ~3 overlays).**
On a single shared canvas every present clears and redraws *all* visible overlays
on one thread (in WPF each painted on its own window/thread, so their per-frame
costs ran in parallel). Measured in Batch 1: tower+splash alone held ~60 fps
(Phase B); adding the featured card — which allocates GPU shaders per frame in its
renderer — serialised its ~10 ms onto the frame and dropped the burst to
~30–45 fps. With 9 overlays this won't hold 60 fps. The fix: render each overlay
that is *visible but not currently animating* once into a cached GPU `SKImage`
(an offscreen surface) and **blit** it each frame; re-render only when it animates
or its content hash changes. Animating overlays (splash video, watch, a pulsing
row) draw live; static ones are a cheap blit. This is the right foundation for the
full consolidation and should land before Batch 4–6. The `GpuFrameUploader`
(Session 28) is the reusable-GPU-surface seam to build it on.

## WPF retirement (final step, a later session)

Once all nine composite faithfully on the GPU canvas and Angelo has verified a
full race live, retire the WPF transparent windows: stop spawning them in
`App.OnStartup`, keep `App`/`PresentationState`/`SceneDirector`/the renderers
(the GPU host depends on them), and make `RUN_APEXHUD_GPU.bat` the launcher. Keep
`LAUNCH_APEXHUD_OVERLAY.bat` (the WPF suite) one commit back as the rollback until
the GPU host has run a full endurance race cleanly. Then Phase D (4K validation)
and the FEATURE_BACKLOG work begin on the stable single canvas.
