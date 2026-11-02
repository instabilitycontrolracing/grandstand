# TV session review preview - Windows (2026-10-27). Renders SessionReviewShow (with the #336 watch) from a real .ibt
# using the build's assets (his dial in assets\clock) and his Documents\iRacing\Grandstand (terrain, clock.json).
#   powershell -ExecutionPolicy Bypass -File tools\session_review_preview\run.ps1 -Ibt "<file.ibt>" [-Keys 6,30] [-Bench]
param(
  [Parameter(Mandatory = $true)][string]$Ibt,
  [string]$GsBin = "$PSScriptRoot\..\..\src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows",
  [string]$Landmarks = "",
  [string]$Keys = "6,30",
  [switch]$Bench,
  [string]$Out = "$([Environment]::GetFolderPath('MyDocuments'))\iRacing\Grandstand\preview_r2\session_review"
)
$ErrorActionPreference = "Stop"
$GsBin = (Resolve-Path $GsBin).Path
$run = Join-Path $PSScriptRoot "bin\run"
dotnet build "$PSScriptRoot\session_review_preview.csproj" -c Release -v q --nologo "-p:GsBin=$GsBin" -o $run
if ($LASTEXITCODE -ne 0) { throw "build failed" }
if (Test-Path "$run\assets") { Remove-Item "$run\assets" -Recurse -Force }
Copy-Item (Join-Path $GsBin "assets") "$run\assets" -Recurse
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$a = @($Ibt, $Out, "--keys", $Keys)
if ($Landmarks) { $a += "--landmarks"; $a += $Landmarks }
if ($Bench) { $a += "--bench" }
dotnet "$run\session_review_preview.dll" @a
