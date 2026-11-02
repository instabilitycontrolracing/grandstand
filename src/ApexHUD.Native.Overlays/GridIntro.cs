using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-18 (Angelo): pre-race broadcast OPENER, played the first time the
/// starting-grid board would appear. Timed to the grid_opener theme's bars
/// (~125 BPM, bar = 1.92 s; first big downbeat 5.85 s):
///   0.00  dark plate fades in, gold hairline sweeps out from centre (build-up)
///   5.85  iRacing logo punches in on the downbeat
///   7.77  series logo (or series name)          - skipped if unknown
///   9.69  track logo (assets/logos/tracks) or country flag + track name
///  11.61  plate dissolves into the starting-grid board
/// The clock is the MUSIC clock: play_sound.ps1 writes the exact UTC instant the
/// audio started (opener_started.txt); until it arrives we estimate.
/// </summary>
public sealed class GridIntro : IDisposable
{
    // Timeline v2 (Angelo 2026-10-18), each change snapped to a measured hit in the theme:
    //   0.00 iRacing logo fades in with the plate
    //   4.40 series logo     9.21 track logo     12.07 starting grid - top class
    //  19.75 next class      25.52 third class, or the TRACK MAP if only two classes
    //  35.39 music ends -> normal 30 s on / 30 s off board cycle
    public const double MapS = 9.21;    // track logo slides up, map draws in beneath (a hit in the theme)
    public const double MapDrawS = 1.8;         // the racing line traces itself in this long
    public const double BoardOutS = 32.21;      // board leaves on the song's last big hit   // track logo slides up, map draws in beneath (a hit in the theme)
    public const double SeriesS = 4.40, TrackS = 8.00, GridS = 12.07, Class2S = 19.75, Class3S = 25.52,   // 3rd class two beats earlier (was 27.41): middle class shorter, top class longer (Angelo 2026-10-19)
                        MusicEndS = 35.39;
    /// <summary>The globe's own beat (Angelo 2026-10-20). On the ORIGINAL theme: the iRacing logo,
    /// then the series logo takes over on the beat at SeriesEarlyS (one combined opening page),
    /// the globe lands on the series hit (SeriesS) and dives into the track hit (TrackS).
    /// Without a ready globe the original layout plays (series page from SeriesS).</summary>
    public const double SeriesEarlyS = 2.53, GlobeBarS = TrackS - SeriesS,
                        GlobeInS = SeriesS, GlobeOutS = TrackS;
    public static bool GlobeMusicExists => SoundFiles.Find("grid_opener_globe", ".mp3", ".m4a", ".wav", ".wma") != null;
    public static readonly double[] Hits = { 0.0, SeriesS, TrackS, GridS };
    public const double DissolveS = 0.6;
    public static double EndS => GridS + PlateHoldS + DissolveS;
    // the plate holds a moment past the grid hit so the zoom-through window can open first
    public const double PlateHoldS = 0.30;
    // zoom-through: starts just before the grid hit, the camera dives through the track outline
    public const double BlastStartS = GridS - 0.12, BlastLenS = 0.62;

    static readonly SKColor Gold = new(0xE8, 0xC2, 0x5A);
    static readonly SKColor Txt  = new(0xF4, 0xF6, 0xFA);
    static readonly SKColor Txt3 = new(0xB4, 0xBE, 0xCE);   // #340 legibility: was 7E8AA0

    double shine = -1;               // logo light-sweep progress (0..1 while sweeping)
    double launchMs = -1;            // compositor clock at launch
    DateTime launchUtc;
    double audioStartMs = -1;        // compositor clock when the music actually started
    bool expectAudio;

    readonly Dictionary<string, SKBitmap?> cache = new(StringComparer.OrdinalIgnoreCase);

