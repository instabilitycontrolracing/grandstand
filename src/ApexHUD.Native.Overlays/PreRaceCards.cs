using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-18 (Angelo): pre-race lower-third info cards. Start when the opener's
/// starting-grid board leaves (StartingGridRenderer.CardsStartMs) and loop until the
/// green flag (phase leaves PreRace). Each card slides up + fades in, holds, slides out:
///   Weather Report (presented by StormExhibit.com) · Race Info · Pole Sitters ·
///   Official Partner: A&amp;B General Contracting · Track Facts · Driver to Watch ·
///   Official Partner: StormExhibit.com
/// </summary>
public sealed partial class PreRaceCards : IDisposable
{
    public const float CardW = 1060f, CardH = 150f;
    // 2026-10-25 (Angelo): the intro now plays earlier, so the pre-green cards stay up much longer (10-15 s each)
    const double InS = 0.45, HoldS = 12.0, OutS = 0.4, GapS = 0.25;
    static double SlotS => InS + HoldS + OutS + GapS;

    enum Kind { Weather, RaceInfo, Poles, PartnerAB, TrackFacts, Driver, PartnerStorm, Globe, Watch }
    // 2026-10-25 (Angelo: "reorder those cards that play before green flag to tell the story of what is about to happen"):
    // where we are (track) -> the conditions (weather) -> what today's race is -> who starts at the front -> who to watch,
    // partners in between
    // 2026-11-06: + Drivers to watch (DriversToWatch) right after the pole sitters - who's at the front, then who to watch
    static readonly Kind[] FullOrder = { Kind.TrackFacts, Kind.Weather, Kind.RaceInfo, Kind.PartnerAB, Kind.Poles, Kind.Watch, Kind.Globe, Kind.Driver, Kind.PartnerStorm };
    /// <summary>The track card or Today's Race card is up - the replay director takes the blimp shot (Angelo 2026-10-25).</summary>
    public static bool ScenicNow { get; private set; }
    // the globe flies in right before the track card - only as a half-screen panel, and only once
    // this track's globe has been prepared in the background (GlobeIntro)
    // (2026-10-20: the globe moved into the broadcast opener, so it no longer plays as a card)
    // partner panels only while that partner is switched on in sponsors.json (all on by default)
    static Kind[] OrderFor(RaceSnapshot s, bool panels) => FullOrder.Where(k => k != Kind.Globe
        && (k != Kind.Watch || (ShowWatch && DriversToWatch.For(s).Count >= 2))
        && (k != Kind.PartnerAB || SponsorPack.For("PreRacePanelAB") == Partner.AB)
        && (k != Kind.PartnerStorm || SponsorPack.For("PreRacePanelStorm") == Partner.Storm)).ToArray();
    /// <summary>2026-11-06 (#901): the pre-race cards in order and what each will show - for the commentator's booth.</summary>
    public static List<(string Card, List<string> Notes)> BoothPreview(RaceSnapshot s)
    {
        var res = new List<(string Card, List<string> Notes)>();
        try
        {
            foreach (var k in OrderFor(s, true))
            {
                var n = new List<string>();
                if (k == Kind.TrackFacts)
                {
                    n.Add(s.TrackDisplayName ?? "");
                    LapFor(s);
                    if (lapStops is { Count: > 0 }) n.Add("The lap walk: " + string.Join(", ", lapStops.Select(x => x.Name)));
                }
                else if (k == Kind.Weather)
                    n.Add($"Air {s.AirTempC * 9 / 5 + 32:0} F ({s.AirTempC:0} C), track {s.TrackTempC * 9 / 5 + 32:0} F ({s.TrackTempC:0} C)" + (s.IsRaining ? ", raining" : ""));
                else if (k == Kind.RaceInfo)
                {
                    n.Add(string.Join(", ", s.ClassLeaderboard.Select(cl => $"{cl.Drivers.Count} {cl.Name}")) + " cars");
                    if (s.TotalLaps is > 0 and < 2000) n.Add($"{s.TotalLaps} laps");
                    else if (s.SessionTimeRemaining > 0) n.Add($"{Math.Round(s.SessionTimeRemaining / 60)} minutes");
                }
                else if (k == Kind.Poles)
                {
                    foreach (var cl in s.ClassLeaderboard)
                    {
                        var pole = cl.Drivers.FirstOrDefault(d => s.GridClassPositions.TryGetValue(d.CarIdx, out var g1) && g1 == 1);
                        if (pole != null) n.Add($"{cl.Name} pole: #{pole.CarNumber} {pole.Name}" + (pole.QualifyTime > 0 ? $" ({LapTimeFormat.Lap(pole.QualifyTime)})" : ""));
                    }
                }
                else if (k == Kind.Watch)
                {
                    foreach (var pk in DriversToWatch.For(s)) n.Add($"{pk.Tag}: #{pk.Number} {pk.Name} ({pk.ClassName}) - {pk.Reason}");
                }
                else if (k == Kind.Driver && s.Player is { } me)
                    n.Add($"#{me.CarNumber} {me.Name} - {me.CarName}, starts P{(s.GridClassPositions.TryGetValue(me.CarIdx, out var g2) ? g2 : me.PositionInClass)} in {me.ClassName}");
                else if (k == Kind.PartnerAB) n.Add("A&B General Contracting");
                else if (k == Kind.PartnerStorm) n.Add("Closing partner panel before the start");
                n.RemoveAll(string.IsNullOrWhiteSpace);
                string name = k switch { Kind.TrackFacts => "Track intro", Kind.Weather => "Weather", Kind.RaceInfo => "Today's race", Kind.Poles => "Pole sitters",
                    Kind.Watch => "Drivers to watch", Kind.Driver => "Driver card", Kind.PartnerAB => "Partner", Kind.PartnerStorm => "Closing partner", _ => k.ToString() };
                res.Add((name, n));
            }
        }
        catch { }
        return res;
    }

    static double HoldFor(Kind k) => k == Kind.Globe ? GlobeIntro.Length - 0.2
        : k == Kind.TrackFacts && lapStops is { Count: > 0 } ? Math.Clamp(WalkStartS + lapStops.Count * WalkStepS + 8.4, HoldS, 190) : HoldS;

    // ---- the lap walk (2026-10-25, Angelo: the track card "could roll through each named corner and straight, taking us
    // through the lap", during the blimp shots): once the circuit has traced itself, a gold light runs the lap stop by stop -
    // each corner / named straight lights up on the outline with its name beside it. The card stays up for as long as that takes.
    const double WalkStartS = 7.8, WalkStepS = 7.2;   // 2026-10-25 (Angelo: "1.5 times as long but only play once"): x1.5 again   // 2026-10-25: doubled (Angelo: "make it take double the time")   // 2026-10-25: slower, so each stop has time to fly in and be read
    static List<(string Name, float StartPct, float EndPct, bool Straight)>? lapStops;
    static string lapKey = "", lapTrack = "";
    static string? lapCls; static bool lapImperial;
    static void LapFor(RaceSnapshot s)
    {
        lapCls = s.Player?.ClassName; lapImperial = IsImperial(s);
        string k = (s.TrackRawName ?? "") + "|" + s.Extras.TrackLength;
        if (k == lapKey) return;
        lapKey = k;
        lapStops = TrackCorners.Lap(s.TrackRawName, s.Extras.TrackLength);
        lapTrack = s.TrackRawName ?? "";
    }

    // 2026-10-25 (Angelo: "make sure the corner names in the intro all take you to the corner or part of the track"): the
    // map flies in to each stop as the lap walk reaches it - zooms on that corner / straight, then on to the next, and
    // back out to the whole circuit at the end. Returns the matrix for the outline (screen space) and how far zoomed.
    float walkZoomAmt;
    SKMatrix WalkZoom(SKPath p, SKRect zone)
    {
        walkZoomAmt = 0;
        if (lapStops is not { Count: > 0 }) return SKMatrix.Identity;
        double tw = cardLocal - InS - WalkStartS;
        if (tw < 0) return SKMatrix.Identity;
        using var meas = new SKPathMeasure(p, true);
        float L = meas.Length;
        if (L <= 0) return SKMatrix.Identity;
        var zc = new SKPoint(zone.MidX, zone.MidY);
        (float Z, SKPoint F, float R) Target(int i)
        {
            if (i < 0 || i >= lapStops.Count) return (1f, zc, 0f);
            var st = lapStops[i];
            float s0 = st.StartPct, s1 = st.EndPct; if (s1 < s0) s1 += 1;
            using var seg = new SKPath();
            void Add(float a0, float a1) { using var part = new SKPath(); meas.GetSegment(L * a0, L * a1, part, true); seg.AddPath(part); }
            if (s1 <= 1) Add(s0, s1); else { Add(s0, 1); Add(0, s1 - 1); }
            var b = seg.Bounds;
            float ext = Math.Max(Math.Max(b.Width, b.Height), Math.Min(zone.Width, zone.Height) * 0.12f);
            float z = Math.Clamp(Math.Min(zone.Width, zone.Height) * 0.5f / ext, 1.3f, 2.3f);
            // 2026-10-27 (Angelo: "rotate the map when zoomed in so the direction of travel is pointed up"): turn the map so
            // the car's direction through this stop (the chord from its start to its end) points straight up the screen
            float r = 0f;
            float mid = (s0 + s1) / 2f; if (mid >= 1) mid -= 1;
            if (meas.GetPosition(L * s0 % L, out var pa) && meas.GetPosition(L * (s1 >= 1 ? s1 - 1 : s1), out var pb)
                && (Math.Abs(pb.X - pa.X) + Math.Abs(pb.Y - pa.Y)) > 2f)
                r = -90f - (float)(Math.Atan2(pb.Y - pa.Y, pb.X - pa.X) * 180 / Math.PI);
            else if (meas.GetPositionAndTangent(L * mid, out _, out var tg))
                r = -90f - (float)(Math.Atan2(tg.Y, tg.X) * 180 / Math.PI);
            r = ((r % 360f) + 540f) % 360f - 180f;                       // -180..180
            return (z, new SKPoint(b.MidX, b.MidY), r);
        }
        int n = lapStops.Count;
        int i = (int)(tw / WalkStepS);
        float u = (float)((tw - i * WalkStepS) / WalkStepS);
        var (z0, f0, r0) = Target(i - 1);
        var (z1, f1, r1) = Target(Math.Min(i, n));
        float e = Ease(Math.Clamp(u / (i >= n ? 0.55f : 0.32f), 0, 1));
        float z = z0 + (z1 - z0) * e;
        var f = new SKPoint(f0.X + (f1.X - f0.X) * e, f0.Y + (f1.Y - f0.Y) * e);
        float dr = ((r1 - r0) % 360f + 540f) % 360f - 180f;            // the short way round
        float rot = r0 + dr * e;
        walkZoomAmt = Math.Clamp((z - 1f) / 0.35f, 0, 1);
        // keep the zoomed track inside the zone: move the focus to the zone's centre
        var m = SKMatrix.CreateTranslation(-f.X, -f.Y);
        m = m.PostConcat(SKMatrix.CreateRotationDegrees(rot));
        m = m.PostConcat(SKMatrix.CreateScale(z, z));
        m = m.PostConcat(SKMatrix.CreateTranslation(zc.X + (f.X - zc.X) * (1 - Math.Min(1, (z - 1) / 0.35f)), zc.Y + (f.Y - zc.Y) * (1 - Math.Min(1, (z - 1) / 0.35f))));
        return m;
    }

