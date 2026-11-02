using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Draws the Timing Tower — the signature full-height standings strip down the
/// left edge of the screen, grouped by class. Port of the production-visible
/// parts of the legacy <c>timing-tower.html</c> (PORT_PLAN §2): the deep-glass
/// card, the track-name header, the per-class headers + dividers, and the
/// 9-column driver rows (stripe / pos / arrow / num / logo / lic / name /
/// interval / gap) with leader/player/pit/fastest/disconnected highlights,
/// proximity battle glow, lap pulse, and PB pulse.
///
/// <para>Deliberately out of scope (PORT_PLAN §2 + the Port 04 brief): scene
/// rotation + class-intro <c>.mp4</c> transitions, gap-mode rotation,
/// session-progress bar, lap counter in the header, iRacing wordmark / series
/// chip. The header in production is just the track name.</para>
/// </summary>
public sealed class TimingTowerRenderer : IDisposable
{
    // Tower card geometry. Width matches the legacy 400 px tower; height fills
    // whatever the host gives us via Draw.
    public const float TowerWidth = 400f;
    public const float HeaderHeight = 30f;
    public const float CapHeight = 90f;
    public const float ClassHeaderHeight = 22f;
    public const float RowHeight = 40f;
    public const float ContentPadRight = 12f;
    public const float ContentPadLeft = 0f; // stripe is the left edge

    // Row column layout. The trailing two value columns (legacy
    // ColInterval 64 + ColGap 82) are collapsed into a single 92 px
    // right-aligned ColValue (Session 22). The freed 54 px goes to the
    // 1fr name column, which now breathes considerably more than the
    // legacy 84-ish px window.
    public const float ColStripe   = 3f;
    public const float ColPos      = 22f;
    public const float ColArrow    = 11f;
    public const float ColNum      = 32f;
    public const float ColLogo     = 26f;
    public const float ColLic      = 20f;
    public const float ColValue    = 92f;

    // Right-edge cutoff for the driver name. Deliberately SMALLER than ColValue
    // so a truncated name runs close to the gap number instead of stopping a
    // full value-column away. Short values (race gaps, LEADER) still clear it;
    // wide values (the 10 s lap-time flash, big intervals) are drawn on top and
    // cover the name's tail by design. No font sizes change. Set to 48 − width
    // of "OS" (≈39.3 px at the 27.73 name size) so names run right up to the
    // value number per request; the longest names overlap their gap (rare), and
    // the lap-time flash fades the name's tail under the time (see DrawRowColumns).
    public const float NameRightReserve = 8.71f;

    // Effects + thresholds (ported verbatim from the legacy CSS / JS):
    const float BattleThresholdSec   = 1.0f;     // gap below this -> battle glow
    const float BattleCloseSec       = 0.3f;     // gap below this -> close-battle pulse
    const float ArrowTtlMs           = 3500f;    // position-change arrow fades after 3.5s
    const float LapPulseMs           = 900f;     // lap-completed white pulse window
    const float PbPulseMs            = 1300f;    // personal-best green pulse window
    const float ClosePulseSec        = 1.6f;     // close-battle brightness pulse period
    const double ValidLapFlashMs     = 10_000;   // valid-lap flash: hold last lap time

    // --- palette (built once) ---
    static readonly SKColor BgGlass         = ParseHex(BroadcastPalette.TowerGlassBase, 209);  // ~0.82 alpha
    static readonly SKColor BgRowOdd        = new(255, 255, 255, 1);   // ~0.003 alpha
    static readonly SKColor BgRowEven       = new(255, 255, 255, 4);   // ~0.014 alpha
    static readonly SKColor BorderHairline  = ParseHex(BroadcastPalette.BorderHairline, 92);
    static readonly SKColor TextPrimary     = ParseHex(BroadcastPalette.TextPrimary);
    static readonly SKColor TextSecondary   = ParseHex(BroadcastPalette.TextSecondary);
    static readonly SKColor TextTertiary    = ParseHex(BroadcastPalette.TextTertiary);
    static readonly SKColor LeaderGold      = ParseHex(BroadcastPalette.Leader);
    static readonly SKColor PlayerBlue      = ParseHex(BroadcastPalette.Player);
    static readonly SKColor PlayerStripe    = ParseHex(BroadcastPalette.PlayerStripe);
    static readonly SKColor FastestPurple   = ParseHex(BroadcastPalette.FastestPurple);
    static readonly SKColor PitAmber        = ParseHex(BroadcastPalette.PitAmber);
    static readonly SKColor BattleOrange    = new(0xFF, 0x9F, 0x40);  // legacy battle gradient
    static readonly SKColor PbGreen         = new(0x4A, 0xDE, 0x80);
    static readonly SKColor LossRed         = new(0xF8, 0x71, 0x71);

    // Static paints
    readonly SKPaint glassPaint;
    readonly SKPaint hairlinePaint;
    readonly SKPaint rowOddPaint;
    readonly SKPaint rowEvenPaint;
    readonly SKPaint numBgPaint;
    readonly SKPaint numTextPaint;
    readonly SKPaint licTextPaintLight;
    readonly SKPaint licTextPaintDark;

    // Manufacturer logo cache, keyed by brand slug. Lives for process lifetime.
    readonly Dictionary<string, SKBitmap?> logoCache = new(StringComparer.OrdinalIgnoreCase);

