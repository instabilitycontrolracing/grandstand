# ApexHUD-Native

A native Windows broadcast overlay system for iRacing, built for personal use
by Angelo and his wife to enjoy together.

## What this is

A handcrafted, beautiful broadcast graphics system that turns iRacing into the
kind of viewing experience you'd see on a real-world endurance racing broadcast.
The Rolex clock anchoring the top of the screen. The timing tower on the left.
The session bar across the top. Battle cards. Starting grids. Final
classifications. All rendered natively, smoothly, beautifully â€” the way SimHub
feels, with the visual polish of an IMSA Radio Le Mans broadcast.

## What this is not

- A product. We are not shipping this to anyone.
- A service. No cloud, no telemetry, no analytics.
- A configurable platform. It does what we want it to do, the way we want it
  to look. Customization is a feature for software meant to please everyone;
  this is meant to please two people.
- A replacement for ApexHUD-Web (the legacy browser-based system) until it
  surpasses it. The old system continues to live in a sibling directory and
  is the daily driver until this one is unquestionably better.

## Who this is for

Angelo, who races iRacing in VR. His wife, who watches. Nobody else. The
audience of two is the design constraint that makes everything else simpler.

## The feeling this should evoke

Sitting down to watch a real-world endurance race on a Saturday afternoon.
The polish of Eurosport's Le Mans coverage. The visual language of Formula 1's
broadcast graphics. The quiet dignity of a Rolex Cosmograph Daytona on the
dashboard.

When the system is running well during a race, the screen should feel less
like "iRacing with overlays" and more like "a broadcast of a race I happen to
be in." The overlays are not the point; they support the point, which is the
race.

## Why we are rebuilding

The current ApexHUD-Web is four full browser windows running on top of
iRacing. It works. It looks good. But:

- It uses 13+ GB of RAM during a race
- It leaks memory over hours, threatening endurance races
- Restarts cause visible system pauses
- It will never match the smoothness of native graphics

A native rebuild using the same technology stack that SimHub uses (WPF + GPU
rendering) gets us to ~150 MB RAM, sub-second startup, and no leaks. It also
opens up the things the browser version cannot do: a narrative-aware director
system, a printable race chronicle for Angelo's wife, replay bookmarking, and
cinematic transitions.

## Core principles

1. **Beauty before features.** A small set of overlays that look stunning
   beats a large set that look adequate.
2. **Native or nothing.** No web technology. No browsers. No HTML.
3. **The repo is the memory.** Every architectural decision is documented.
   Every session note is committed. Future sessions read the docs first.
4. **Opinionated, not configurable.** It looks the way it looks. We change
   the code, not a settings panel.
5. **Le Mans is sacred.** ApexHUD-Web stays as the daily driver until the
   native version is provably better. No mid-project switchovers.

## Status

Started: 2026-05-27
Phase: 0 â€” Setup
Daily driver: ApexHUD-Web (sibling directory)
