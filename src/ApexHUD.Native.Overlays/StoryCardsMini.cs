using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

// 2026-10-27 (#339, PREVIEW - setting miniCardsInBattle, OFF by default): while Angelo is in a battle and driving live the
// full story cards are held (#338). A mini card is a short plate with the card's single headline fact, in the card's own
// colours, type and partner mark, in the right third under the battle map - never in the centre third, never over the
// tower lane, the session bar / ticker, the battle card, the battle map or the radio card.

public static partial class StoryDirector
{
    /// <summary>control.json "miniCardsInBattle" (#339): mini story cards during a battle while the full ones are held
    /// (#338). OFF by default - a preview until Angelo has seen it.</summary>
    public static bool MiniCardsInBattle;
    public const double MiniInMs = 400, MiniHoldMs = 6000, MiniOutMs = 400;
    /// <summary>At most one mini card every 45 s, and the first one 8 s into the battle (the battle card lands first).</summary>
    public const double MiniGapMs = 45000, MiniFirstMs = 8000;

    /// <summary>The mini card on screen (None when nothing is up).</summary>
    public static StoryKind Mini { get; private set; }
    public static double MiniStartedMs { get; private set; } = -1;
    public static double MiniExitAtMs { get; private set; } = -1;
    public static string MiniWeather { get; private set; } = "";
    public static RaceDiary.Contact? MiniContact { get; private set; }
    public static bool MiniVisible => Mini != StoryKind.None;
    static double lastMiniStartMs = double.NegativeInfinity, hushSinceMs = double.NegativeInfinity;

    static void MiniReset()
    {
        Mini = StoryKind.None; lastMiniStartMs = double.NegativeInfinity; hushSinceMs = double.NegativeInfinity;
    }

    /// <summary>Preview / test hook: a mini card now, whatever the rules.</summary>
    public static void ForceMini(StoryKind k, double nowMs, string weather = "")
    {
        Mini = k; MiniStartedMs = nowMs; MiniExitAtMs = nowMs + MiniInMs + MiniHoldMs; MiniWeather = weather;
        MiniContact = k == StoryKind.CarContact && RaceDiary.Contacts.Count > 0 ? RaceDiary.Contacts[^1] : null;
    }

    /// <summary>Something else wants the mini card's spot (the radio card, a custom card) or a moment it must not cover.</summary>
    static bool MiniBlocked(RaceSnapshot snap)
    {
        if (Blocked(snap) || PresentationState.OnboardSoon || CustomCards.Visible) return true;
        if (VpLayout.RadioVisible)
        {
            var r = StoryCards.MiniRect(); r.Inflate(8, 8);
            if (r.IntersectsWith(VpLayout.Radio)) return true;
        }
        return false;
    }

    static void MiniTick(RaceSnapshot snap, double nowMs, bool hush)
    {
        if (Mini != StoryKind.None)
        {
            // the battle is over (the full cards take over again), a full card is up, or the spot is needed: out now
            if (nowMs < MiniExitAtMs && (!hush || !MiniCardsInBattle || Current != StoryKind.None || MiniBlocked(snap)))
            {
                // cut off before anyone could read it: back to the front of the line with the same content
                if (nowMs - MiniStartedMs < 2000)
                    queue.Insert(0, new Queued(Mini, nowMs, MiniWeather, MiniContact, 9));
                MiniExitAtMs = nowMs;
            }
            if (nowMs >= MiniExitAtMs + MiniOutMs) Mini = StoryKind.None;
            return;
        }
        if (!MiniCardsInBattle || !hush) return;
        if (nowMs - hushSinceMs < MiniFirstMs || nowMs - lastMiniStartMs < MiniGapMs) return;
        if (snap.Player == null || MiniBlocked(snap) || PresentationState.ShotSettling) return;   // #510: not on a camera cut
        // the same order as the full cards: a new fastest lap first (it takes over), then highest priority, then oldest.
        // A mini card tells the story, so it leaves the line (it does not play again in full after the battle).
        StoryKind k; string weather = ""; RaceDiary.Contact? ct = null;
        if (fastestNow)
        {
            fastestNow = false;
            for (int i = queue.Count - 1; i >= 0; i--) if (queue[i].Kind == StoryKind.FastestLaps) queue.RemoveAt(i);
            k = StoryKind.FastestLaps;
        }
        else
        {
            int best = -1;
            for (int i = 0; i < queue.Count; i++)
                if (best < 0 || queue[i].Prio > queue[best].Prio || (queue[i].Prio == queue[best].Prio && queue[i].At < queue[best].At)) best = i;
            if (best < 0) return;
            var q = queue[best];
            queue.RemoveAt(best);
            k = q.Kind; weather = q.Weather; ct = q.Contact;
        }
        Mini = k; MiniStartedMs = nowMs; MiniExitAtMs = nowMs + MiniInMs + MiniHoldMs; MiniWeather = weather; MiniContact = ct;
        lastMiniStartMs = nowMs;
        OverlayLog.Write($"story: mini card {k} in the battle ({queue.Count} more waiting)");
    }
}

