# Polish backlog

Cosmetic and small-tweak items Angelo flagged during port reviews,
batched for execution in Session 09 (polish pass) rather than
interrupting the port queue.

Each item gets a one-line description. Session 09 reads this whole
file and works through it top to bottom.

---

## From Session 03 review (session bar)

- ~~**Bar window clips the top of the watch.**~~ **Resolved 2026-05-29
  (Session 09).** `MainWindow.BarPlateTop` shifted from 14 → 34, so the
  watch (radius 64, centre y=80) now has ~16 px of breathing room above
  its bezel. Window size unchanged at primary-monitor-wide × 230 px @
  (0, 0). See DECISIONS.md "2026-05-29 — Bar window: bar plate dropped
  20 px so the watch doesn't clip". Confirm at next live launch.

## From Session 13 / live on-rig review (battle-mode choreography)

- **Featured card during a battle still fades to 10 %.** Session 14
  did not touch that path — Angelo's brief listed bar/tower/battle/ticker
  as the four windows to re-choreograph, and the featured card's existing
  10 % yield (Port 06) was left in place. If during a live battle the
  featured-card silhouette reads cluttered alongside the new ticker,
  consider either dropping its battle-yield alpha to 0 or hiding it
  entirely. Decide after the first live battle eyeball pass.

## From Session 05 review (featured card)

- **TrackCorners only covers Spa.** The legacy data only had corner
  names for Spa-Francorchamps, so the new native lookup inherits the
  same limitation. Not a bug, an inheritance — but if Angelo races at
  Le Mans, Sebring, Daytona, Watkins Glen, or anywhere else, the
  featured card will fall back to plain LapDistPct percentage instead
  of "approaching Eau Rouge". Future expansion: populate corner names
  for Angelo's regular tracks. Low priority unless the corner-name
  callout starts feeling missed.
