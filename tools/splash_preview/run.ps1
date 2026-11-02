# TV loading splash preview - Windows. Uses the build's assets (fonts, maps, clock\dial.png = his watch face) and his
# Documents\iRacing\Grandstand (clock.json). Output: Documents\iRacing\Grandstand\preview_r2\splash
param(
  [string]$GsBin = "$PSScriptRoot\..\..\src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows",
  [string]$Map = "",
  [string]$Out = "$([Environment]::GetFolderPath('MyDocuments'))\iRacing\Grandstand\preview_r2\splash"
)
$ErrorActionPreference = "Stop"
$GsBin = (Resolve-Path $GsBin).Path
$run = Join-Path $PSScriptRoot "bin\run"
dotnet build "$PSScriptRoot\splash_preview.csproj" -c Release -v q --nologo "-p:GsBin=$GsBin" -o $run
if ($LASTEXITCODE -ne 0) { throw "build failed" }
if (Test-Path "$run\assets") { Remove-Item "$run\assets" -Recurse -Force }
Copy-Item (Join-Path $GsBin "assets") "$run\assets" -Recurse
if (-not $Map) { $Map = Join-Path $GsBin "assets\maps\Circuit des 24 Heures du Mans.json" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
dotnet "$run\splash_preview.dll" $Out $Map