    /// <summary>Draws the lap walk on a traced circuit (meas = the outline in screen space).</summary>
    void DrawLapWalk(SKCanvas c, SKPathMeasure meas, float L, float a, float k, float labelSize, bool chip = true)
    {
        if (lapStops is not { Count: > 0 } || L <= 0) return;
        double tw = cardLocal - InS - WalkStartS;
        if (tw < 0) return;
        int i = (int)(tw / WalkStepS);
        if (i >= lapStops.Count) return;
        float u = (float)((tw - i * WalkStepS) / WalkStepS);            // 0..1 through this stop
        var st = lapStops[i];
        float s0 = st.StartPct, s1 = st.EndPct; if (s1 < s0) s1 += 1;
        float fadeIn = Math.Clamp(u / 0.18f, 0, 1), fadeOut = Math.Clamp((1 - u) / 0.14f, 0, 1);
        float ka = a * Math.Min(fadeIn, fadeOut);
        if (ka <= 0.01f) return;
        byte A(float v) => (byte)Math.Clamp(v * 255 * ka, 0, 255);

        // the stretch, lit in gold (split across the start line when it wraps)
        using var seg = new SKPath();
        void Add(float p0, float p1) { using var part = new SKPath(); meas.GetSegment(L * p0, L * p1, part, true); seg.AddPath(part); }
        if (s1 <= 1) Add(s0, s1); else { Add(s0, 1); Add(0, s1 - 1); }
        using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 12 * k, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                   Color = Gold.WithAlpha(A(0.55f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6 * k) })
            c.DrawPath(seg, glow);
        using (var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4.5f * k, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = Gold.WithAlpha(A(1)) })
            c.DrawPath(seg, line);

        // the light running through it
        float runK = Ease(Math.Clamp(u / 0.85f, 0, 1));
        float pp = s0 + (s1 - s0) * runK; pp -= MathF.Floor(pp);
        if (!meas.GetPosition(L * pp, out var head)) return;
        using (var halo = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(head, 15 * k,
                   new[] { new SKColor(255, 248, 228, A(1)), Gold.WithAlpha(A(0.6f)), Gold.WithAlpha(0) }, new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawCircle(head, 15 * k, halo);

        if (!chip) return;
        // the name beside it, on a glass chip, kept on screen; "3 / 16" counts the lap along
        float midP = s0 + (s1 - s0) * 0.5f; midP -= MathF.Floor(midP);
        meas.GetPosition(L * midP, out var anchor);
        using var np = new SKPaint { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = labelSize, Color = Txt.WithAlpha(A(1)) };
        using var ep = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = labelSize * 0.50f, Color = Gold.WithAlpha(A(1)) };   // #340: was 0.42 (11 px on the card)
        string nm = st.Name.ToUpperInvariant();
        string eye = (st.Straight ? "STRAIGHT" : "CORNER") + "  " + (i + 1) + " / " + lapStops.Count;
        float tw1 = np.MeasureText(nm), tw2 = ep.MeasureText(eye) + eye.Length * 1.6f;
        var brandLogo = CornerTheme.For(st.Name)?.Kind == CornerTheme.Kind.Dunlop ? null : CornerBrand.For(st.Name);
        string? her = CornerTheme.HeritageOf(lapTrack, st.Name);
        using var hp = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = labelSize * 0.47f, Color = new SKColor(0xDA, 0xDE, 0xE4, A(1f)) };   // #340: was 0.36, C9CED6 @0.9
        float tw3 = her != null ? hp.MeasureText(her) + her.Length * 1.2f : 0;
        tw1 = Math.Max(tw1, tw3);
        float bh = labelSize * (her != null ? 2.55f : 2.0f), logoH = labelSize * 2.0f - 14 * k;
        float logoW = brandLogo != null ? brandLogo.Width * Math.Min(labelSize * 2.4f / brandLogo.Width, logoH / brandLogo.Height) : 0;
        // the speed there: the slowest point of a corner, the top speed of a straight (TrackSpeeds, from the field)
        float? kmh = TrackSpeeds.Of(lapCls, st.Name, st.Straight);
        string spd = kmh is { } v ? (lapImperial ? Math.Round(v / 1.609344f) : Math.Round(v)).ToString(CultureInfo.InvariantCulture) : "";
        string spdLbl = (st.Straight ? "TOP " : "MIN ") + (lapImperial ? "MPH" : "KM/H");
        using var sp = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.ExtraBold, TextSize = labelSize * 1.25f, Color = Txt.WithAlpha(A(1)) };
        using var sl = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = labelSize * 0.45f };   // #340: was 0.34
        float spdW = spd.Length > 0 ? Math.Max(sp.MeasureText(spd), sl.MeasureText(spdLbl) + spdLbl.Length * 1.4f) + 22 * k : 0;
        float bw = Math.Max(tw1, tw2) + 30 * k + (logoW > 0 ? logoW + 14 * k : 0) + spdW;
        var dev = c.DeviceClipBounds; var inv = c.TotalMatrix.TryInvert(out var im) ? im : SKMatrix.Identity;
        var vis = inv.MapRect(new SKRect(dev.Left, dev.Top, dev.Right, dev.Bottom));
        float bx = anchor.X - bw / 2, by = anchor.Y - bh - 22 * k;
        if (by < vis.Top + 8) by = anchor.Y + 22 * k;
        bx = Math.Clamp(bx, vis.Left + 8, Math.Max(vis.Left + 8, vis.Right - 8 - bw));
        var box = new SKRect(bx, by, bx + bw, by + bh);
        // themed to the corner (2026-10-25): Michelin blue, PlayStation space, Dunlop grey with the real bridge over the chip
        var accent = CornerTheme.Chip(c, box, st.Name, ka);
        var th = CornerTheme.For(st.Name);
        if (th is { Kind: CornerTheme.Kind.Dunlop, Logo: { } bridge })
        {
            float dw = bw * 1.15f, dh = bridge.Height * (dw / bridge.Width);
            using var dp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(A(1)) };
            c.DrawBitmap(bridge, new SKRect(box.MidX - dw / 2, box.Top - dh * 0.74f, box.MidX + dw / 2, box.Top - dh * 0.74f + dh), dp);
        }
        using (var bp = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(A(1)) }) c.DrawRect(new SKRect(box.Left, box.Top, box.Left + 3 * k, box.Bottom), bp);
        ep.Color = accent.WithAlpha(A(1));
        TextTracked.Draw(c, ep, eye, box.Left + 15 * k, box.Top + labelSize * 0.68f, 1.6f);
        c.DrawText(nm, box.Left + 15 * k, box.Top + labelSize * 1.62f, np);
        if (her != null) TextTracked.Draw(c, hp, her, box.Left + 15 * k, box.Top + labelSize * 2.2f, 1.2f);
        if (brandLogo != null && th?.Kind != CornerTheme.Kind.Dunlop) CornerBrand.Draw(c, brandLogo, box.Right - 12 * k - spdW, box.Top + labelSize, logoW + 0.5f, logoH, ka, alignRight: true);
        if (spd.Length > 0)
        {
            float sx = box.Right - 14 * k;
            using (var dv = new SKPaint { Color = accent.WithAlpha(A(0.5f)) }) c.DrawRect(new SKRect(box.Right - spdW, box.Top + 10 * k, box.Right - spdW + 1.2f, box.Bottom - 10 * k), dv);
            sl.Color = accent.WithAlpha(A(1)); sl.TextAlign = SKTextAlign.Right;
            c.DrawText(spdLbl, sx, box.Top + labelSize * 0.68f, sl);
            sp.TextAlign = SKTextAlign.Right;
            c.DrawText(spd, sx, box.Top + labelSize * 1.75f, sp);
        }
    }
    static double SlotFor(Kind k) => InS + HoldFor(k) + OutS + GapS;

    static readonly SKColor Glass = new(0x0B, 0x0E, 0x14, 232);
    static readonly SKColor Txt   = new(0xF4, 0xF6, 0xFA);
    // #340 (legibility, read from across the room): Txt2 was B9C1CF, Txt3 was 7E8AA0 (~55% - too dim on the TV)
    static readonly SKColor Txt2  = new(0xD3, 0xD9, 0xE3);
    static readonly SKColor Txt3  = new(0xB4, 0xBE, 0xCE);
    /// <summary>#340: the small caps label floor - anything under 13 px goes up 2-4 px (to at least 13).</summary>
    static float Lbl(float size) => size >= 13 ? size : Math.Min(size + 4, Math.Max(13, size + 2));
    static readonly SKColor Gold  = new(0xE8, 0xC2, 0x5A);
    static readonly SKColor Storm = new(0x3D, 0x6B, 0x8E);         // stormexhibit.com theme colour
    static readonly SKColor StormLight = new(0x6F, 0xA3, 0xC8);
    static readonly SKColor PlayerBlue = new(0x4D, 0x9F, 0xFF);

    readonly Dictionary<string, SKBitmap?> bmps = new(StringComparer.OrdinalIgnoreCase);
    double cardLocal;   // seconds since the current card started fading in

    // ---- sound (2026-10-20): each info card arrives with its own cue, once per card ----
    double cuedRun = double.NaN; int cuedSlot = -1;
    void CueSound(Kind kind, int slot, double local)
    {
        if (cuedRun != StartingGridRenderer.CardsStartMs) { cuedRun = StartingGridRenderer.CardsStartMs; cuedSlot = -1; }
        if (slot == cuedSlot) return;
        cuedSlot = slot;
        if (local > InS) return;   // joined mid-card: stay quiet
        StorySound.Play(kind switch
        {
            Kind.Weather => "pre_weather",       // radar ping
            Kind.Poles => "pre_poles",           // five start-light beeps
            Kind.PartnerAB => "pre_ab",          // nail gun + hammer
            Kind.PartnerStorm => "pre_storm",    // radar sweep (thunder waits for Angelo's own audio)
            Kind.Globe => "pre_globe",           // space hum, lock-on blips, the dive, the pin landing
            _ => "pre_panel_in",                 // race info, track facts, your car (engine fly-by waits for Angelo's audio)
        });
    }

    /// <summary>True while a card is on screen or animating (keeps the host presenting).</summary>
    public static bool Active(RaceSnapshot snap) =>
        snap.Phase == RacePhase.PreRace && StartingGridRenderer.CardsStartMs >= 0;

    public void Draw(SKCanvas c, float x, float y, RaceSnapshot snap, double nowMs)
    {
        GlobeIntro.Prepare(snap);   // starts the background build as soon as the track is known
        if (!Active(snap)) { ScenicNow = false; return; }
        double t = (nowMs - StartingGridRenderer.CardsStartMs) / 1000.0 - 0.6;   // small breath after the board leaves
        if (t < 0) { ScenicNow = StartingGridRenderer.MusicEndMs >= 0 && nowMs >= StartingGridRenderer.MusicEndMs; return; }   // #394: blimp from the end of the music
        // cards can have different lengths (the globe runs longer), so walk the running order
        LapFor(snap);
        var order = OrderFor(snap, !Screen.IsEmpty);
        // 2026-10-25 (Angelo: "the track intro card should not play twice ... it should not come into sequence again
        // before the green flag"): the first pass plays every card; after that the loop runs without the track card
        double first = order.Sum(SlotFor);
        int fullLen = order.Length;
        int cycles = 0; double tc = t;
        if (t >= first)
        {
            var rest = order.Where(k => k != Kind.TrackFacts).ToArray();
            if (rest.Length > 0) order = rest;
            double cycle = order.Sum(SlotFor);
            double t2 = t - first;
            cycles = 1 + (int)(t2 / cycle);
            tc = t2 - (cycles - 1) * cycle;
        }
        int idx = 0;
        while (idx < order.Length - 1 && tc >= SlotFor(order[idx])) { tc -= SlotFor(order[idx]); idx++; }
        int slot = cycles == 0 ? idx : fullLen + (cycles - 1) * order.Length + idx;   // unique per showing
        double local = tc;
        var kind = order[idx];
        ScenicNow = kind is Kind.TrackFacts or Kind.RaceInfo;
        double hold = HoldFor(kind);
        CueSound(kind, slot, local);

        float a, dy;
        if (local < InS) { float k = Ease((float)(local / InS)); a = k; dy = (1 - k) * 46; }
        else if (local < InS + hold) { a = 1; dy = 0; }
        else if (local < InS + hold + OutS) { float k = Ease((float)((local - InS - hold) / OutS)); a = 1 - k; dy = k * 30; }
        else return;

        // Half-screen panels whenever the host gives us the screen (Angelo 2026-10-19)
        if (!Screen.IsEmpty) { DrawPanel(c, snap, kind, slot, local, a); return; }
        var r = new SKRect(x, y + dy, x + CardW, y + dy + CardH);
        int scaleSave = c.Save();
        c.Scale(CardScale, CardScale, x + CardW / 2f, y + CardH);   // 20 % bigger, growing up from the bottom edge
        using var shape = ShapeFor(kind, r);
        carBreakBmp = null;
        SKColor accent = kind switch
        {
            Kind.Weather or Kind.PartnerStorm => StormLight,
            Kind.Driver => PlayerBlue,
            _ => Gold,
        };
        var backdrop = kind switch
        {
            Kind.Weather or Kind.PartnerStorm => Bmp("logos", "sponsors", "storm_radar_bg.png"),   // StormExhibit hallmark
            Kind.PartnerAB => Bmp("logos", "sponsors", "ab_roof_bg.png"),                            // A&B roofers painting
            Kind.Driver => CarBackdrop(snap.Player?.CarName, snap.Player)                                         // e.g. BMW M Hybrid V8 front end
                           ?? CarArt.Fallback(snap.Player?.CarName, snap.Player?.CarBrand, snap.Player?.ClassName, snap.Player?.ClassColor),   // user picture / drawn panel (2026-10-24)
            _ => null,
        };
        // Track Facts: the whole card takes the track logo's own background colour
        // (e.g. Le Mans navy) so the logo sits on it seamlessly
        SKColor? fill = kind == Kind.TrackFacts ? GridIntro.Matte(TrackLogoFor(snap)).Matte : null;

        // ---- per-card entrances (Angelo 2026-10-19): each card arrives its own way ----
        cardLocal = local;
        frameRevealR = -1; frameShingles = false; frameCarSlide = 0; frameBackdropA = 1; frameMaxScale = float.MaxValue;
        if (kind == Kind.Weather)                       // radar ping grows out of the StormExhibit icon
        { frameRevealCenter = new SKPoint(r.Left + 46, r.Top + 97); frameRevealR = K(0.0, 1.1) * 1150; }
        if (kind == Kind.PartnerAB) frameShingles = true;             // the painting is laid like shingles
        if (kind == Kind.Driver) frameCarSlide = 1 - K(0.25, 0.55);   // the car rushes in from the right
        if (kind == Kind.PartnerStorm) frameBackdropA = K(0.08, 0.7);  // the radar glows up after the strike

        int irisSave = -1;
        if (kind == Kind.Driver)                        // camera aperture opening from the middle
        {
            float R = K(0.0, 0.5) * 620;
            using var iris = new SKPath(); iris.AddCircle(r.MidX, r.MidY, Math.Max(1, R));
            irisSave = c.Save(); c.ClipPath(iris, antialias: true);
        }
        DrawFrame(c, r, accent, a, backdrop, kind is Kind.PartnerAB or Kind.Driver, fill, rightHalf: kind == Kind.Driver, shape: shape);
        if (kind == Kind.Weather) DrawScope(c, r, a);
        switch (kind)
        {
            case Kind.Weather:      DrawWeather(c, r, snap, a); break;
            case Kind.RaceInfo:     DrawRaceInfo(c, r, snap, a); break;
            case Kind.Poles:        DrawPoles(c, r, snap, a); break;
            case Kind.Watch:        DrawWatch(c, r, snap, a); break;
            case Kind.PartnerAB:    DrawPartnerAB(c, r, a); break;
            case Kind.TrackFacts:   DrawTrackFacts(c, r, snap, a); break;
            case Kind.Driver:       DrawDriver(c, r, snap, a); break;
            case Kind.PartnerStorm: DrawPartnerStorm(c, r, a); break;
        }
        if (irisSave >= 0)
        {
            c.RestoreToCount(irisSave);
            float flash = 1 - K(0.0, 0.28);             // the camera flash
            if (flash > 0) using (var fp = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(200 * flash * a)) })
                c.DrawPath(shape, fp);
        }
        if (kind == Kind.Driver) DrawCarBreakout(c, r, a);
        c.RestoreToCount(scaleSave);
    }

    // entrance timing: 0..1 eased progress of a step that starts at s seconds and lasts d seconds
    float K(double s, double d) => Ease((float)Math.Clamp((cardLocal - s) / d, 0, 1));
    float Lin(double s, double d) => (float)Math.Clamp((cardLocal - s) / d, 0, 1);
    // A&B roof build timing (s into the card)
    const double ShingleStart = 0.05, ShingleCourse = 0.26, ShingleTab = 0.07, ShingleSettle = 0.4, ShingleBuildEnd = 2.3;
    SKPoint frameRevealCenter; float frameRevealR = -1, frameCarSlide, frameBackdropA = 1, frameMaxScale = float.MaxValue; bool frameShingles;

    // count a value up: "66°F" at k=0.5 -> "33°F"; keeps the units and decimal places
    static string Tick(string v, float k)
    {
        if (k >= 1f) return v;
        var m = System.Text.RegularExpressions.Regex.Match(v, @"\d[\d,]*(\.\d+)?");
        if (!m.Success) return v;
        string num = m.Value; bool comma = num.Contains(',');
        if (!double.TryParse(num.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return v;
        int dec = m.Groups[1].Success ? m.Groups[1].Value.Length - 1 : 0;
        string fmt = comma ? "N" + dec : "F" + dec;
        return v.Substring(0, m.Index) + (d * k).ToString(fmt, CultureInfo.InvariantCulture) + v.Substring(m.Index + m.Length);
    }

    // ------------------------------------------------------------------ frame
    void DrawFrame(SKCanvas c, SKRect cardR, SKColor accent, float a, SKBitmap? backdrop = null, bool heavyWash = false, SKColor? fill = null, bool rightHalf = false, SKPath? shape = null)
    {
        using var ownShape = shape == null ? RoundShape(cardR) : null;
        var sp = shape ?? ownShape!;
        var r = sp.Bounds;   // fills the whole silhouette, bulges and gables included
        using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(110 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
        { c.Save(); c.Translate(0, 8); c.DrawPath(sp, sh); c.Restore(); }
        using (var bg = new SKPaint { IsAntialias = true, Color = fill is SKColor fc ? fc.WithAlpha((byte)(255 * a)) : Glass.WithAlpha((byte)(Glass.Alpha * a)) })
            c.DrawPath(sp, bg);
        c.Save();
        c.ClipPath(sp, antialias: true);
        if (fill == null) Theme.SkinNow(c, r, ThemeSurface.Splash, a);   // #601: the broadcast theme (nothing on Default)
        if (backdrop != null && rightHalf)
        {
            // Driver to Watch: the car's nose fills the right half of the card (cover-fit),
            // melting into the card on its left edge, lightly darkened for the stats on top.
            var area = new SKRect(r.Left + r.Width * 0.44f, r.Top, r.Right, r.Bottom);
            // a photo fills the half (cover); our own cut-out car sits whole in it, on the card's floor (contain)
            float sc = backdropCutout ? Math.Min(area.Width * 0.92f / backdrop.Width, area.Height * 0.96f / backdrop.Height)
                                      : Math.Max(area.Width / backdrop.Width, area.Height / backdrop.Height);
            float w = backdrop.Width * sc, h = backdrop.Height * sc;
            var dst = backdropCutout ? new SKRect(area.MidX - w / 2 + area.Width * 0.06f, area.Bottom - h - 4, area.MidX + w / 2 + area.Width * 0.06f, area.Bottom - 4)
                                     : new SKRect(area.MidX - w / 2, area.MidY - h / 2, area.MidX + w / 2, area.MidY + h / 2);
            c.Save();
            c.ClipRect(area);
            float slide = frameCarSlide * 300;
            // speed-blur ghosts trailing the car while it rushes in
            if (slide > 1)
                for (int gi = 3; gi >= 1; gi--)
                    using (var gp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium, Color = SKColors.White.WithAlpha((byte)(60 * a * frameCarSlide / gi)) })
                        c.DrawBitmap(backdrop, new SKRect(dst.Left + slide + gi * 40 * frameCarSlide, dst.Top, dst.Right + slide + gi * 40 * frameCarSlide, dst.Bottom), gp);
            using (var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * (1 - 0.4f * frameCarSlide))) })
                c.DrawBitmap(backdrop, new SKRect(dst.Left + slide, dst.Top, dst.Right + slide, dst.Bottom), ip);
            carBreakBmp = backdrop; carBreakDst = new SKRect(dst.Left + slide, dst.Top, dst.Right + slide, dst.Bottom); carBreakA = a * (1 - 0.4f * frameCarSlide);
            var g = Glass;
            using (var fade = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(area.Left, 0), new SKPoint(area.Left + area.Width * 0.45f, 0),
                       new[] { g.WithAlpha((byte)(255 * a)), g.WithAlpha((byte)(80 * a)) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(area, fade);
            c.Restore();
        }
        else if (backdrop != null)
        {
            // StormExhibit hallmark: the radar image stretched edge to edge (cover-fit),
            // then a dark wash so the text on top stays readable.
            // never enlarge a small picture past frameMaxScale (keeps the radar crisp on the big panels)
            float sc = Math.Min(Math.Max(r.Width / backdrop.Width, r.Height / backdrop.Height), frameMaxScale);
            float w = backdrop.Width * sc, h = backdrop.Height * sc;
            var dst = new SKRect(r.MidX - w / 2, r.MidY - h / 2, r.MidX + w / 2, r.MidY + h / 2);
            c.Save();
            if (frameRevealR >= 0)   // radar ping: only what the ring has passed over is revealed
            {
                using var ring = new SKPath(); ring.AddCircle(frameRevealCenter.X, frameRevealCenter.Y, Math.Max(1, frameRevealR));
                c.ClipPath(ring, antialias: true);
            }
            if (frameShingles && cardLocal < ShingleBuildEnd)
            {
                // A&B (Angelo 2026-10-19): a stone-coated steel roof goes on - Angelo's own roof photo,
                // made into a seamless panel (ab_roof_tile.png = one panel, two waves wide, one course
                // tall). Courses from the eave (bottom) up; each course is set panel by panel from the
                // left, every panel dropping onto its battens with a shadow and a glint off the stone
                // chips. When the roof is on, the painting fades up over it.
                var roof = Bmp("logos", "sponsors", "ab_roof_tile.png");
                float pitch = Math.Min(cardR.Height / 3f, 92f);   // panels: real-size roof courses
                int Courses = (int)Math.Ceiling(r.Height / pitch) + 1;   // the gable needs an extra course
                if (roof != null)
                {
                    float tw = roof.Width * (pitch / roof.Height);
                    int cols = (int)Math.Ceiling(r.Width / tw) + 1;
                    using var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                    using var shp = new SKPaint { IsAntialias = true };
                    for (int j = 0; j < Courses; j++)
                    {
                        float bottom = r.Bottom - j * pitch;
                        float top = bottom - pitch;
                        for (int ci = 0; ci < cols; ci++)
                        {
                            float x0 = r.Left - tw * 0.18f + ci * tw;
                            double ts = ShingleStart + j * Math.Min(ShingleCourse, 1.15 / Courses) + ci * ShingleTab;
                            float k = K(ts, 0.24);
                            if (k <= 0) continue;
                            float drop = (1 - k) * 22f;
                            var panel = new SKRect(x0, top - drop, x0 + tw, bottom - drop);
                            // the panel's shadow on the course below while it comes down
                            float fresh = 1 - K(ts + 0.2, 0.35);
                            shp.Color = new SKColor(0, 0, 0, (byte)(170 * a * k * fresh));
                            shp.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4);
                            c.DrawRect(new SKRect(panel.Left + 2, panel.Bottom - 6, panel.Right - 2, panel.Bottom + 4 + drop * 0.4f), shp);
                            shp.MaskFilter = null;
                            ip.Color = SKColors.White.WithAlpha((byte)(255 * a * Math.Min(1f, k * 2f)));
                            c.DrawBitmap(roof, panel, ip);
                            // side lap where the panel overlaps its neighbour, fading once seated
                            shp.Color = new SKColor(0, 0, 0, (byte)(110 * a * k * fresh));
                            c.DrawRect(new SKRect(panel.Left, panel.Top, panel.Left + 2, panel.Bottom), shp);
                            // glint across the stone chips as the panel seats
                            float gl = k * (1 - K(ts + 0.1, 0.35));
                            if (gl > 0.01f)
                                using (var gp = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Screen, Shader = SKShader.CreateLinearGradient(
                                           new SKPoint(panel.Left, panel.Bottom), new SKPoint(panel.Right, panel.Top),
                                           new[] { new SKColor(255, 240, 215, 0), new SKColor(255, 240, 215, (byte)(90 * a * gl)), new SKColor(255, 240, 215, 0) },
                                           new[] { Math.Max(0f, 1 - k - 0.25f), 1 - k * 0.75f, Math.Min(1f, 1.25f - k * 0.5f) }, SKShaderTileMode.Clamp) })
                                    c.DrawRect(panel, gp);
                        }
                    }
                }
                // the roof is on: the painting fades up over it
                float pa = K(ShingleBuildEnd - 0.7, 0.65);
                if (pa > 0 || roof == null)
                    using (var ip2 = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * (roof == null ? 1 : pa))) })
                        c.DrawBitmap(backdrop, dst, ip2);
            }
            else if (frameShingles)
                using (var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)) })
                    c.DrawBitmap(backdrop, dst, ip);
            else
                using (var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * frameBackdropA)) })
                    c.DrawBitmap(backdrop, dst, ip);
            c.Restore();
            using (var wash = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0),
                       heavyWash
                         ? new[] { new SKColor(6, 10, 16, (byte)(225 * a)), new SKColor(6, 10, 16, (byte)(150 * a)), new SKColor(6, 10, 16, (byte)(135 * a)) }
                         : new[] { new SKColor(6, 10, 16, (byte)(225 * a)), new SKColor(6, 10, 16, (byte)(165 * a)), new SKColor(6, 10, 16, (byte)(120 * a)) },
                       new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawRect(r, wash);
        }
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f,
                   Color = accent == StormLight ? StormLight.WithAlpha((byte)(150 * a)) : new SKColor(255, 255, 255, (byte)(70 * a)) })
            c.DrawPath(sp, edge);
        // (no coloured accent bar / top line on the info cards - Angelo 2026-10-19)
        c.Restore();
    }

    // ------------------------------------------------------------------ card shapes
    // Angelo 2026-10-19: every card gets its own silhouette (and they're 20 % bigger):
    //   Weather       - the radar scope bulges out of the top edge
    //   Race Info     - an event ticket: stub notches top and bottom at the tear line
    //   Pole Sitters  - a slanted livery parallelogram
    //   A&B partner   - a roof: the top edge rises to a gable
    //   Track Facts   - a clean card; the circuit map breaks out above it (DrawTrackFacts)
    //   Driver        - a sharp angled cut on the right, the car's nose bursts through it
    //   StormExhibit  - a lightning bolt notched into the top edge where the strike lands
    const float CardScale = 1.2f;
    static SKPoint ScopeCenter(SKRect r) => new(r.Left + 104, r.Top + 10);
    const float ScopeR = 64f;

    static SKPath RoundShape(SKRect r) { var p = new SKPath(); p.AddRoundRect(r, 12, 12); return p; }

    static SKPath RoundedPoly(float rad, params SKPoint[] pts)
    {
        using var poly = new SKPath();
        poly.AddPoly(pts, true);
        using var pe = SKPathEffect.CreateCorner(rad);
        using var pp = new SKPaint { PathEffect = pe, Style = SKPaintStyle.Fill };
        var o = new SKPath();
        if (!pp.GetFillPath(poly, o)) { o.Dispose(); o = new SKPath(poly); }
        return o;
    }

    static SKPath Combine(SKPath a, SKPath b, SKPathOp op)
    {
        var o = a.Op(b, op);
        if (o == null) return new SKPath(a);
        return o;
    }

    static SKPath ShapeFor(Kind k, SKRect r)
    {
        float L = r.Left, T = r.Top, R = r.Right, B = r.Bottom, W = r.Width;
        switch (k)
        {
            case Kind.Weather:
            {
                using var box = RoundShape(r);
                using var scope = new SKPath(); var sc = ScopeCenter(r); scope.AddCircle(sc.X, sc.Y, ScopeR);
                using var both = new SKPath(box); both.AddPath(scope);
                return both.Simplify() ?? new SKPath(both);   // one outline: card + scope
            }
            case Kind.RaceInfo:
            {
                using var box = RoundShape(r);
                using var notches = new SKPath();
                notches.AddCircle(L + 330, T, 18); notches.AddCircle(L + 330, B, 18);
                return Combine(box, notches, SKPathOp.Difference);
            }
            case Kind.Poles:
                return RoundedPoly(12, new SKPoint(L + 34, T), new SKPoint(R, T), new SKPoint(R - 34, B), new SKPoint(L, B));
            case Kind.PartnerAB:
                return RoundedPoly(12, new SKPoint(L, B), new SKPoint(L, T + 30), new SKPoint(L + W * 0.70f, T - 46),
                                   new SKPoint(R, T + 30), new SKPoint(R, B));
            case Kind.Driver:
                return RoundedPoly(12, new SKPoint(L, T), new SKPoint(R - 76, T), new SKPoint(R, B), new SKPoint(L, B));
            case Kind.PartnerStorm:
            {
                using var box = RoundShape(r);
                float x = L + W * 0.58f;
                using var bolt = new SKPath();
                bolt.AddPoly(new[] { new SKPoint(x - 22, T - 2), new SKPoint(x + 30, T - 2), new SKPoint(x + 8, T + 38), new SKPoint(x + 28, T + 38),
                                     new SKPoint(x - 14, T + 92), new SKPoint(x - 2, T + 50), new SKPoint(x - 24, T + 50) }, true);
                return Combine(box, bolt, SKPathOp.Difference);
            }
            default:
                return RoundShape(r);
        }
    }

    /// <summary>Weather: the radar scope in the bulge - range rings, crosshair, a sweeping beam.</summary>
    void DrawScope(SKCanvas c, SKRect r, float a)
    {
        var sc = ScopeCenter(r);
        float k = K(0.0, 0.6);
        c.Save();
        using (var clip = new SKPath()) { clip.AddCircle(sc.X, sc.Y, ScopeR - 1); c.ClipPath(clip, antialias: true); }
        using (var dim = new SKPaint { Shader = SKShader.CreateRadialGradient(sc, ScopeR,
                   new[] { new SKColor(4, 12, 20, (byte)(90 * a)), new SKColor(4, 12, 20, (byte)(170 * a)) }, null, SKShaderTileMode.Clamp) })
            c.DrawCircle(sc, ScopeR, dim);
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = StormLight.WithAlpha((byte)(110 * a * k)) })
        {
            for (int i = 1; i <= 3; i++) c.DrawCircle(sc, ScopeR * i / 3.4f, ring);
            c.DrawLine(sc.X - ScopeR, sc.Y, sc.X + ScopeR, sc.Y, ring);
            c.DrawLine(sc.X, sc.Y - ScopeR, sc.X, sc.Y + ScopeR, ring);
        }
        float ang = (float)(cardLocal * 2.2);
        using (var beam = new SKPaint { IsAntialias = true, Shader = SKShader.CreateSweepGradient(sc,
                   new[] { StormLight.WithAlpha(0), StormLight.WithAlpha(0), StormLight.WithAlpha((byte)(150 * a * k)) }, new[] { 0f, 0.82f, 1f }) })
        {
            c.Save(); c.RotateRadians(ang, sc.X, sc.Y);
            c.DrawCircle(sc, ScopeR, beam);
            c.Restore();
        }
        c.Restore();
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = StormLight.WithAlpha((byte)(230 * a)) })
            c.DrawCircle(sc, ScopeR - 0.5f, rim);
    }

    // Driver: the car's nose bursts through the angled cut (drawn outside the card, fading off)
    SKBitmap? carBreakBmp; SKRect carBreakDst; float carBreakA;
    void DrawCarBreakout(SKCanvas c, SKRect r, float a)
    {
        if (carBreakBmp == null || carBreakA <= 0.01f) return;
        using var wedge = new SKPath();
        wedge.AddPoly(new[] { new SKPoint(r.Right - 77, r.Top), new SKPoint(r.Right - 40, r.Top), new SKPoint(r.Right + 37, r.Bottom), new SKPoint(r.Right - 1, r.Bottom) }, true);
        var bounds = new SKRect(r.Right - 80, r.Top, r.Right + 50, r.Bottom);
        c.Save();
        c.ClipPath(wedge, antialias: true);
        c.SaveLayer(bounds, null);
        using (var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * carBreakA)) })
            c.DrawBitmap(carBreakBmp, carBreakDst, ip);
        // fade along the cut's outward normal
        float ex = 76f, ey = r.Height, el = MathF.Sqrt(ex * ex + ey * ey);
        var nrm = new SKPoint(ey / el, -ex / el);
        var mid = new SKPoint(r.Right - 38, r.MidY);
        using (var fade = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateLinearGradient(mid, new SKPoint(mid.X + nrm.X * 36, mid.Y + nrm.Y * 36),
                   new[] { SKColors.White.WithAlpha(220), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(bounds, fade);
        c.Restore();
        c.Restore();
        using (var cut = new SKPaint { IsAntialias = true, StrokeWidth = 2.5f, Color = PlayerBlue.WithAlpha((byte)(230 * a)),
                   MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Solid, 2) })
            c.DrawLine(r.Right - 74, r.Top + 2, r.Right - 2, r.Bottom - 4, cut);
    }

    // label block on the left: tracked caps title + a line beneath
    static void Title(SKCanvas c, SKRect r, string title, SKColor accent, float a)
    {
        using var p = P(Fonts.Inter.Bold, 14, accent, a);   // #340: was 12
        TextTracked.Draw(c, p, title, r.Left + 30, r.Top + 42, 3.4f);
    }

    // a stat column: small caps label over a big value
    static void Stat(SKCanvas c, float x, SKRect r, string label, string value, float a, string? sub = null, float maxW = 1000,
                     float labelSize = 10, float valueSize = 28, float labelY = 52, float valueY = 94, float subY = 118, float subSize = 12)
    {
        labelSize = Lbl(labelSize);
        using (var lp = P(Fonts.Inter.Bold, labelSize, Txt3, a)) TextTracked.Draw(c, lp, label, x, r.Top + labelY, 0.2f * labelSize);
        if (sub != null) subSize = Lbl(subSize);
        using (var vp = P(Fonts.Inter.Bold, valueSize, Txt, a)) { FitSize(vp, value, maxW, 15); c.DrawText(value, x, r.Top + valueY, vp); }
        if (sub != null) using (var sp = P(Fonts.Inter.SemiBold, subSize, Txt2, a)) c.DrawText(sub, x, r.Top + subY, sp);
    }

    // compact stat for a bottom row; returns the right edge
    static float MiniStat(SKCanvas c, float x, SKRect r, string label, string value, float a,
                          float labelSize = 9, float valueSize = 19, float labelY = 109, float valueY = 131)
    {
        float w;
        labelSize = Lbl(labelSize);
        using (var lp = P(Fonts.Inter.Bold, labelSize, Txt3, a)) { TextTracked.Draw(c, lp, label, x, r.Top + labelY, 0.2f * labelSize); w = lp.MeasureText(label) + label.Length * 0.2f * labelSize; }
        using (var vp = P(Fonts.Inter.Bold, valueSize, Txt, a)) { c.DrawText(value, x, r.Top + valueY, vp); w = Math.Max(w, vp.MeasureText(value)); }
        return x + w;
    }

    // big white serif headline fitted to a width (e.g. "POLE SITTERS", "OFFICIAL PARTNER")
    static float Headline(SKCanvas c, string text, float x, float baseline, float maxW, float maxSize, float a, bool center = false)
    {
        using var p = P(Fonts.CormorantGaramond.Bold, maxSize, Txt, a);
        FitSize(p, text, maxW, 12);
        if (center) p.TextAlign = SKTextAlign.Center;
        c.DrawText(text, x, baseline, p);
        return p.TextSize;
    }

    static void Divider(SKCanvas c, float x, SKRect r, float a)
    {
        using var d = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(22 * a)) };
        c.DrawRect(new SKRect(x, r.Top + 30, x + 1, r.Bottom - 30), d);
    }

    // ---------------------------------------------------------------- cards
    void DrawWeather(SKCanvas c, SKRect r, RaceSnapshot s, float a)
    {
        // no title: "presented by" + a big StormExhibit wordmark fill the left block,
        // and the conditions run large across the rest (Angelo 2026-10-19)
        // ENTRANCE: a radar beam sweeps out of the StormExhibit icon; the radar picture and each
        // reading light up as the ping ring reaches them, the numbers ticking in like live data.
        float la = a * K(0.0, 0.35);
        if (SponsorPack.IsOn(Partner.Storm))
            using (var pp = P(Fonts.Inter.SemiBold, 17, Txt2, la)) c.DrawText("presented by", r.Left + 30, r.Top + 56, pp);
        float wm = 40;
        while (wm > 18 && WordmarkWidth(wm) > 282) wm -= 1;
        StormWordmark(c, r.Left + 30, r.Top + 108, wm, la);
        Divider(c, r.Left + 330, r, a);

        float beamA = 1 - K(1.0, 0.6);
        if (beamA > 0)
        {
            var ctr = frameRevealCenter;
            c.Save();
            using (var rr = new SKRoundRect(r, 12, 12)) c.ClipRoundRect(rr, antialias: true);
            c.Translate(ctr.X, ctr.Y); c.RotateDegrees((float)(cardLocal * 330 - 60));
            using (var beam = new SKPaint { IsAntialias = true, Shader = SKShader.CreateSweepGradient(SKPoint.Empty,
                       new[] { SKColors.Transparent, StormLight.WithAlpha(0), StormLight.WithAlpha((byte)(150 * beamA * a)) }, new[] { 0f, 0.86f, 1f }) })
                c.DrawCircle(0, 0, 1200, beam);
            using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = StormLight.WithAlpha((byte)(170 * beamA * a)) })
                c.DrawCircle(0, 0, frameRevealR, ring);
            c.Restore();
        }

        var e = s.Extras;
        bool f = IsImperial(s);
        float x0 = r.Left + 356;
        const float LS = 12, VS = 35, LY = 46, VY = 94, SY = 122, SS = 14;
        // each reading appears as the ping ring reaches its column, then ticks up to its value
        float Hit(float x) => Math.Clamp((frameRevealR - (x - frameRevealCenter.X)) / 140f, 0, 1);
        float TickK(float x) { float h = Hit(x); return h <= 0 ? 0 : Ease(Math.Clamp((frameRevealR - (x - frameRevealCenter.X)) / 600f, 0, 1)); }
        Stat(c, x0, r, "AIR", Tick(Temp(s.AirTempC, f), TickK(x0)), a * Hit(x0), null, 92, LS, VS, LY, VY, SY, SS);
        Stat(c, x0 + 106, r, "TRACK", Tick(Temp(s.TrackTempC, f), TickK(x0 + 106)), a * Hit(x0 + 106), null, 92, LS, VS, LY, VY, SY, SS);
        Stat(c, x0 + 214, r, "SKY", Sky(e.Skies), a * Hit(x0 + 214), null, 184, LS, VS, LY, VY, SY, SS);
        string wind = e.WindMs > 0.05f ? (f ? $"{e.WindMs * 2.237f:0} MPH" : $"{e.WindMs * 3.6f:0} KM/H") : "CALM";
        Stat(c, x0 + 414, r, "WIND", Tick(wind, TickK(x0 + 414)), a * Hit(x0 + 414), e.WindMs > 0.05f ? "from " + Compass(e.WindDirRad) : null, 126, LS, VS, LY, VY, SY, SS);
        string rain = e.PrecipNow > 0.01f || s.IsRaining ? "RAIN" : $"{e.RainChancePct:0}%";
        Stat(c, x0 + 562, r, "RAIN", Tick(rain, TickK(x0 + 562)), a * Hit(x0 + 562), e.Humidity >= 0 ? $"humidity {e.Humidity * 100:0}%" : null, 100, LS, VS, LY, VY, SY, SS);
    }

    void DrawRaceInfo(SKCanvas c, SKRect r, RaceSnapshot s, float a)
    {
        // Layout (no title, Angelo 2026-10-19): big series name on top, big stats below.
        string series = (SeriesNames.ResolveName(s.SeriesId) ?? "TODAY'S RACE").ToUpperInvariant();

        // series logo on the right of the card
        float textRight = r.Right - 30;
        var slug = SeriesNames.ResolveLogoSlug(s.SeriesId);
        var slogo = slug != null ? Bmp("logos", "series", slug + ".png") : null;
        if (slogo != null)
        {
            float maxW = 230, maxH = 104, sc = Math.Min(maxW / slogo.Width, maxH / slogo.Height);
            float lw = slogo.Width * sc, lh = slogo.Height * sc;
            var dst = new SKRect(r.Right - 36 - lw, r.MidY - lh / 2, r.Right - 36, r.MidY + lh / 2);
            // ENTRANCE: the series logo spins round into place
            float fk = K(0.15, 0.75);
            float sx = (float)Math.Cos((1 - fk) * Math.PI);
            using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * Math.Min(1f, fk * 3))) };
            c.Save();
            c.Translate(dst.MidX, dst.MidY); c.Scale(Math.Max(0.02f, Math.Abs(sx)), 1); c.Translate(-dst.MidX, -dst.MidY);
            c.DrawBitmap(slogo, dst, lp);
            c.Restore();
            textRight = dst.Left - 34;
            Divider(c, dst.Left - 18, r, a);
        }

        float rowW = textRight - (r.Left + 30);
        // ENTRANCE: the series name flips on letter by letter like a stadium scoreboard
        using (var p = P(Fonts.CormorantGaramond.Bold, 46, Txt, a))
        {
            FitSize(p, series, rowW, 13);
            float cx = r.Left + 30, baseY = r.Top + 62, mid = baseY - p.TextSize * 0.34f;
            for (int i = 0; i < series.Length; i++)
            {
                string ch = series[i].ToString();
                float w = p.MeasureText(ch);
                float k = K(0.05 + i * 0.028, 0.22);
                if (k > 0 && ch != " ")
                {
                    byte al = p.Color.Alpha;
                    p.Color = p.Color.WithAlpha((byte)(al * k));
                    c.Save(); c.Translate(0, mid); c.Scale(1, Math.Max(0.02f, k)); c.Translate(0, -mid);
                    c.DrawText(ch, cx, baseY, p);
                    c.Restore();
                    p.Color = p.Color.WithAlpha(al);
                }
                cx += w;
            }
        }
        using (var ln = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(22 * a)) })
            c.DrawRect(new SKRect(r.Left + 30, r.Top + 78, r.Left + 30 + (textRight - r.Left - 30) * K(0.3, 0.6), r.Top + 79), ln);

        int cars = 0; var irs = new List<int>(); var classNames = new List<string>();
        foreach (var cl in s.ClassLeaderboard)
        {
            if (cl.Drivers.Count == 0) continue;
            classNames.Add(ClassLogo.DisplayName(cl.Name));
            foreach (var d in cl.Drivers) { cars++; if (d.IRating > 0) irs.Add(d.IRating); }
        }
        // 2026-10-31: the scheduled length (SessionTimeTotal) - before the green the time left is not the race length
        double total = s.Extras.SessionTimeTotal > 0 && s.Extras.SessionTimeTotal < 86400 * 2 ? s.Extras.SessionTimeTotal : s.SessionTimeRemaining;
        string length = s.TotalLaps > 0 ? $"{s.TotalLaps} LAPS" : Duration(total);
        float x = r.Left + 30;
        const float LS = 11, VS = 27, LY = 100, VY = 132;
        // ENTRANCE: the stats pop up one after another (and count up)
        float Pop(int i) => K(0.7 + i * 0.16, 0.35);
        float PopX(SKCanvas cv, float xx, string lab, string val, int i)
        {
            float k = Pop(i);
            cv.Save(); cv.Translate(0, (1 - k) * 14);
            MiniStat(cv, xx, r, lab, Tick(val, K(0.7 + i * 0.16, 0.8)), a * k, LS, VS, LY, VY);
            cv.Restore();
            // lay out by the FINAL value's width so nothing shuffles while numbers count up
            using var lp = P(Fonts.Inter.Bold, LS, Txt3, 1);
            using var vp = P(Fonts.Inter.Bold, VS, Txt, 1);
            return xx + Math.Max(lp.MeasureText(lab) + lab.Length * 0.3f * LS, vp.MeasureText(val));
        }
        x = PopX(c, x, "LENGTH", length, 0) + 48;
        x = PopX(c, x, "FIELD", $"{cars} CARS", 1) + 48;
        string classes = classNames.Count == 0 ? "-" : string.Join("  ·  ", classNames);
        x = PopX(c, x, classNames.Count == 1 ? "1 CLASS" : $"{classNames.Count} CLASSES", classes, 2) + 48;
        PopX(c, x, "STRENGTH OF FIELD", irs.Count > 0 ? Sof(irs).ToString("N0", CultureInfo.InvariantCulture) : "-", 3);
    }

    void DrawPoles(SKCanvas c, SKRect r, RaceSnapshot s, float a)
    {
        // no small title: a big "POLE SITTERS" fills the left block from the edge to the divider
        // ENTRANCE: the headline wipes on with its gold underline, a row of race-start lights
        // fills across the card, and when the lights go out the pole sitters slide in.
        c.Save();
        c.ClipRect(new SKRect(r.Left, r.Top, r.Left + 30 + 300 * K(0.0, 0.45), r.Bottom));
        Headline(c, "POLE SITTERS", r.Left + 30, r.MidY + 12, 284, 64, a);
        c.Restore();
        using (var ln = new SKPaint { Color = Gold.WithAlpha((byte)(200 * a)) })
            c.DrawRect(new SKRect(r.Left + 30, r.MidY + 28, r.Left + 30 + 90 * K(0.35, 0.35), r.MidY + 30), ln);
        Divider(c, r.Left + 330, r, a);

        const double LightsOn = 0.35, LightStep = 0.2, LightsOut = LightsOn + 5 * LightStep + 0.35;   // ~1.7 s
        if (cardLocal < LightsOut + 0.25)
        {
            float outFade = 1 - Lin(LightsOut, 0.12);
            // BIG gantry (Angelo 2026-10-19): the five lights span the whole right side of the card
            float areaL = r.Left + 362, areaR = r.Right - 30, sp = (areaR - areaL) / 5f;
            float hA = Math.Min(1f, K(0.1, 0.25)) * (1 - Lin(LightsOut + 0.05, 0.2));
            float hw = sp * 0.42f, hh = Math.Min(62f, r.Height * 0.36f), bulbR = Math.Min(hw, hh) * 0.66f;
            // the gantry beam the light pods hang from
            using (var beam = new SKPaint { IsAntialias = true, Color = new SKColor(14, 16, 21, (byte)(235 * a * hA)) })
                c.DrawRoundRect(new SKRect(areaL + 6, r.MidY - 7, areaR - 6, r.MidY + 7), 4, 4, beam);
            for (int li = 0; li < 5; li++)
            {
                float lx = areaL + sp * (li + 0.5f), ly = r.MidY;
                bool lit = cardLocal >= LightsOn + li * LightStep && outFade > 0;
                var pod = new SKRect(lx - hw, ly - hh, lx + hw, ly + hh);
                using (var housing = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, pod.Top), new SKPoint(0, pod.Bottom),
                           new[] { new SKColor(34, 37, 46, (byte)(240 * a * hA)), new SKColor(12, 13, 18, (byte)(240 * a * hA)) }, null, SKShaderTileMode.Clamp) })
                    c.DrawRoundRect(pod, 12, 12, housing);
                using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(255, 255, 255, (byte)(30 * a * hA)) })
                    c.DrawRoundRect(pod, 12, 12, rim);
                if (lit)
                {
                    using (var glow = new SKPaint { IsAntialias = true, Color = new SKColor(255, 40, 40, (byte)(130 * a * outFade)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) })
                        c.DrawCircle(lx, ly, bulbR * 1.35f, glow);
                    using (var bulb = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(lx - bulbR * 0.25f, ly - bulbR * 0.3f), bulbR * 1.2f,
                               new[] { new SKColor(255, 170, 160, (byte)(255 * a * outFade)), new SKColor(255, 45, 45, (byte)(255 * a * outFade)), new SKColor(190, 10, 20, (byte)(255 * a * outFade)) },
                               new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp) })
                        c.DrawCircle(lx, ly, bulbR, bulb);
                }
                else using (var off = new SKPaint { IsAntialias = true, Color = new SKColor(62, 18, 22, (byte)(220 * a * hA)) })
                    c.DrawCircle(lx, ly, bulbR, off);
            }
        }
        var poles = new List<(DriverClass Cls, Driver D)>();
        foreach (var cl in s.ClassLeaderboard)
        {
            Driver? best = null;
            foreach (var d in cl.Drivers)
                if (d.PositionInClass > 0 && (best == null || d.PositionInClass < best.PositionInClass)) best = d;
            if (best != null) poles.Add((cl, best));
        }
        if (poles.Count == 0) return;
        // top three classes, shown lowest class on the left -> top class on the right (e.g. GTD PRO, LMP2, GTP)
        poles = poles.Take(3).Reverse().ToList();
        int n = poles.Count;
        float colW = (r.Right - 30 - (r.Left + 362)) / n;
        c.Save();
        c.ClipRect(new SKRect(r.Left + 340, r.Top, r.Right - 4, r.Bottom));
        float aBase = a;
        for (int i = 0; i < n; i++)
        {
            var (cls, d) = poles[i];
            // each pole sitter slides in from the left, one after another
            float k = Ease((float)Math.Clamp((cardLocal - (LightsOut + 0.05) - i * 0.18) / 0.5, 0, 1));   // "lights out and away we go"
            if (k <= 0) continue;
            float x = r.Left + 362 + i * colW - (1 - k) * 120;
            a = aBase * k;
            var logo = Bmp("logos", "classes", (ClassLogo.Resolve(cls.Name) ?? cls.Name.Replace(" ", "").ToUpperInvariant()) + ".png");
            if (logo != null)
            {
                float h = 22, w = logo.Width * (h / logo.Height);
                using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)) };
                c.DrawBitmap(logo, new SKRect(x, r.Top + 26, x + w, r.Top + 26 + h), lp);
            }
            else using (var cp = P(Fonts.Inter.Bold, 14, Gold, a)) TextTracked.Draw(c, cp, ClassLogo.DisplayName(cls.Name), x, r.Top + 44, 3);
            using (var np = P(Fonts.CormorantGaramond.Bold, 34, Txt, a))
            { string nm = NameFormat.Broadcast(d); FitSize(np, nm, colW - 14, 18); c.DrawText(Fit(nm, np, colW - 14), x, r.Top + 92, np); }
            using (var tp = P(Fonts.Inter.SemiBold, 17, Txt2, a)) c.DrawText(Lap(d.QualifyTime) + "   #" + d.CarNumber, x, r.Top + 124, tp);
        }
        c.Restore();
    }

    /// <summary>2026-11-06: the Drivers to watch card can be switched off (Settings / the producer page; control.json driversToWatch).</summary>
    public static bool ShowWatch = true;

    /// <summary>2026-11-06: DRIVERS TO WATCH - three drivers and why (DriversToWatch): the tag in gold over the class colour,
    /// the name, then the number that makes the case (lap time / grid slot / iRating) and the reason.</summary>
    void DrawWatch(SKCanvas c, SKRect r, RaceSnapshot s, float a)
    {
        c.Save();
        c.ClipRect(new SKRect(r.Left, r.Top, r.Left + 30 + 300 * K(0.0, 0.45), r.Bottom));
        Headline(c, "DRIVERS TO WATCH", r.Left + 30, r.MidY + 12, 284, 52, a);
        c.Restore();
        using (var ln = new SKPaint { Color = Gold.WithAlpha((byte)(200 * a)) })
            c.DrawRect(new SKRect(r.Left + 30, r.MidY + 28, r.Left + 30 + 90 * K(0.35, 0.35), r.MidY + 30), ln);
        Divider(c, r.Left + 330, r, a);
        var picks = DriversToWatch.For(s);
        int n = Math.Min(3, picks.Count);
        if (n == 0) return;
        float colW = (r.Right - 30 - (r.Left + 362)) / n;
        c.Save();
        c.ClipRect(new SKRect(r.Left + 340, r.Top, r.Right - 4, r.Bottom));
        float aBase = a;
        for (int i = 0; i < n; i++)
        {
            var p = picks[i];
            float k = Ease((float)Math.Clamp((cardLocal - 0.45 - i * 0.22) / 0.5, 0, 1));
            if (k <= 0) continue;
            float x = r.Left + 362 + i * colW + (1 - k) * 60;
            a = aBase * k;
            var cc = SKColor.TryParse(p.ClassColor.StartsWith('#') ? p.ClassColor : "#" + p.ClassColor, out var pc) ? pc : Gold;
            using (var bar = new SKPaint { IsAntialias = true, Color = cc.WithAlpha((byte)(255 * a)) })
                c.DrawRoundRect(new SKRect(x, r.Top + 24, x + 4, r.Top + 46), 2, 2, bar);
            using (var tp = P(Fonts.Inter.Bold, 12, Gold, a))
                TextTracked.Draw(c, tp, (p.Tag + " · " + ClassLogo.DisplayName(p.ClassName)).ToUpperInvariant(), x + 12, r.Top + 40, 2.4f);
            using (var np = P(Fonts.CormorantGaramond.Bold, 30, Txt, a))
            { string nm = p.Name + "  #" + p.Number; FitSize(np, nm, colW - 16, 17); c.DrawText(Fit(nm, np, colW - 16), x, r.Top + 80, np); }
            using (var vp = P(Fonts.Inter.Bold, 17, Gold, a)) c.DrawText(Fit(p.Value, vp, colW - 16), x, r.Top + 106, vp);
            using (var rp = P(Fonts.Inter.Medium, 12.5f, Txt2, a)) { FitSize(rp, p.Reason, colW - 16, 9.5f); c.DrawText(Fit(p.Reason, rp, colW - 16), x, r.Top + 128, rp); }
        }
        c.Restore();
    }

    void DrawPartnerAB(SKCanvas c, SKRect r, float a)
    {
        // "OFFICIAL PARTNER" as the big white headline across the left block, then the
        // three-line tagline centred beneath it (Angelo 2026-10-19)
        // ENTRANCE: the painting is laid row by row like shingles (see DrawFrame), the words
        // settle in, then the A&B logo STAMPS down onto the roof with a puff of dust.
        float cx0 = r.Left + 30 + 140;
        float frameA = a;
        a = frameA * K(0.45, 0.4);
        Headline(c, "OFFICIAL PARTNER", cx0, r.Top + 56, 280, 40, a, center: true);
        using (var p = P(Fonts.Inter.SemiBold, 16, Txt2, a))
        {
            p.TextAlign = SKTextAlign.Center;
            FitSize(p, "Insurance Backed Restoration", 280, 11);
            c.DrawText("Insurance Backed Restoration", cx0, r.Top + 84, p);
            using (var amp = P(Fonts.CormorantGaramond.Bold, p.TextSize * 1.25f, Gold, a)) { amp.TextAlign = SKTextAlign.Center; c.DrawText("&", cx0, r.Top + 106, amp); }
            c.DrawText("General Contracting", cx0, r.Top + 128, p);
        }
        var logo = Bmp("logos", "sponsors", "AB_GC_Logo.png");
        if (logo != null)
        {
            float maxW = 560, maxH = 92, sc = Math.Min(maxW / logo.Width, maxH / logo.Height);
            float w = logo.Width * sc, h = logo.Height * sc;
            // right edge, so the roofers in the backdrop painting stay visible
            var dst = new SKRect(r.Right - 44 - w, r.MidY - h / 2, r.Right - 44, r.MidY + h / 2);
            const double StampAt = 2.05, StampLen = 0.28;   // stamps down once the roof is laid and painted
            float sk = (float)Math.Clamp((cardLocal - StampAt) / StampLen, 0, 1);
            if (sk > 0)
            {
                float fall = sk * sk;                        // accelerates down onto the roof
                float scale = 1.8f - 0.8f * fall;
                float logoA = frameA * Math.Min(1f, sk * 2.5f);
                using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * logoA)),
                                             ImageFilter = SKImageFilter.CreateDropShadow(0, 2 + 10 * (1 - fall), 6 + 10 * (1 - fall), 6 + 10 * (1 - fall), new SKColor(0, 0, 0, (byte)(200 * Math.Min(1f, sk * 2.5f)))) };
                c.Save();
                c.Translate(dst.MidX, dst.MidY); c.Scale(scale); c.Translate(-dst.MidX, -dst.MidY);
                c.DrawBitmap(logo, dst, lp);
                c.Restore();
                // impact: a dust puff kicks out from under the logo and a quick shake of light
                float dk = (float)Math.Clamp((cardLocal - StampAt - StampLen) / 0.7, 0, 1);
                if (dk > 0 && dk < 1)
                {
                    var rnd = new Random(11);
                    for (int i = 0; i < 26; i++)
                    {
                        float ang = (float)(rnd.NextDouble() * Math.PI);           // spray out sideways and up
                        float dir = rnd.NextDouble() < 0.5 ? -1 : 1;
                        float dist = (40 + (float)rnd.NextDouble() * 120) * Ease(dk);
                        float px = dst.MidX + dir * (w * 0.35f + dist * (float)Math.Cos(ang * 0.3));
                        float py = dst.Bottom - 6 - dist * 0.35f * (float)Math.Sin(ang);
                        float pr = (6 + (float)rnd.NextDouble() * 12) * (0.6f + dk);
                        using var dp = new SKPaint { IsAntialias = true, Color = new SKColor(210, 196, 176, (byte)(110 * (1 - dk) * frameA)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) };
                        c.DrawCircle(px, py, pr, dp);
                    }
                }
            }
        }
        else using (var p = P(Fonts.CormorantGaramond.Bold, 40, Txt, a)) { p.TextAlign = SKTextAlign.Center; c.DrawText("A&B GENERAL CONTRACTING", (r.Left + 330 + r.Right) / 2, r.MidY + 14, p); }
    }

    void DrawTrackFacts(SKCanvas c, SKRect r, RaceSnapshot s, float a)
    {
        // Layout: row 1 = title, row 2 = full-width track name (never cut off),
        // row 3 = flag · location · length · turns. Map sits on the right.
        // (no "TRACK FACTS" title - the name and facts run bigger instead, Angelo 2026-10-19)
        var e = s.Extras;
        bool f = IsImperial(s);

        var map = MapFor(s);
        SKRect mapDst = SKRect.Empty;
        if (map != null)
        {
            float maxW = 360, maxH = CardH + 40;   // the circuit BREAKS OUT above the card (Angelo 2026-10-19)
            float sc = Math.Min(maxW / map.Width, maxH / map.Height);
            float mw = map.Width * sc, mh = map.Height * sc;
            float my = r.Bottom - 14 - mh / 2;
            mapDst = new SKRect(r.Right - 30 - mw, my - mh / 2, r.Right - 30, my + mh / 2);
        }
        float textRight = map != null ? mapDst.Left - 30 : r.Right - 30;

        // track logo: INSIDE the circuit's infield when there's a map (Angelo 2026-10-19, like the
        // splash page) - drawn after the trace below; otherwise just left of the text's right edge.
        var tlogo = TrackLogoFor(s);
        if (tlogo != null && map == null)
        {
            var mt = GridIntro.Matte(tlogo);
            var img = mt.Feathered ?? tlogo;
            float maxW = 200, maxH = 104, sc = Math.Min(maxW / img.Width, maxH / img.Height);
            float lw = img.Width * sc, lh = img.Height * sc;
            float right = map != null ? mapDst.Left - 22 : r.Right - 30;
            var ldst = new SKRect(right - lw, r.MidY - lh / 2, right, r.MidY + lh / 2);
            // ENTRANCE: the track logo fades up once the track has started drawing
            using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * K(0.55, 0.5))) };
            c.DrawBitmap(img, ldst, lp);
            textRight = ldst.Left - 30;
        }
        float rowW = textRight - (r.Left + 30);

        string name = (s.TrackDisplayName ?? "").ToUpperInvariant();
        using (var p = P(Fonts.CormorantGaramond.Bold, 44, Txt, a * K(0.0, 0.4))) { FitSize(p, name, rowW, 13); c.DrawText(Fit(name, p, rowW), r.Left + 30, r.Top + 62, p); }

        using (var ln = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(22 * a)) })
            c.DrawRect(new SKRect(r.Left + 30, r.Top + 78, r.Left + 30 + (textRight - r.Left - 30) * K(0.2, 0.6), r.Top + 79), ln);

        // ENTRANCE: the facts fade in and the length / turns count up from zero
        const float LS = 11, VS = 26, LY = 100, VY = 131;
        float sa = a * K(0.35, 0.35), tk = K(0.45, 1.1);
        float x = r.Left + 30;
        var flag = string.IsNullOrEmpty(s.TrackCountryCode) ? null : Bmp("flags", s.TrackCountryCode.ToLowerInvariant() + ".png");
        if (flag != null)
        {
            float h = 36, w = flag.Width * (h / flag.Height);
            using var fp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * sa)) };
            c.DrawBitmap(flag, new SKRect(x, r.Top + 94, x + w, r.Top + 94 + h), fp);
            x += w + 22;
        }
        string place = string.Join(", ", new[] { e.TrackCity, e.TrackCountry }.Where(z => !string.IsNullOrWhiteSpace(z)));
        if (place.Length > 0) x = MiniStat(c, x, r, "LOCATION", place, sa, LS, VS, LY, VY) + 36;
        string len = TrackLen(e.TrackLength, f);
        float lenRight = MiniStat(c, x, r, "LENGTH", Tick(len, tk), sa, LS, VS, LY, VY);
        using (var vp = P(Fonts.Inter.Bold, VS, Txt, 1)) x = Math.Max(lenRight, x + vp.MeasureText(len)) + 36;
        MiniStat(c, x, r, "TURNS", e.TrackTurns > 0 ? Tick(e.TrackTurns.ToString(CultureInfo.InvariantCulture), tk) : "-", sa, LS, VS, LY, VY);

        if (map != null)
        {
            var dst = mapDst;
            // ENTRANCE: the circuit traces itself around with a glowing comet head
            var path = MapPathFor(s);
            if (path != null)
            {
                float sc = dst.Width / map.Width;
                using var pth = new SKPath(path);
                pth.Transform(SKMatrix.CreateScaleTranslation(sc, sc, dst.Left, dst.Top));
                var zone = new SKRect(dst.Left - 24, dst.Top - 30, dst.Right + 24, dst.Bottom + 12);
                pth.Transform(WalkZoom(pth, zone));
                using var meas = new SKPathMeasure(pth, true);
                float L = meas.Length;
                float pk = K(0.1, 1.5);
                using var seg = new SKPath();
                c.Save(); c.ClipRoundRect(new SKRoundRect(zone, 18, 18), antialias: true);
                if (L > 0 && pk > 0)
                {
                    meas.GetSegment(0, L * pk, seg, true);
                    using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                               Color = Gold.WithAlpha((byte)(110 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5) })
                        c.DrawPath(seg, glow);
                    using (var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                               Color = SKColors.White.WithAlpha((byte)(240 * a)) })
                        c.DrawPath(seg, line);
                    if (pk < 1 && meas.GetPositionAndTangent(L * pk, out var head, out _))
                        using (var halo = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(head, 16,
                                   new[] { new SKColor(255, 244, 214, (byte)(255 * a)), Gold.WithAlpha((byte)(150 * a)), Gold.WithAlpha(0) }, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp) })
                            c.DrawCircle(head, 16, halo);
                }
                c.Restore();
                DrawLapWalk(c, meas, L, a, 0.8f, 26);
            }
            else
            {
                using var mp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(235 * a * K(0.1, 0.6))),
                                             ImageFilter = SKImageFilter.CreateDropShadow(0, 0, 5, 5, Gold.WithAlpha((byte)(140 * a))) };
                c.DrawBitmap(map, dst, mp);
            }

            // the track logo sits in the infield: centred on the circuit's centroid, ~40 % of the
            // map's width, and zooms gently down into place once the trace is well under way.
            if (tlogo != null)
            {
                var mt = GridIntro.Matte(tlogo);
                var img = mt.Feathered ?? tlogo;
                SKPoint ctr = new(dst.MidX, dst.MidY);
                if (path != null)
                {
                    float sc = dst.Width / map.Width;
                    using var pth = new SKPath(path);
                    pth.Transform(SKMatrix.CreateScaleTranslation(sc, sc, dst.Left, dst.Top));
                    using var meas = new SKPathMeasure(pth, true);
                    float L = meas.Length; double sx = 0, sy = 0; int n = 0;
                    for (int i = 0; i < 200 && L > 0; i++)
                        if (meas.GetPosition(L * i / 200f, out var pp)) { sx += pp.X; sy += pp.Y; n++; }
                    if (n > 0) ctr = new SKPoint((float)(sx / n), (float)(sy / n));
                }
                float lk = K(0.55, 0.6);
                float lwMax = dst.Width * 0.56f, lhMax = dst.Height * 0.52f;
                float ls = Math.Min(lwMax / img.Width, lhMax / img.Height) * (1.25f - 0.25f * lk);
                float lw = img.Width * ls, lh = img.Height * ls;
                var ldst = new SKRect(ctr.X - lw / 2, ctr.Y - lh / 2, ctr.X + lw / 2, ctr.Y + lh / 2);
                using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * lk * (1 - walkZoomAmt))) };
                c.DrawBitmap(img, ldst, lp);
            }
        }
    }

    // the racing line for the Track Facts trace (same JSON the intro uses), in the map PNG's pixels
    readonly Dictionary<string, SKPath?> mapPaths = new(StringComparer.OrdinalIgnoreCase);
    SKPath? MapPathFor(RaceSnapshot s)
    {
        var b = TrackMapCache.Resolve(s.TrackConfigName, s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName);
        if (b == null) return null;
        if (mapPaths.TryGetValue(b, out var hit)) return hit;
        SKPath? p = null;
        try
        {
            var json = Assets.Resolve("maps", b + ".json");
            if (System.IO.File.Exists(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(json));
                p = new SKPath(); bool first = true;
                foreach (var pt in doc.RootElement.GetProperty("points").EnumerateArray())
                {
                    float px = pt[0].GetSingle(), py = pt[1].GetSingle();
                    if (first) { p.MoveTo(px, py); first = false; } else p.LineTo(px, py);
                }
                p.Close();
            }
        }
        catch { p = null; }
        return mapPaths[b] = p;
    }

    // Driver-to-Watch backdrop per car: assets\logos\sponsors\car_<car name>.png,
    // e.g. "BMW M Hybrid V8" -> car_bmw_m_hybrid_v8.png. No file -> plain card.
    SKBitmap? CarBackdrop(string? carName, Driver? player = null)
    {
        // 2026-10-25 (Angelo: "the driver to watch is still that BMW stock photo"): our own picture of the car - the front
        // photo from the sim (photo booth) or iRacing's render with the livery - instead of a press photo
        var own = LiveryArt.GetFront(player) ?? LiveryArt.Get(player);
        backdropCutout = own != null;
        if (own != null)
        {
            if (!ReferenceEquals(own, ownImg)) { ownBmp?.Dispose(); ownBmp = SKBitmap.FromImage(own); ownImg = own; }
            return ownBmp;
        }
        if (string.IsNullOrWhiteSpace(carName)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (char ch in carName.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        string slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        return Bmp("logos", "sponsors", "car_" + slug + ".png");
    }

    SKImage? ownImg; SKBitmap? ownBmp; bool backdropCutout;

    void DrawDriver(SKCanvas c, SKRect r, RaceSnapshot s, float a)
    {
        Title(c, r, "DRIVER TO WATCH", PlayerBlue, a);
        var d = s.Player;
        if (d == null) return;
        // bigger name (fills the left block), bigger logo and bigger stats (Angelo 2026-10-19)
        using (var p = P(Fonts.CormorantGaramond.Bold, 50, Txt, a * K(0.25, 0.4)))
        { string nm = NameFormat.Broadcast(d); FitSize(p, nm, 284, 18); c.DrawText(Fit(nm, p, 284), r.Left + 30, r.Top + 98, p); }
        using (var p = P(Fonts.Inter.SemiBold, 15, Txt2, a)) c.DrawText(Fit((d.CarName ?? "").ToUpperInvariant(), p, 284), r.Left + 30, r.Top + 126, p);
        Divider(c, r.Left + 330, r, a);
        float x = r.Left + 352;
        var slug = ManufacturerBrand.Resolve(d.CarBrand ?? "");
        var logo = slug != null ? Bmp("logos", "manufacturers", slug + ".png") : null;
        if (logo != null)
        {
            float maxW = 120, maxH = 96, sc = Math.Min(maxW / logo.Width, maxH / logo.Height);
            float w = logo.Width * sc, h = logo.Height * sc;
            using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)),
                                         ImageFilter = SKImageFilter.CreateDropShadow(0, 2, 6, 6, new SKColor(0, 0, 0, (byte)(170 * a))) };
            c.DrawBitmap(logo, new SKRect(x + (maxW - w) / 2, r.MidY - h / 2, x + (maxW - w) / 2 + w, r.MidY + h / 2), lp);
            x += maxW + 26;
        }
        const float LS = 13, VS = 40, LY = 46, VY = 96, SY = 124, SS = 14;
        // ENTRANCE (after the aperture + flash in Draw): the stats drop in one by one and the
        // iRating spins up like an odometer
        float S(int i) => a * K(0.55 + i * 0.12, 0.3);
        Stat(c, x, r, "CAR", "#" + d.CarNumber, S(0), null, 110, LS, VS, LY, VY, SY, SS); x += 122;
        Stat(c, x, r, "STARTS", d.PositionInClass > 0 ? $"P{d.PositionInClass}" : "-", S(1), d.ClassName?.ToUpperInvariant() + " CLASS", 110, LS, VS, LY, VY, SY, SS); x += 128;
        string ir = d.IRating > 0 ? d.IRating.ToString("N0", CultureInfo.InvariantCulture) : "-";
        Stat(c, x, r, "IRATING", Tick(ir, K(0.75, 1.4)), S(2), null, 130, LS, VS, LY, VY, SY, SS); x += 148;
        Stat(c, x, r, "LICENCE", string.IsNullOrEmpty(d.LicenceString) ? "-" : d.LicenceString, S(3), null, 1036 - (x - r.Left), LS, VS, LY, VY, SY, SS);
    }

    void DrawPartnerStorm(SKCanvas c, SKRect r, float a)
    {
        // ENTRANCE: LIGHTNING. A white flash and a crackling bolt split the card open, the radar
        // glows up in the afterglow (DrawFrame), the tagline appears, the logo sweeps in last.
        float ta = a * K(0.3, 0.4);
        Headline(c, "Recorded weather history", r.Left + 30, r.MidY - 6, 284, 40, ta);
        using (var p = P(Fonts.CormorantGaramond.Bold, 40, StormLight, ta))
        { FitSize(p, "Recorded weather history", 284, 12); c.DrawText("for any US address", r.Left + 30, r.MidY + 34, p); }
        Divider(c, r.Left + 330, r, a);
        float w = WordmarkWidth(46);
        float lk = K(0.5, 0.55);
        if (lk > 0) StormWordmark(c, r.Right - 44 - w + (1 - lk) * 160, r.MidY + 17, 46, a * lk);   // sits near the right edge

        // the strike itself
        float boltA = 1 - Lin(0.05, 0.45);
        if (boltA > 0)
        {
            c.Save();
            using (var rr = new SKRoundRect(r, 12, 12)) c.ClipRoundRect(rr, antialias: true);
            float flash = 1 - Lin(0.0, 0.18);
            if (flash > 0) using (var fp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(230 * flash * a)) }) c.DrawRect(r, fp);
            // jagged bolt top -> bottom, flickering (re-seeded every couple of frames)
            var rnd = new Random(5 + (int)(cardLocal * 24) % 3);
            using var bolt = new SKPath();
            float bx = r.Left + r.Width * 0.58f, by = r.Top - 4;
            bolt.MoveTo(bx, by);
            while (by < r.Bottom + 4)
            {
                by += 10 + (float)rnd.NextDouble() * 16;
                bx += ((float)rnd.NextDouble() - 0.5f) * 46;
                bolt.LineTo(bx, by);
                if (rnd.NextDouble() < 0.18)   // a short fork
                {
                    using var fork = new SKPath(); fork.MoveTo(bx, by);
                    fork.LineTo(bx + ((float)rnd.NextDouble() - 0.5f) * 90, by + 18 + (float)rnd.NextDouble() * 22);
                    using var fpnt = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(220, 236, 255, (byte)(200 * boltA * a)) };
                    c.DrawPath(fork, fpnt);
                }
            }
            using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 16, StrokeJoin = SKStrokeJoin.Round,
                       Color = StormLight.WithAlpha((byte)(160 * boltA * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10) })
                c.DrawPath(bolt, glow);
            using (var core = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, StrokeJoin = SKStrokeJoin.Round,
                       Color = new SKColor(240, 248, 255, (byte)(255 * boltA * a)) })
                c.DrawPath(bolt, core);
            c.Restore();
        }
    }

    internal static float WordmarkWidth(float size)
    {
        using var p1 = P(Fonts.Inter.Bold, size, Txt, 1);
        using var p3 = P(Fonts.Inter.SemiBold, size * 0.62f, Txt3, 1);
        return size * 0.52f * 2 + size * 0.3f + p1.MeasureText("StormExhibit") + 2 + p3.MeasureText(".com");
    }

    // StormExhibit wordmark: radar-sweep icon + "Storm" (white) "Exhibit" (brand blue) ".com"
    internal static void StormWordmark(SKCanvas c, float x, float baseline, float size, float a)
    {
        if (!SponsorPack.IsOn(Partner.Storm)) return;     // partner switched off in sponsors.json
        float r = size * 0.52f, cx = x + r, cy = baseline - size * 0.36f;
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, size * 0.07f), Color = StormLight.WithAlpha((byte)(255 * a)) })
        {
            c.DrawCircle(cx, cy, r, ring);
            ring.Color = StormLight.WithAlpha((byte)(150 * a)); c.DrawCircle(cx, cy, r * 0.58f, ring);
        }
        using (var sweep = new SKPaint { IsAntialias = true, Shader = SKShader.CreateSweepGradient(new SKPoint(cx, cy),
                   new[] { StormLight.WithAlpha(0), StormLight.WithAlpha((byte)(200 * a)) }, new[] { 0.75f, 1f }) })
            c.DrawCircle(cx, cy, r, sweep);
        using (var dot = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * a)) }) c.DrawCircle(cx + r * 0.42f, cy - r * 0.38f, size * 0.07f, dot);
        float tx = x + r * 2 + size * 0.3f;
        using var p1 = P(Fonts.Inter.Bold, size, Txt, a);
        c.DrawText("Storm", tx, baseline, p1); tx += p1.MeasureText("Storm");
        using var p2 = P(Fonts.Inter.Bold, size, StormLight, a);
        c.DrawText("Exhibit", tx, baseline, p2); tx += p2.MeasureText("Exhibit");
        using var p3 = P(Fonts.Inter.SemiBold, size * 0.62f, Txt3, a);
        c.DrawText(".com", tx + 2, baseline, p3);
    }

    // ---------------------------------------------------------------- helpers
    static SKPaint P(SKTypeface tf, float size, SKColor col, float a) =>
        new() { IsAntialias = true, Typeface = tf, TextSize = size, Color = col.WithAlpha((byte)(col.Alpha * a)) };

    /// <summary>Shrink the paint's text size until <paramref name="text"/> fits (not below <paramref name="min"/>).</summary>
    static void FitSize(SKPaint p, string text, float maxW, float min)
    {
        float w = p.MeasureText(text);
        if (w > maxW && w > 0) p.TextSize = Math.Max(min, p.TextSize * maxW / w * 0.985f);   // a hair under, so Fit() never adds "..."
    }

    static float Ease(float k) { k = Math.Clamp(k, 0, 1); return 1 - (1 - k) * (1 - k) * (1 - k); }

    static bool IsImperial(RaceSnapshot s) => UnitPrefs.Fahrenheit(s);
    static string Temp(float c, bool f) => f ? $"{c * 9 / 5 + 32:0}°F" : $"{c:0}°C";
    static string Sky(int skies) => skies switch { 0 => "CLEAR", 1 => "PARTLY CLOUDY", 2 => "MOSTLY CLOUDY", 3 => "OVERCAST", _ => "-" };
    static string Compass(float rad)
    {
        string[] pts = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        double deg = (rad * 180 / Math.PI % 360 + 360) % 360;
        return pts[(int)Math.Round(deg / 45) % 8];
    }
    static string Duration(double secs)
    {
        if (secs <= 0 || secs > 86400 * 2) return "-";
        var ts = TimeSpan.FromSeconds(secs);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}H {ts.Minutes:00}M" : $"{ts.Minutes} MIN";
    }
    static string Lap(float s) { if (s <= 0) return "NO TIME"; int m = (int)(s / 60); return m > 0 ? $"{m}:{s - m * 60:00.000}" : $"{s:0.000}"; }
    static string TrackLen(string raw, bool imperial)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "-";
        var parts = raw.Trim().Split(' ');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return raw.ToUpperInvariant();
        bool km = parts.Length < 2 || parts[1].StartsWith("km", StringComparison.OrdinalIgnoreCase);
        double kmV = km ? v : v * 1.609344;
        return imperial ? $"{kmV / 1.609344:0.00} MI" : $"{kmV:0.00} KM";
    }
    static int Sof(List<int> irs)
    {
        // iRacing strength-of-field: br * ln(N / sum(exp(-ir / br))), br = 1600 / ln 2
        double br = 1600 / Math.Log(2), sum = 0;
        foreach (var ir in irs) sum += Math.Exp(-ir / br);
        return (int)Math.Round(br * Math.Log(irs.Count / sum));
    }
    static string Fit(string text, SKPaint p, float maxW)
    {
        if (string.IsNullOrEmpty(text) || p.MeasureText(text) <= maxW) return text ?? "";
        for (int n = text.Length - 1; n > 1; n--) { var t = text[..n].TrimEnd() + "…"; if (p.MeasureText(t) <= maxW) return t; }
        return text[..1];
    }

    SKBitmap? Bmp(params string[] path)
    {
        string k = string.Join("/", path);
        if (bmps.TryGetValue(k, out var b)) return b;
        try { var f = Assets.Resolve(path); if (System.IO.File.Exists(f)) { using var st = System.IO.File.OpenRead(f); b = Assets.Decode(st); } }
        catch { b = null; }
        return bmps[k] = b;
    }

    SKBitmap? MapFor(RaceSnapshot s)
    {
        var b = TrackMapCache.Resolve(s.TrackConfigName, s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName);
        return b == null ? null : Bmp("maps", b + ".png");
    }
    SKBitmap? TrackLogoFor(RaceSnapshot s) => TrackAsset(s, "logos", "tracks");
    /// <summary>The track's logo (the same one the track card uses) - for the TV session review (2026-10-27).</summary>
    internal static SKBitmap? TrackLogoOf(RaceSnapshot s) { lock (logoFinder) return logoFinder.TrackLogoFor(s); }

    // 2026-10-25 (Angelo: "the iPad background colour to be whatever background colour the track logo is"): the track
    // logo's plate colour - the same one the track card is filled with - for the iPad page
    static readonly PreRaceCards logoFinder = new();
    static string logoColKey = ""; static SKColor? logoCol;
    public static SKColor? TrackLogoColour(RaceSnapshot s)
    {
        string k = (s.TrackDisplayName ?? "") + "|" + s.TrackRawName;
        if (k == logoColKey) return logoCol;
        logoColKey = k;
        try { logoCol = GridIntro.Matte(logoFinder.TrackLogoFor(s)).Matte; } catch { logoCol = null; }
        return logoCol;
    }

    SKBitmap? TrackAsset(RaceSnapshot s, params string[] folder)
    {
        try
        {
            var dir = Assets.Resolve(folder);
            if (!System.IO.Directory.Exists(dir)) return null;
            foreach (var raw in new[] { s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string n = Norm(raw.Split(" - ")[0]);
                if (n.Length < 4) continue;
                foreach (var f in System.IO.Directory.GetFiles(dir, "*.png"))
                {
                    string key = Norm(System.IO.Path.GetFileNameWithoutExtension(f));
                    if (n == key || n.Contains(key) || key.Contains(n))
                        return Bmp(folder.Append(System.IO.Path.GetFileName(f)).ToArray());
                }
            }
        }
        catch { }
        return null;
    }
    static string Norm(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (char ch in d)
            if (char.IsLetterOrDigit(ch) && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    public void Dispose() { foreach (var b in bmps.Values) b?.Dispose(); bmps.Clear(); }
}
