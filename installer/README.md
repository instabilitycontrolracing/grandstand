# Grandstand beta installer

- `grandstand_publish.ps1` - run on the owner's PC from this repo: `dotnet publish` (Release, win-x64,
  self-contained) into `dist\Grandstand`, copies the whole `assets\` folder and checks `control\` sit next to
  the exe (the app loads them from `AppContext.BaseDirectory`), then makes `dist\Grandstand-<version>-win-x64.zip`
  and `dist\Grandstand-SHA256SUMS.txt`. Version from `VERSION` (or `-Version`).

  ```
  powershell -NoProfile -ExecutionPolicy Bypass -File installer\grandstand_publish.ps1
  ```

- `grandstand_install.ps1` + `Install Grandstand.cmd` - travel inside the zip (the .cmd sits at the zip's top
  level and runs `installer\grandstand_install.ps1`). Installs to `%LOCALAPPDATA%\Programs\Grandstand`, no admin,
  Desktop + Start Menu shortcuts, a "Grandstand Control" link (http://127.0.0.1:8790/), an Apps & features entry
  and an uninstaller. Re-running = update.
- `grandstand_uninstall.ps1` - removes the program and shortcuts; asks before deleting the user's settings in
  `Documents\iRacing\Grandstand`, `Documents\iRacing\ApexHUD\maps`, `%LOCALAPPDATA%\Grandstand`,
  `%LOCALAPPDATA%\ApexHUD\globe` (the owner's dev copy uses the same folders - answer no on the owner's PC).

The full plain-English guide (both apps) is `installer/README.md` in the Instability Control repo.

## Personal assets - never shipped (2026-10-24)

The publish script leaves these out and FAILS if one is still in the package (rules: `$Personal` in
`grandstand_publish.ps1`, kept in step with `.gitignore`): `assets\transitions\frames` (IMSA footage),
`logos\sponsors\car_*_panel.png` + `car_bmw_m_hybrid_v8.png` (press photos), `logos\sponsors\ab_roof_*`,
`storm_radar_*`, `AB_GC_Logo.png`, `gofast_logo.png`, `tradingpaints_logo.png` (the owner's sponsors),
`assets\audio` (sounds of unconfirmed origin), every underscore-prefixed folder, `clock\dial.png`, and
`fonts\RaycerRay*` (licence not confirmed), and since 2026-10-28 `logos\series` (series marks; the app shows the
series name instead), any `corner_logos` / `michelin_run` / `glyphs <brand>` folder and any `sponsors.json`. The release replaces them with the built-in animated class
transition, drawn car panels, neutral sponsor defaults ("YOUR BRAND HERE") and `assets\audio_public`
(original, CC0, `tools\make_sfx.py`). It also checks every bundled font has its OFL licence
(`assets\licenses`) and prints a "Public assets" summary.

## Third-party marks - replace before any PUBLIC release

The default logos/marks are placeholders for personal use and a private beta. Folders under `assets\` that hold
other companies' marks or material: `logos\manufacturers`, `logos\series`, `logos\classes`, `logos\sponsors`
(built-in partner defaults: GO FAST, Trading Paints, StormExhibit, A&B General Contracting), `logos\iracing-brand`,
`logos\tracks`, `fonts` (RaycerRay - check its licence; Inter/Cormorant/Barlow are OFL), `audio` (opener theme -
check it is yours to share), `transitions\frames`.

## Known owner-PC-only paths (harmless elsewhere)

`StoryCards.cs` test trigger `C:\SimRacing\Tools\story_test.txt`, `GameDuck.cs` stream flag
`C:\SimRacing\Tools\Stream4K\streaming.flag` (absent = not streaming), `HeroShot.cs` output path
(only with `--hero-shot`), `ApexHUD.Native.Probe` (not shipped).
