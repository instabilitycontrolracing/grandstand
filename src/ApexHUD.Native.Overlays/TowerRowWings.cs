using SkiaSharp;
using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Roadmap items 4 + 5 (2026-10-18, design by Angelo): broadcast "wings" - small
/// tabs that slide out from the RIGHT edge of a driver's timing-tower row.
///   * PIT wing (amber): appears when a car drives INTO pit lane from the track.
///     Live pit-lane timer, switching to the stationary time while the car is
///     stopped in its box; holds ~4 s after the car rejoins, then slides back.
///     Cars merely parked in the garage never get one (only an observed
///     track -> pit-lane transition arms it), and it gives up after 60 s parked.
///   * FASTEST-LAP wing (purple): when a driver BEATS the existing class-best
///     lap, "FASTEST LAP 3:30.304" slides out of their row for ~6 s. The first
///     laps of a session (no prior class best) never trigger it.
/// Both share one slide (~0.38 s, matching the tower transition wipes) and follow
/// the driver's row if it moves. Owned and drawn by <see cref="TimingTowerRenderer"/>.
/// </summary>
public sealed class TowerRowWings : IDisposable
{
    public const double SlideMs = 380;
    public const double PitHoldAfterExitMs = 4000;
    public const double PitParkedGiveUpMs = 60000;
    public const double FastestHoldMs = 6000;
    public const float WingWidth = 176f;
    public const float WingHeight = 30f;

    sealed class PitState
    {
        public bool Seen, InPit, Tracking, WasOnTrack;
        public double EnterMs = -1, ExitMs = -1, StoppedSinceMs = -1, StationaryMs, LastMoveMs;
        public float LastPct = -1f;
    }

    readonly Dictionary<int, PitState> pit = new();
    readonly Dictionary<string, float> classBest = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, float> lastBest = new();
    readonly Dictionary<int, float> rowY = new();
    bool primed;

    int fastestCar = -1;
    float fastestTime;
    double fastestAtMs = -1;

    // Per-class fastest-lap HOLDER (2026-10-18, Angelo): replaces the purple dot
    // after the name. Each class's current best-lap holder gets a small purple
    // tab on their row; it slides in when the holder changes.
    public const float MiniWidth = 12f;
    readonly Dictionary<string, (int Car, double SinceMs)> classHolder = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> holders = new();

    static readonly SKColor PitColor = SKColor.Parse(BroadcastPalette.PitAmber);
    static readonly SKColor FastColor = SKColor.Parse(BroadcastPalette.FastestPurple);

