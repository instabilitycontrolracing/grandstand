# ApexHUD-Native — Feature Backlog (the "make it fascinating" pass)

> Ambition: not a standard broadcast overlay — a tech-showcase that makes
> viewers lean in. Restraint is the discipline that separates *fascinating*
> from *cluttered*; every item below is judged against that.

## CRITICAL SEQUENCING NOTE
Almost everything here is **rendering/layout/animation** that touches the very
overlays being rebuilt on the GPU canvas (Phases C/D in `poc/GPU_FINDINGS.md`).
These must be **designed onto the new GPU canvas, not bolted onto the dying WPF
windows** — the sophisticated animations, scrolling headers, live mini-map, and
video sponsor card simply will not hold 60fps on the WPF software path we
measured at 15fps. The GPU rewrite is the *prerequisite* for this list, not a
detour from it.

**Doable NOW (rendering-independent — fine to start while Phase B runs):**
- Tower logo squish **bug** fix (aspect-ratio; benefits current + GPU tower).
- Manufacturer logo asset set + treatment decision.
- Driver-nationality data-availability check (iRacing SDK).
- Mini-map outline-recording feasibility spike (pure data: LapDistPct + Lat/Lon).

---

## 0. FOUNDATION — a single "motion language" (decide before animating anything)
World-class motion is **one coherent vocabulary** applied with restraint, not a
pile of per-overlay effects. Define once, inherit everywhere:
- shared easing curves, a sense of depth/parallax, staggered reveals, a signature
  entrance/exit personality;
- HARD CONSTRAINT 1 — **fast**: entrances/exits in the ~250–450ms range; a 2s
  flourish is unacceptable when a battle erupts mid-race;
- HARD CONSTRAINT 2 — **motion budget**: not everything scrolling/pulsing/
  sweeping at once. One or two focal motions at a time, the rest still.
- OPEN: define the signature (e.g. glass-slide + glow settle? panel-assemble?).

## 1. Manufacturer logos (FOUNDATION — grid, battle card, tower all depend on it)
- **Tower logos look squished — BUG.** Aspect ratio not preserved on draw (fixed
  box). Fit-and-center instead. Check whether it's the draw code or the source
  asset. DOABLE NOW; benefits the current tower and carries to GPU.
- **Updated logo set + treatment.** DECISION (Angelo): **monochrome/white marks**
  (broadcast-clean, consistent, work as watermarks) vs full-color (accurate but
  noisy). Claude's rec: mono/white. OPEN: which manufacturers (IMSA GTP + GT3
  field — Porsche, Ferrari, BMW, Cadillac, Acura, Lamborghini, Aston, Corvette,
  Mercedes-AMG, McLaren, Ford, Lexus, etc.)? Trademark note: fine for personal/
  stream use; gather transparent PNG/SVG, ideally white versions.

## 2. Starting grid (F1-style)
- **Fleshed-out cards**: larger, driver **full name**, **nationality**, car #,
  manufacturer mark featured. OPEN/RESEARCH: does the iRacing SDK expose driver
  **nationality**? If not → manual country map or drop it. Verify before
  designing a flag into the layout.
- **Cycle through the grid like F1** (no driver photos; marque-forward). TRAP:
  F1 ≈ 20 cars; an IMSA grid can be **50+**. Cycling all is tedious. DECISION:
  scope rule — all / top-N / class-leaders, and seconds-per-card. Once, pre-race
  (not looping).
- **Fade out slightly before green** so cars on pit straight aren't blocked.
  HARD PART: "before green" isn't reliably knowable (waved-off starts). Robust
  version = **position trigger** (leader past a set point on the final pace lap),
  not a green-minus-Ns timer. RESEARCH: what iRacing gives for "approaching
  green" / final pace lap.