public sealed partial class StoryCards
{
    public const float MiniW = 560f, MiniH = 110f;
    static readonly SKColor MiniLabel = new(0xC6, 0xCB, 0xD3), MiniSub = new(0xD3, 0xD9, 0xE3);   // #340 bright greys

    /// <summary>Where the mini card sits (design units): right-aligned with the battle map (x 1336..1896), just under it -
    /// or under the session bar / ticker if they reach lower. Clear of the centre third (x &lt;= 1280), the tower lane,
    /// the battle card (its top is ~730 with its tabs) and, the director makes sure, the radio card.</summary>
    public static SKRect MiniRect(float w = 1920f)
    {
        float mapBottom = VpLayout.BattleMap.Height > 1 ? VpLayout.BattleMap.Bottom : 172f + BattleMap.H;
        float top = Math.Max(mapBottom, VpLayout.TopStackBottom) + 14f, right = w - 24f;
        return new SKRect(right - MiniW, top, right, top + MiniH);
    }

    double miniHotFor = double.NaN; string? miniHotName; int miniHotCount;

    struct MiniFact { public string Label, Value, Suffix; public SKColor Col; }
    readonly MiniFact[] facts = new MiniFact[3];
    int factCount;
    void Fact(string label, string value, SKColor col, string suffix = "")
    {
        if (factCount >= facts.Length) return;
        facts[factCount++] = new MiniFact { Label = label, Value = value, Suffix = suffix, Col = col };
    }

    /// <summary>The mini card for the battle (#339). Drawn only while <see cref="StoryDirector.Mini"/> is up and no full card is.</summary>
    public void DrawMini(SKCanvas c, float w, float h, RaceSnapshot snap, double nowMs)
    {
        var kind = StoryDirector.Mini;
        if (kind == StoryKind.None || snap.Player == null || StoryDirector.Visible) return;
        double t = (nowMs - StoryDirector.MiniStartedMs) / 1000.0;
        double tx = (nowMs - StoryDirector.MiniExitAtMs) / 1000.0;
        float outK = tx > 0 ? Ease((float)(tx / (StoryDirector.MiniOutMs / 1000.0))) : 0;
        if (outK >= 0.999f || t < 0) return;
        var r = MiniRect(w);
        var cls = ClassOf(snap, snap.Player);
        bool dq = kind == StoryKind.CarContact && StoryDirector.MiniContact?.Dq == true;
        var col = kind switch
        {
            StoryKind.WeatherWatch => Storm, StoryKind.DriverOfDay => Gold, StoryKind.FastestLaps or StoryKind.Limits => GoGreen,
            StoryKind.YourRace => Hud, StoryKind.PitStops => MYellow, StoryKind.CarContact => dq ? TpRed : TpBlue, StoryKind.Cleanest => TpBlue,
            StoryKind.MoveOfRace => Gold, StoryKind.RaceSoFar => Red,
            _ => ParseHex(cls?.Color ?? snap.Player.ClassColor, Gold),
        };
        float inK = K(t, 0, StoryDirector.MiniInMs / 1000.0);
        float bandX = r.Left - 30 + (r.Width + 60) * outK;
        int sv = c.Save();
        // in: wipes open from the right edge as it slides in; out: the card's colour bar sweeps it away (as the full cards)
        c.ClipRect(new SKRect(Math.Max(bandX, r.Right - (r.Width + 40) * inK), r.Top - 14, w + 10, r.Bottom + 16));
        c.Translate((1 - inK) * 70, 0);
        float a = 1;
        var inner = MiniBg(c, kind, r, cls, col, t, a);
        MiniContent(c, kind, inner, r, snap, cls, col, dq, t, a);
        c.RestoreToCount(sv);
        ImpactFlash(c, r, t - StoryDirector.MiniInMs / 1000.0, a * (1 - outK));
        if (outK > 0 && outK < 1)
        {
            using (var bp = new SKPaint { Color = col }) c.DrawRect(new SKRect(bandX, r.Top - 6, bandX + 16, r.Bottom + 6), bp);
            using (var hl = new SKPaint { Color = SKColors.White.WithAlpha(160) }) c.DrawRect(new SKRect(bandX + 13, r.Top - 6, bandX + 16, r.Bottom + 6), hl);
        }
    }

