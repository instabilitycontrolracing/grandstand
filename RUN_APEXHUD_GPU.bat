@echo off
REM ============================================================
REM  ApexHUD GPU overlay host - Phase C
REM  Single native GPU canvas compositing the existing renderers
REM  (timing tower + transition splash + featured card, this first
REM  batch) over iRacing, with an on-demand render loop.
REM
REM  Run iRacing in BORDERLESS-WINDOWED first (not exclusive
REM  fullscreen) so the per-pixel-alpha overlay can composite over
REM  it. Then launch this. Ctrl+Shift+Q quits.
REM
REM  No argument  -> uses LIVE iRacing telemetry when connected,
REM                  else the canned demo snapshot.
REM  "demo"       -> force the canned snapshot (full OVERALL->GTP->
REM                  LMP2->GT3 rotation + the GTP video splash),
REM                  handy when no GTP car is in the live session.
REM
REM  Cadence + on-demand idle/burst is self-logged to
REM  C:\Users\Administrator\Documents\iRacing\gpu_host_phaseC.txt
REM  (present RATE per 5s window proves the rest-state idle).
REM
REM  The WPF overlay suite (LAUNCH_APEXHUD_OVERLAY.bat) is the
REM  instant rollback and is unchanged by this.
REM ============================================================

setlocal
set HOST=%~dp0src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows\ApexHUD.Native.GpuHost.exe

if not exist "%HOST%" (
  echo Release build not found. Building...
  dotnet build "%~dp0src\ApexHUD.Native.GpuHost\ApexHUD.Native.GpuHost.csproj" -c Release -nologo
)

if /I "%~1"=="demo" (
  start "" "%HOST%" --demo
) else (
  start "" "%HOST%"
)
endlocal