## 3. Battle card
- **Manufacturer logo as backdrop instead of the place.** Battling cars are
  often different marques → must be a **per-driver-row watermark** (each row
  backed by that driver's marque), very low opacity or it fights text. CONFIRM:
  position number stays legible, just moves off the backdrop.
- **Expand to 3 drivers** when 2 cars are within 2s ahead/behind (same fade
  rules). DECISIONS: (1) **same-class only** — a GTP lapping a GT3 within 2s is
  NOT a battle (almost certainly yes). (2) Trigger geometry: one-ahead+one-behind
  around the target vs a same-side 3-car train — define exactly. (3) 3-row card
  is taller — check it doesn't collide with other overlays.
- **BUG to fix first (found 2026-06-01):** the battle card's "gap to car ahead"
  reads `snap.Player.IntervalSeconds`, but `Player` (like Leader/CameraTarget)
  is captured BEFORE the per-class interval fill in RaceStateService, so it
  keeps a pre-fill copy and that gap is always null → never displays. The
  featured-card fix re-resolved only CameraTarget; do the same for Player here
  (re-resolve from the finalised class buckets) as part of this pass.

## 4. Timing tower header — scrolling iRacing + series logos
White iRacing logo + series logo across the top of the tower. FLAG: constant
motion in peripheral vision during racing nags the eye. DECISION: subtle branded
band, **slow/occasional**, not a perpetual marquee. Is it scrolling because
content overflows, or for style? Claude's rec: gentle, intermittent.

## 5. Session bar — anchor contents on expand
Bar contents must **stay in their default-mode position** and not slide to the
screen edges when the bar expands. Behavior fix, no open questions. (Layout —
cleanest on the GPU canvas in Phase C; could be a quick fix on the current WPF
bar if it bugs Angelo sooner.)

## 6. Mini-map / TRACK MAP (confirmed: real track map, not a positional ladder)
Occasionally shows: the **target car's class**, from the **class leader down to
the target**; if the target IS the class leader, show the **top 10 behind** it.

**How (SimHub's method, replicated natively — no SimHub/external-file dependency):**
- iRacing gives `CarIdxLapDistPct` (0..1) for **every** car, plus the player's
  `Lat`/`Lon` and `LapDistPct`.
- Record the outline ONCE per track from a clean lap (sample LapDistPct → Lat/Lon
  → project/smooth to a 2D path), **cache per track id**. Exactly how SimHub does
  it ("drive one lap and the map generates").
- At runtime, each dot = sample the cached path at that car's `CarIdxLapDistPct`;
  color by class; filter to the class-leader→target (or target→+10) range.
- COST/CAVEAT: each track needs a one-time clean recording lap. Since Angelo
  designs on **replays**, decide how the outline gets recorded for replay design
  (record from the replay's player trace, or pre-build a small library of his
  regular tracks). RESEARCH/SPIKE: can be prototyped NOW (pure data).
- Show cadence + the entrance/exit per the motion language.

## 7. Second white watermark (next to the iRacing white watermark)
Angelo's own white mark beside the iRacing white mark. **Replays: persistent the
whole time. Races: both shown only occasionally.** NEEDS: the asset (white
version) + a race cadence number (e.g. show ~8s every ~60s?).

## 8. Sponsor card — total rework to "world-class"
Scrap the current sponsor card; rebuild as a standout. Angelo will provide
**videos** for it later. REUSE: the GPU frame-streaming tech built for the tower
transitions (background decode-ahead → GPU blit) drives video here directly.
Scope when assets arrive.

## 9. Featured card — full name + fill the space
Show the **target car driver's full name**; use the space better. Claude to bring
a **curated** info set (not max density — glanceable hierarchy wins): car #,
manufacturer mark, class, gap to car ahead, last lap / best lap, and stint/tire
age IF the SDK exposes it. OPEN: Angelo's additions + sizing preference.
(See also POLISH_BACKLOG.md: the featured-card battle-yield alpha decision.)

## 10. Animations everywhere — see §0 (Motion language)
This is the umbrella ask. It is governed by the §0 motion-language system, not
done per-overlay ad hoc. Tech-showcase feel = disciplined, fast, budgeted motion.

---

## Open decisions blocking design (Angelo to weigh in, no rush)
1. Logo treatment: **mono/white** vs color. (rec: mono)
2. Grid cycle scope on 50+ car fields: all / top-N / class-leaders + secs/card.
3. Battle 3-driver: same-class-only? + trigger geometry.
4. Tower header: subtle/occasional vs marquee. (rec: occasional)
5. Watermark race cadence + asset.
6. Motion-language signature direction.
7. Featured-card extra fields + sizing.

## Research/spikes (rendering-independent, can run now)
- iRacing SDK: driver **nationality** field? "approaching green"/final-pace-lap
  signal? stint/tire-age fields for the featured card?
- Mini-map outline recording prototype (LapDistPct + Lat/Lon → cached path).
- Manufacturer logo asset gathering (white/mono).