    // iRacing wordmark header backdrop: lazy-loaded inline logo + reusable paint.
    SKBitmap? iracingWmLogo;
    bool iracingWmTried;
    readonly SKPaint wmPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    SKBitmap? seriesCapLogo;
    int seriesCapLogoId = -2;
    readonly SKPaint capLogoPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };

    // Per-car render state that lives across snapshots: previous position
    // (drives the arrow), last lap completed (drives the lap pulse), last best
    // (drives the PB pulse). Replaces the legacy JS row attributes the CSS
    // selectors keyed off.
    readonly Dictionary<int, RowState> rowStateByCarIdx = new();

    // 2026-10-24 (Angelo): true while the tower is showing a PIT STOPS page (SceneFocus.Pits) - rows then show
    // how many stops each car has made and on which laps instead of the gap.
    bool pitBoard;
    readonly SKPaint pitCountPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 18f, TextAlign = SKTextAlign.Right };
    readonly SKPaint pitUnitPaint  = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 12.5f, TextAlign = SKTextAlign.Right };   // #340: was 9.5
    readonly SKPaint pitLapsPaint  = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 13.5f, TextAlign = SKTextAlign.Right };   // #340: was 11.5

    // Outer presentation gate: the tower is only visible during the broadcast
    // phase, after the overall leader has finished lap 1, AND when no battle
    // is active (the standings ticker takes over the standings lane during a
    // battle). Drives a 220 ms cross-fade so the swap reads as a deliberate
    // cut, not a snap or a lingering dim. Single source of truth lives in
    // PresentationState; we just track the boolean across frames here to
    // schedule the fade.
    bool wasGated;
    double gateFlipAtMs = -1;
    const double GateFadeAnimMs = 220;

    // ---- 2026-10-29 (efficiency): what the rows used to create every frame (a new SKPaint per text / stripe / badge,
    // a text blob per DrawText, a gradient per highlighted row, the formatted gap / name strings) is kept between frames.
    // Each kept paint gets exactly the properties the old "new SKPaint { ... }" had, set on every use, so the pixels are
    // the same (checked with tools/race_frame's pixel hashes); text goes through TextBlobs (same blob as DrawText).
    readonly SKPaint statVal = new() { Color = TextPrimary, IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };
    readonly SKPaint statLab = new() { Color = TextTertiary, IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };
    readonly SKPaint aaShaderPaint = new() { IsAntialias = true };              // { IsAntialias = true, Shader = ... }
    readonly SKPaint solidPaint = new() { IsAntialias = false };                // { Color = ... } / { IsAntialias = false, Color = ... }
    readonly SKPaint playerInsetPaint = new() { IsAntialias = false, Color = PlayerStripe };
    readonly SKPaint playerBorderPaint = new() { IsAntialias = false, Color = PlayerStripe.WithAlpha(76), Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    readonly SKPaint classNamePaint = new() { Color = LeaderGold, IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14f };
    readonly SKPaint classCountPaint = new() { Color = TextTertiary, IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 13f, TextAlign = SKTextAlign.Right };
    readonly SKPaint posPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 24.2f, TextAlign = SKTextAlign.Center };
    readonly SKPaint arrowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    readonly SKPath arrowPath = new();
    readonly SKPaint logoPaint = new() { FilterQuality = SKFilterQuality.High, IsAntialias = true };
    readonly SKColorFilter?[] logoAlphaFilters = new SKColorFilter?[256];
    readonly SKPaint namePaint = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 27.73f };
    readonly SKPaint fadeMaskPaint = new() { BlendMode = SKBlendMode.DstIn };
    readonly SKPaint pitTagPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f, TextAlign = SKTextAlign.Right };
    readonly SKPaint leaderTagPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13.5f, TextAlign = SKTextAlign.Right };
    readonly SKPaint valueTextPaint = new() { IsAntialias = true, TextAlign = SKTextAlign.Right };
    readonly SKPaint sectorPaint = new() { IsAntialias = true };
    readonly ShaderSlot dividerShader = new(), playerShader = new(), leaderShader = new();
    readonly Dictionary<int, ShaderSlot> nameFadeShaders = new();
    readonly Dictionary<string, SKBitmap?> brandLogos = new(StringComparer.Ordinal);   // render thread: brand -> LoadLogo(brand)
    readonly List<int> rowPick = new();
    static readonly float[] StopsDivider = { 0f, 1f }, StopsPlayer = { 0f, 0.4f, 1f }, StopsLeader = { 0f, 0.6f, 1f },
                            StopsGlow = { 0f, 0.4f, 0.85f, 1f }, StopsFade = { 0f, 1f };
    // formatted strings: same text as before, made once per distinct value (bounded)
    readonly Dictionary<(string?, string?), string> nameText = new();
    readonly Dictionary<double, string> gapText = new();
    readonly Dictionary<int, string> lapsDownText = new(), carsText = new(), pitStopsText = new();
    readonly Dictionary<double, string> deltaText = new();
    readonly Dictionary<float, string> lapText = new();
    readonly Dictionary<string, string> upperText = new(StringComparer.Ordinal);
    string? sessionLabelIn, sessionLabelOut; int sofValue = int.MinValue; string sofText = "";

    static string Cached<TK>(Dictionary<TK, string> d, TK key, Func<TK, string> make) where TK : notnull
    {
        if (d.TryGetValue(key, out var v)) return v;
        if (d.Count > 2048) d.Clear();
        return d[key] = make(key);
    }

    public TimingTowerRenderer()
    {
        glassPaint    = new SKPaint { IsAntialias = true, Color = BgGlass };
        hairlinePaint = new SKPaint { IsAntialias = false, Color = BorderHairline, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        rowOddPaint   = new SKPaint { IsAntialias = false, Color = BgRowOdd };
        rowEvenPaint  = new SKPaint { IsAntialias = false, Color = BgRowEven };

        numBgPaint    = new SKPaint { IsAntialias = false, Color = SKColors.White };
        numTextPaint  = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(8, 10, 16, 242),
            Typeface = Fonts.Inter.Bold,
            TextSize = 15.4f,
            TextAlign = SKTextAlign.Center,
        };
        licTextPaintLight = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White,
            Typeface = Fonts.Inter.Bold,
            TextSize = 14.3f,
            TextAlign = SKTextAlign.Center,
        };
        licTextPaintDark = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0x0A, 0x0E, 0x14),
            Typeface = Fonts.Inter.Bold,
            TextSize = 14.3f,
            TextAlign = SKTextAlign.Center,
        };
    }

    /// <summary>
    /// Draw the tower at <paramref name="originX"/>,<paramref name="originY"/>
    /// inside an area of <paramref name="availableHeight"/>. The renderer
    /// no-ops when the race phase is pre-/post-race (the starting-grid + final
    /// classification overlays take over those phases — Port 07).
    /// </summary>
    public void Draw(
        SKCanvas canvas,
        float originX,
        float originY,
        float availableHeight,
        RaceSnapshot snapshot,
        double elapsedMs)
    {
        // Disconnected: render the empty glass card so we don't pop on/off.
        if (!snapshot.IsConnected)
        {
            DrawCard(canvas, originX, originY, availableHeight, elapsedMs);
            return;
        }

        // Single visibility gate: PresentationState.TowerVisible folds the old
        // phase check, the lap-1-complete latch, and the battle-active signal
        // into one boolean. Cross-fade in/out so the swap to the ticker reads
        // as a deliberate cut, and skip painting entirely once fully hidden.
        float gateAlpha = ComputeGateAlpha(elapsedMs);
        if (gateAlpha <= 0.01f) return;

        bool needsLayer = gateAlpha < 0.999f;
        if (needsLayer)
        {
            gateLayerPaint.Color = SKColors.Black.WithAlpha((byte)(gateAlpha * 255));
            // 2026-10-27 (#44): the fade layer covers the tower lane (+ its wings), not the whole 4K screen
            canvas.SaveLayer(LayerBounds(originX, originY, availableHeight), gateLayerPaint);
        }
        DrawBody(canvas, originX, originY, availableHeight, snapshot, elapsedMs, withWings: true);
        if (needsLayer) canvas.Restore();
    }

    readonly SKPaint gateLayerPaint = new();

    /// <summary>Everything the tower draws: the card, the moving rows' shadows and the wings on its right edge.</summary>
    static SKRect LayerBounds(float originX, float originY, float availableHeight) =>
        new(originX - 16f, originY - 16f, originX + TowerWidth + TowerRowWings.WingWidth + 48f, originY + availableHeight + 16f);

    void DrawBody(SKCanvas canvas, float originX, float originY, float availableHeight, RaceSnapshot snapshot, double elapsedMs, bool withWings)
    {
        DrawCard(canvas, originX, originY, availableHeight, elapsedMs);
        DrawCap(canvas, originX, originY, snapshot, elapsedMs);
        DrawHeader(canvas, originX, originY + CapHeight, snapshot, elapsedMs);

        // Row wings (roadmap 4+5): advance pit / fastest-lap state, then collect
        // row positions during the row loop below and draw after it.
        if (withWings) { wings.Update(snapshot, elapsedMs); wings.BeginFrame(); }

        float cursorY = originY + CapHeight + HeaderHeight;
        float bottom = originY + availableHeight;

        // Session-best lap (the purple bullet) — scan once per frame across all
        // classes for the minimum positive BestLapTime.
        float sessionBest = ComputeSessionBest(snapshot);

        // Session-type switch (Session 22): drives the value-column default
        // — race rows show gap-to-ahead-in-class with LEADER at the top,
        // practice/quali rows show the leader's fastest lap and everyone
        // else's delta to ahead-in-class on best lap.
        bool isRace = LooksLikeRace(snapshot.SessionTypeName);

        // Scene rotation focus filter (Phase 1): when SceneDirector is on a
        // class-focus board, draw only that class's section; on OVERALL,
        // iterate every class as before. Purely additive — the loop body and
        // every per-row/per-class drawing path are unchanged.
        var scene = SceneDirector.CurrentScene;
        pitBoard = scene.Focus == SceneFocus.Pits && scene.ClassName != null && isRace;
        bool focusOne = (scene.Focus == SceneFocus.Class || pitBoard) && scene.ClassName != null;
        string sceneKey = pitBoard ? "PITS:" + scene.Label : scene.Label;

        // Group iteration: legacy order is top-class first (lowest leader
        // overall position). The snapshot already orders ClassLeaderboard that
        // way, so iterate in order.
        // 2026-10-18 (Angelo): OVERALL board no longer spills off the bottom (which
        // cut later classes off entirely). Rows that fit are shared FAIRLY across
        // classes: equal slices, a class with fewer cars than its slice gives the
        // leftover to the others. The player's own row is always kept: if they're
        // outside their class's slice, the last slot shows them. Class pages
        // (focusOne) are unchanged and show the full class.
        Dictionary<string, int>? quota = null;
        if (!focusOne)
            quota = QuotasFor(snapshot.ClassLeaderboard, bottom - cursorY);

        bool firstClass = true;
        var classList = snapshot.ClassLeaderboard;
        for (int cIdx = 0; cIdx < classList.Count; cIdx++)   // indexed: foreach over the interface allocated an enumerator per frame
        {
            var cls = classList[cIdx];
            if (cursorY >= bottom) break;
            if (focusOne && !string.Equals(cls.Name, scene.ClassName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (quota != null && quota.TryGetValue(cls.Name ?? "", out int q0) && q0 <= 0)
                continue;
            cursorY = DrawClassDivider(canvas, originX, cursorY, cls);
            firstClass = false;

            // Which rows of this class to draw (indexes into cls.Drivers).
            int count = cls.Drivers.Count;
            int limit = quota != null && quota.TryGetValue(cls.Name ?? "", out int q) ? Math.Min(q, count) : count;
            var rows = rowPick;
            rows.Clear();
            for (int i = 0; i < limit; i++) rows.Add(i);
            if (limit < count && limit > 0)
            {
                int pIdx = -1;
                for (int i = 0; i < count; i++) if (cls.Drivers[i].CarIdx == snapshot.PlayerCarIdx) { pIdx = i; break; }
                if (pIdx >= limit) rows[limit - 1] = pIdx;   // keep "you" on screen
            }

            foreach (int i in rows)
            {
                if (cursorY + RowHeight > bottom) break;
                var driver = cls.Drivers[i];
                var ahead  = i > 0 ? cls.Drivers[i - 1] : null;

                // Position-change motion (2026-10-19): a row whose slot changed glides
                // from where it was to its new place; moving rows draw last, on top.
                var (rowY, moving, flash, gained) = RowMotion(driver.CarIdx, cursorY, sceneKey, elapsedMs);
                if (moving || flash > 0)
                    // 2026-10-29 (efficiency): the closure is built in its own method, so it is only allocated for a row
                    // that actually moves (it used to be allocated for every row, every frame)
                    AddMover(canvas, originX, rowY, driver, ahead, snapshot, i, sessionBest, elapsedMs, isRace, moving, flash, gained);
                else
                    DrawRow(canvas, originX, rowY, driver, ahead, snapshot.PlayerCarIdx,
                            rowIndexInClass: i, sessionBest: sessionBest, elapsedMs: elapsedMs,
                            isRace: isRace);
                if (withWings) wings.NoteRow(driver.CarIdx, cursorY);
                cursorY += RowHeight;
            }
        }

        foreach (var m in movers) m();
        movers.Clear();
        PruneRowMotion(elapsedMs);

        // Wings attach to the tower's right edge, on top of everything in the tower.
        if (withWings) wings.Draw(canvas, originX + TowerWidth, RowHeight, elapsedMs);
    }

    // ----- first-show warm-up (2026-10-27, #44 "first-show timing tower hitch") -----
    // The first frame the tower ever drew did all of its one-off work on the render thread, inside the 220 ms fade-in:
    // PNG decodes of every manufacturer / class logo in the field, the iRacing wordmark and the series logo, their
    // pre-scaled copies, JIT of the whole draw path, every row's text blobs and glyphs, the gate fade's full-screen
    // layer and (on the GPU) the texture uploads and the moving-row blur. Now:
    //   Prewarm(snap)  - background thread, once per field: decodes and pre-scales every picture the tower will need;
    //   WarmCode(snap) - background thread, once per process: a throwaway tower (no pictures) draws the field twice on a
    //                    small raster surface, so the draw path is JIT-compiled and Skia's glyph cache holds the fonts;
    //   WarmGpu(...)   - render thread, once per field while the tower is still hidden: one draw at alpha 1/255 through
    //                    the same full-screen fade layer (textures uploaded, layer allocated, blobs built, blur compiled).
    // The row / arrow state the warm draw creates is thrown away, so the real first show looks exactly as before.

    readonly object logoGate = new();
    readonly List<IDisposable> retired = new();   // series pictures replaced off the render thread (disposed on it)
    bool noImages;            // the throwaway WarmCode tower draws no pictures
    string warmedField = "";  // which field WarmGpu last ran for
    static int codeWarmed;

    /// <summary>The set of pictures a snapshot's field needs (brands, classes, series), as a key.</summary>
    public static string FieldKey(RaceSnapshot snap)
    {
        // order-free (positions change all race; only a new brand / class / series needs new pictures)
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var cl in snap.ClassLeaderboard)
        {
            set.Add("c:" + cl.Name);
            foreach (var d in cl.Drivers) set.Add(d.CarBrand ?? "");
        }
        return (snap.SeriesId ?? -1) + "|" + string.Join(",", set);
    }

    /// <summary>Background-thread safe: decode / pre-scale every picture this field will show (logos, wordmark, series
    /// cap). Cheap when everything is already cached.</summary>
    public void Prewarm(RaceSnapshot snap)
    {
        if (noImages) return;
        try
        {
            foreach (var cl in snap.ClassLeaderboard)
            {
                LoadClassLogo(cl.Name);
                foreach (var d in cl.Drivers) LoadLogo(d.CarBrand);
            }
            EnsureWordmark();
            EnsureSeries(snap.SeriesId);
        }
        catch { /* a warm-up must never hurt */ }
    }

    /// <summary>Background thread, once per process: JIT the draw path and fill Skia's glyph cache with a throwaway tower.</summary>
    public static void WarmCode(RaceSnapshot snap)
    {
        if (Interlocked.Exchange(ref codeWarmed, 1) == 1) return;
        try
        {
            using var t = new TimingTowerRenderer { noImages = true };
            using var surf = SKSurface.Create(new SKImageInfo(448, 1100, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surf == null) return;
            var c = surf.Canvas;
            for (int i = 0; i < 2; i++)
            {
                c.Clear(SKColors.Transparent);
                using (var p = new SKPaint { Color = SKColors.Black.WithAlpha(128) }) c.SaveLayer(p);
                t.DrawBody(c, 16f, 18f, 1060f, snap, 1000 + i * 16.7, withWings: true);
                c.Restore();
            }
            using var blur = new SKPaint { Color = new SKColor(0, 0, 0, 120), ImageFilter = SKImageFilter.CreateBlur(6, 6) };
            c.DrawRect(new SKRect(20, 20, 400, 60), blur);
            c.Flush();
        }
        catch { /* never hurt the app */ }
    }

    /// <summary>Render thread, while the tower is hidden, a frame before <see cref="WarmGpu"/>: the whole draw into a
    /// no-draw canvas - builds this thread's text blobs and the per-row layout without touching the GPU, so the warm work
    /// is split over two frames. Row / arrow state is restored after.</summary>
    public void WarmLayout(RaceSnapshot snap, double elapsedMs, float availableHeight)
    {
        if (!snap.IsConnected || snap.ClassLeaderboard.Count == 0) return;
        bool freshRows = rowStateByCarIdx.Count == 0;
        try
        {
            using var nd = new SKNoDrawCanvas(3840, 2160);
            DrawBody(nd, 16f, 18f, availableHeight, snap, elapsedMs, withWings: false);
        }
        catch { }
        finally
        {
            if (freshRows) rowStateByCarIdx.Clear();
            rowGlide.Clear(); rowSlot.Clear(); rowSceneKey = ""; rowLastMs = -1; rowsAnimating = false; movers.Clear();
        }
    }

    /// <summary>True when <see cref="WarmGpu"/> has run for this field already.</summary>
    public bool GpuWarmFor(RaceSnapshot snap) => warmedField == FieldKey(snap);

    /// <summary>Render thread, while the tower is hidden: draw it once, invisibly (alpha 1/255, through the same full-screen
    /// layer the fade-in uses), so the first real frame finds everything uploaded. Row / arrow state is restored after.</summary>
    public void WarmGpu(SKCanvas canvas, float originX, float originY, float availableHeight, RaceSnapshot snap, double elapsedMs)
    {
        warmedField = FieldKey(snap);
        if (!snap.IsConnected || snap.ClassLeaderboard.Count == 0) return;
        bool freshRows = rowStateByCarIdx.Count == 0;
        try
        {
            gateLayerPaint.Color = SKColors.Black.WithAlpha(1);
            int save = canvas.SaveLayer(LayerBounds(originX, originY, availableHeight), gateLayerPaint);
            DrawBody(canvas, originX, originY, availableHeight, snap, elapsedMs, withWings: false);
            warmBlur ??= new SKPaint { Color = new SKColor(0, 0, 0, 120), ImageFilter = SKImageFilter.CreateBlur(6, 6) };
            canvas.DrawRect(new SKRect(originX + 4, originY + 3, originX + TowerWidth - 4, originY + RowHeight + 3), warmBlur);
            canvas.RestoreToCount(save);
        }
        catch { }
        finally
        {
            if (freshRows) rowStateByCarIdx.Clear();
            rowGlide.Clear(); rowSlot.Clear(); rowSceneKey = ""; rowLastMs = -1; rowsAnimating = false; movers.Clear();
        }
    }
    SKPaint? warmBlur;

    void AddMover(SKCanvas canvas, float originX, float ry, Driver driver, Driver? ahead, RaceSnapshot snapshot, int ri,
                  float sessionBest, double elapsedMs, bool isRace, bool mv, float fl, bool gn)
    {
        movers.Add(() =>
        {
            if (mv)
            {
                using var sh = new SKPaint { Color = new SKColor(0, 0, 0, 120), ImageFilter = SKImageFilter.CreateBlur(6, 6) };
                canvas.DrawRect(new SKRect(originX + 4, ry + 3, originX + TowerWidth - 4, ry + RowHeight + 3), sh);
            }
            DrawRow(canvas, originX, ry, driver, ahead, snapshot.PlayerCarIdx,
                    rowIndexInClass: ri, sessionBest: sessionBest, elapsedMs: elapsedMs, isRace: isRace);
            DrawSwapFlash(canvas, new SKRect(originX, ry, originX + TowerWidth, ry + RowHeight), fl, gn);
        });
    }

    // 2026-10-29 (efficiency): the OVERALL quotas only depend on the class names, their car counts and the height - the
    // last answer is reused while those are the same (it was rebuilt every frame). Callers only read it.
    Dictionary<string, int>? lastQuota;
    float lastQuotaHeight = float.NaN;
    readonly List<(string? Name, int Count)> lastQuotaKey = new();

    Dictionary<string, int> QuotasFor(IReadOnlyList<DriverClass> classes, float height)
    {
        bool same = lastQuota != null && height == lastQuotaHeight && classes.Count == lastQuotaKey.Count;
        for (int i = 0; same && i < classes.Count; i++)
            same = string.Equals(classes[i].Name, lastQuotaKey[i].Name, StringComparison.Ordinal) && classes[i].Drivers.Count == lastQuotaKey[i].Count;
        if (same) return lastQuota!;
        lastQuotaKey.Clear();
        for (int i = 0; i < classes.Count; i++) lastQuotaKey.Add((classes[i].Name, classes[i].Drivers.Count));
        lastQuotaHeight = height;
        return lastQuota = OverallQuotas(classes, height);
    }

    /// <summary>OVERALL board: how many rows each class gets so the whole board
    /// fits the available height. Every class gets a header + an equal slice of
    /// the rows; a class with fewer cars than its slice hands the rest to the
    /// others (water-filling), so the space is always used and nothing spills.
    /// If even one row per class can't fit, the lowest classes are dropped.</summary>
    static Dictionary<string, int> OverallQuotas(IReadOnlyList<DriverClass> classes, float height)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var live = new List<DriverClass>();
        foreach (var c in classes) if (c.Drivers.Count > 0) live.Add(c); else result[c.Name ?? ""] = 0;

        // Drop the lowest classes if there isn't room for a header + 1 row each.
        while (live.Count > 0 && live.Count * (ClassHeaderHeight + RowHeight) > height)
        {
            result[live[^1].Name ?? ""] = 0;
            live.RemoveAt(live.Count - 1);
        }
        if (live.Count == 0) return result;

        int budget = (int)Math.Floor((height - live.Count * ClassHeaderHeight) / RowHeight);
        var alloc = new int[live.Count];
        var open = new List<int>();
        for (int i = 0; i < live.Count; i++) open.Add(i);
        while (budget > 0 && open.Count > 0)
        {
            int share = Math.Max(1, budget / open.Count);
            bool progressed = false;
            for (int k = 0; k < open.Count && budget > 0; )
            {
                int ci = open[k];
                int want = live[ci].Drivers.Count - alloc[ci];
                int give = Math.Min(Math.Min(share, want), budget);
                alloc[ci] += give; budget -= give;
                if (give > 0) progressed = true;
                if (alloc[ci] >= live[ci].Drivers.Count) open.RemoveAt(k); else k++;
            }
            if (!progressed) break;
        }
        for (int i = 0; i < live.Count; i++) result[live[i].Name ?? ""] = alloc[i];
        return result;
    }

    // Row wings (roadmap 4+5) - pit timer / fastest-lap tabs on the tower's right edge.
    readonly TowerRowWings wings = new();

    /// <summary>True while a row wing is on screen, so the compositor keeps
    /// presenting (slide animation + live pit timer).</summary>
    public bool WingsAnimating => wings.Animating || rowsAnimating;

    // ----- position-change motion (2026-10-19, Angelo: "rows physically swap") -----
    const double RowGlideMs = 560, RowFlashMs = 1400;
    readonly Dictionary<int, (float From, float To, double Start, bool Gained)> rowGlide = new();
    readonly Dictionary<int, float> rowSlot = new();
    readonly List<Action> movers = new();
    string rowSceneKey = "";
    double rowLastMs = -1;
    bool rowsAnimating;

    /// <summary>Where to draw this car's row this frame, and whether it is gliding /
    /// flashing. A slot change of up to 4 rows (same board) starts a glide; scene
    /// switches, long gaps between frames or big jumps simply snap.</summary>
    (float Y, bool Moving, float Flash, bool Gained) RowMotion(int carIdx, float slotY, string sceneKey, double now)
    {
        if (sceneKey != rowSceneKey || (rowLastMs >= 0 && now - rowLastMs > 1500))
        {
            rowSceneKey = sceneKey; rowGlide.Clear(); rowSlot.Clear();
        }
        if (rowSlot.TryGetValue(carIdx, out float prev) && Math.Abs(prev - slotY) > 0.5f
            && Math.Abs(prev - slotY) <= RowHeight * 4.5f)
        {
            float from = prev;
            if (rowGlide.TryGetValue(carIdx, out var g0) && now - g0.Start < RowGlideMs)
                from = g0.From + (g0.To - g0.From) * Ease((float)((now - g0.Start) / RowGlideMs));
            rowGlide[carIdx] = (from, slotY, now, slotY < prev);
        }
        rowSlot[carIdx] = slotY;
        if (!rowGlide.TryGetValue(carIdx, out var g)) return (slotY, false, 0, false);
        double t = now - g.Start;
        if (t >= RowFlashMs) { rowGlide.Remove(carIdx); return (slotY, false, 0, false); }
        rowsAnimating = true;
        float flash = t < RowGlideMs ? 1f : 1f - (float)((t - RowGlideMs) / (RowFlashMs - RowGlideMs));
        if (t >= RowGlideMs) return (slotY, false, flash, g.Gained);
        float e = Ease((float)(t / RowGlideMs));
        return (g.From + (g.To - g.From) * e, true, flash, g.Gained);

        static float Ease(float x) { x = Math.Clamp(x, 0, 1); return 1 - (1 - x) * (1 - x) * (1 - x); }
    }

    void PruneRowMotion(double now)
    {
        rowLastMs = now;
        rowsAnimating = false;
        foreach (var kv in rowGlide) if (now - kv.Value.Start < RowFlashMs) { rowsAnimating = true; break; }
    }

    /// <summary>Green wash + stripe for a place gained, a quieter red for a place lost.</summary>
    static void DrawSwapFlash(SKCanvas c, SKRect r, float k, bool gained)
    {
        if (k <= 0.01f) return;
        var col = gained ? PbGreen : new SKColor(0xF8, 0x71, 0x71);
        float strength = gained ? 1f : 0.6f;
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0),
                   new[] { col.WithAlpha((byte)(90 * k * strength)), col.WithAlpha((byte)(20 * k * strength)), col.WithAlpha(0) },
                   new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { Shader = sh })
            c.DrawRect(r, p);
        using (var s = new SKPaint { Color = col.WithAlpha((byte)(255 * k * strength)) })
            c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 4, r.Bottom), s);
    }

    // Shared position / car-number slot timing (2026-10-18).
    const double SwapHoldMs = 6000;   // each side shown this long
    const double SwapFlipMs = 280;    // card-flip duration at each changeover

    /// <summary>Which side of the shared slot to show, and the vertical flip scale
    /// (1 = flat, 0 = edge-on). The flip straddles each changeover: the outgoing
    /// side squashes to a line over the first half, the incoming one opens over the
    /// second half. One global clock, so every row flips together.</summary>
    static (bool ShowNumber, float Scale) SlotSwapState(double nowMs)
    {
        double cycle = 2 * SwapHoldMs;
        double t = ((nowMs % cycle) + cycle) % cycle;
        bool number = t >= SwapHoldMs;
        // distance to the nearest changeover (at 0 / SwapHoldMs / cycle)
        double sinceSwap = number ? t - SwapHoldMs : t;
        double untilSwap = SwapHoldMs - sinceSwap;
        double half = SwapFlipMs / 2;
        float scale = 1f;
        if (sinceSwap < half) scale = (float)(sinceSwap / half);          // incoming side opening
        else if (untilSwap < half) scale = (float)(untilSwap / half);     // outgoing side closing
        scale = scale * scale * (3 - 2 * scale);                           // smoothstep
        return (number, scale);
    }

    float ComputeGateAlpha(double nowMs)
    {
        bool visible = PresentationState.TowerVisible;
        if (visible != wasGated)
        {
            gateFlipAtMs = nowMs;
            wasGated = visible;
        }
        if (gateFlipAtMs < 0) return visible ? 1f : 0f;
        double t = (nowMs - gateFlipAtMs) / GateFadeAnimMs;
        float prog = (float)BroadcastTiming.Smoothstep(t);
        return visible ? prog : 1f - prog;
    }

    // ----- card background -----

    void DrawCard(SKCanvas c, float x, float y, float height, double elapsedMs = 0)
    {
        var rect = new SKRect(x, y, x + TowerWidth, y + height);
        c.DrawRect(rect, glassPaint);
        Theme.Skin(c, rect, ThemeSurface.Tower, elapsedMs);   // #494: the broadcast theme's tint / texture / motif (nothing on Default)
        // Top hairline — gold accent on the legacy uses --class-color of the
        // top class; the simpler always-on hairline keeps the visual quiet and
        // is consistent across single/multi-class.
        c.DrawLine(rect.Left, rect.Top, rect.Right, rect.Top, hairlinePaint);
        c.DrawLine(rect.Left, rect.Top + CapHeight + HeaderHeight, rect.Right, rect.Top + CapHeight + HeaderHeight, hairlinePaint);
    }

    // ----- header backdrop: sliding sporadic iRacing-logo rows -----
    // Subtle parallax field of the bundled inline iRacing wordmark, drawn over
    // the glass and under the header text (the gate SaveLayer fades it with the
    // tower). Per-row scale/speed/phase variation reads as sporadic, not tiled.
    static readonly (float H, byte A, float Speed, float Gap, float Phase)[] WmRows =
    {
        (9f,  16, 16f, 40f,  0f),
        (13f, 26, 11f, 60f, 35f),
        (10f, 20, 14f, 48f, 18f),
        (12f, 24, 12f, 54f, 50f),
        (8f,  16, 18f, 36f, 12f),
    };
    const float WmAspect = 1307f / 244f;

    // ROADMAP-3 PERF (2026-10-18): the cap logos were drawn straight from the
    // full-size mutable SKBitmaps (the wordmark is 1307x244) with Medium filtering,
    // so Skia rebuilt the mip chain on EVERY draw - ~40 backdrop draws per frame
    // cost 20-25 ms (the whole 60 fps budget). Each logo is now pre-scaled ONCE to
    // ~2x its on-screen size and wrapped as an immutable SKImage, whose texture +
    // mips Skia caches on the GPU. Same look, near-zero cost per draw.
    SKImage? wmBackdropImage, wmCapImage, seriesCapImage;

    static SKImage? MakeCachedImage(SKBitmap? src, int targetHeight)
    {
        if (src is null || src.Height <= 0) return null;
        try
        {
            if (src.Height > targetHeight)
            {
                int w = Math.Max(1, (int)Math.Round(src.Width * (targetHeight / (double)src.Height)));
                using var scaled = src.Resize(new SKImageInfo(w, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul),
                                              SKFilterQuality.High);
                if (scaled != null) return SKImage.FromBitmap(scaled);
            }
            return SKImage.FromBitmap(src);
        }
        catch { return null; }
    }

    /// <summary>The iRacing wordmark and its two pre-scaled copies (render thread or <see cref="Prewarm"/>).</summary>
    void EnsureWordmark()
    {
        if (noImages) { iracingWmTried = true; return; }
        lock (logoGate)
        {
            if (!iracingWmTried)
            {
                try { iracingWmLogo = Assets.LoadBitmap("logos", "iracing-brand", "iRacing-Inline-BW-White.png"); }
                catch { iracingWmLogo = null; }
                wmBackdropImage ??= MakeCachedImage(iracingWmLogo, 26);   // rows are 8-13 px tall -> 2x headroom
                wmCapImage ??= MakeCachedImage(iracingWmLogo, (int)(CapHeight * 2f));
                iracingWmTried = true;
            }
        }
    }

    /// <summary>The series cap logo for this SeriesID and its pre-scaled copy (render thread or <see cref="Prewarm"/>).</summary>
    void EnsureSeries(int? seriesId)
    {
        int key = seriesId ?? -1;
        if (noImages) { seriesCapLogoId = key; return; }
        lock (logoGate)
        {
            if (key == seriesCapLogoId) return;
            var slug = SeriesNames.ResolveLogoSlug(seriesId);
            SKBitmap? bmp = null;
            if (slug != null)
                try { bmp = Assets.LoadBitmap("logos", "series", slug + ".png"); }
                catch { bmp = null; }
            var img = MakeCachedImage(bmp, (int)(CapHeight * 2f));
            // the render thread may be drawing the old ones right now (Prewarm runs in the background): they're
            // disposed by the render thread on its next cap draw
            if (seriesCapLogo != null) retired.Add(seriesCapLogo);
            if (seriesCapImage != null) retired.Add(seriesCapImage);
            seriesCapLogo = bmp; seriesCapImage = img;
            seriesCapLogoId = key;
        }
    }

    void DrawHeaderBackdrop(SKCanvas c, float x, float y, float height, double elapsedMs)
    {
        if (!iracingWmTried) EnsureWordmark();
        if (iracingWmLogo is null) return;
        if (wmBackdropImage is null) return;

        var header = new SKRect(x, y, x + TowerWidth, y + height);
        int save = c.Save();
        c.ClipRect(header);

        double t = elapsedMs / 1000.0;
        for (int r = 0; r < WmRows.Length; r++)
        {
            var row = WmRows[r];
            float w = row.H * WmAspect;
            float period = w + row.Gap;
            float off = (float)(((row.Phase - t * row.Speed) % period + period) % period);
            float top = y + height * (r + 0.5f) / WmRows.Length - row.H / 2f;
            wmPaint.Color = new SKColor(255, 255, 255, row.A);
            for (float px = header.Left - period + off; px < header.Right; px += period)
                c.DrawImage(wmBackdropImage, new SKRect(px, top, px + w, top + row.H), wmPaint);
        }
        c.RestoreToCount(save);
    }

    // ----- series-logo cap (top of tower) -----
    // Auto from SeriesID; falls back to the iRacing wordmark so it is never blank.
    void DrawCap(SKCanvas c, float x, float y, RaceSnapshot snap, double elapsedMs)
    {
        DrawHeaderBackdrop(c, x, y, CapHeight, elapsedMs);
        if ((snap.SeriesId ?? -1) != seriesCapLogoId) EnsureSeries(snap.SeriesId);
        if (retired.Count > 0) lock (logoGate) { foreach (var o in retired) o.Dispose(); retired.Clear(); }
        if (!iracingWmTried) EnsureWordmark();
        var logo = seriesCapLogo ?? iracingWmLogo;
        if (logo is null) return;
        float padX = 18f, padY = 9f;
        float maxW = TowerWidth - 2f * padX;
        float maxH = CapHeight - 2f * padY;
        float scale = Math.Min(maxW / logo.Width, maxH / logo.Height);
        float w = logo.Width * scale, h = logo.Height * scale;
        float lx = x + (TowerWidth - w) / 2f;
        float ly = y + (CapHeight - h) / 2f;
        // Draw the cached pre-scaled image (see MakeCachedImage); geometry above is
        // still computed from the original bitmap, so placement is unchanged.
        SKImage? capImg = seriesCapLogo != null ? seriesCapImage : wmCapImage;
        if (capImg != null) c.DrawImage(capImg, new SKRect(lx, ly, lx + w, ly + h), capLogoPaint);
        else c.DrawBitmap(logo, new SKRect(lx, ly, lx + w, ly + h), capLogoPaint);
        c.DrawLine(x, y + CapHeight, x + TowerWidth, y + CapHeight, hairlinePaint);
    }

    void DrawHeader(SKCanvas c, float x, float y, RaceSnapshot snap, double elapsedMs)
    {
        DrawHeaderStats(c, x, y, snap);
    }

    // ----- header line 2: session type / car count / strength of field -----
    void DrawHeaderStats(SKCanvas c, float x, float y, RaceSnapshot snap)
    {
        float baseline = y + 19f;
        float tracking = 0.07f * 10f;   // #340: 13 px (was 10) with a little less tracking so CARS / SOF keep their columns
        var val = statVal; var lab = statLab;

        if (!ReferenceEquals(snap.SessionTypeName, sessionLabelIn) || sessionLabelOut == null)
        { sessionLabelIn = snap.SessionTypeName; sessionLabelOut = SessionLabel(snap.SessionTypeName); }
        string st = sessionLabelOut;
        if (st.Length > 0)
            TextTracked.Draw(c, val, st, x + 18f, baseline, tracking);

        DrawStatGroup(c, lab, val, "CARS", SmallStrings.Num(snap.OpponentsCount), x + 196f, baseline, tracking);

        int sof = ComputeSof(snap);
        if (sof != sofValue) { sofValue = sof; sofText = sof.ToString(CultureInfo.InvariantCulture); }
        if (sof > 0)
            DrawStatGroup(c, lab, val, "SOF", sofText, x + 296f, baseline, tracking);
    }

    // Label (muted) then value (bright), left-to-right from startX so they never
    // collide regardless of value width.
    static void DrawStatGroup(SKCanvas c, SKPaint lab, SKPaint val, string label, string value, float startX, float baseline, float tracking)
    {
        TextTracked.Draw(c, lab, label, startX, baseline, tracking);
        float labW = TextBlobs.Measure(label, lab) + tracking * label.Length;
        TextTracked.Draw(c, val, value, startX + labW + 7f, baseline, tracking);
    }

    static string SessionLabel(string s)
    {
        s = (s ?? string.Empty).ToLowerInvariant();
        if (s.Contains("race")) return "RACE";
        if (s.Contains("qual")) return "QUALIFYING";
        if (s.Contains("practice") || s.Contains("open") || s.Contains("test") || s.Contains("warmup")) return "PRACTICE";
        return s.Length > 0 ? s.ToUpperInvariant() : string.Empty;
    }

    // iRacing's Elo-weighted Strength of Field (matches the in-sim number),
    // computed across the whole field; 0 when no iRatings are present.
    static int ComputeSof(RaceSnapshot snap)
    {
        double k = Math.Log(2) / 1600.0;
        double sumExp = 0; int n = 0;
        var cl = snap.ClassLeaderboard;
        for (int ci = 0; ci < cl.Count; ci++)
        {
            var ds = cl[ci].Drivers;
            for (int di = 0; di < ds.Count; di++)
            {
                var d = ds[di];
                if (!d.IsPaceCar && d.IRating > 0) { sumExp += Math.Exp(-k * d.IRating); n++; }
            }
        }
        if (n == 0 || sumExp <= 0) return 0;
        return (int)Math.Round(1600.0 / Math.Log(2) * Math.Log(n / sumExp));
    }

    // ----- class divider -----

    float DrawClassDivider(SKCanvas c, float x, float y, DriverClass cls)
    {
        var rect = new SKRect(x, y, x + TowerWidth, y + ClassHeaderHeight);

        // Background: subtle horizontal gradient (matches legacy class-divider).
        aaShaderPaint.Shader = dividerShader.Linear(new SKPoint(rect.Left, 0), new SKPoint(rect.Right, 0),
            [new SKColor(255, 255, 255, 6), new SKColor(255, 255, 255, 1)], StopsDivider, SKShaderTileMode.Clamp);
        c.DrawRect(rect, aaShaderPaint);

        // Top + bottom hairlines.
        c.DrawLine(rect.Left, rect.Top, rect.Right, rect.Top, hairlinePaint);
        c.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, hairlinePaint);

        // Class name in gold (gold, not class colour — matches the editorial
        // look the legacy went with).
        // Class identity: bundled IMSA class logo when one exists, else the
        // gold wordmark (GT4/GTE/LMP3/OTHER have no logo -> text fallback).
        var clsLogo = LoadClassLogo(cls.Name);
        if (clsLogo != null && clsLogo.Height > 0)
        {
            const float logoH = 20f;
            float scale = logoH / clsLogo.Height;
            float lw = clsLogo.Width * scale;
            float ltop = rect.MidY - logoH / 2f;
            c.DrawBitmap(clsLogo, new SKRect(rect.Left + 12f, ltop, rect.Left + 12f + lw, ltop + logoH), capLogoPaint);
        }
        else
        {
            var namePaint = classNamePaint;   // LeaderGold, Inter Bold 14 (#340: was 12.1)
            namePaint.GetFontMetrics(out var nm);
            float nameBaseline = rect.MidY - (nm.Ascent + nm.Descent) / 2f;
            TextTracked.Draw(c, namePaint, Cached(upperText, cls.Name, static n => n.ToUpperInvariant()), rect.Left + 12f, nameBaseline, 0.42f * 11f / 4f);
        }

        // Right-aligned car count.
        string countLabel = pitBoard
            ? Cached(pitStopsText, RaceDiary.TotalPitStops(cls.Drivers), static n => $"PIT STOPS  ·  {n}")
            : Cached(carsText, cls.Drivers.Count, static n => $"{n} CAR{(n == 1 ? "" : "S")}");
        var countPaint = classCountPaint;   // TextTertiary, Inter SemiBold 13 (#340: was 11), right-aligned
        countPaint.GetFontMetrics(out var cm);
        float countBaseline = rect.MidY - (cm.Ascent + cm.Descent) / 2f;
        TextTracked.Draw(c, countPaint, countLabel, rect.Right - ContentPadRight, countBaseline, 0.28f * 10f / 4f);

        return rect.Bottom;
    }

    // ----- driver row -----

    void DrawRow(
        SKCanvas c,
        float x,
        float y,
        Driver driver,
        Driver? ahead,
        int playerCarIdx,
        int rowIndexInClass,
        float sessionBest,
        double elapsedMs,
        bool isRace)
    {
        var rect = new SKRect(x, y, x + TowerWidth, y + RowHeight);

        bool isLeader = rowIndexInClass == 0;
        bool isPlayer = playerCarIdx >= 0 && driver.CarIdx == playerCarIdx;
        bool isPit    = driver.IsInPit;
        bool isFastest = sessionBest > 0 && driver.BestLapTime > 0
                      && Math.Abs(driver.BestLapTime - sessionBest) < 0.001f;
        bool isDc     = !driver.IsConnected;

        // Track per-row state (arrow / lap pulse / PB pulse).
        var state = UpdateRowState(driver, elapsedMs);

        // Row base zebra.
        c.DrawRect(rect, rowIndexInClass % 2 == 0 ? rowOddPaint : rowEvenPaint);

        // Battle glow (under the row content). Use the gap to the car ahead
        // in the same class — the snapshot exposes that as Driver.IntervalSeconds.
        double? intervalToAhead = driver.IntervalSeconds;
        if (!isLeader && !isPit && ahead != null && intervalToAhead is double gap && gap > 0 && gap < BattleThresholdSec)
        {
            float intensity = 1f - Math.Clamp((float)gap / BattleThresholdSec, 0f, 1f); // 0..1
            float closePulse = gap < BattleCloseSec
                ? 1.0f + 0.35f * (float)(0.5 + 0.5 * Math.Sin(elapsedMs / 1000.0 * Math.PI * 2.0 / ClosePulseSec))
                : 1.0f;
            using var glow = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, 0), new SKPoint(rect.Right, 0),
                new[]
                {
                    BattleOrange.WithAlpha((byte)(intensity * 0.05f * 255 * closePulse)),
                    BattleOrange.WithAlpha((byte)(intensity * 0.22f * 255 * closePulse)),
                    BattleOrange.WithAlpha((byte)(intensity * 0.10f * 255 * closePulse)),
                    new SKColor(0, 0, 0, 0),
                },
                StopsGlow,
                SKShaderTileMode.Clamp);
            aaShaderPaint.Shader = glow;
            c.DrawRect(rect, aaShaderPaint);
            aaShaderPaint.Shader = null;
        }

        // Player row highlight (blue tint + bright inset stripe).
        if (isPlayer)
        {
            aaShaderPaint.Shader = playerShader.Linear(new SKPoint(rect.Left, 0), new SKPoint(rect.Right, 0),
                [PlayerBlue.WithAlpha(107), PlayerBlue.WithAlpha(41), PlayerBlue.WithAlpha(10)], StopsPlayer, SKShaderTileMode.Clamp);
            c.DrawRect(rect, aaShaderPaint);

            // Bright 4px inset stripe on the left + 1px hairline border.
            c.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + 4, rect.Bottom), playerInsetPaint);
            c.DrawRect(rect, playerBorderPaint);
        }
        else if (isLeader)
        {
            // Leader row: warm-gold gradient wash.
            aaShaderPaint.Shader = leaderShader.Linear(new SKPoint(rect.Left, 0), new SKPoint(rect.Right, 0),
                [LeaderGold.WithAlpha(36), LeaderGold.WithAlpha(5), new SKColor(0, 0, 0, 0)], StopsLeader, SKShaderTileMode.Clamp);
            c.DrawRect(rect, aaShaderPaint);
        }

        // Disconnected dim wash on top of everything that isn't text yet.
        // Apply by fading the whole row.
        float globalAlpha = isDc ? 0.42f : 1f;
        if (isPit && !isPlayer) globalAlpha *= 0.95f;

        // Lap pulse + PB pulse overlays (transient inset glows).
        if (state.LapPulseFraction > 0)
        {
            byte a = (byte)(state.LapPulseFraction * 0.18f * 255);
            solidPaint.Color = new SKColor(255, 255, 255, a);
            c.DrawRect(rect, solidPaint);
        }
        if (state.PbPulseFraction > 0)
        {
            byte a = (byte)(state.PbPulseFraction * 0.35f * 255);
            solidPaint.Color = PbGreen.WithAlpha(a);
            c.DrawRect(rect, solidPaint);
            solidPaint.Color = PbGreen.WithAlpha((byte)(state.PbPulseFraction * 0.95f * 255));
            c.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + 4, rect.Bottom), solidPaint);
        }

        // Now draw the columns.
        DrawRowColumns(c, rect, driver, ahead, isLeader, isPlayer, isPit, isFastest, isDc,
                       state, globalAlpha, isRace, elapsedMs);
    }

    void DrawRowColumns(
        SKCanvas c,
        SKRect rect,
        Driver driver,
        Driver? ahead,
        bool isLeader,
        bool isPlayer,
        bool isPit,
        bool isFastest,
        bool isDc,
        RowState state,
        float globalAlpha,
        bool isRace,
        double elapsedMs)
    {
        // Compute column positions left-to-right, mirroring the legacy grid.
        float left = rect.Left;
        float colX = left;

        // Stripe (3px, class colour) — vertical 22px centred.
        var stripeColor = ParseHex(string.IsNullOrEmpty(driver.ClassColor)
            ? BroadcastPalette.Class(driver.ClassName)
            : driver.ClassColor);
        solidPaint.Color = stripeColor.WithAlpha((byte)(255 * globalAlpha));
        c.DrawRect(new SKRect(colX, rect.MidY - 11, colX + ColStripe, rect.MidY + 11), solidPaint);
        colX += ColStripe;

        // ---- Shared POSITION / CAR NUMBER slot (2026-10-18, Angelo) -------------
        // Position and car number now share ONE 32 px slot (was 22 + 32), flipping
        // between them on a global clock - position for SwapHoldMs, a quick card-
        // flip, car number for SwapHoldMs, and back - all rows in sync. Frees 22 px
        // for driver names.
        var (showNumber, flipScale) = SlotSwapState(elapsedMs);
        float slotCx = colX + ColNum / 2f;
        c.Save();
        if (flipScale < 0.999f)
        {
            c.Translate(slotCx, rect.MidY);
            c.Scale(1f, Math.Max(0.001f, flipScale));
            c.Translate(-slotCx, -rect.MidY);
        }
        if (!showNumber)
        {
            // Position (big tabular number, gold if leader; dash when no place yet).
            var posColor = isLeader ? LeaderGold : TextPrimary;
            posPaint.Color = posColor.WithAlpha((byte)(255 * globalAlpha));   // Inter Bold 24.2, centred
            posPaint.GetFontMetrics(out var m);
            float baseline = rect.MidY - (m.Ascent + m.Descent) / 2f;
            TextBlobs.Draw(c, driver.PositionInClass > 0
                           ? SmallStrings.Num(driver.PositionInClass)
                           : "–",
                slotCx, baseline, posPaint);
        }
        else
        {
            // Car number pill (white bg + dark text), same look as before.
            var numRect = new SKRect(colX + 2, rect.MidY - 10, colX + ColNum - 2, rect.MidY + 10);
            numBgPaint.Color = SKColors.White.WithAlpha((byte)(245 * globalAlpha));
            c.DrawRect(numRect, numBgPaint);
            solidPaint.Color = new SKColor(255, 255, 255, (byte)(178 * globalAlpha));
            c.DrawRect(new SKRect(numRect.Left, numRect.Top, numRect.Right, numRect.Top + 1), solidPaint);
            numTextPaint.Color = new SKColor(8, 10, 16, (byte)(242 * globalAlpha));
            // 3-digit numbers (#420) overflowed the chip: shrink to fit, then restore.
            float baseSize = numTextPaint.TextSize;
            float maxW = numRect.Width - 3f;
            float tw = TextBlobs.Measure(driver.CarNumber, numTextPaint);
            if (tw > maxW && tw > 0) numTextPaint.TextSize = baseSize * (maxW / tw);
            numTextPaint.GetFontMetrics(out var nm);
            float numBaseline = numRect.MidY - (nm.Ascent + nm.Descent) / 2f;
            TextBlobs.Draw(c, driver.CarNumber, numRect.MidX, numBaseline, numTextPaint);
            numTextPaint.TextSize = baseSize;
        }
        c.Restore();
        colX += ColNum;

        // Arrow (11px) — green up / red down, fading by ArrowTtlMs.
        if (state.ArrowDirection != 0 && state.ArrowFraction > 0)
        {
            byte alpha = (byte)(state.ArrowFraction * 255 * globalAlpha);
            arrowPaint.Color = (state.ArrowDirection > 0 ? PbGreen : LossRed).WithAlpha(alpha);
            float cx = colX + ColArrow / 2f;
            float cy = rect.MidY;
            var path = arrowPath;
            path.Reset();
            if (state.ArrowDirection > 0) // gain: triangle pointing up
            {
                path.MoveTo(cx, cy - 4);
                path.LineTo(cx - 4, cy + 2);
                path.LineTo(cx + 4, cy + 2);
            }
            else // loss: triangle pointing down
            {
                path.MoveTo(cx, cy + 4);
                path.LineTo(cx - 4, cy - 2);
                path.LineTo(cx + 4, cy - 2);
            }
            path.Close();
            c.DrawPath(path, arrowPaint);
        }
        colX += ColArrow;
        // (Car number now lives in the shared position/number slot above.)

        // Manufacturer logo — scale to fit the column box PRESERVING aspect
        // ratio, then centre. DrawBitmap stretches the bitmap to the dst rect,
        // so the old fixed-square dst squashed every non-square mark (Ferrari
        // shield, Chevy bowtie, Acura caliper). Mirrors the grid renderer.
        string brandKey = driver.CarBrand ?? "";
        if (!brandLogos.TryGetValue(brandKey, out var logoBmp))
        {
            logoBmp = LoadLogo(driver.CarBrand);
            if (logoBmp != null || noImages) brandLogos[brandKey] = logoBmp;   // a missing logo is looked up again (as before)
        }
        if (logoBmp != null && logoBmp.Width > 0 && logoBmp.Height > 0)
        {
            const float LogoMaxW = ColLogo - 2f;   // 24 px inside the 26 px column
            const float LogoMaxH = 24f;
            float scale = Math.Min(LogoMaxW / logoBmp.Width, LogoMaxH / logoBmp.Height);
            float dw = logoBmp.Width * scale;
            float dh = logoBmp.Height * scale;
            float cx = colX + ColLogo / 2f;
            float cy = rect.MidY;
            var dst = new SKRect(cx - dw / 2f, cy - dh / 2f, cx + dw / 2f, cy + dh / 2f);
            byte logoAlpha = (byte)(242 * globalAlpha);
            logoPaint.ColorFilter = logoAlphaFilters[logoAlpha] ??= SKColorFilter.CreateBlendMode(new SKColor(255, 255, 255, logoAlpha), SKBlendMode.DstIn);
            BitmapImages.Draw(c, logoBmp, dst, logoPaint);
        }
        colX += ColLogo;

        // License badge (20×20).
        string lic = string.IsNullOrEmpty(driver.LicenceString) ? "R" : SmallStrings.Char(char.ToUpperInvariant(driver.LicenceString[0]));
        var licColor = ParseHex(BroadcastPalette.License(lic));
        var licRect = new SKRect(colX + 3, rect.MidY - 10, colX + 3 + ColLic, rect.MidY + 10);
        solidPaint.Color = licColor.WithAlpha((byte)(255 * globalAlpha));
        c.DrawRect(licRect, solidPaint);
        var licTextPaint = lic == "C" ? licTextPaintDark : licTextPaintLight;
        licTextPaint.Color = (lic == "C" ? new SKColor(0x0A, 0x0E, 0x14) : SKColors.White).WithAlpha((byte)(255 * globalAlpha));
        licTextPaint.GetFontMetrics(out var lm);
        float licBaseline = licRect.MidY - (lm.Ascent + lm.Descent) / 2f;
        TextBlobs.Draw(c, lic, licRect.MidX, licBaseline, licTextPaint);
        colX += ColLic;

        // Name (flex). Runs close to the value number (NameRightReserve, not the
        // full ColValue), so truncated names sit near the gap. The value column
        // is drawn AFTER this, so a wide value (lap-time flash) overlaps and
        // covers the name's tail rather than the name reserving space for it.
        float nameLeft = colX + 8f;
        // 2026-10-18 (Angelo: names must never overlap the times): stop the name
        // 10 px short of THIS row's actual value text, measured live, so long
        // names still get every pixel the row's value doesn't need.
        float valueLeft = rect.Right - ContentPadRight
                        - MeasureValueWidth(driver, ahead, isLeader, isPit, state, isRace, elapsedMs);
        float nameRight = Math.Min(rect.Right - ContentPadRight - NameRightReserve, valueLeft - 10f);

        var nameColor = isPlayer ? SKColors.White
                      : isPit    ? TextTertiary
                      :            TextPrimary;
        namePaint.Color = nameColor.WithAlpha((byte)(255 * globalAlpha));   // Inter SemiBold 27.73
        {
            namePaint.GetFontMetrics(out var m);
            float baseline = rect.MidY - (m.Ascent + m.Descent) / 2f;

            if (!nameText.TryGetValue((driver.Name, driver.AbbrevName), out var display))
            {
                if (nameText.Count > 2048) nameText.Clear();
                nameText[(driver.Name, driver.AbbrevName)] = display = NameFormat.Broadcast(driver);
            }
            // (The purple fastest-lap dot was replaced 2026-10-18 by a per-class
            // purple tab on the row's edge - see TowerRowWings - so no reserve needed.)
            float nameMax = nameRight - nameLeft;
            // 2026-10-18 (Angelo): no "..." - a name too long for the row runs to
            // its limit and DISSOLVES (soft 26 px fade) just before the row's
            // time/delta. Names that fit are drawn plainly (no layer, no cost).
            string ellipsised = display;
            float fullW = TextBlobs.Measure(display, namePaint);
            if (fullW <= nameMax)
            {
                TextBlobs.Draw(c, display, nameLeft, baseline, namePaint);
            }
            else
            {
                float fadeEnd = nameLeft + nameMax;
                float fadeStart = fadeEnd - 26f;
                var fadeBounds = new SKRect(nameLeft, rect.Top, fadeEnd, rect.Bottom);
                c.SaveLayer(fadeBounds, null);                    // tight, row-sized layer
                c.ClipRect(fadeBounds);
                TextBlobs.Draw(c, display, nameLeft, baseline, namePaint);
                if (!nameFadeShaders.TryGetValue(driver.CarIdx, out var fadeSlot)) nameFadeShaders[driver.CarIdx] = fadeSlot = new ShaderSlot();
                fadeMaskPaint.Shader = fadeSlot.Linear(new SKPoint(fadeStart, 0), new SKPoint(fadeEnd, 0),
                    [SKColors.Black, SKColors.Transparent], StopsFade, SKShaderTileMode.Clamp);
                c.DrawRect(fadeBounds, fadeMaskPaint);
                c.Restore();
            }

            // (Old purple fastest-lap bullet removed - replaced by the per-class tab.)
        }

        // Single value column (Session 22) — right-aligned. Order of precedence:
        //   1. PIT badge wins whenever the driver is on pit road.
        //   2. The 10s valid-lap flash, if active for this row, paints the
        //      just-completed lap time over either default.
        //   3. Otherwise the session-type default: race → LEADER / gap-to-
        //      ahead-in-class / lap-down; practice & quali → leader's fastest
        //      lap time / delta to the car ahead on fastest, with em-dash for
        //      missing data.
        DrawValueColumn(c, rect, driver, ahead, isLeader, isPit, state, globalAlpha,
                        isRace, elapsedMs);
    }

    // F1-style sector bars under the lap time in practice / qualifying (2026-10-24, Angelo):
    // purple = fastest in class, green = own best, yellow = slower, dim = not done yet this lap.
    static readonly SKColor SecPurple = new(168, 85, 247), SecGreen = new(34, 197, 94), SecYellow = new(250, 204, 21),
                            SecEmpty = new(255, 255, 255, 34);
    void DrawSectorBars(SKCanvas c, SKRect rect, float valueRight, int carIdx, float alpha)
    {
        var marks = SectorTimer.MarksFor(carIdx);
        if (marks == null || marks.Length == 0) return;
        const float total = 66f, gap = 3f, h = 3f;
        float w = (total - gap * (marks.Length - 1)) / marks.Length;
        float x = valueRight - total, y = rect.Bottom - 6f;
        var p = sectorPaint;
        for (int i = 0; i < marks.Length; i++)
        {
            var col = marks[i] switch
            {
                SectorTimer.Mark.ClassBest => SecPurple,
                SectorTimer.Mark.PersonalBest => SecGreen,
                SectorTimer.Mark.Slower => SecYellow,
                _ => SecEmpty,
            };
            p.Color = col.WithAlpha((byte)(col.Alpha * alpha));
            c.DrawRoundRect(new SKRect(x, y, x + w, y + h), 1.5f, 1.5f, p);
            x += w + gap;
        }
    }

    void DrawValueColumn(
        SKCanvas c,
        SKRect rect,
        Driver driver,
        Driver? ahead,
        bool isLeader,
        bool isPit,
        RowState state,
        float globalAlpha,
        bool isRace,
        double elapsedMs)
    {
        float valueRight = rect.Right - ContentPadRight;
        if (pitBoard) { DrawPitValue(c, rect, valueRight, driver, isPit, globalAlpha, elapsedMs); return; }
        if (!isRace) DrawSectorBars(c, rect, valueRight, driver.CarIdx, globalAlpha);

        if (isPit)
        {
            // PIT badge: unchanged from the legacy treatment, just sitting in
            // the new single value column.
            var pp = pitTagPaint;   // Inter Bold 13 (#340: was 11), right-aligned
            pp.Color = PitAmber.WithAlpha((byte)(255 * globalAlpha));
            pp.GetFontMetrics(out var pm);
            float baseline = rect.MidY - (pm.Ascent + pm.Descent) / 2f;
            TextTracked.Draw(c, pp, "PIT", valueRight, baseline, 0.22f * 10f / 4f, alignRight: true);
            return;
        }

        // Flash overrides both leader and default — for 10 s the row shows
        // the just-completed lap time. Validity (no in-/out-laps) is enforced
        // upstream in UpdateRowState.
        if (state.IsFlashing(elapsedMs) && state.FlashLapTime > 0)
        {
            DrawValueText(c, rect, valueRight, Cached(lapText, state.FlashLapTime, static t => LapTimeFormat.Lap(t)),
                          TextPrimary, Fonts.Inter.SemiBold, 16.5f, globalAlpha);
            return;
        }

        if (isRace)
        {
            if (isLeader)
            {
                // "LEADER" tag — same gold treatment as before.
                var lp = leaderTagPaint;   // Inter Bold 13.5 (#340: was 12.1), right-aligned
                lp.Color = LeaderGold.WithAlpha((byte)(255 * globalAlpha));
                lp.GetFontMetrics(out var leaderM);
                float baseline = rect.MidY - (leaderM.Ascent + leaderM.Descent) / 2f;
                TextTracked.Draw(c, lp, "LEADER", valueRight, baseline, 0.08f * 11f, alignRight: true);
                return;
            }

            // Lap 1 (2026-10-24, Angelo): places only - no gaps until the leader has crossed the line.
            if (!PresentationState.Lap1Complete) return;

            // Race default: gap to car ahead in class, with the existing
            // lap-down "+1L" preservation. Lap-down rows pick up the amber
            // bold treatment the legacy interval column had.
            string text = FormatInterval(driver);
            bool lapDown = driver.LapDifference != 0;
            DrawValueText(c, rect, valueRight, text,
                          lapDown ? PitAmber : TextSecondary,
                          lapDown ? Fonts.Inter.Bold : Fonts.Inter.SemiBold,
                          16.5f, globalAlpha);
            return;
        }

        // Practice / qualifying.
        if (isLeader)
        {
            string text = driver.BestLapTime > 0 ? Cached(lapText, driver.BestLapTime, static t => LapTimeFormat.Lap(t)) : "—";
            DrawValueText(c, rect, valueRight, text,
                          driver.BestLapTime > 0 ? LeaderGold : TextSecondary,
                          Fonts.Inter.SemiBold, 16.5f, globalAlpha);
            return;
        }

        // Quali / practice rows: delta to the car directly ahead in class on
        // fastest lap. The snapshot orders ClassLeaderboard rows by class
        // position which iRacing assigns by best lap in non-race sessions,
        // so `ahead` is already the next-faster driver.
        if (driver.BestLapTime > 0 && ahead != null && ahead.BestLapTime > 0)
        {
            double delta = driver.BestLapTime - ahead.BestLapTime;
            if (delta < 0) delta = 0; // defensive: ordered list should keep this positive
            DrawValueText(c, rect, valueRight, Cached(deltaText, delta, static d => $"+{d:0.000}"),
                          TextSecondary, Fonts.Inter.SemiBold, 16.5f, globalAlpha);
        }
        else
        {
            DrawValueText(c, rect, valueRight, "—",
                          TextSecondary, Fonts.Inter.SemiBold, 16.5f, globalAlpha);
        }
    }

    // ---- No name/value overlap (2026-10-18, Angelo) -----------------------------
    // Width of exactly what DrawValueColumn will draw for this row (same branch
    // order + fonts), so the driver name can stop just short of it. Reused paints:
    // no per-row allocation.
    readonly SKPaint valMeasSemi = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 16.5f };
    readonly SKPaint valMeasBold = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 16.5f };
    readonly SKPaint valMeasPit  = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };
    readonly SKPaint valMeasLead = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13.5f };

    float MeasureValueWidth(Driver driver, Driver? ahead, bool isLeader, bool isPit,
                            RowState state, bool isRace, double elapsedMs)
    {
        if (pitBoard) return MeasurePitValue(driver, isPit);
        if (isPit)
            return TextBlobs.Measure("PIT", valMeasPit) + 3 * (0.22f * 10f / 4f);
        if (state.IsFlashing(elapsedMs) && state.FlashLapTime > 0)
            return TextBlobs.Measure(Cached(lapText, state.FlashLapTime, static t => LapTimeFormat.Lap(t)), valMeasSemi);
        if (isRace)
        {
            if (isLeader) return TextBlobs.Measure("LEADER", valMeasLead) + 6 * (0.08f * 11f);
            bool lapDown = driver.LapDifference != 0;
            return TextBlobs.Measure(FormatInterval(driver), lapDown ? valMeasBold : valMeasSemi);
        }
        if (isLeader)
            return TextBlobs.Measure(driver.BestLapTime > 0 ? Cached(lapText, driver.BestLapTime, static t => LapTimeFormat.Lap(t)) : "—", valMeasSemi);
        if (driver.BestLapTime > 0 && ahead != null && ahead.BestLapTime > 0)
        {
            double delta = Math.Max(0, driver.BestLapTime - ahead.BestLapTime);
            return TextBlobs.Measure(Cached(deltaText, delta, static d => $"+{d:0.000}"), valMeasSemi);
        }
        return TextBlobs.Measure("—", valMeasSemi);
    }

    // ----- PIT STOPS page (2026-10-24, Angelo) -----
    // Two quiet lines in the value column: the count on top ("2" + a small STOPS), the laps underneath
    // ("L9 · L17", last three) - or IN PIT, softly pulsing amber, while the car is in the lane right now.
    const float PitUnitTrack = 0.9f, PitLapsTrack = 0.6f;

    static string PitLapsText(IReadOnlyList<int> laps)
    {
        if (laps.Count == 0) return "";
        int from = Math.Max(0, laps.Count - 3);
        var sb = new System.Text.StringBuilder(from > 0 ? "… " : "");
        for (int i = from; i < laps.Count; i++)
        {
            if (i > from) sb.Append("  ·  ");
            sb.Append('L').Append(laps[i]);
        }
        return sb.ToString();
    }

    static float MeasureTracked(SKPaint paint, string text, float spacing)
        => string.IsNullOrEmpty(text) ? 0f : paint.MeasureText(text) + spacing * (text.Length - 1);

    float MeasurePitValue(Driver d, bool isPit)
    {
        int n = RaceDiary.PitStops(d.CarIdx);
        if (n == 0 && !isPit) return MeasureTracked(pitUnitPaint, "NO STOP", PitUnitTrack);
        string unit = n == 1 ? "STOP" : "STOPS";
        float top = pitCountPaint.MeasureText(n.ToString()) + 4f + MeasureTracked(pitUnitPaint, unit, PitUnitTrack);
        string sub = isPit ? "IN PIT" : PitLapsText(RaceDiary.PitLaps(d.CarIdx));
        return Math.Max(top, MeasureTracked(pitLapsPaint, sub, PitLapsTrack));
    }

    void DrawPitValue(SKCanvas c, SKRect rect, float right, Driver d, bool isPit, float alpha, double nowMs)
    {
        int n = RaceDiary.PitStops(d.CarIdx);
        if (n == 0 && !isPit)
        {
            pitUnitPaint.Color = TextTertiary.WithAlpha((byte)(215 * alpha));   // #340: was 170
            pitUnitPaint.GetFontMetrics(out var um);
            TextTracked.Draw(c, pitUnitPaint, "NO STOP", right, rect.MidY - (um.Ascent + um.Descent) / 2f, PitUnitTrack, alignRight: true);
            return;
        }
        // line 1: count + unit
        string unit = n == 1 ? "STOP" : "STOPS";
        float y1 = rect.MidY + 1f;
        pitUnitPaint.Color = TextTertiary.WithAlpha((byte)(230 * alpha));
        float uw = MeasureTracked(pitUnitPaint, unit, PitUnitTrack);
        TextTracked.Draw(c, pitUnitPaint, unit, right, y1, PitUnitTrack, alignRight: true);
        pitCountPaint.Color = TextPrimary.WithAlpha((byte)(255 * alpha));
        TextBlobs.Draw(c, SmallStrings.Num(n), right - uw - 4f, y1, pitCountPaint);
        // line 2: laps, or IN PIT
        float y2 = rect.MidY + 15f;
        if (isPit)
        {
            float pulse = 0.65f + 0.35f * (float)(0.5 + 0.5 * Math.Sin(nowMs / 1000.0 * Math.PI * 1.6));
            pitLapsPaint.Color = PitAmber.WithAlpha((byte)(255 * alpha * pulse));
            TextTracked.Draw(c, pitLapsPaint, "IN PIT", right, y2, PitLapsTrack, alignRight: true);
        }
        else
        {
            pitLapsPaint.Color = TextSecondary.WithAlpha((byte)(225 * alpha));
            TextTracked.Draw(c, pitLapsPaint, PitLapsText(RaceDiary.PitLaps(d.CarIdx)), right, y2, PitLapsTrack, alignRight: true);
        }
    }

    void DrawValueText(
        SKCanvas c, SKRect rect, float right, string text,
        SKColor color, SKTypeface typeface, float textSize, float globalAlpha)
    {
        var paint = valueTextPaint;   // { IsAntialias = true, TextAlign = Right } + these three, every use
        paint.Color = color.WithAlpha((byte)(255 * globalAlpha));
        paint.Typeface = typeface;
        paint.TextSize = textSize;
        paint.GetFontMetrics(out var m);
        float baseline = rect.MidY - (m.Ascent + m.Descent) / 2f;
        TextBlobs.Draw(c, text, right, baseline, paint);
    }

    // ----- formatters -----

    string FormatInterval(Driver d)
    {
        if (d.LapDifference > 0)
            return Cached(lapsDownText, d.LapDifference, static n => $"+{n}L");
        if (d.IntervalSeconds is double s && s > 0)
            return Cached(gapText, s, static v => LapTimeFormat.Gap(v));
        return "—";
    }

    // Race vs everything else (qualifying / practice / warmup). Same shape
    // as the helper in BattleCardRenderer — substring-match the iRacing
    // session-type name. Kept as a local copy so the two renderers don't
    // grow a shared static-helper module just for one method.
    static bool LooksLikeRace(string? sessionTypeName)
        => !string.IsNullOrEmpty(sessionTypeName)
           && sessionTypeName.IndexOf("race", StringComparison.OrdinalIgnoreCase) >= 0;

    static string EllipsiseToWidth(string text, SKPaint paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        float w = paint.MeasureText(text);
        if (w <= maxWidth) return text;

        const string ellipsis = "…";
        float ellipsisW = paint.MeasureText(ellipsis);
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            float candW = paint.MeasureText(text.AsSpan(0, mid).ToString()) + ellipsisW;
            if (candW <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return text.AsSpan(0, lo).ToString() + ellipsis;
    }

    // ----- session best -----

    static float ComputeSessionBest(RaceSnapshot snap)
    {
        float best = 0;
        var cl = snap.ClassLeaderboard;
        for (int ci = 0; ci < cl.Count; ci++)
        {
            var ds = cl[ci].Drivers;
            for (int di = 0; di < ds.Count; di++)
            {
                var d = ds[di];
                if (d.BestLapTime > 0 && (best == 0 || d.BestLapTime < best))
                    best = d.BestLapTime;
            }
        }
        return best;
    }

    // ----- per-row state tracker -----

    RowState UpdateRowState(Driver driver, double nowMs)
    {
        if (!rowStateByCarIdx.TryGetValue(driver.CarIdx, out var s))
        {
            s = new RowState
            {
                LastPosition = driver.PositionInClass,
                LastLapsCompleted = driver.LapsCompleted,
                LastBestLap = driver.BestLapTime,
                // If we first see a driver while they're on pit road, treat
                // their current lap as already invalid so the next crossing
                // doesn't fire a spurious flash for what's actually an out-lap.
                WasInPitDuringLap = driver.IsInPit,
            };
            rowStateByCarIdx[driver.CarIdx] = s;
            return s;
        }

        // Position change: positive = gained places (lower number = better)
        if (driver.PositionInClass != s.LastPosition && driver.PositionInClass > 0)
        {
            s.ArrowDirection = driver.PositionInClass < s.LastPosition ? +1 : -1;
            s.ArrowAtMs = nowMs;
            s.LastPosition = driver.PositionInClass;
        }
        s.ArrowFraction = s.ArrowDirection == 0
            ? 0f
            : (float)Math.Clamp(1.0 - (nowMs - s.ArrowAtMs) / ArrowTtlMs, 0, 1);
        if (s.ArrowFraction <= 0f) s.ArrowDirection = 0;

        // Lap completion: lap counter incremented. Drives the white lap pulse
        // (already), and (Session 22) the 10 s valid-lap flash. A lap is valid
        // iff the driver was never on pit road during it — that rejects both
        // in-laps (entered pits during the lap) and out-laps (left pits, so
        // we observed pit-road at the start of the lap).
        if (driver.LapsCompleted > s.LastLapsCompleted)
        {
            bool valid = !s.WasInPitDuringLap;
            s.LapPulseAtMs = nowMs;
            s.LastLapsCompleted = driver.LapsCompleted;
            // Reset for the new lap; the trailing OR below will re-mark it
            // if the driver is still on pit road right now.
            s.WasInPitDuringLap = false;

            if (valid && driver.LastLapTime > 0)
            {
                s.FlashUntilMs = nowMs + ValidLapFlashMs;
                s.FlashLapTime = driver.LastLapTime;
            }
        }
        s.LapPulseFraction = (float)Math.Clamp(1.0 - (nowMs - s.LapPulseAtMs) / LapPulseMs, 0, 1);

        // Track pit-road presence across the current lap. Once true, stays
        // true until the next LapsCompleted increment resets it above.
        if (driver.IsInPit) s.WasInPitDuringLap = true;

        // Personal best improved: new best lap is shorter than previous best.
        if (driver.BestLapTime > 0 && (s.LastBestLap <= 0 || driver.BestLapTime < s.LastBestLap - 0.001f))
        {
            // Skip the very first time we see a best (no pulse for "first ever lap").
            if (s.LastBestLap > 0) s.PbPulseAtMs = nowMs;
            s.LastBestLap = driver.BestLapTime;
        }
        s.PbPulseFraction = (float)Math.Clamp(1.0 - (nowMs - s.PbPulseAtMs) / PbPulseMs, 0, 1);

        return s;
    }

    // ----- logo cache -----

    SKBitmap? LoadLogo(string? brand)
    {
        if (noImages) return null;
        var slug = ManufacturerBrand.Resolve(brand);
        if (slug == null) return null;
        lock (logoGate) if (logoCache.TryGetValue(slug, out var cached)) return cached;

        SKBitmap? bmp = null;
        try
        {
            var path = Assets.Resolve("logos", "manufacturers", slug + ".png");
            if (System.IO.File.Exists(path))
            {
                using var stream = System.IO.File.OpenRead(path);
                bmp = Assets.Decode(stream);
            }
        }
        catch
        {
            bmp = null;
        }
        lock (logoGate)
        {
            if (logoCache.TryGetValue(slug, out var other)) { bmp?.Dispose(); return other; }   // the other thread won
            logoCache[slug] = bmp;
        }
        return bmp;
    }

    SKBitmap? LoadClassLogo(string? classLabel)
    {
        if (noImages) return null;
        var slug = ClassLogo.Resolve(classLabel);
        if (slug == null) return null;
        var key = "class:" + slug;
        lock (logoGate) if (logoCache.TryGetValue(key, out var cached)) return cached;

        SKBitmap? bmp = null;
        try
        {
            var path = Assets.Resolve("logos", "classes", slug + ".png");
            if (System.IO.File.Exists(path))
            {
                using var stream = System.IO.File.OpenRead(path);
                bmp = Assets.Decode(stream);
            }
        }
        catch
        {
            bmp = null;
        }
        lock (logoGate)
        {
            if (logoCache.TryGetValue(key, out var other)) { bmp?.Dispose(); return other; }
            logoCache[key] = bmp;
        }
        return bmp;
    }

    // ----- hex helper -----

    static SKColor ParseHex(string hex, byte alpha = 255)
    {
        var s = hex.AsSpan();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6) return new SKColor(255, 255, 255, alpha);
        byte r = byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SKColor(r, g, b, alpha);
    }

    public void Dispose()
    {
        glassPaint.Dispose();
        hairlinePaint.Dispose();
        rowOddPaint.Dispose();
        rowEvenPaint.Dispose();
        numBgPaint.Dispose();
        numTextPaint.Dispose();
        licTextPaintLight.Dispose();
        licTextPaintDark.Dispose();
        lock (logoGate) { foreach (var b in logoCache.Values) b?.Dispose(); logoCache.Clear(); }
        brandLogos.Clear();
        foreach (var kp in new[] { statVal, statLab, aaShaderPaint, solidPaint, playerInsetPaint, playerBorderPaint, classNamePaint, classCountPaint,
                                   posPaint, arrowPaint, logoPaint, namePaint, fadeMaskPaint, pitTagPaint, leaderTagPaint, valueTextPaint, sectorPaint })
            kp.Dispose();
        arrowPath.Dispose();
        foreach (var f in logoAlphaFilters) f?.Dispose();
        dividerShader.Dispose(); playerShader.Dispose(); leaderShader.Dispose();
        foreach (var fs in nameFadeShaders.Values) fs.Dispose();
        gateLayerPaint.Dispose();
        warmBlur?.Dispose();
        iracingWmLogo?.Dispose();
        wmPaint.Dispose();
        seriesCapLogo?.Dispose();
        capLogoPaint.Dispose();
        wmBackdropImage?.Dispose();
        wmCapImage?.Dispose();
        seriesCapImage?.Dispose();
        wings.Dispose();
    }

    sealed class RowState
    {
        public int LastPosition;
        public int LastLapsCompleted;
        public float LastBestLap;

        public int ArrowDirection; // +1 gain, -1 loss, 0 none
        public double ArrowAtMs;
        public float ArrowFraction;

        public double LapPulseAtMs = -1e9;
        public float LapPulseFraction;

        public double PbPulseAtMs = -1e9;
        public float PbPulseFraction;

        // Valid-lap flash: 10 s window where the row's value column shows the
        // just-completed lap time instead of the session-type default. The
        // WasInPitDuringLap flag tracks whether the driver touched pit road
        // anywhere across the current lap — if so, the lap is rejected as an
        // in-lap or out-lap and no flash fires.
        public bool WasInPitDuringLap;
        public double FlashUntilMs = -1;
        public float FlashLapTime;

        public bool IsFlashing(double nowMs) => nowMs < FlashUntilMs;
    }
}

