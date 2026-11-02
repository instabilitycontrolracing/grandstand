# Windows: the same bench on the owner's PC with the real assets (and real logo sizes).
#   .\tools\tower_warm\run.ps1 [-Assets "<repo>\src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows\assets"]
param([string]$Assets = "$PSScriptRoot\..\..\src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows\assets",
      [string]$GsBin = "$PSScriptRoot\..\..\src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows")
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
dotnet build -c Release -v q --nologo "-p:GsBin=$GsBin" -o bin\run | Out-Null
if (Test-Path bin\run\assets) { Remove-Item bin\run\assets -Recurse -Force }
Copy-Item $Assets bin\run\assets -Recurse
dotnet bin\run\tower_warm.dll cold
dotnet bin\run\tower_warm.dll warm
Pop-Location
