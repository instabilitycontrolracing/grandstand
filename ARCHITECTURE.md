# ApexHUD-Native Architecture

## Stack

| Layer | Choice | Rationale |
|---|---|---|
| Language | C# / .NET 8 | Industry standard for native Windows. Long support window. Excellent tooling. |
| UI framework | WPF (Windows Presentation Foundation) | Battle-tested. What SimHub uses. Native transparency. GPU compositing. |
| 2D graphics | SkiaSharp | GPU-accelerated 2D rendering. Same engine Chrome/Android/Flutter use. Mature C# bindings. |
| iRacing data | IRSDKSharp (NuGet) | Direct access to iRacing's shared-memory telemetry. 60 Hz, microsecond latency. Bypasses SimHub entirely for time-critical data. |
| SimHub data (optional) | Existing HTTP endpoints | For CrewChief properties and SimHub-derived data, we keep using the same /api/getproperty calls. Not time-critical. |
| Persistence | LiteDB | Single-file embedded NoSQL. For session history, lap records, post-race chronicle data. |
| Animations | SkiaSharp.Skottie (Lottie player) | Designer-friendly motion graphics. Free, performant. |
| Build | dotnet CLI | No Visual Studio required for builds. Claude Code can drive everything from terminal. |
| AI director (Phase 2+) | Phi-3 / Phi-4 mini via ONNX Runtime DirectML | Runs on Angelo's 3080 Ti. Local inference. The unique feature. |

## Process model

**Single process, multiple windows.** The entire system runs in one .NET
process. WPF lets us spawn multiple transparent click-through windows that
share state via in-process events. No HTTP. No IPC. No separate launcher.
No watchdog needed.

Compare to ApexHUD-Web which runs:
- 1 Python launcher process
- 1 Python HTTP server
- 4 WebView2 main processes
- ~9 WebView2 child processes (GPU, network, storage, etc.)
- 1 Python watchdog
- Total: ~16 processes

ApexHUD-Native runs:
- 1 .NET process
- Total: 1 process

## Data flow

```
iRacing (process)
   |
   v  shared memory (60 Hz)
IRSDKSharp (in-process library)
   |
   v  C# events
ApexHUD-Native.DataLayer
   |
   v  observable properties
ApexHUD-Native.Overlays  (WPF windows binding to data)
```

There is no HTTP. There is no JSON. There is no polling. Data arrives at
60 Hz and overlays bind to it directly.

## Project structure

```
ApexHUD-Native/
â”œâ”€â”€ PROJECT.md              # Vision (this file's sibling)
â”œâ”€â”€ ARCHITECTURE.md         # This file
â”œâ”€â”€ DECISIONS.md            # Why we chose what we chose
â”œâ”€â”€ JOURNAL.md              # Per-session notes
â”œâ”€â”€ docs/                   # Design references, screenshots
â”œâ”€â”€ assets/                 # Images, fonts, lottie files
â”œâ”€â”€ src/
â”‚   â”œâ”€â”€ ApexHUD.Native.sln              # Solution file
â”‚   â”œâ”€â”€ ApexHUD.Native.Data/            # IRSDK + state management (class library)
â”‚   â”œâ”€â”€ ApexHUD.Native.Overlays/        # WPF overlay windows (executable)
â”‚   â””â”€â”€ ApexHUD.Native.Common/          # Shared types, helpers (class library)
â””â”€â”€ tests/
    â””â”€â”€ ApexHUD.Native.Tests/           # Unit tests for algorithms
```

## Build & run

From the `src/` directory:

```
dotnet build
dotnet run --project ApexHUD.Native.Overlays
```

## What we are deliberately not doing

- **No web tech.** Not even Electron. Not even a hidden WebView.
- **No plugin system.** Hardcode everything. The audience is two people.
- **No cloud sync.** State lives on this machine.
- **No installer.** It runs from a folder. Copy the folder, it works.
- **No cross-platform.** Windows only. iRacing is Windows only.
- **No theming engine.** It looks the way it looks. Code changes if we change our minds.