/// <summary>
/// Per-glyph letter-spacing helper, lifted from the session bar's
/// <c>DrawTextTracked</c>. SkiaSharp 2.88 doesn't expose letter-spacing on
/// <see cref="SKPaint"/>; this lays out glyphs one at a time using
/// <see cref="SKPaint.GetGlyphWidths(string)"/>.
/// </summary>
internal static class TextTracked
{
    // 2026-10-24 (choppy intro): drawn from cached text blobs (TextBlobs) - same glyph positions as the old
    // per-character DrawText loop, without a float[] + one string + one text blob per character every frame.
    public static void Draw(SKCanvas c, SKPaint paint, string text, float x, float y,
                            float letterSpacingPx, bool alignRight = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (letterSpacingPx <= 0.01f)
        {
            TextBlobs.Draw(c, text, x, y, paint);
            return;
        }
        TextBlobs.DrawTracked(c, text, x, y, letterSpacingPx, paint);
    }

    // #340 (2026-10-27, legibility): a soft dark halo under small text that sits straight on the video (no plate),
    // so a light label stays readable over a bright sky or a white kerb from across the room. One paint + one blur
    // per thread, built once - no per-frame allocation.
    [ThreadStatic] static SKPaint? haloPaint;
    static SKMaskFilter? haloBlur;

    /// <summary>Draws <paramref name="text"/> with a soft dark halo behind it (same tracking / alignment as <see cref="Draw"/>).
    /// <paramref name="strength"/> scales the halo's opacity (1 = 60% black at full text alpha).</summary>
    public static void DrawHalo(SKCanvas c, SKPaint paint, string text, float x, float y,
                                float letterSpacingPx, bool alignRight = false, float strength = 1f)
    {
        if (string.IsNullOrEmpty(text)) return;
        haloBlur ??= SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.8f);
        var h = haloPaint ??= new SKPaint { IsAntialias = true };
        h.MaskFilter = haloBlur;
        h.Typeface = paint.Typeface; h.TextSize = paint.TextSize; h.TextAlign = paint.TextAlign; h.TextScaleX = paint.TextScaleX;
        h.Color = new SKColor(0, 0, 0, (byte)Math.Clamp(153f * strength * paint.Color.Alpha / 255f, 0f, 255f));
        Draw(c, h, text, x, y + 0.6f, letterSpacingPx, alignRight);
        Draw(c, paint, text, x, y, letterSpacingPx, alignRight);
    }
}
