# ApexHUD-Native — World-Class Roadmap

Created 2026-10-18 with Angelo. Work down this list in order, one item per session:
build → Angelo verifies live → tick the box → next. (See CLAUDE.md working rules:
small surgical changes, one deployment + one verification at a time, never delete
or rewrite assets without Angelo's say-so.) Related detail lives in
FEATURE_BACKLOG.md, POLISH_BACKLOG.md and PORT_PLAN.md — check them before starting
an item.

## Phase 1 — Finish what's there (solid foundation)
- [x] **1. Battle card gap-to-car-ahead bug** (built 2026-10-18 - Player/Leader re-resolved from finalised class buckets; AWAITING Angelo live check: card must trigger for a car AHEAD within 2 s and show its gap) — EASY. Re-resolve Player from the - CONFIRMED LIVE by Angelo 2026-10-18 (root fix: session type now read from live telemetry SessionNum; battle_diag.log records what battle mode sees every 5 s)
      finalised class buckets (same pattern as the CameraTarget fix, FEATURE_BACKLOG Â§3).
- [x] **2. Strip diagnostic instrumentation from the GPU path** (2026-10-18: audited - nothing heavy in the GPU path. Most grep hits were "Temp"=temperature. Kept: the 5 s perf report in gpu_host_phaseC.txt (needed for item 3); BattleHistoryDirector log already off unless --bh-log.) — EASY. Most markers
      are in the retired WPF windows; only a handful live in GpuHost/Overlays
      (SessionBarRenderer, RaceStateService, RaceSnapshot, BattleHistoryDirector,
      OverlayCompositor, NativeOverlayHost). Leave WPF rollback files alone.
- [x] **3. Steady 60 fps** (DONE 2026-10-18, verified by Angelo. Profiling showed the tower CAP logo backdrop cost 19-25 ms/frame: ~40 draws of the full-size 1307 px wordmark from a mutable SKBitmap with Medium filtering rebuilt mips every draw. Fix: pre-scale once + immutable SKImage (MakeCachedImage in TimingTowerRenderer). Result: burst 27 -> 59.5 fps, >33 ms frames 56% -> 0.1%, tower 26-38 -> ~3-5 ms. Profiling timers removed.) Original idea: — MEDIUM. PORT_PLAN
      "requirement B": render static parts (tower bg, logos) to a cached layer.
      Measure present cadence before/after in a live session (gpu_host_phaseC.txt).

## Phase 2 — Broadcast features (what TV coverage has)
- [~] **4. Tower PIT WING** (BUILT 2026-10-18 in TowerRowWings.cs - STOP state seen live on D. Lapshin; awaiting Angelo's own pit-in check) (design confirmed with Angelo 2026-10-18; the standalone pit banner already exists, this is different) - a small ORANGE tab slides out from the RIGHT edge of the pitting driver's tower row: "PIT 0:24.6" live pit-lane timer, switching to stationary time while stopped; slides back in a few s after rejoin. ~0.4 s slide (match the transition wipes); tab follows the driver if the row moves.
      PitCalloutWindow to the GPU host, then add live pit-lane timer, stationary
      time, and positions lost/gained on rejoin.
- [~] **5. Tower FASTEST-LAP WING** (BUILT 2026-10-18, shares TowerRowWings; awaiting a live class-best lap) (design confirmed 2026-10-18; standalone banner already exists) - when a driver sets the CLASS fastest lap, a PURPLE tab slides out of their tower row: "FASTEST LAP 3:30.304", holds ~6 s, slides back. Same slide timing/behaviour as the pit wing (build both on one shared row-wing component).
      FastestLapWindow; class-fastest-lap lower third ("Fastest Lap â€“ #40 â€“ 3:30.304").
- [ ] **6. Overtake alerts** — small "position change" pop, own class prioritised.
- [ ] **7. Flag & incident graphics** — FCY, safety car, black flag, penalties;
      broadcast-styled and synced with the SimHub race-director voice clips.
- [ ] **8. Start & finish sequences** — grid/lights at the start (see FEATURE_BACKLOG
      Â§2 starting grid); checkered-flag class classification card with gaps + podium.
- [ ] **9. Gap trend graph** — "closing 0.4 s/lap" chart for the car ahead/behind.

## Phase 3 — Standout features
- [ ] **10. Replay auto-director** — drive iRacing's replay camera to the best battle
      automatically (broadcast-style replays).
- [ ] **11. Minimal in-headset HUD** — gap ahead/behind, position, fuel laps, shown
      in VR via DesktopXR's overlay feature (DesktopXR stays installed — don't remove).
- [ ] **12. Theme / layout switcher** — IMSA / WEC / GT World Challenge looks per series.

## Done (2026-10-17 / 18, for reference)
- [x] Auto show/hide: overlay only appears while a session is live (telemetry
      flowing); hidden on loading screens and when the sim closes. Launched by
      START_IRACING via the "ApexHUD Launch (Admin)" task.
- [x] Aston Martin Valkyrie recognised as GTP (ClassDetection keywords).
- [x] ApexHUD + iRacing both run with admin rights (required on SteamVR+Oasis+EAC);
      STOP_APEXHUD / LAUNCH_APEXHUD_OVERLAY use the admin tasks.
- [x] Log path fixed to the current user's Documents\iRacing\gpu_host_phaseC.txt.



- [x] Tower transitions: clips still play in full; soft cross-fade replaced by a LEFT->RIGHT wipe (~0.38 s in/out) with a class-colour glowing leading edge. Exit wipe shows the NEW board live (frozen tower picture skipped while wiping out). (2026-10-18, approved by Angelo)
- [x] Transition video smoothness (2026-10-18, confirmed by Angelo): (a) FrameStreamer now calls NotifyPixelsChanged() on reused pooled frames (stale GPU texture); (b) OverlayCompositor.SplashNowMs is now a forward-only clock (old formula rescaled the whole timeline when the present-interval estimate wobbled -> clip stepped back frames). Per-clip health line in gpu_host_phaseC.txt: on-time / held / BACKWARD-steps / slow-presents.



- [x] Tower row polish (2026-10-18, Angelo): (a) names never overlap the value - each name stops 10 px short of its row's measured value and DISSOLVES (soft fade, no '...') when too long; (b) position + car number share one 32 px slot, flipping every 6 s (0.28 s card-flip, all rows in sync) - names gained 22 px; 3-digit numbers auto-shrink; (c) drivers with no lap time sort to the BOTTOM of their class with a dash instead of '0'; (c) garbled accented names: iRacing's server AbbrevName arrives mojibake'd AND truncated mid-character (e.g. 'LÃ³pez, Ã' for 'Òscar López López') - when garbled it is dropped and the name is built from the correct full UserName (RaceStateService.LooksGarbled / FixMojibake).

- [x] (2026-10-18) Practice/qualify/warmup tower order = FASTEST LAP per class (rank shown as position, no-time drivers at bottom with dash); races keep live running order. Purple dot replaced by a small per-class fastest-lap TAB on the holder's row (big FASTEST LAP wing collapses into it). Pit + fastest-lap wings now sized to their content.


- [x] (2026-10-18) Pit + fastest-lap wings CONFIRMED live. Pre-race starting-order board cycles 30 s on / 30 s off (StartingGridRenderer.BoardOnMs/BoardOffMs). Notifications (pit callout, pit/fastest wings, fastest-lap callout) armed only after the green in races (PresentationState.NotificationsArmed) + pit entry requires the car to have been loaded in and on track (drivers loading into stalls no longer read as pit-ins). Three-way battle card: BEHIND | gap | YOU | gap | AHEAD, 1280 px, 2.0/2.3 s hysteresis, 320 ms morph.
- [~] STARTING GRID redesign BUILT 2026-10-18 - design B "Front Row Heroes" (GridHeroBoard.cs): one page per class cross-fading within the 30 s on-window; POLE (gold) + P2 hero cards with huge faded brand-logo backdrops, number/licence chips, car, qualifying time + gap to pole; rest of class in a balanced 2-column list; player glows. Qualifying times now carried on Driver.QualifyTime. Preview: GridTest tool + Desktop\Starting Grid Preview. Awaiting Angelo live look at a real pre-race.