# GPU overlay — diagnosis & validated path (Session 26)

## TL;DR
The timing-tower video transitions were clunky. Root cause was **not** our
drawing or decoding — it was the **present path**: WPF software-composited
transparent windows (`AllowsTransparency=true` forces software compositing, and
`SKElement` is CPU-only) reached the screen only ~15 fps over iRacing. A GPU-
composited overlay on its own render thread was proven to hold **~58 fps at
1920×1080 over a live replay**. Decision: rebuild the overlay window layer on a
native GPU (OpenGL + SkiaSharp) transparent-overlay host. Drawing/renderers stay.

## How we got here (measured, not guessed)
1. Phase 2.1 moved JPEG decode off the render thread → no change.
2. Phase 2.2 removed the per-frame offscreen `SaveLayer`, cached the scrim
   shader, reused paints → no change. This proved per-frame **draw cost was
   fine** (instrumented: ~8 ms/frame against a 33 ms budget).
3. Instrumented the splash window's actual cadence:
   - render cadence ~59–67 ms (≈15 fps), wildly variable (max ~104–138 ms)
   - draw cost avg ~8 ms, max ~29 ms (under budget)
   - UI-thread availability probe: avg ~24 ms with spikes to ~126 ms
   → dominant bottleneck = **present/compositor path** (Cause A), with a
     secondary **UI-thread contention** problem (Cause B, the 126 ms spikes).
4. Research (MS WPF docs + SkiaSharp maintainers):
   - `AllowsTransparency=true` per-pixel layered windows are **software-
     composited, no hardware acceleration** — by design, still true today.
   - SkiaSharp's WPF view (`SKElement`) is **CPU-only**; its GPU path does not
     work inside a transparent WPF window. So the GPU overlay **cannot** live in
     a WPF `AllowsTransparency` window — the window host itself must change.

## The POC (poc/ApexHUD.GpuPoc) — validated
A standalone native GPU overlay using **OpenGL + SkiaSharp 3.119.4** via the
**OverlaySharp** library (MIT), attached to iRacing's window, presenting on its
own render thread (not WPF). Played the real GTP frames over a live replay.

Result (gpu_poc.txt, 1920×1080, live replay, GPU loaded):
- 874 frames / 15 s ≈ **58 fps**
- target correctly chosen: "iRacing.com Simulator" 1920×1080 @ (0,0)
- per-pixel alpha confirmed (15%-opacity tint showed the replay through it)
- vs the WPF path's ~15 fps → ~4× improvement, smooth to the eye

Known POC rough edges (not blockers): OverlaySharp's `StopOverlayAsync()` can
hang on teardown (guarded with a timeout in the POC); window targeting must skip
the hidden launcher stub at (-32000,-32000) and pick the visible, large window.

## Decision
Rebuild the overlay **window/present layer** on a native GPU transparent-overlay
host (OpenGL + SkiaSharp). **Keep** every existing renderer — they draw to an
`SKCanvas`, which we will hand them from a GPU-backed surface instead of a WPF
software `SKElement`. This also lets us **consolidate** the nine separate WPF
transparent windows into a single GPU canvas, which fixes the secondary UI-
thread contention (Cause B) at the same time.

Open design choice for production (decide at Phase B): **adopt OverlaySharp** as
the host vs **own the native GL interop** ourselves (a few hundred lines of
Win32 + WGL + Skia-GPU, now known to work). Leaning toward owning it, because
this is a tool Angelo depends on and the renderers need a raw `SKCanvas`, which
OverlaySharp wraps behind its own `IGraphics`. The POC proves the approach
either way.

## Staged conversion plan
- **Phase A — validate GPU present cadence.** DONE (~58 fps).
- **Phase B — single GPU overlay host.** Build one native GPU transparent
  window exposing a raw `SKCanvas`; render the timing tower + splash through it
  (reusing the existing renderers unchanged). Run alongside/instead of the WPF
  tower; confirm 60 fps live. The teardown + targeting quirks get solved here.
- **Phase C — consolidate.** Move the remaining overlays (session bar, watch,
  featured/battle cards, ticker, grid, sponsor, fastest-lap, pit) onto the one
  GPU canvas, retiring the WPF transparent windows. Kills Cause B.
- **Phase D — 4K + polish.** Validate at 4K (where the WPF path would have
  scaled *worse*), tune, retire the WPF overlay.

## 4K note
The WPF software-present cost scales with window area, so 4K would have made the
old path worse. The GPU path composites on the GPU and is built for it — but
Angelo has no 4K display yet, so Phase D 4K validation is deferred until one is
available. The POC and plan are 4K-ready by design.