    readonly SKPaint bgPaint = new() { IsAntialias = true };
    readonly SKPaint edgePaint = new() { IsAntialias = true };
    readonly SKPaint labelPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };   // #340 legibility: was 10.5
    readonly SKPaint valuePaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 16f, TextAlign = SKTextAlign.Right };

    /// <summary>True while any wing is on screen (keeps the host presenting so the
    /// slide animates and the pit timer ticks).</summary>
    public bool Animating { get; private set; }

    /// <summary>Advance pit / fastest-lap state from the latest snapshot.</summary>
    public void Update(RaceSnapshot snap, double nowMs)
    {
        bool trigger = primed;
        holders.Clear();
        foreach (var cls in snap.ClassLeaderboard)
        {
            // Current class fastest-lap holder (lowest positive best lap).
            int holderCar = -1; float holderTime = float.MaxValue;
            foreach (var d in cls.Drivers)
                if (d.BestLapTime > 0 && d.BestLapTime < holderTime) { holderTime = d.BestLapTime; holderCar = d.CarIdx; }
            if (holderCar >= 0)
            {
                if (!classHolder.TryGetValue(cls.Name ?? "", out var h) || h.Car != holderCar)
                    classHolder[cls.Name ?? ""] = (holderCar, nowMs);
                holders.Add(holderCar);
            }

            foreach (var d in cls.Drivers)
            {
                UpdatePit(d, nowMs);

                float b = d.BestLapTime;
                lastBest.TryGetValue(d.CarIdx, out float prev);
                if (b > 0 && (prev <= 0 || b < prev - 0.0005f))
                {
                    classBest.TryGetValue(d.ClassName ?? "", out float cb);
                    if (trigger && PresentationState.NotificationsArmed && cb > 0 && b < cb - 0.0005f)
                    {
                        fastestCar = d.CarIdx;
                        fastestTime = b;
                        fastestAtMs = nowMs;
                    }
                    if (cb <= 0 || b < cb) classBest[d.ClassName ?? ""] = b;
                }
                lastBest[d.CarIdx] = b;
            }
        }
        primed = true;
    }

    void UpdatePit(Driver d, double nowMs)
    {
        if (!pit.TryGetValue(d.CarIdx, out var ps)) { ps = new PitState(); pit[d.CarIdx] = ps; }
        if (!ps.Seen) { ps.Seen = true; ps.InPit = d.IsInPit; ps.WasOnTrack = d.IsConnected && !d.IsInPit; return; }   // already in pits when first seen -> no wing

        // 2026-10-18: a car LOADING INTO the session pops straight into its stall -
        // not a pit stop. Count an entry only when notifications are armed (race:
        // after the green) AND the car was loaded in and on track a moment ago.
        bool wasOnTrack = ps.WasOnTrack;
        ps.WasOnTrack = d.IsConnected && !d.IsInPit;
        if (!ps.InPit && d.IsInPit && (!PresentationState.NotificationsArmed || !wasOnTrack))
        {
            ps.InPit = true;          // note it, but no wing
            return;
        }

        if (!ps.InPit && d.IsInPit)
        {
            ps.Tracking = true;
            ps.EnterMs = nowMs; ps.ExitMs = -1;
            ps.StationaryMs = 0; ps.StoppedSinceMs = -1;
            ps.LastPct = d.LapDistPct; ps.LastMoveMs = nowMs;
        }
        else if (ps.InPit && !d.IsInPit && ps.Tracking && ps.ExitMs < 0)
        {
            if (ps.StoppedSinceMs >= 0) { ps.StationaryMs += nowMs - ps.StoppedSinceMs; ps.StoppedSinceMs = -1; }
            ps.ExitMs = nowMs;
        }
        ps.InPit = d.IsInPit;

        if (ps.Tracking && ps.ExitMs < 0)
        {
            // Stopped in the box = lap position not changing for > 0.5 s.
            if (Math.Abs(d.LapDistPct - ps.LastPct) > 0.00002f)
            {
                ps.LastPct = d.LapDistPct;
                if (ps.StoppedSinceMs >= 0) { ps.StationaryMs += nowMs - ps.StoppedSinceMs; ps.StoppedSinceMs = -1; }
                ps.LastMoveMs = nowMs;
            }
            else if (ps.StoppedSinceMs < 0 && nowMs - ps.LastMoveMs > 500)
            {
                ps.StoppedSinceMs = ps.LastMoveMs;
            }
            if (nowMs - ps.EnterMs > PitParkedGiveUpMs) ps.Tracking = false;   // parked - not a stop
        }
        else if (ps.Tracking && nowMs - ps.ExitMs > PitHoldAfterExitMs + SlideMs)
        {
            ps.Tracking = false;
        }
    }

    /// <summary>Clear the per-frame row positions (call before the row loop).</summary>
    public void BeginFrame() => rowY.Clear();

    /// <summary>Record where a driver's row was drawn this frame.</summary>
    public void NoteRow(int carIdx, float y) => rowY[carIdx] = y;

    /// <summary>Draw every active wing attached to the right edge
    /// (<paramref name="x"/>) of its driver's row.</summary>
    public void Draw(SKCanvas c, float x, float rowHeight, double nowMs)
    {
        bool any = false;
        foreach (var kv in rowY)
        {
            int car = kv.Key;
            float y = kv.Value + (rowHeight - WingHeight) / 2f;

            // Fastest lap takes priority over pit for the same row.
            if (car == fastestCar && fastestAtMs >= 0)
            {
                float p = Progress(nowMs, fastestAtMs, fastestAtMs + FastestHoldMs);
                if (p > 0f)
                {
                    // As the big wing slides back, the small holder tab grows in
                    // beneath it so it reads as collapsing into the tab.
                    double tabStart = fastestAtMs + FastestHoldMs - SlideMs;
                    if (holders.Contains(car) && nowMs > tabStart)
                        DrawMiniTab(c, x, y, (float)Smooth(Math.Clamp((nowMs - tabStart) / SlideMs, 0, 1)));
                    if (!lapStrings.TryGetValue(fastestTime, out var lapText))
                    {
                        if (lapStrings.Count > 256) lapStrings.Clear();
                        lapStrings[fastestTime] = lapText = FormatLap(fastestTime);
                    }
                    DrawWing(c, x, y, p, FastColor, "FASTEST LAP", lapText);
                    any = true;
                    continue;
                }
            }

            if (pit.TryGetValue(car, out var ps) && ps.Tracking && ps.EnterMs >= 0)
            {
                double end = ps.ExitMs >= 0 ? ps.ExitMs + PitHoldAfterExitMs : ps.EnterMs + PitParkedGiveUpMs;
                float p = Progress(nowMs, ps.EnterMs, end);
                if (p > 0f)
                {
                    string label, value;
                    if (ps.ExitMs < 0 && ps.StoppedSinceMs >= 0)
                    {
                        label = "STOP";
                        value = FormatSecs((ps.StationaryMs + (nowMs - ps.StoppedSinceMs)) / 1000.0);
                    }
                    else
                    {
                        double laneMs = (ps.ExitMs >= 0 ? ps.ExitMs : nowMs) - ps.EnterMs;
                        label = "PIT";
                        value = FormatSecs(laneMs / 1000.0);
                    }
                    DrawWing(c, x, y, p, PitColor, label, value);
                    any = true;
                    continue;
                }
            }

            // Small purple tab: this car holds its class's fastest lap right now.
            if (holders.Contains(car))
            {
                double since = -1;
                foreach (var h in classHolder.Values) if (h.Car == car) { since = h.SinceMs; break; }
                // If the big FASTEST LAP wing just ran for this car, the small tab
                // grows in as the big one slides back ("collapses into" the tab).
                if (car == fastestCar && fastestAtMs >= 0)
                    since = Math.Max(since, fastestAtMs + FastestHoldMs - SlideMs);
                double inP = since < 0 ? 1 : Math.Clamp((nowMs - since) / SlideMs, 0, 1);
                if (inP > 0)
                {
                    DrawMiniTab(c, x, y, (float)Smooth(inP));
                    if (inP < 1) any = true;
                }
            }
        }
        if (fastestAtMs >= 0 && nowMs - fastestAtMs > FastestHoldMs + 200) fastestAtMs = -1;
        Animating = any;
    }

    void DrawWing(SKCanvas c, float x, float y, float progress, SKColor color, string label, string value)
    {
        // 2026-10-18 (Angelo): sized to its content - just big enough for the
        // label + value with tight padding, no dead space between them. The
        // value is measured with every digit as '0' so a ticking pit timer
        // doesn't make the wing twitch wider/narrower.
        const float padLeft = 12f, gap = 8f, padRight = 10f;
        float labelW = TextBlobs.Measure(label, labelPaint);
        float valueW = TextBlobs.Measure(DigitsAsZero(value), valuePaint);
        float wingW = padLeft + labelW + gap + valueW + padRight;

        float w = wingW * progress;
        var full = new SKRect(x, y, x + wingW, y + WingHeight);
        var shown = new SKRect(x, y, x + w, y + WingHeight);

        c.Save();
        c.ClipRect(shown);
        bgPaint.Color = new SKColor(14, 16, 22, 235);
        {
            var rr = wingRect;   // 2026-10-29 (efficiency): one kept round-rect, fully re-set each use
            rr.SetRectRadii(full, WingRadii);
            c.DrawRoundRect(rr, bgPaint);
            edgePaint.Color = color.WithAlpha(46);          // class-of-event tint
            c.DrawRoundRect(rr, edgePaint);
        }
        // Solid coloured accent strip on the tower side.
        edgePaint.Color = color;
        c.DrawRect(new SKRect(x, y, x + 4f, y + WingHeight), edgePaint);

        labelPaint.Color = color;
        TextBlobs.Draw(c, label, x + padLeft, y + WingHeight / 2f + 4.5f, labelPaint);
        valuePaint.Color = SKColors.White;
        TextBlobs.Draw(c, value, x + wingW - padRight, y + WingHeight / 2f + 6f, valuePaint);   // right-aligned
        c.Restore();
    }

    readonly SKRoundRect wingRect = new(), miniRect = new();
    static readonly SKPoint[] WingRadii = { new(0, 0), new(6, 6), new(6, 6), new(0, 0) }, MiniRadii = { new(0, 0), new(4, 4), new(4, 4), new(0, 0) };
    readonly Dictionary<string, string> zeroed = new(StringComparer.Ordinal);
    readonly Dictionary<float, string> lapStrings = new();

    string DigitsAsZero(string s)
    {
        if (zeroed.TryGetValue(s, out var z)) return z;
        if (zeroed.Count > 512) zeroed.Clear();
        return zeroed[s] = DigitsAsZeroCore(s);
    }

    static string DigitsAsZeroCore(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++) if (char.IsDigit(a[i])) a[i] = '0';
        return new string(a);
    }

    // Small persistent class-fastest-lap tab (replaces the old purple dot).
    readonly SKPaint miniPaint = new() { IsAntialias = true };
    readonly SKPaint miniGlow = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.5f) };

    void DrawMiniTab(SKCanvas c, float x, float y, float progress)
    {
        if (progress <= 0f) return;
        const float h = 22f;
        float top = y + (WingHeight - h) / 2f;
        float w = MiniWidth * progress;
        var r = new SKRect(x, top, x + w, top + h);
        miniGlow.Color = FastColor.WithAlpha(110);
        miniPaint.Color = FastColor;
        var rr = miniRect;
        rr.SetRectRadii(r, MiniRadii);
        c.DrawRoundRect(rr, miniGlow);
        c.DrawRoundRect(rr, miniPaint);
    }

    static float Progress(double now, double start, double end)
    {
        if (now < start || now > end) return 0f;
        double inP = Math.Clamp((now - start) / SlideMs, 0, 1);
        double outP = Math.Clamp((end - now) / SlideMs, 0, 1);
        return (float)(Smooth(inP) * Smooth(outP));
    }

    static double Smooth(double t) => t * t * (3 - 2 * t);

    static string FormatSecs(double s) =>
        s < 60 ? s.ToString("0.0") : $"{(int)(s / 60)}:{s % 60:00.0}";

    static string FormatLap(float s)
    {
        if (s <= 0) return "--";
        int m = (int)(s / 60);
        return $"{m}:{s - m * 60:00.000}";
    }

    public void Dispose()
    {
        bgPaint.Dispose(); edgePaint.Dispose(); labelPaint.Dispose(); valuePaint.Dispose();
        miniPaint.Dispose(); miniGlow.Dispose(); wingRect.Dispose(); miniRect.Dispose();
    }
}