    // 2026-10-24 (Angelo: "the intro is horribly choppy"): paints, shaders and paths kept between frames, and the
    // track's pictures / outline / layout worked out once per track (in the background before the opener) instead
    // of on every frame - the opener used to make dozens of SKPaints and shaders, and look the track logo up by
    // name (string normalising) and its map by file name, every frame. Measured with tools/intro_bench.
    readonly SKPaint fill = new(), layer = new(), text = new();
    readonly ShaderSlot globeFlash = new(), seriesGlow = new(), hitFlash = new(), shineSlot = new(), cometSlot = new(),
        irPin = new(), irPool = new(), irVig = new();
    readonly ShaderSlot[] irBlobs = { new(), new(), new(), new() };
    readonly SKPath holePath = new(), linePath = new(), lineSeg = new();
    SKPathMeasure? lineMeasure; SKPath? lineSrc; SKRect lineDst; float lineScale;
    static readonly SKMaskFilter lineGlowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7);
    static readonly SKImageFilter mapGlow = SKImageFilter.CreateDropShadowOnly(0, 0, 9, 9, Gold.WithAlpha(170));

    public bool Running => launchMs >= 0;

    public void Start(double nowMs, bool audioLaunched, bool globe = false)
    {
        launchMs = nowMs; launchUtc = DateTime.UtcNow; audioStartMs = -1; expectAudio = audioLaunched; globeOn = globe;
    }

    /// <summary>2026-10-20 (Angelo): the globe gets its own full-screen beat between the series page
    /// and the track page. Skipped (original theme + timings) when the globe isn't prepared yet.</summary>
    public static bool GlobeOpener = true;
    bool globeOn;
    public void Reset() { launchMs = -1; audioStartMs = -1; }

    /// <summary>Theme clock: seconds into the ORIGINAL theme. With the globe beat on, the series page
    /// holds through the repeated bar and the globe's beat, and the clock resumes at the track hit.</summary>
    public double T(double nowMs)
    {
        return RawT(nowMs);   // (2026-10-20: the globe now sits inside the original theme - no extra bar)
    }
    public bool GlobeOn => globeOn;

    /// <summary>Seconds since the music started (estimated until the player reports).</summary>
    double RawT(double nowMs)
    {
        if (launchMs < 0) return -1;
        if (expectAudio && audioStartMs < 0)
        {
            try
            {
                long ticks = OpenerAudio.StartedUtcTicks;      // the in-process player reports the exact start
                if (ticks > 0)
                {
                    var started = new DateTime(ticks, DateTimeKind.Utc);
                    double offs = (started - launchUtc).TotalMilliseconds;
                    if (offs >= -200 && offs < 8000) audioStartMs = launchMs + Math.Max(0, offs);
                }
            }
            catch { }
            if (audioStartMs < 0 && nowMs - launchMs > 4000) audioStartMs = launchMs + 400;   // player never reported
        }
        double start = audioStartMs >= 0 ? audioStartMs : launchMs + (expectAudio ? 400 : 0);
        return (nowMs - start) / 1000.0;
    }

    public bool Done(double nowMs) => Running && T(nowMs) >= EndT;

    /// <summary>When the camera dives through the track outline. Normally on the grid hit
    /// (GridS). Le Mans (Angelo 2026-10-19): the track page stays up as the backdrop behind
    /// every class page, holds alone for 2 s after the last class leaves, THEN dives.</summary>
    public double DiveS = GridS;
    /// <summary>Where the series logo sits across the screen on its window page (0.5 = centre).</summary>
    public const float SeriesX = 0.70f;
    public double EndT => DiveS + PlateHoldS + DissolveS;
    public const double HoldAfterBoardS = 2.0;

    /// <summary>Tracks whose intro keeps the track page up behind the grid. Le Mans first (2026-10-19); since
    /// 2026-10-28 (Angelo: "everything Le Mans has at every track") every track whose map Grandstand has.</summary>
    public static bool HoldsTrackPlate(RaceSnapshot s)
    {
        foreach (var n in new[] { s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName, s.TrackConfigName })
            if (!string.IsNullOrEmpty(n) && (n.Contains("Le Mans", StringComparison.OrdinalIgnoreCase)
                || n.Contains("lemans", StringComparison.OrdinalIgnoreCase) || n.Contains("du Mans", StringComparison.OrdinalIgnoreCase)))
                return true;
        try { return TrackMapCache.Resolve(s.TrackConfigName, s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName) != null; }
        catch { return false; }
    }

    /// <summary>Whole-screen rect in the same coordinates as the board card; the splash
    /// pages fill it. Empty = draw in the card only.</summary>
    public SKRect Screen;

    // --------------------------------------------------------------- drawing
    /// <summary>The globe's full-screen beat: enters on the repeated series hit with a flash, turns to
    /// the circuit, locks on, and dives in to land on the track hit.</summary>
    void DrawGlobeBeat(SKCanvas c, SKRect card, RaceSnapshot snap, double u)
    {
        var scr = Screen.IsEmpty ? card : Screen;
        int sv = c.Save();
        c.ClipRect(scr);
        fill.Reset(); fill.Color = new SKColor(0x03, 0x06, 0x0E); c.DrawRect(scr, fill);
        GlobeIntro.DrawSplash(c, scr, snap, u, 1f);
        float fl = (float)Math.Clamp(1 - u / 0.4, 0, 1);
        if (fl > 0)
        {
            fill.Reset();
            fill.Shader = globeFlash.Radial(new SKPoint(scr.MidX, scr.MidY), Math.Max(scr.Width, scr.Height) * 0.6f,
                [new SKColor(255, 255, 255, (byte)(150 * fl)), new SKColor(255, 255, 255, (byte)(40 * fl)), SKColors.Transparent], Stops.At05);
            c.DrawRect(scr, fill);
        }
        c.RestoreToCount(sv);
    }

    /// <summary>Intro watcher (2026-10-24): the opener's music time this frame, or -1 when it isn't drawing.
    /// The host resets it before each frame; the frame log uses it to tag every frame of the intro.</summary>
    public static double LiveRawT = -1;
    /// <summary>Set by the host when the globe's dress rehearsal for this track has finished (the early opener waits for it).</summary>
    public static bool RehearsalDone;
    static readonly int PBg = Prof.Slot("intro.bg"), PLogoIr = Prof.Slot("intro.iracinglogo"), PLogoSeries = Prof.Slot("intro.serieslogo"),
        PTrack = Prof.Slot("intro.track"), PPlate = Prof.Slot("intro.plate"), PWarm = Prof.Slot("intro.warm");

    public void Draw(SKCanvas c, SKRect card, RaceSnapshot snap, double nowMs)
    {
        double t = T(nowMs);
        if (t < -0.5 || t >= EndT) return;
        LiveRawT = RawT(nowMs);
        if (t < 0) t = 0;
        GlobeIntro.Prepare(snap);
        if (globeOn)
        {
            double raw = RawT(nowMs);
            if (raw >= GlobeInS && raw < GlobeOutS) { DrawGlobeBeat(c, card, snap, raw - GlobeInS); return; }
            // get the globe pictures onto the graphics card early (and its place names laid out for the screen it fills)
            if (raw > 0.5 && raw < GlobeInS) { long pw0 = Prof.T(); GlobeGT.Warm(c, Screen.IsEmpty ? card : Screen); Prof.Lap(PWarm, pw0); }
        }
        // the track page's pictures and layout (worked out once per track; normally ready from Prewarm)
        TrackInfo? ti = t >= TrackS ? Track(snap) : null;
        if (t < TrackS) { long pw1 = Prof.T(); WarmGpu(c, Screen.IsEmpty ? card : Screen, snap); Prof.Lap(PWarm, pw1); }

        float plateA = (float)Math.Clamp(t / 1.2, 0, 1);
        if (t > DiveS + PlateHoldS) plateA *= 1f - (float)((t - DiveS - PlateHoldS) / DissolveS);
        plateA = Smooth(plateA);
        if (plateA <= 0.003f) return;

        // plate - on the track page it takes the track logo's own background colour
        // (e.g. Le Mans navy) so the logo sits seamlessly on the whole plate
        SKColor plateCol = new(0x0B, 0x0E, 0x14);
        float plateMax = 236;
        if (t >= TrackS)
        {
            var tm = ti!.Matte;
            if (tm is SKColor m)
            {
                float pk = Smooth((float)Math.Clamp((t - TrackS) / 0.15, 0, 1));
                plateCol = new SKColor((byte)(plateCol.Red + (m.Red - plateCol.Red) * pk),
                                       (byte)(plateCol.Green + (m.Green - plateCol.Green) * pk),
                                       (byte)(plateCol.Blue + (m.Blue - plateCol.Blue) * pk));
                plateMax = 236 + 19 * pk;
            }
        }
        // FULL-SCREEN splash (Angelo 2026-10-18): the iRacing / series / track pages fill the
        // whole screen (solid), then just before the grid arrives the plate shrinks back into
        // the normal translucent board box. m: 0 = full screen, 1 = board box.
        var scr = Screen.IsEmpty ? card : Screen;
        // The plate stays full screen and dissolves while the map blasts toward the camera,
        // revealing the normal-size grid board underneath (no shrink - it fought the blast).
        float shrink = 0f;
        var plate = new SKRect(scr.Left + (card.Left - scr.Left) * shrink, scr.Top + (card.Top - scr.Top) * shrink,
                               scr.Right + (card.Right - scr.Right) * shrink, scr.Bottom + (card.Bottom - scr.Bottom) * shrink);
        float radius = 12f * shrink;
        float s0 = Math.Min(scr.Width / card.Width, scr.Height / card.Height);
        float contentScale = s0 + (1f - s0) * shrink;
        plateMax = plateMax + (255 - plateMax) * (1 - shrink);   // solid while full screen
        // Zoom-through window: during the blast the INSIDE of the track outline is cut out of
        // the plate, so as the camera dives through it the grid board behind shows through.
        float blast = t >= TrackS ? (float)Math.Clamp((t - (DiveS - 0.12)) / BlastLenS, 0, 1) : 0f;
        var geo = ti != null ? TrackGeom(ti, card, 1f) : null;
        SKPath? hole = null;
        if (blast > 0 && geo?.Path != null)
        {
            var g = geo.Value;
            float bs = BlastScale(blast);
            hole = holePath;   // reused: a copy of the outline, moved with the dive
            hole.Reset(); hole.FillType = g.Path.FillType; hole.AddPath(g.Path);
            var mtx = SKMatrix.CreateScaleTranslation(g.Scale, g.Scale, g.Dst.Left, g.Dst.Top);                          // png -> card
            mtx = mtx.PostConcat(SKMatrix.CreateScale(bs, bs, g.Center.X, g.Center.Y));                                   // the dive
            mtx = mtx.PostConcat(SKMatrix.CreateTranslation(-card.MidX, -card.MidY))
                     .PostConcat(SKMatrix.CreateScale(contentScale, contentScale))
                     .PostConcat(SKMatrix.CreateTranslation(plate.MidX, plate.MidY));                                     // card -> screen
            hole.Transform(mtx);
        }
        c.Save();
        if (hole != null) c.ClipPath(hole, SKClipOperation.Difference, antialias: true);
        // Series page = a WINDOW onto the cars rolling off for the formation lap (Angelo 2026-10-19):
        // the plate clears just after the series hit and turns solid again for the track punch-in;
        // only the series logo (on a soft dark glow) floats over the live picture.
        double ss = globeOn ? SeriesEarlyS : SeriesS;   // series logo's beat
        float win = Smooth((float)Math.Clamp((t - ss) / 0.35, 0, 1)) * Smooth((float)Math.Clamp((TrackS - t) / 0.28, 0, 1));
        if (SeriesNames.ResolveLogoSlug(snap.SeriesId) == null && SeriesNames.ResolveName(snap.SeriesId) == null) win = 0;
        fill.Reset(); fill.IsAntialias = true; fill.Color = plateCol.WithAlpha((byte)(plateMax * plateA * (1 - win)));
        c.DrawRoundRect(plate, radius, radius, fill);
        c.Restore();
        fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 1;
        fill.Color = new SKColor(255, 255, 255, (byte)(26 * plateA * shrink));
        c.DrawRoundRect(plate, radius, radius, fill);

        // iRacing page backdrop: an animated red + blue design behind the logo, fading out
        // as the series logo punches in (Angelo 2026-10-19)
        if (t < ss + 0.4 && IracingBgStyle > 0)
        {
            float bgA = plateA * Smooth((float)Math.Clamp(t / 1.0, 0, 1)) * (1f - Smooth((float)Math.Clamp((t - ss) / 0.35, 0, 1)));
            long pb = Prof.T();
            if (bgA > 0.003f) DrawIracingBg(c, plate, t, bgA);
            Prof.Lap(PBg, pb);
        }

        // which beat are we on?  0 = iRacing, 1 = series, 2 = track
        int stage = t >= TrackS ? 2 : t >= ss ? 1 : 0;
        // series unknown -> the iRacing logo simply holds until the track hit
        string? seriesSlug = SeriesNames.ResolveLogoSlug(snap.SeriesId);
        string? seriesName = SeriesNames.ResolveName(snap.SeriesId);
        if (stage == 1 && seriesSlug == null && seriesName == null) stage = 0;

        // Series beat only: rows of white iRacing wordmarks racing each other across the
        // plate behind the series logo (same look as the tower header, Angelo 2026-10-18).
        if (false && (seriesSlug != null || seriesName != null))   // (no racing logo rows - the series page is a window now)
        {
            float rin  = Smooth((float)Math.Clamp((t - ss) / 0.30, 0, 1));
            float rout = Smooth((float)Math.Clamp((TrackS + 0.30 - t) / 0.30, 0, 1));
            float ra = plateA * rin * rout;
            if (ra > 0.003f) DrawRacingRows(c, plate, t - ss, ra, contentScale);
        }

        // the soft dark glow that keeps the series logo readable over the live picture
        if (win > 0.003f)
        {
            fill.Reset();
            fill.Shader = seriesGlow.Radial(new SKPoint(plate.Left + plate.Width * SeriesX, plate.MidY - 20 * contentScale), Math.Min(plate.Width, plate.Height) * 0.42f,
                [new SKColor(0, 0, 0, (byte)(170 * win * plateA)), new SKColor(0, 0, 0, (byte)(90 * win * plateA)), SKColors.Transparent], Stops.At05);
            c.DrawRect(plate, fill);
        }
        // everything below is laid out for the board box; scale it up to the full screen
        c.Save();
        c.Translate(plate.MidX, plate.MidY);
        c.Scale(contentScale);
        c.Translate(-card.MidX, -card.MidY);

        double since = t - (stage == 0 ? 0.0 : stage == 1 ? ss : Hits[stage]);
        float scale, a;
        if (stage == 0)
        {
            // iRacing: a calm fade-in with the plate (no punch, no flash)
            scale = 1.04f - 0.04f * EaseOut((float)Math.Clamp(t / 2.0, 0, 1));
            a = plateA * Smooth((float)Math.Clamp((t - 0.2) / 1.4, 0, 1));
        }
        else
        {
            // series / track: punch in on the hit, with a flash
            scale = 1.10f - 0.10f * EaseOut((float)Math.Clamp(since / 0.32, 0, 1));
            a = plateA * Smooth(Math.Min(1f, (float)(since / 0.12)));
            float flash = (float)Math.Clamp(1 - since / 0.45, 0, 1);
            if (flash > 0)
            {
                fill.Reset(); fill.IsAntialias = true;
                fill.Shader = hitFlash.Radial(new SKPoint(card.MidX, card.MidY - 20), card.Width * 0.45f,
                    [new SKColor(255, 255, 255, (byte)(70 * flash * plateA)), SKColors.Transparent], []);
                c.DrawRect(card, fill);
            }
        }
        // Track page -> grid (Angelo 2026-10-18): the camera dives THROUGH the track outline,
        // centred on the logo inside it; the logo melts first, then the line rushes past.
        // (no map: the old fade before the grid still applies)
        if (stage != 2 || geo == null)
        {
            if (t > DiveS - 0.25) a *= (float)Math.Clamp((DiveS + DissolveS * 0.5 - t) / (0.25 + DissolveS * 0.5), 0, 1);
        }
        else if (blast > 0f)
        {
            float bs = BlastScale(blast);
            var ctr = geo.Value.Center;
            c.Translate(ctr.X, ctr.Y);
            c.Scale(bs);
            c.Translate(-ctr.X, -ctr.Y);
        }
        // light sweep across the logo shortly after it lands
        shine = stage == 0 ? (t - 1.5) / 0.8 : (since - 0.15) / 0.7;
        c.Save();
        c.Translate(card.MidX, card.MidY - 20);
        c.Scale(scale);
        c.Translate(-card.MidX, -(card.MidY - 20));
        // series page (Angelo 2026-10-19): the logo sits middle-right, clear of the driver's-eye
        // VR view in the window behind it
        if (stage == 1) c.Translate((SeriesX - 0.5f) * plate.Width / contentScale, 0);
        long pl = Prof.T();
        switch (stage)
        {
            case 0: DrawLogo(c, card, IracingLogo(), 560, 130, a, null); break;
            case 1:
                var sb = SeriesLogo(seriesSlug);
                if (sb != null) DrawLogo(c, card, sb, 620, 300, a, null);
                else DrawBigText(c, card, SeriesCaps(seriesName), a);
                break;
            case 2: DrawTrack(c, card, ti!, a, t, blast); break;
        }
        Prof.Lap(stage == 0 ? PLogoIr : stage == 1 ? PLogoSeries : PTrack, pl);
        c.Restore();
        c.Restore();   // full-screen content scale
    }

    // ------------------------------------------------- iRacing page backdrop (red + blue)
    /// <summary>0 = none, 1 = split livery, 2 = warp speed, 3 = liquid colour.</summary>
    public static int IracingBgStyle = 3;   // Angelo picked liquid colour (2026-10-19)
    static readonly SKColor IrRed  = new(0xD3, 0x16, 0x2C);
    static readonly SKColor IrBlue = new(0x16, 0x3A, 0x8E);
    static readonly SKColor IrNavy = new(0x07, 0x10, 0x2A);
    // LIQUID COLOUR's four pools (were a new array every frame)
    static readonly (SKColor Col, float R, float Ox, float Oy, float Sp, float Ph)[] IrBlobs =
    {
        (new SKColor(0x1E, 0x4C, 0xC8), 0.50f, 0.32f, 0.22f, 0.28f, 2.1f),   // royal blue
        (new SKColor(0xD8, 0x14, 0x2E), 0.46f, 0.30f, 0.20f, 0.35f, 0.0f),   // iRacing red
        (new SKColor(0x0C, 0x24, 0x70), 0.42f, 0.26f, 0.25f, 0.31f, 5.3f),   // deep blue
        (new SKColor(0x9C, 0x0A, 0x22), 0.38f, 0.24f, 0.24f, 0.42f, 4.0f),   // dark crimson
    };

    static SKBitmap? pinTile;
    static SKBitmap MakePinTile()
    {
        var b = new SKBitmap(new SKImageInfo(14, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var cv = new SKCanvas(b);
        cv.Clear(SKColors.Transparent);
        using var pp = new SKPaint { Color = new SKColor(255, 255, 255, 10) };
        cv.DrawRect(new SKRect(0, 0, 2, 4), pp);
        return b;
    }

    void DrawIracingBg(SKCanvas c, SKRect p, double t, float alpha)
    {
        // 2026-10-24 (choppy intro): the full-screen offscreen layer is only needed while the page fades
        if (alpha < 0.995f) { layer.Color = new SKColor(0, 0, 0, (byte)(255 * alpha)); c.SaveLayer(p, layer); }
        else c.Save();
        c.ClipRect(p);
        float W = p.Width, H = p.Height;
        switch (IracingBgStyle)
        {
            case 1: // SPLIT LIVERY: red | blue on a drifting diagonal, a white light riding the seam, sliding stripes
            {
                using (var red = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(p.Left, p.Top), new SKPoint(p.MidX, p.Bottom),
                           new[] { new SKColor(0xE8, 0x22, 0x3A), IrRed, new SKColor(0x7A, 0x0A, 0x18) }, null, SKShaderTileMode.Clamp) })
                    c.DrawRect(p, red);
                float drift = (float)Math.Sin(t * 0.55) * W * 0.03f;
                float topX = p.MidX + W * 0.10f + drift, botX = p.MidX - W * 0.10f + drift;
                using var blueSide = new SKPath();
                blueSide.MoveTo(topX, p.Top); blueSide.LineTo(p.Right, p.Top); blueSide.LineTo(p.Right, p.Bottom); blueSide.LineTo(botX, p.Bottom); blueSide.Close();
                using (var blue = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(p.MidX, p.Top), new SKPoint(p.Right, p.Bottom),
                           new[] { new SKColor(0x23, 0x52, 0xC4), IrBlue, new SKColor(0x08, 0x16, 0x45) }, null, SKShaderTileMode.Clamp) })
                    c.DrawPath(blueSide, blue);
                // racing stripes sliding along the diagonal on both halves
                float ang = (float)Math.Atan2(p.Bottom - p.Top, botX - topX);
                c.Save();
                c.Translate(p.MidX, p.MidY); c.RotateRadians(ang - (float)Math.PI / 2); c.Translate(-p.MidX, -p.MidY);
                float slide = (float)(t * 60 % 90);
                using (var st = new SKPaint { Color = new SKColor(255, 255, 255, 14) })
                    for (float x = p.Left - W; x < p.Right + W; x += 90) c.DrawRect(new SKRect(x + slide, p.Top - H, x + slide + 26, p.Bottom + H), st);
                c.Restore();
                // the seam: a hot white line with a light pulse travelling down it
                using (var seamGlow = new SKPaint { IsAntialias = true, StrokeWidth = 22, Color = new SKColor(255, 255, 255, 60), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
                    c.DrawLine(topX, p.Top, botX, p.Bottom, seamGlow);
                using (var seam = new SKPaint { IsAntialias = true, StrokeWidth = 4, Color = new SKColor(255, 255, 255, 230) })
                    c.DrawLine(topX, p.Top, botX, p.Bottom, seam);
                float u = (float)(t / 1.6 % 1.0);
                var pt = new SKPoint(topX + (botX - topX) * u, p.Top + H * u);
                using (var pulse = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(pt, 120,
                           new[] { new SKColor(255, 255, 255, 200), new SKColor(255, 255, 255, 0) }, null, SKShaderTileMode.Clamp) })
                    c.DrawCircle(pt, 120, pulse);
                break;
            }
            case 2: // WARP SPEED: deep blue, red + blue light streaks rushing past
            {
                using (var bg = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(p.MidX, p.MidY), Math.Max(W, H) * 0.7f,
                           new[] { new SKColor(0x14, 0x2C, 0x72), IrNavy, new SKColor(0x02, 0x05, 0x12) }, new[] { 0f, 0.6f, 1f }, SKShaderTileMode.Clamp) })
                    c.DrawRect(p, bg);
                var rnd = new Random(7);
                for (int i = 0; i < 70; i++)
                {
                    float y = p.Top + (float)rnd.NextDouble() * H;
                    float len = 120 + (float)rnd.NextDouble() * 520;
                    float speed = 700 + (float)rnd.NextDouble() * 1900;
                    float thick = 1.5f + (float)rnd.NextDouble() * 4.5f;
                    bool red = rnd.NextDouble() < 0.45;
                    float span = W + len * 2;
                    float x = p.Left - len + (float)(((t * speed) + rnd.NextDouble() * span) % span);
                    var col = red ? new SKColor(0xFF, 0x2A, 0x45) : new SKColor(0x4A, 0x8C, 0xFF);
                    byte al = (byte)(90 + rnd.Next(150));
                    using var sp = new SKPaint { IsAntialias = true, StrokeWidth = thick, StrokeCap = SKStrokeCap.Round,
                        Shader = SKShader.CreateLinearGradient(new SKPoint(x - len, y), new SKPoint(x, y),
                            new[] { col.WithAlpha(0), col.WithAlpha(al), SKColors.White.WithAlpha(al) }, new[] { 0f, 0.85f, 1f }, SKShaderTileMode.Clamp) };
                    c.DrawLine(x - len, y, x, y, sp);
                }
                break;
            }
            case 3: // LIQUID COLOUR: red and blue pools swirling into each other + pinstripes + light sweep
            {
                fill.Reset(); fill.Color = IrNavy; c.DrawRect(p, fill);
                // normal blending: each pool keeps its true red or blue, only the edges mix
                for (int i = 0; i < IrBlobs.Length; i++)
                {
                    var b = IrBlobs[i];
                    double ang = t * b.Sp + b.Ph;
                    var ctr = new SKPoint(p.MidX + (float)Math.Cos(ang) * W * b.Ox, p.MidY + (float)Math.Sin(ang * 1.3) * H * b.Oy);
                    float rad = Math.Max(W, H) * b.R;
                    // the pool's gradient is built once around (0,0) and moved with the canvas (2026-10-24)
                    fill.Reset();
                    fill.Shader = irBlobs[i].Radial(default, rad, [b.Col.WithAlpha(245), b.Col.WithAlpha(160), b.Col.WithAlpha(0)], Stops.At045);
                    c.Save(); c.Translate(ctr.X, ctr.Y);
                    c.DrawRect(new SKRect(p.Left - ctr.X, p.Top - ctr.Y, p.Right - ctr.X, p.Bottom - ctr.Y), fill);
                    c.Restore();
                }
                // pinstripes: one tiled pattern instead of ~800 separate rectangles at 4K (2026-10-24)
                pinTile ??= MakePinTile();
                var pm = SKMatrix.CreateRotationDegrees(-30, p.MidX, p.MidY);
                fill.Reset(); fill.Shader = irPin.Bitmap(pinTile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, pm);
                c.DrawRect(p, fill);
                // (no light sweep across the colour - Angelo 2026-10-19)
                break;
            }
        }
        // keep the white logo popping: a soft dark pool behind it + an edge vignette
        fill.Reset();
        fill.Shader = irPool.Radial(new SKPoint(p.MidX, p.MidY - 20), Math.Min(W, H) * 0.55f, [new SKColor(0, 0, 0, 120), new SKColor(0, 0, 0, 0)], []);
        c.DrawRect(p, fill);
        fill.Shader = irVig.Radial(new SKPoint(p.MidX, p.MidY), Math.Max(W, H) * 0.75f,
            [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 150)], Stops.At06);
        c.DrawRect(p, fill);
        c.Restore();
    }

    // ------------------------------------------------- racing iRacing wordmark rows
    // (height px, alpha, speed px/s, gap px, phase px) - every row runs left -> right
    // at its own pace so the rows look like they are racing each other.
    static readonly (float H, byte A, float Speed, float Gap, float Phase)[] RaceRows =
    {
        (26f, 22, 230f, 70f,   0f),
        (34f, 30, 175f, 90f, 120f),
        (22f, 18, 290f, 60f,  40f),
        (38f, 34, 150f, 100f, 260f),
        (24f, 20, 260f, 64f, 180f),
        (32f, 28, 200f, 84f,  70f),
        (22f, 18, 310f, 58f, 220f),
    };
    SKImage? raceRowImage; bool raceRowTried;
    readonly SKPaint raceRowPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };

    void DrawRacingRows(SKCanvas c, SKRect card, double t, float alpha, float scale = 1f)
    {
        if (!raceRowTried)
        {
            raceRowTried = true;
            var bmp = Bitmap("logos", "iracing-brand", "iRacing-Inline-BW-White.png");
            if (bmp != null && bmp.Height > 0)
            {
                int h = 80, w = Math.Max(1, (int)(bmp.Width * (h / (double)bmp.Height)));
                using var scaled = bmp.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), SKFilterQuality.High);
                if (scaled != null) raceRowImage = SKImage.FromBitmap(scaled);
            }
        }
        if (raceRowImage is null) return;
        float aspect = raceRowImage.Width / (float)raceRowImage.Height;

        c.Save();
        using (var rr = new SKRoundRect(card, 12, 12)) c.ClipRoundRect(rr, antialias: true);
        int n = RaceRows.Length;
        for (int r = 0; r < n; r++)
        {
            var row = RaceRows[r];
            float rh = row.H * scale;
            float w = rh * aspect, period = w + row.Gap * scale;
            // quick launch: rows sprint in a little faster for the first half second
            double dist = (row.Speed * t + row.Speed * 0.6 * (1 - Math.Exp(-t / 0.5)) + row.Phase) * scale;
            float off = (float)(dist % period);
            float top = card.Top + card.Height * (r + 0.5f) / n - rh / 2f;
            raceRowPaint.Color = new SKColor(255, 255, 255, (byte)(row.A * alpha));
            for (float px = card.Left - period + off; px < card.Right; px += period)
                c.DrawImage(raceRowImage, new SKRect(px, top, px + w, top + rh), raceRowPaint);
        }
        c.Restore();
    }

    void DrawBuildText(SKCanvas c, SKRect card, double t, float plateA)
    {
        // quiet "coming up" text during the build-up
        float a = plateA * Smooth((float)Math.Clamp((t - 1.0) / 1.0, 0, 1)) * (float)Math.Clamp((Hits[0] - 0.15 - t) / 0.4, 0, 1);
        if (a <= 0.01f) return;
        using var p = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13, TextAlign = SKTextAlign.Center,
                                    Color = Txt3.WithAlpha((byte)(255 * a)) };
        TextTracked.Draw(c, p, "THE GRID IS FORMING", card.MidX, card.MidY + 6, 0.5f * 13f);
    }

    // Track page geometry: the circuit fills the page, and the logo lives INSIDE the loop.
    readonly record struct TrackGeo(SKRect Dst, float Scale, SKPoint Center, SKPath? Path, float LogoScale, SKPoint LogoAt);

    // logo + caption block at full size (DrawTrackHeader / DrawLogo): roughly centred on the pivot
    static (float W, float H) HeaderBlock(SKBitmap? logo, string name)
    {
        if (logo == null) return (Math.Min(1000f, 40f + name.Length * 38f), 190f);   // flag + big name
        float s = Math.Min(600f / logo.Width, 230f / logo.Height);
        using var p = new SKPaint { Typeface = Fonts.Inter.Bold, TextSize = 14 };
        float cap = p.MeasureText(name) + 0.4f * 14f * Math.Max(0, name.Length - 1);
        return (Math.Max(logo.Width * s, cap) + 24f, logo.Height * s + 64f);
    }

    /// <summary>Everything the track page needs that only depends on the track: worked out once per track
    /// (2026-10-24), in the background before the opener when <see cref="Prewarm"/> got there first.</summary>
    sealed record TrackInfo(string? Display, string? Broadcast, string? Raw, string? Config, string? Country, int Gen,
                            SKBitmap? Logo, SKColor? Matte, SKBitmap? Map, SKPath? Path, string Name, float HeadW, float HeadH, SKRect? Hole, SKBitmap? Flag);
    readonly object trackGate = new();
    TrackInfo? trackInfo;

    TrackInfo Track(RaceSnapshot snap)
    {
        lock (trackGate)
        {
            RefreshMaps();
            var t = trackInfo;
            if (t != null && t.Gen == mapGen && t.Display == snap.TrackDisplayName && t.Broadcast == snap.TrackBroadcastName
                && t.Raw == snap.TrackRawName && t.Config == snap.TrackConfigName && t.Country == snap.TrackCountryCode) return t;
            var logo = TrackLogo(snap);
            var matte = Matte(logo).Matte;
            var b = MapBase(snap);
            var map = b == null ? null : Bitmap("maps", b + ".png");
            var path = map == null ? null : MapPath(snap);
            string name = (snap.TrackDisplayName ?? snap.TrackBroadcastName ?? "").ToUpperInvariant();
            var (hw, hh) = HeaderBlock(logo, name);
            var hole = path == null || map == null ? null : Hole((b ?? "") + "|" + (int)(100 * hw / hh), path, map.Width, map.Height, hw / hh);
            var flag = logo == null && !string.IsNullOrEmpty(snap.TrackCountryCode) ? Bitmap("flags", snap.TrackCountryCode.ToLowerInvariant() + ".png") : null;
            return trackInfo = new TrackInfo(snap.TrackDisplayName, snap.TrackBroadcastName, snap.TrackRawName, snap.TrackConfigName, snap.TrackCountryCode, mapGen,
                                             logo, matte, map, path, name, hw, hh, hole, flag);
        }
    }

    TrackGeo? TrackGeom(TrackInfo ti, SKRect card, float k)
    {
        var map = ti.Map;
        if (map == null) return null;
        var area = new SKRect(card.Left + 50, card.Top + 60, card.Right - 50, card.Bottom - 60);
        float sFit = Math.Min(area.Width / map.Width, area.Height / map.Height);
        var path = ti.Path;
        var scr = card;   // the track page is laid out in the card's space (it's blown up to the screen afterwards)
        float LogoW = ti.HeadW, LogoH = ti.HeadH;
        float grow = 0.94f + 0.06f * k;
        // 1) The logo sits INSIDE the circuit (Angelo 2026-10-21): find the roomiest open spot in the infield
        //    and let the outline grow (never edge to edge) until that spot holds the logo at a decent size.
        var hole = ti.Hole;
        if (hole is SKRect hr && hr.Width > 0)
        {
            float sMax = Math.Min(scr.Width * 0.86f / map.Width, scr.Height * 0.84f / map.Height);
            float sNeed = Math.Max(LogoW * 0.6f / hr.Width, LogoH * 0.6f / hr.Height);
            float s = Math.Max(sFit, Math.Min(sNeed, sMax));
            float ls = Math.Min(0.8f, Math.Min(hr.Width * s / LogoW, hr.Height * s / LogoH) * 0.96f);
            if (ls >= 0.45f)
            {
                var mid = s > sFit ? new SKPoint(scr.MidX, scr.MidY) : new SKPoint(area.MidX, area.MidY);
                s *= grow;
                float w = map.Width * s, h = map.Height * s;
                var dst = new SKRect(mid.X - w / 2, mid.Y - h / 2, mid.X + w / 2, mid.Y + h / 2);
                var at = new SKPoint(dst.Left + hr.MidX * s, dst.Top + hr.MidY * s);
                return new TrackGeo(dst, s, at, path, ls * grow, at);
            }
        }
        // 2) No room inside (thin or twisty layouts like Suzuka and COTA): the outline takes the upper part
        //    of the page and the logo sits underneath it (Angelo 2026-10-21).
        {
            const float ls = 0.6f, gap = 34f;
            float bh = LogoH * ls;
            float availH = scr.Height * 0.84f - bh - gap;
            float s = Math.Min(scr.Width * 0.82f / map.Width, availH / map.Height) * grow;
            float w = map.Width * s, h = map.Height * s;
            float top = scr.MidY - (h + gap + bh) / 2;
            var dst = new SKRect(scr.MidX - w / 2, top, scr.MidX + w / 2, top + h);
            var below = new SKPoint(scr.MidX, dst.Bottom + gap + bh / 2);
            return new TrackGeo(dst, s, new SKPoint(dst.MidX, dst.MidY), path, ls, below);
        }
    }

    /// <summary>The biggest logo-shaped box that fits in the circuit's infield without touching the
    /// line, in the map's pixel space. Worked out once per layout.</summary>
    readonly Dictionary<string, SKRect?> holeCache = new(StringComparer.OrdinalIgnoreCase);
    SKRect? Hole(string key, SKPath path, int mw, int mh, float A)
    {
        if (holeCache.TryGetValue(key, out var hit)) return hit;
        SKRect? result = null;
        try
        {
            float gs = 260f / Math.Max(mw, mh);
            int gw = (int)Math.Ceiling(mw * gs) + 2, gh = (int)Math.Ceiling(mh * gs) + 2;
            using var bmp = new SKBitmap(new SKImageInfo(gw, gh, SKColorType.Alpha8, SKAlphaType.Premul));
            using (var cv = new SKCanvas(bmp))
            {
                cv.Clear(SKColors.Transparent);
                cv.Scale(gs);
                using var p2 = new SKPath(path) { FillType = SKPathFillType.Winding };
                p2.Close();
                using (var fill = new SKPaint { Color = SKColors.White }) cv.DrawPath(p2, fill);
                using (var line = new SKPaint { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src, Style = SKPaintStyle.Stroke,
                                                StrokeWidth = Math.Max(mw, mh) * 0.035f, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round })
                    cv.DrawPath(p2, line);
            }
            var px = bmp.Bytes; int rb = bmp.RowBytes, W1 = gw + 1;
            var sum = new int[W1 * (gh + 1)];
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                    sum[(y + 1) * W1 + x + 1] = (px[y * rb + x] < 128 ? 1 : 0) + sum[y * W1 + x + 1] + sum[(y + 1) * W1 + x] - sum[y * W1 + x];
            bool Free(int cx, int cy, int hh)
            {
                int hw = (int)Math.Ceiling(hh * A);
                int x0 = cx - hw, x1 = cx + hw, y0 = cy - hh, y1 = cy + hh;
                if (x0 < 0 || y0 < 0 || x1 >= gw || y1 >= gh) return false;
                return sum[(y1 + 1) * W1 + x1 + 1] - sum[y0 * W1 + x1 + 1] - sum[(y1 + 1) * W1 + x0] + sum[y0 * W1 + x0] == 0;
            }
            int best = 0, bx = 0, by = 0;
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                    while (Free(x, y, best + 1)) { best++; bx = x; by = y; }
            // 2026-10-24 (Angelo: "the track logo is not centred in the track outline on the splash"): centre it on
            // the circuit's middle - the average of points around the lap, like the pre-race track card - sized as
            // big as fits there. Only if the middle is much more cramped than the roomiest spot, keep that spot.
            using (var meas = new SKPathMeasure(path, true))
            {
                float L = meas.Length; double sx = 0, sy = 0; int n = 0;
                for (int i = 0; i < 200 && L > 0; i++)
                    if (meas.GetPosition(L * i / 200f, out var pp)) { sx += pp.X; sy += pp.Y; n++; }
                if (n > 0)
                {
                    int cx = (int)Math.Round(sx / n * gs), cy = (int)Math.Round(sy / n * gs);
                    int cb = 0;
                    while (Free(cx, cy, cb + 1)) cb++;
                    if (cb >= 3 && cb >= best * 0.6) { best = cb; bx = cx; by = cy; }
                }
            }
            if (best >= 3)
            {
                float hw = best * A;
                result = new SKRect((bx - hw) / gs, (by - best) / gs, (bx + hw) / gs, (by + best) / gs);
            }
        }
        catch { result = null; }
        holeCache[key] = result;
        return result;
    }

    // accelerating dive: the infield grows until it swallows the screen
    static float BlastScale(float b) => 1f + 13f * (float)Math.Pow(b, 2.2);

    void DrawTrack(SKCanvas c, SKRect card, TrackInfo ti, float a, double t, float blast = 0f)
    {
        // At MapS the logo (or flag + name) glides INTO the middle of the circuit and
        // shrinks to fit the infield while the racing line draws itself around it
        // (Angelo 2026-10-18). No map for this track yet -> the logo simply holds.
        var map = ti.Map;
        float k = map == null ? 0f : Smooth((float)Math.Clamp((t - MapS) / 0.55, 0, 1));
        var geo = TrackGeom(ti, card, k);
        float pivotX = card.MidX, pivotY = card.MidY - 20;
        var target = geo?.LogoAt ?? new SKPoint(pivotX, pivotY);
        // logo + caption are ~430 x 300 at full size; inside the loop they take ~38% of its width
        float inScale = geo == null ? 1f : geo.Value.LogoScale;
        float headerA = a * (1f - Smooth(Math.Min(1f, blast * 2.6f)));   // the logo melts first in the dive
        c.Save();
        c.Translate(pivotX + (target.X - pivotX) * k, pivotY + (target.Y + 6 - pivotY) * k);
        c.Scale(1f - (1f - inScale) * k);
        c.Translate(-pivotX, -pivotY);
        DrawTrackHeader(c, card, ti, headerA);
        c.Restore();
        if (map == null || geo == null || k <= 0.001f) return;

        var dst = geo.Value.Dst; float s = geo.Value.Scale;
        float lineA = a * (1f - Smooth(blast));
        var path = geo.Value.Path;
        if (path != null) { DrawRacingLine(c, path, dst, s, lineA, t); return; }
        a = lineA;
        layer.Color = new SKColor(255, 255, 255, (byte)(255 * a * k)); c.SaveLayer(dst, layer);
        fill.Reset(); fill.IsAntialias = true; fill.FilterQuality = SKFilterQuality.High; fill.ImageFilter = mapGlow;
        BitmapImages.Draw(c, map, dst, fill);
        fill.Reset(); fill.IsAntialias = true; fill.FilterQuality = SKFilterQuality.High;
        BitmapImages.Draw(c, map, dst, fill);
        c.Restore();
    }

    // ---- animated racing line ("the track draws itself") ---------------------
    readonly Dictionary<string, SKPath?> pathCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>maps/&lt;track&gt;.json (written next to the map PNG by map_from_ibt.py):
    /// the racing line in the PNG's pixel space.</summary>
    SKPath? MapPath(RaceSnapshot snap)
    {
        var b = MapBase(snap);
        if (b == null) return null;
        string file = b + ".png";
        if (pathCache.TryGetValue(file, out var cached)) return cached;
        SKPath? p = null;
        try
        {
            var json = Assets.Resolve("maps", System.IO.Path.ChangeExtension(file, ".json"));
            if (System.IO.File.Exists(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(json));
                p = new SKPath();
                bool first = true;
                foreach (var pt in doc.RootElement.GetProperty("points").EnumerateArray())
                {
                    float x = pt[0].GetSingle(), y = pt[1].GetSingle();
                    if (first) { p.MoveTo(x, y); first = false; } else p.LineTo(x, y);
                }
                p.Close();
            }
        }
        catch { p = null; }
        return pathCache[file] = p;
    }

    void DrawRacingLine(SKCanvas c, SKPath src, SKRect dst, float scale, float a, double t)
    {
        // the outline placed on the page + its measure: rebuilt only while the page is still moving (2026-10-24)
        if (lineMeasure == null || !ReferenceEquals(src, lineSrc) || dst != lineDst || scale != lineScale)
        {
            linePath.Reset(); linePath.FillType = src.FillType; linePath.AddPath(src);
            linePath.Transform(SKMatrix.CreateScaleTranslation(scale, scale, dst.Left, dst.Top));
            lineMeasure ??= new SKPathMeasure();
            lineMeasure.SetPath(linePath, true);
            lineSrc = src; lineDst = dst; lineScale = scale;
        }
        var measure = lineMeasure;
        float len = measure.Length;
        if (len <= 0) return;
        float p = (float)Math.Clamp((t - MapS - 0.3) / MapDrawS, 0, 1);   // starts once the logo has cleared
        p = 1 - (1 - p) * (1 - p) * (1 - p);                       // ease-out: quick start, settles at the line
        var seg = lineSeg;
        seg.Reset();
        measure.GetSegment(0, len * p, seg, true);

        fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 12; fill.StrokeCap = SKStrokeCap.Round;
        fill.StrokeJoin = SKStrokeJoin.Round; fill.Color = Gold.WithAlpha((byte)(120 * a)); fill.MaskFilter = lineGlowBlur;
        c.DrawPath(seg, fill);
        fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 5; fill.StrokeCap = SKStrokeCap.Round;
        fill.StrokeJoin = SKStrokeJoin.Round; fill.Color = new SKColor(255, 255, 255, (byte)(255 * a));
        c.DrawPath(seg, fill);

        // comet head while drawing, stopping at the line. No endless lapping (Angelo 2026-10-19):
        // the light runs ONE more lap just before the dive, finishing on the start/finish line.
        const double FinalLapS = 2.4;
        double lapStart = DiveS - 0.12 - FinalLapS;
        float headAt = -1;
        if (p < 1) headAt = len * p;
        else if (t >= lapStart && lapStart > MapS + 0.3 + MapDrawS + 0.3)
        {
            float q = (float)Math.Clamp((t - lapStart) / FinalLapS, 0, 1);
            q = q < 0.5f ? 2 * q * q : 1 - (-2 * q + 2) * (-2 * q + 2) / 2;   // ease in-out
            if (q < 1) headAt = len * q;
        }
        if (headAt >= 0 && measure.GetPositionAndTangent(headAt, out var pos, out _))
        {
            float r = p < 1 ? 16 : 13;
            // the comet's glow is built around (0,0) and moved with the canvas
            fill.Reset(); fill.IsAntialias = true;
            fill.Shader = cometSlot.Radial(default, r * 2.2f,
                [new SKColor(255, 244, 214, (byte)(255 * a)), Gold.WithAlpha((byte)(150 * a)), Gold.WithAlpha(0)], Stops.At035);
            c.Save(); c.Translate(pos.X, pos.Y);
            c.DrawCircle(0, 0, r * 2.2f, fill);
            c.Restore();
        }
        // start / finish tick appears once the lap is complete
        if (p >= 1 && measure.GetPositionAndTangent(0, out var sf, out var tan))
        {
            var nrm = new SKPoint(-tan.Y, tan.X);
            fill.Reset(); fill.IsAntialias = true; fill.StrokeWidth = 4; fill.Color = Gold.WithAlpha((byte)(255 * a));
            c.DrawLine(sf.X - nrm.X * 14, sf.Y - nrm.Y * 14, sf.X + nrm.X * 14, sf.Y + nrm.Y * 14, fill);
        }
    }

    void DrawTrackHeader(SKCanvas c, SKRect card, TrackInfo ti, float a)
    {
        var logo = ti.Logo;
        string name = ti.Name;
        if (logo != null) { DrawLogo(c, card, logo, 600, 230, a, name); return; }

        // Typographic fallback: country flag + track name, broadcast style.
        var flag = ti.Flag;
        float y = card.MidY - 20;
        if (flag != null)
        {
            float fh = 54, fw = flag.Width * (fh / flag.Height);
            fill.Reset(); fill.IsAntialias = true; fill.FilterQuality = SKFilterQuality.High; fill.Color = SKColors.White.WithAlpha((byte)(255 * a));
            BitmapImages.Draw(c, flag, new SKRect(card.MidX - fw / 2, y - 92, card.MidX + fw / 2, y - 92 + fh), fill);
        }
        DrawBigText(c, card, name, a);
    }

    void DrawBigText(SKCanvas c, SKRect card, string text, float a)
    {
        if (string.IsNullOrEmpty(text)) return;
        var p = this.text;
        p.Reset(); p.IsAntialias = true; p.Typeface = Fonts.CormorantGaramond.Bold; p.TextSize = 64; p.TextAlign = SKTextAlign.Center;
        p.Color = Txt.WithAlpha((byte)(255 * a));
        float maxW = card.Width - 120;
        float w = p.MeasureText(text) + 0.08f * p.TextSize * Math.Max(0, text.Length - 1);
        if (w > maxW) p.TextSize *= maxW / w;
        TextTracked.Draw(c, p, text, card.MidX, card.MidY + 2, 0.08f * p.TextSize);
    }

    // ---- logos that come on a solid background (e.g. Le Mans' navy plate) -----------
    // The intro plate takes on that colour for the track page, and the logo's edges are
    // feathered so the seam between logo and plate disappears (Angelo 2026-10-18).
    static readonly Dictionary<SKBitmap, (SKColor? Matte, SKBitmap? Feathered)> matteCache = new();

    /// <summary>Shared with the pre-race Track Facts card.</summary>
    internal static (SKColor? Matte, SKBitmap? Feathered) Matte(SKBitmap? bmp)
    {
        if (bmp == null) return (null, null);
        lock (matteCache) return MatteLocked(bmp);   // also reached from the background warm-up (Prewarm)
    }

    static (SKColor? Matte, SKBitmap? Feathered) MatteLocked(SKBitmap bmp)
    {
        if (matteCache.TryGetValue(bmp, out var m)) return m;
        SKColor? matte = null; SKBitmap? feathered = null;
        try
        {
            int w = bmp.Width, h = bmp.Height;
            long rs = 0, gs = 0, bs = 0, n = 0; int opaque = 0;
            void Add(int x, int y) { var p = bmp.GetPixel(x, y); if (p.Alpha >= 245) opaque++; rs += p.Red; gs += p.Green; bs += p.Blue; n++; }
            for (int x = 0; x < w; x += 2) { Add(x, 0); Add(x, h - 1); }
            for (int y = 0; y < h; y += 2) { Add(0, y); Add(w - 1, y); }
            if (n > 0 && opaque >= n * 0.95)
            {
                // most common border colour (an average gets pulled off by stray pixels)
                var counts = new Dictionary<uint, int>();
                for (int x = 0; x < w; x++) foreach (int y in new[] { 0, 1, h - 2, h - 1 })
                { var p = bmp.GetPixel(x, y); uint key = (uint)((p.Red << 16) | (p.Green << 8) | p.Blue); counts[key] = counts.GetValueOrDefault(key) + 1; }
                uint best = counts.OrderByDescending(kv => kv.Value).First().Key;
                var col = new SKColor((byte)(best >> 16), (byte)(best >> 8), (byte)best);
                // only a real "plate": border pixels must all sit close to that colour
                int close = 0, total = 0;
                for (int x = 0; x < w; x += 3) foreach (int y in new[] { 0, h - 1 })
                { var p = bmp.GetPixel(x, y); total++; if (Math.Abs(p.Red - col.Red) + Math.Abs(p.Green - col.Green) + Math.Abs(p.Blue - col.Blue) < 60) close++; }
                if (close >= total * 0.9)
                {
                    matte = col;
                    feathered = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                    float ramp = 4;   // plate colour matches, so only the last few pixels need softening
                    // 2026-10-24: written into a buffer and copied in (SetPixel made a canvas per pixel: ~90 MB of
                    // garbage and a long stall on the track page's first frame). Same bytes: SetPixel on this
                    // unpremultiplied bitmap stores (r, g, b, a), or all zero when a = 0.
                    var buf = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            var p = bmp.GetPixel(x, y);
                            float d = Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y));
                            float k = Math.Clamp(d / ramp, 0, 1); k = k * k * (3 - 2 * k);
                            // KEY OUT the plate colour (Angelo 2026-10-19): only the artwork stays, so when the
                            // logo zooms over the starting grid there is no navy box around it
                            int dist = Math.Abs(p.Red - col.Red) + Math.Abs(p.Green - col.Green) + Math.Abs(p.Blue - col.Blue);
                            float key = Math.Clamp((dist - 14) / 46f, 0, 1); key = key * key * (3 - 2 * key);
                            byte al = (byte)(p.Alpha * k * key);
                            int i = x * 4;
                            if (al == 0) { buf[i] = 0; buf[i + 1] = 0; buf[i + 2] = 0; buf[i + 3] = 0; }
                            else { buf[i] = p.Red; buf[i + 1] = p.Green; buf[i + 2] = p.Blue; buf[i + 3] = al; }
                        }
                        System.Runtime.InteropServices.Marshal.Copy(buf, 0, feathered.GetPixels() + y * feathered.RowBytes, w * 4);
                    }
                    feathered.NotifyPixelsChanged();
                    feathered.SetImmutable();
                }
            }
        }
        catch { matte = null; feathered = null; }
        return matteCache[bmp] = (matte, feathered);
    }

    void DrawLogo(SKCanvas c, SKRect card, SKBitmap? bmp, float maxW, float maxH, float a, string? caption)
    {
        if (bmp == null) return;
        var mt = Matte(bmp);
        if (mt.Feathered != null) bmp = mt.Feathered;
        float s = Math.Min(maxW / bmp.Width, maxH / bmp.Height);
        float w = bmp.Width * s, h = bmp.Height * s;
        float cy = card.MidY - 20 - (caption != null ? 20 : 0);
        var dst = new SKRect(card.MidX - w / 2, cy - h / 2, card.MidX + w / 2, cy + h / 2);
        layer.Color = new SKColor(255, 255, 255, (byte)(255 * a)); c.SaveLayer(dst, layer);
        fill.Reset(); fill.IsAntialias = true; fill.FilterQuality = SKFilterQuality.High;
        BitmapImages.Draw(c, bmp, dst, fill);
        if (shine > 0 && shine < 1 && mt.Matte == null)   // no sweep on plated logos: it would outline the plate
        {
            // diagonal light band, painted only onto the logo's own pixels (SrcATop); the band's gradient is
            // built relative to its own x and moved with the canvas, so it isn't remade every frame (2026-10-24)
            float bx = dst.Left - dst.Width * 0.35f + (float)shine * dst.Width * 1.7f;
            fill.Reset(); fill.IsAntialias = true; fill.BlendMode = SKBlendMode.SrcATop;
            fill.Shader = shineSlot.Linear(new SKPoint(-60, dst.Top), new SKPoint(60, dst.Bottom),
                [SKColors.Transparent, new SKColor(255, 255, 255, 185), SKColors.Transparent], []);
            c.Save(); c.Translate(bx, 0);
            c.DrawRect(new SKRect(dst.Left - bx, dst.Top, dst.Right - bx, dst.Bottom), fill);
            c.Restore();
        }
        c.Restore();
        if (caption != null)
        {
            var p = text;
            p.Reset(); p.IsAntialias = true; p.Typeface = Fonts.Inter.Bold; p.TextSize = 14; p.TextAlign = SKTextAlign.Center;
            p.Color = Txt3.WithAlpha((byte)(255 * a));
            TextTracked.Draw(c, p, caption, card.MidX, dst.Bottom + 34, 0.4f * 14f);
        }
    }

    /// <summary>Finds assets/logos/tracks/&lt;name&gt;.png for the current track. Matching
    /// ignores case, accents, punctuation and layout suffixes ("- Grand Prix"), and accepts
    /// either name containing the other ("Road Atlanta" ~ "Michelin Raceway Road Atlanta").</summary>
    SKBitmap? TrackLogo(RaceSnapshot snap) => FindTrackAsset(snap, "logos", "tracks", ref trackFiles);
    SKBitmap? TrackMap(RaceSnapshot snap)
    {
        var b = MapBase(snap);
        return b == null ? null : Bitmap("maps", b + ".png");
    }
    // the right layout's map: "Road Atlanta - Full Course" beats "Road Atlanta - Club"
    string? MapBase(RaceSnapshot snap)
    {
        RefreshMaps();
        return TrackMapCache.Resolve(snap.TrackConfigName, snap.TrackDisplayName, snap.TrackBroadcastName, snap.TrackRawName);
    }
    List<(string Key, string File)>? mapFiles;
    int mapGen = -1;
    // pull in any maps the live recorder drew since last time (Documents\iRacing\ApexHUD\maps)
    void RefreshMaps()
    {
        TrackMapCache.Sync();
        if (mapGen != TrackMapCache.Generation) { mapGen = TrackMapCache.Generation; mapFiles = null; }
    }

    SKBitmap? FindTrackAsset(RaceSnapshot snap, string dir1, string? dir2, ref List<(string Key, string File)>? index)
    {
        index ??= LoadTrackFileIndex(dir2 == null ? new[] { dir1 } : new[] { dir1, dir2 });
        var trackFiles = index;
        foreach (var raw in new[] { snap.TrackDisplayName, snap.TrackBroadcastName, snap.TrackRawName })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string n = Norm(raw.Split(" - ")[0]);
            if (n.Length < 4) continue;
            foreach (var (key, file) in trackFiles)
                if (n == key || n.Contains(key) || key.Contains(n))
                    return dir2 == null ? Bitmap(dir1, file) : Bitmap(dir1, dir2, file);
        }
        return null;
    }

    List<(string Key, string File)>? trackFiles;
    static List<(string, string)> LoadTrackFileIndex(string[] folder)
    {
        var list = new List<(string, string)>();
        try
        {
            var dir = Assets.Resolve(folder);
            if (System.IO.Directory.Exists(dir))
                foreach (var f in System.IO.Directory.GetFiles(dir, "*.png"))
                {
                    var name = System.IO.Path.GetFileName(f);
                    list.Add((Norm(System.IO.Path.GetFileNameWithoutExtension(f)), name));
                }
        }
        catch { }
        list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));   // most specific first
        return list;
    }

    static string Norm(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (char ch in d)
            if (char.IsLetterOrDigit(ch) && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Opener timeline slot: the circuit map (assets/maps/&lt;track&gt;.png) with the
    /// track logo (or flag + name) above it. Shown when there are fewer than three classes.</summary>
    public void DrawTrackMap(SKCanvas c, SKRect body, RaceSnapshot snap, float a)
    {
        if (a <= 0.01f) return;
        string name = (snap.TrackDisplayName ?? snap.TrackBroadcastName ?? "").ToUpperInvariant();
        // header strip, styled like the class strips
        var strip = new SKRect(body.Left, body.Top, body.Right, body.Top + 36);
        using (var bg = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(strip.Left, 0), new SKPoint(strip.Right, 0),
                   new[] { Gold.WithAlpha((byte)(60 * a)), Gold.WithAlpha(0) }, null, SKShaderTileMode.Clamp) })
            c.DrawRoundRect(strip, 6, 6, bg);
        using (var bar = new SKPaint { Color = Gold.WithAlpha((byte)(255 * a)) })
            c.DrawRect(new SKRect(strip.Left, strip.Top, strip.Left + 4, strip.Bottom), bar);
        using (var p = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14, Color = Txt.WithAlpha((byte)(255 * a)) })   // #340: was 12
            TextTracked.Draw(c, p, "THE CIRCUIT", strip.Left + 16, strip.MidY + 5, 3.2f);
        using (var p = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13, TextAlign = SKTextAlign.Right, Color = Txt3.WithAlpha((byte)(255 * a)) })   // #340: was 11
            TextTracked.Draw(c, p, name, strip.Right - 14, strip.MidY + 5, 2.0f);

        var area = new SKRect(body.Left, strip.Bottom + 16, body.Right, body.Bottom);
        var map = TrackMap(snap);
        var logo = TrackLogo(snap);
        if (map == null)
        {
            // no map yet: big track logo (or flag + name) centred instead
            if (logo != null) DrawLogo(c, area, logo, 620, 300, a, name);
            else DrawBigText(c, area, name, a);
            return;
        }
        // logo top-left, map filling the rest
        float mapTop = area.Top;
        if (logo != null)
        {
            float lh = 70, lw = Math.Min(260, logo.Width * (lh / logo.Height)); lh = lw * logo.Height / logo.Width;
            var ld = new SKRect(area.Left + 8, area.Top + 4, area.Left + 8 + lw, area.Top + 4 + lh);
            using (var fade = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * a)) }) c.SaveLayer(ld, fade);
            using (var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High }) c.DrawBitmap(logo, ld, lp);
            c.Restore();
        }
        float s = Math.Min((area.Width - 60) / map.Width, (area.Height - 20) / map.Height);
        float w = map.Width * s, h = map.Height * s;
        var dst = new SKRect(area.MidX - w / 2, area.MidY - h / 2, area.MidX + w / 2, area.MidY + h / 2);
        using (var fade = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * a)) }) c.SaveLayer(dst, fade);
        using (var glow = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High,
                   ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 0, 8, 8, Gold.WithAlpha(150)) })
            c.DrawBitmap(map, dst, glow);
        using (var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High }) c.DrawBitmap(map, dst, lp);
        c.Restore();
    }

    SKBitmap? Bitmap(params string[] path)
    {
        string k = string.Join("/", path);
        lock (cache)   // also reached from the background warm-up (Prewarm)
        {
            if (cache.TryGetValue(k, out var b)) return b;
            try
            {
                var p = Assets.Resolve(path);
                if (System.IO.File.Exists(p)) { using var st = System.IO.File.OpenRead(p); b = Assets.Decode(st); }
            }
            catch { b = null; }
            return cache[k] = b;
        }
    }

    // the page logos, looked up once (Bitmap() joins its path into a key string on every call)
    SKBitmap? irLogo; bool irLogoTried;
    SKBitmap? IracingLogo()
    {
        if (!irLogoTried) { irLogo = Bitmap("logos", "iracing-brand", "iRacing-Inline-Color-White.png"); irLogoTried = true; }
        return irLogo;
    }
    string? seriesLogoSlug; SKBitmap? seriesLogoBmp;
    SKBitmap? SeriesLogo(string? slug)
    {
        if (slug == null) return null;
        if (slug != seriesLogoSlug) { seriesLogoBmp = Bitmap("logos", "series", slug + ".png"); seriesLogoSlug = slug; }
        return seriesLogoBmp;
    }
    string? seriesCapsOf; string seriesCaps = "";
    string SeriesCaps(string? name)
    {
        if (name != seriesCapsOf) { seriesCaps = (name ?? "").ToUpperInvariant(); seriesCapsOf = name; }
        return seriesCaps;
    }

    Task? prewarm; string? prewarmFor;
    /// <summary>Call every frame before the opener (cheap): decodes the opener's logos, keys out the track
    /// logo's plate, finds the map and its infield - in the background, once per track - so none of it lands
    /// on the render thread mid-opener (2026-10-24).</summary>
    public void Prewarm(RaceSnapshot snap)
    {
        string? id = snap.TrackDisplayName;
        if (prewarm != null && id == prewarmFor) return;
        prewarmFor = id;
        prewarm = StartPrewarm(snap);
    }

    // separate so its closure is only allocated when a warm-up starts (not on every pre-opener frame)
    Task StartPrewarm(RaceSnapshot snap)
    {
        var sid = snap.SeriesId;
        return Task.Run(() =>
        {
            try
            {
                Matte(Bitmap("logos", "iracing-brand", "iRacing-Inline-Color-White.png"));
                var slug = SeriesNames.ResolveLogoSlug(sid);
                if (slug != null) Matte(Bitmap("logos", "series", slug + ".png"));
                Track(snap);
            }
            catch { /* the opener still works it out itself */ }
        });
    }

    static readonly SKPaint gpuWarm = new() { Color = SKColors.White.WithAlpha(1), FilterQuality = SKFilterQuality.High, IsAntialias = true };
    /// <summary>Invisible 2-pixel draws of the opener's pictures (same quality as on air), so the graphics card
    /// has them uploaded - and mip-mapped - before the page that shows them. Skips while the warm-up still runs.</summary>
    public void WarmGpu(SKCanvas c, SKRect at, RaceSnapshot snap)
    {
        if (prewarm == null || !prewarm.IsCompleted) return;
        var dot = new SKRect(at.Left, at.Top, at.Left + 2, at.Top + 2);
        void One(SKBitmap? b) { if (b != null) BitmapImages.Draw(c, Matte(b).Feathered ?? b, dot, gpuWarm); }
        One(IracingLogo());
        var t = trackInfo;
        if (t != null) { One(t.Logo); if (t.Map != null) BitmapImages.Draw(c, t.Map, dot, gpuWarm); if (t.Flag != null) BitmapImages.Draw(c, t.Flag, dot, gpuWarm); }
        One(SeriesLogo(SeriesNames.ResolveLogoSlug(snap.SeriesId)));
    }

    static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3 - 2 * x); }
    static float EaseOut(float x) { x = Math.Clamp(x, 0f, 1f); return 1 - (1 - x) * (1 - x) * (1 - x); }

    public void Dispose() { foreach (var b in cache.Values) b?.Dispose(); cache.Clear(); }
}