    /// <summary>The card's own background, small: draws it, clips to it, returns the content rect.</summary>
    SKRect MiniBg(SKCanvas c, StoryKind kind, SKRect r, DriverClass? cls, SKColor col, double t, float a)
    {
        switch (kind)
        {
            case StoryKind.WeatherWatch:
            {
                var rr = new SKRoundRect(r, 14, 14);
                using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(130 * a)), ImageFilter = SKImageFilter.CreateBlur(12, 12) }) c.DrawRoundRect(rr, sh);
                using (var bg = SKShader.CreateRadialGradient(new SKPoint(r.Right - 90, r.MidY), r.Width * 0.8f, new[] { new SKColor(6, 26, 44, (byte)(250 * a)), new SKColor(2, 10, 18, (byte)(252 * a)) }, null, SKShaderTileMode.Clamp))
                using (var bp = new SKPaint { Shader = bg }) c.DrawRoundRect(rr, bp);
                c.ClipRoundRect(rr, antialias: true);
                using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = Storm.WithAlpha((byte)(60 * a)) })
                    for (float rad = 60; rad < 600; rad += 60) c.DrawCircle(r.Right - 90, r.Bottom + 10, rad, ring);
                using (var scl = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(55 * a)) })
                    for (float yy = r.Top; yy < r.Bottom; yy += 3) c.DrawRect(new SKRect(r.Left, yy, r.Right, yy + 1), scl);
                using (var frame = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Storm.WithAlpha((byte)(200 * a)) }) c.DrawRoundRect(rr, frame);
                return new SKRect(r.Left + 26, r.Top, r.Right - 20, r.Bottom);
            }
            case StoryKind.DriverOfDay:
            {
                using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(160 * a)), ImageFilter = SKImageFilter.CreateBlur(14, 14) }) c.DrawRect(r, sh);
                using (var bg = new SKPaint { Color = new SKColor(4, 4, 5, (byte)(250 * a)) }) c.DrawRect(r, bg);
                c.ClipRect(r);
                using (var gf = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = Gold.WithAlpha((byte)(230 * a)) })
                {
                    c.DrawRect(new SKRect(r.Left + 7, r.Top + 7, r.Right - 7, r.Bottom - 7), gf);
                    gf.StrokeWidth = 1; gf.Color = Gold.WithAlpha((byte)(120 * a));
                    c.DrawRect(new SKRect(r.Left + 12, r.Top + 12, r.Right - 12, r.Bottom - 12), gf);
                }
                return new SKRect(r.Left + 26, r.Top + 4, r.Right - 22, r.Bottom);
            }
            case StoryKind.YourRace:
            {
                using var shape = Chamfer(r, 16);
                using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(120 * a)), ImageFilter = SKImageFilter.CreateBlur(12, 12) }) c.DrawPath(shape, sh);
                c.ClipPath(shape, antialias: true);
                using (var bg = new SKPaint { Color = new SKColor(4, 7, 11, (byte)(250 * a)) }) c.DrawRect(r, bg);
                using (var sl = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(40 * a)) })
                    for (float yy = r.Top; yy < r.Bottom; yy += 3) c.DrawRect(new SKRect(r.Left, yy, r.Right, yy + 1), sl);
                using (var bp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Hud.WithAlpha((byte)(230 * a)) })
                {
                    c.DrawLine(r.Left, r.Top + 16, r.Left + 16, r.Top, bp); c.DrawLine(r.Left + 16, r.Top + 1.5f, r.Left + 56, r.Top + 1.5f, bp);
                    c.DrawLine(r.Right - 16, r.Bottom, r.Right, r.Bottom - 16, bp); c.DrawLine(r.Right - 56, r.Bottom - 1.5f, r.Right - 16, r.Bottom - 1.5f, bp);
                }
                return new SKRect(r.Left + 26, r.Top, r.Right - 22, r.Bottom);
            }
            default:
            {
                SKColor c0, c1, edge = col;
                switch (kind)
                {
                    case StoryKind.MoveOfRace: c0 = new SKColor(14, 12, 10); c1 = new SKColor(22, 19, 16); break;
                    case StoryKind.FastestLaps or StoryKind.Limits: c0 = new SKColor(6, 16, 12); c1 = new SKColor(10, 26, 20); break;
                    case StoryKind.CarContact or StoryKind.Cleanest: c0 = new SKColor(16, 17, 28); c1 = new SKColor(28, 30, 46); break;
                    case StoryKind.PitStops: c0 = new SKColor(6, 10, 22); c1 = MBlueDeep; break;      // Michelin blue, yellow edge
                    case StoryKind.RaceSoFar: c0 = new SKColor(9, 12, 18); c1 = new SKColor(15, 19, 28); edge = ParseHex(cls?.Color, Gold); break;
                    default: c0 = new SKColor(10, 13, 20); c1 = new SKColor(14, 18, 27); break;
                }
                using var shape = SlantCol(r);
                GlassCol(c, shape, r, c0, c1, edge, a);
                c.ClipPath(shape, antialias: true);
                return new SKRect(r.Left + 52, r.Top, r.Right - 20, r.Bottom);
            }
        }
    }

    void MiniContent(SKCanvas c, StoryKind kind, SKRect inner, SKRect r, RaceSnapshot s, DriverClass? cls, SKColor col, bool dq, double t, float a)
    {
        float L = inner.Left, R = inner.Right;
        float eyeBase = r.Top + 34, labelBase = r.Top + 58, valueBase = r.Top + 95;
        var p = s.Player!;
        factCount = 0;
        string eyebrow;
        switch (kind)
        {
            case StoryKind.RaceSoFar:
            {
                eyebrow = "RACE SO FAR";
                var fl = cls != null ? RaceDiary.Fastest(cls.Name) : null;
                Fact("LEAD CHANGES", (cls != null ? RaceDiary.LeadChanges(cls.Name) : 0).ToString(CultureInfo.InvariantCulture), Txt);
                Fact("PIT STOPS", (cls != null ? RaceDiary.TotalPitStops(cls.Drivers) : 0).ToString(CultureInfo.InvariantCulture), Txt);
                if (fl is { } f) Fact("FASTEST", LapTimeFormat.Lap(f.Time), Purple, LastName(f.Name));
                break;
            }
            case StoryKind.YourRace:
            {
                eyebrow = "YOUR RACE";
                int start = RaceDiary.GridPos(p.CarIdx), now = p.PositionInClass, gained = start > 0 ? start - now : 0;
                Fact("STARTED", start > 0 ? "P" + start.ToString(CultureInfo.InvariantCulture) : "-", Txt);
                Fact("NOW", "P" + now.ToString(CultureInfo.InvariantCulture), now == 1 ? Gold : Txt);
                Fact("PLACES", gained > 0 ? "+" + gained.ToString(CultureInfo.InvariantCulture) : gained.ToString(CultureInfo.InvariantCulture), gained > 0 ? Gain : gained < 0 ? Loss : Txt2);
                break;
            }
            case StoryKind.MoveOfRace:
            {
                eyebrow = OutroDirector.Running ? "BIGGEST CLIMBER" : "MOVE OF THE RACE";
                if (StoryDirector.MoveOfRace(s) is { } d)
                {
                    int g = Math.Max(1, RaceDiary.GridPos(d.CarIdx)), now = Math.Max(1, d.PositionInClass);
                    var dc = ClassOf(s, d);
                    Fact("#" + d.CarNumber + (dc != null ? "  ·  " + ClassLogo.DisplayName(dc.Name) : ""), NameFormat.Broadcast(d).ToUpperInvariant(), Txt);
                    Fact(g - now == 1 ? "PLACE GAINED" : "PLACES GAINED", "+" + (g - now).ToString(CultureInfo.InvariantCulture), Gain);
                    Fact("FROM P" + g.ToString(CultureInfo.InvariantCulture), "P" + now.ToString(CultureInfo.InvariantCulture), Txt);
                }
                break;
            }
            case StoryKind.FastestLaps:
            {
                eyebrow = "FASTEST LAPS";
                // your class first, then the quickest of the others (the plate keeps what fits)
                if (cls != null && RaceDiary.Fastest(cls.Name) is { } pf) Fact(ClassLogo.DisplayName(cls.Name), LapTimeFormat.Lap(pf.Time), GoGreen, LastName(pf.Name));
                DriverClass? other = null; float ob = float.MaxValue;
                foreach (var cl in s.ClassLeaderboard)
                    if (cl != cls && RaceDiary.Fastest(cl.Name) is { } of && of.Time < ob) { ob = of.Time; other = cl; }
                if (other != null && RaceDiary.Fastest(other.Name) is { } oo) Fact(ClassLogo.DisplayName(other.Name), LapTimeFormat.Lap(oo.Time), GoGreen, LastName(oo.Name));
                break;
            }
            case StoryKind.PitStops:
            {
                eyebrow = "PIT STOPS";
                RaceDiary.PitStop? q = null;
                foreach (var x in RaceDiary.Stops) if (q == null || x.Seconds < q.Seconds) q = x;
                if (q != null) Fact("QUICKEST STOP", q.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s", MYellow, LastName(q.Name));
                Fact("STOPS", RaceDiary.Stops.Count.ToString(CultureInfo.InvariantCulture), Txt);
                Fact("YOURS", RaceDiary.PitStops(p.CarIdx).ToString(CultureInfo.InvariantCulture), Txt);
                break;
            }
            case StoryKind.Halfway:
            {
                eyebrow = "HALFWAY";
                Fact("LEAD CHANGES", RaceDiary.AllLeadChanges.ToString(CultureInfo.InvariantCulture), Txt);
                Fact("PIT STOPS", RaceDiary.Stops.Count.ToString(CultureInfo.InvariantCulture), Txt);
                Fact("YOU", "P" + p.PositionInClass.ToString(CultureInfo.InvariantCulture), p.PositionInClass == 1 ? Gold : Txt,
                     p.PositionInClass > 1 && p.GapToLeaderSeconds is double gl && gl > 0 ? "+" + gl.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "");
                break;
            }
            case StoryKind.WeatherWatch:
            {
                eyebrow = "WEATHER WATCH";
                bool f = UnitPrefs.Fahrenheit(s);
                string head = StoryDirector.MiniWeather;
                var (_, big, _, wcol) = WeatherStory(s, head);
                Fact("TRACK", Temp(s.TrackTempC, f), SKColors.White);
                string lab = head switch
                {
                    "HEATING" => "HEATING UP", "COOLING" => "COOLING DOWN", "RAIN" => "RAIN", "WET" or "DRYING" => "TRACK SURFACE",
                    _ => s.IsRaining ? "RAIN" : "CHANCE OF RAIN",
                };
                Fact(lab, big, wcol, head is "HEATING" or "COOLING" ? "SINCE THE START" : "");
                Fact("AIR", Temp(s.AirTempC, f), Txt);
                break;
            }
            case StoryKind.DriverOfDay:
            {
                eyebrow = "Driver of the Day";
                if (StoryDirector.DriverOfDay(s) is { } d)
                {
                    int g = RaceDiary.Gained(d);
                    Fact("#" + d.CarNumber + "  ·  " + d.CarName.ToUpperInvariant(), d.Name, SKColors.White);
                    Fact("FINISHED", "P" + d.PositionInClass.ToString(CultureInfo.InvariantCulture), d.PositionInClass == 1 ? Gold : Txt);
                    if (g > 0) Fact("PLACES", "+" + g.ToString(CultureInfo.InvariantCulture), Gain);
                }
                break;
            }
            case StoryKind.CarContact:
            {
                var ct = StoryDirector.MiniContact;
                eyebrow = dq ? "DISQUALIFIED" : "CAR CONTACT";
                if (ct == null) { Fact("SO FAR", "A CLEAN RACE", Txt); break; }
                if (dq)
                {
                    Fact("#" + (Find(s, ct.CarIdx)?.CarNumber ?? "") + "  ·  " + ct.Class, ct.Name.ToUpperInvariant(), Txt);
                    Fact("INCIDENTS", RaceDiary.IncOf(ct.CarIdx, ct.Total).ToString(CultureInfo.InvariantCulture) + "x", TpRed);
                    Fact("OUT", "DQ", TpRed);
                }
                else
                {
                    Fact("LAP " + ct.Lap.ToString(CultureInfo.InvariantCulture), ct.Name.ToUpperInvariant(), ct.CarIdx == s.PlayerCarIdx ? SKColors.White : Txt,
                         "+" + ct.Points.ToString(CultureInfo.InvariantCulture) + "x");
                    if (ct.OtherName != null)
                        Fact("WITH", ct.OtherName.ToUpperInvariant(), ct.OtherCarIdx == s.PlayerCarIdx ? SKColors.White : Txt,
                             "+" + ct.OtherPoints.ToString(CultureInfo.InvariantCulture) + "x");
                }
                break;
            }
            case StoryKind.Limits:
            {
                eyebrow = "ON THE LIMIT";
                Fact("TIMES OFF", RaceDiary.TotalOffs.ToString(CultureInfo.InvariantCulture), SKColors.White);
                // the corner with the most trips off (same rule as the full card), worked out once per showing
                if (miniHotFor != StoryDirector.MiniStartedMs)
                {
                    miniHotFor = StoryDirector.MiniStartedMs; miniHotName = null; miniHotCount = 0;
                    var spots = RaceDiary.OffSpots;
                    var counts = new Dictionary<string, int>();
                    foreach (var sp in spots)
                        if (TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, sp.Pct, s.TrackBroadcastName) is { } nm)
                        {
                            int cnt = counts.TryGetValue(nm, out var cc) ? cc + 1 : 1;
                            counts[nm] = cnt;
                            if (cnt > miniHotCount) { miniHotCount = cnt; miniHotName = nm; }
                        }
                    if (miniHotName == null && spots.Count > 0)
                    {
                        // no corner names for this track: the busiest stretch of the lap, as the full card says it
                        const int Bins = 40;
                        var bins = new int[Bins];
                        foreach (var sp in spots) bins[Math.Clamp((int)(sp.Pct * Bins), 0, Bins - 1)]++;
                        int hot = 0; for (int i = 1; i < Bins; i++) if (bins[i] > bins[hot]) hot = i;
                        miniHotCount = bins[hot];
                        miniHotName = ((hot + 0.5f) * 100f / Bins).ToString("0", CultureInfo.InvariantCulture) + "% INTO THE LAP";
                    }
                }
                if (miniHotName != null) Fact("HOTSPOT", miniHotName.ToUpperInvariant(), GoGreen, miniHotCount.ToString(CultureInfo.InvariantCulture) + (miniHotCount == 1 ? " OFF" : " OFFS"));
                break;
            }
            case StoryKind.Cleanest:
            {
                eyebrow = OutroDirector.Running ? "CLEANEST DRIVER" : "CLEANEST SO FAR";
                Driver? best = null;
                foreach (var cl in s.ClassLeaderboard)
                    foreach (var d in cl.Drivers)
                        if (d.PositionInClass > 0 && (best == null || Inc(d) < Inc(best) || (Inc(d) == Inc(best) && d.PositionInClass < best.PositionInClass)) && d.LapsCompleted >= MaxLaps(s) * 0.8) best = d;
                if (best != null)
                {
                    var bc = ClassOf(s, best);
                    Fact("P" + best.PositionInClass.ToString(CultureInfo.InvariantCulture) + (bc != null ? "  ·  " + ClassLogo.DisplayName(bc.Name) : ""), NameFormat.Broadcast(best).ToUpperInvariant(), Txt);
                    Fact("INCIDENTS", Inc(best).ToString(CultureInfo.InvariantCulture) + "x", TpBlue);
                }
                break;
            }
            default: eyebrow = kind.ToString().ToUpperInvariant(); break;
        }

        // ---- eyebrow (the card's own type) + partner mark on the right
        float ek = a * K(t, 0.15, 0.3);
        float pl = MiniPartner(c, kind, R, eyeBase - 7, ek);
        if (kind == StoryKind.DriverOfDay)
            using (var ep = P(Fonts.CormorantGaramond.Bold, 27, Gold, ek)) c.DrawText(Fit(eyebrow, ep, pl - L - 12), L, eyeBase + 1, ep);
        else if (kind == StoryKind.PitStops)
        {
            using (var ep = P(Fonts.BarlowCondensed.Black, 24, SKColors.White, ek)) TextTracked.Draw(c, ep, eyebrow, L, eyeBase, 2.6f);
            using (var hr = new SKPaint { Color = MYellow.WithAlpha((byte)(200 * ek)) }) c.DrawRect(new SKRect(L, eyeBase + 6, L + 90, eyeBase + 8), hr);
        }
        else
            using (var ep = P(kind == StoryKind.Halfway ? Fonts.BarlowCondensed.Black : Fonts.BarlowCondensed.Bold, 21, kind == StoryKind.Halfway ? Txt : col, ek))
            {
                float sp = 3f, avail = pl - L - 12;
                if (TrackedWidth(ep, eyebrow, sp) > avail) sp = 1.4f;
                TextTracked.Draw(c, ep, eyebrow, L, eyeBase, sp);
                if (kind == StoryKind.Halfway)
                    using (var tg = P(Fonts.Inter.Bold, 13, MiniLabel, ek))
                    {
                        float x = L + TrackedWidth(ep, eyebrow, sp) + 10;
                        string tgo = ToGo(s).ToUpperInvariant();
                        if (x + tg.MeasureText(tgo) < avail + L) TextTracked.Draw(c, tg, tgo, x, eyeBase - 1, 1.4f);
                    }
            }

        // ---- the facts: label (small caps, bright grey) over the value (the card's big type), dividers between
        if (factCount == 0) return;
        var valueFace = kind == StoryKind.DriverOfDay ? Fonts.CormorantGaramond.Bold : Fonts.BarlowCondensed.ExtraBold;
        using var lp = P(Fonts.Inter.Bold, 13, MiniLabel, a);
        using var vp = P(valueFace, 38, Txt, a);
        using var sp2 = P(Fonts.BarlowCondensed.Bold, 21, MiniSub, a);
        const float Gap = 26, MinSize = 30;
        int n = factCount;
        float total;
        while (true)
        {
            total = 0;
            for (int i = 0; i < n; i++) total += FactWidth(facts[i], lp, vp, sp2) + (i > 0 ? Gap : 0);
            if (total <= R - L) break;
            if (vp.TextSize > MinSize) { vp.TextSize -= 2; sp2.TextSize = Math.Max(17, vp.TextSize * 0.55f); continue; }
            if (n > 1) { n--; vp.TextSize = 38; sp2.TextSize = 21; continue; }
            break;
        }
        float fx = L;
        using var dv = new SKPaint { StrokeWidth = 1.2f, Color = col.WithAlpha((byte)(90 * a)) };
        for (int i = 0; i < n; i++)
        {
            var fct = facts[i];
            float k = K(t, 0.25 + i * 0.12, 0.35);
            if (k <= 0) break;
            float fw = FactWidth(fct, lp, vp, sp2), room = n == 1 || i == n - 1 ? R - fx : fw;
            if (i > 0) { dv.Color = col.WithAlpha((byte)(90 * a * k)); c.DrawLine(fx - Gap / 2, labelBase - 12, fx - Gap / 2, valueBase + 2, dv); }
            lp.Color = MiniLabel.WithAlpha((byte)(255 * a * k));
            TextTracked.Draw(c, lp, Fit(fct.Label, lp, room), fx, labelBase, 1.8f);
            vp.Color = fct.Col.WithAlpha((byte)(fct.Col.Alpha * a * k));
            string v = fct.Value;
            float sw = fct.Suffix.Length > 0 ? sp2.MeasureText(fct.Suffix) + 8 : 0;
            if (RollW(v, vp) > room - sw) { sw = 0; v = Fit(v, vp, room); }
            if (kind == StoryKind.DriverOfDay) c.DrawText(v, fx, valueBase, vp);
            else DrawRolling(c, v, fx, valueBase, vp, K(t, 0.25 + i * 0.12, 0.7), SKTextAlign.Left);
            if (sw > 0)
            {
                sp2.Color = MiniSub.WithAlpha((byte)(255 * a * k));
                c.DrawText(fct.Suffix, fx + RollW(v, vp) + 8, valueBase, sp2);
            }
            fx += fw + Gap;
        }
    }

    float FactWidth(MiniFact f, SKPaint lp, SKPaint vp, SKPaint sp)
    {
        float vw = RollW(f.Value, vp) + (f.Suffix.Length > 0 ? 8 + sp.MeasureText(f.Suffix) : 0);
        return Math.Max(vw, TrackedWidth(lp, f.Label, 1.8f));
    }

    /// <summary>The width <see cref="DrawRolling"/> gives a value (digits on fixed cells).</summary>
    static float RollW(string text, SKPaint p)
    {
        float cell = p.MeasureText("0"), total = 0;
        foreach (char ch in text) total += char.IsDigit(ch) ? cell : p.MeasureText(ch.ToString());
        return total;
    }

    static string LastName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        int i = name.LastIndexOf(' ');
        return (i >= 0 ? name[(i + 1)..] : name).ToUpperInvariant();
    }

    static int MaxLaps(RaceSnapshot s)
    {
        int m = 0;
        foreach (var cl in s.ClassLeaderboard) foreach (var d in cl.Drivers) if (d.PositionInClass > 0 && d.LapsCompleted > m) m = d.LapsCompleted;
        return m;
    }

    /// <summary>The partner's mark, small, right-aligned ("BY [mark]"; the Michelin logo on the pit card). Returns its left edge.</summary>
    static float MiniPartner(SKCanvas c, StoryKind kind, float right, float midY, float a)
    {
        if (a <= 0.01f) return right;
        if (kind == StoryKind.PitStops)
        {
            var m = CornerBrand.User("michelin");
            return m == null ? right : right - CornerBrand.Draw(c, m, right, midY, 120, 30, a, alignRight: true);
        }
        var p = Presenters.For(kind);
        if (p == Partner.None) return right;
        const float MarkH = 28;
        float mw = Presenters.MarkWidth(p, MarkH);
        if (mw <= 0) return right;
        string verb = Presenters.Verb(kind) == "BUILT BY" ? "BUILT BY" : "BY";
        using var vp = P(Fonts.Inter.Bold, 13, MiniLabel, a);
        float vw = TrackedWidth(vp, verb, 1.6f), x = right - mw - 8 - vw;
        TextTracked.Draw(c, vp, verb, x, midY + 5, 1.6f);
        Presenters.DrawMark(c, p, right - mw, midY, MarkH, a);
        return x;
    }
}
