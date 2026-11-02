using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Where the pit lane is on this track (2026-10-25): the lap fraction where cars turn onto pit road and where they
/// leave it, learned from every car that pits, and our own pit box (where we stop). Fed by the race diary.
/// </summary>
public static class PitLaneMap
{
    static string track = "";
    public static float Entry { get; private set; } = -1;
    public static float Exit { get; private set; } = -1;
    public static float Box { get; private set; } = -1;

    public static void UseTrack(string t) { if (t != track) { track = t; Entry = Exit = Box = -1; } }
    public static void SawEntry(float pct) { if (pct >= 0) Entry = Entry < 0 ? pct : Blend(Entry, pct); }
    public static void SawExit(float pct) { if (pct >= 0) Exit = Exit < 0 ? pct : Blend(Exit, pct); }
    public static void SawBox(float pct) { if (pct >= 0) Box = pct; }
    static float Blend(float a, float b) { float d = b - a; if (d > 0.5f) d -= 1; if (d < -0.5f) d += 1; float r = a + d * 0.3f; return r - MathF.Floor(r); }

    /// <summary>0..1 along the pit lane (entry to exit) for a lap fraction.</summary>
    public static float Along(float pct)
    {
        float en = Entry >= 0 ? Entry : 0.97f, ex = Exit >= 0 ? Exit : (en + 0.05f) % 1f;
        float len = ex - en; if (len <= 0) len += 1;
        float d = pct - en; if (d < -0.5f) d += 1; if (d > 0.5f && len < 0.5f) d -= 1;
        return Math.Clamp(d / len, 0f, 1f);
    }
}

/// <summary>
/// The pit lane battle card (2026-10-25, Angelo picked "pit lane race"): while we and a class rival are both on pit road
/// the battle card turns into a pit board - a strip of pit lane from entry to exit with both cars moving along it and
/// our box marked, each car's status (ENTERING / IN THE BOX / LEAVING) with how long it has been on pit road and
/// stopped, and who looks like getting out first. Black board, pit-orange header, like the Pit Stops story card.
/// </summary>
public sealed class PitBattleCard
{
    public const float W = 1060f, H = 236f;
    static readonly SKColor Board = new(10, 10, 12, 236), Orange = new(0xFF, 0x7A, 0x1A), Ivory = new(0xF4, 0xF1, 0xE8),
        Dim = new(0xC4, 0xC9, 0xD2),   // #340 legibility: was 9AA0AC
        Blue = new(0x4D, 0x9F, 0xFF), Lane = new(255, 255, 255, 28), Green = new(0x5C, 0xE6, 0x8E);

    sealed class CarState { public double EnterMs = -1, StopStartMs = -1, StoppedMs, LastMs; public float LastPct = -1; public bool Stopped, WasStopped; }
    readonly Dictionary<int, CarState> st = new();
    readonly SKPaint fill = new() { IsAntialias = true }, text = new() { IsAntialias = true }, stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    double shownSinceMs = -1;

    void Track(Driver d, double nowMs)
    {
        if (!st.TryGetValue(d.CarIdx, out var s)) st[d.CarIdx] = s = new CarState();
        if (!d.IsInPit) { s.EnterMs = -1; s.StopStartMs = -1; s.StoppedMs = 0; s.Stopped = s.WasStopped = false; s.LastPct = d.LapDistPct; s.LastMs = nowMs; return; }
        if (s.EnterMs < 0) s.EnterMs = nowMs;
        if (nowMs - s.LastMs >= 250)
        {
            float dp = Math.Abs(d.LapDistPct - s.LastPct); if (dp > 0.5f) dp = 1 - dp;
            bool stopped = s.LastPct >= 0 && dp < 0.00004f;
            if (stopped && !s.Stopped) s.StopStartMs = nowMs;
            if (!stopped && s.Stopped) { s.StoppedMs += nowMs - s.StopStartMs; s.WasStopped = true; }
            s.Stopped = stopped; s.LastPct = d.LapDistPct; s.LastMs = nowMs;
        }
    }

    double StoppedFor(Driver d, double nowMs)
        => st.TryGetValue(d.CarIdx, out var s) ? s.StoppedMs + (s.Stopped && s.StopStartMs >= 0 ? nowMs - s.StopStartMs : 0) : 0;

    string Status(Driver d)
    {
        if (!st.TryGetValue(d.CarIdx, out var s)) return "PIT LANE";
        if (s.Stopped) return "IN THE BOX";
        return s.WasStopped ? "LEAVING" : "ENTERING";
    }

    /// <summary>Called every frame for the player and their rival (keeps timers right even when not drawn).</summary>
    public void Tick(RaceSnapshot snap, Driver? rival, double nowMs)
    {
        if (snap.Player is { } p)
        {
            Track(p, nowMs);
            if (p.IsInPit && st.TryGetValue(p.CarIdx, out var ps) && ps.Stopped) PitLaneMap.SawBox(p.LapDistPct);
        }
        if (rival != null) Track(rival, nowMs);
    }

    public void Draw(SKCanvas c, float cx, float bottom, RaceSnapshot snap, Driver me, Driver rival, int battlePos, double nowMs, float alpha)
    {
        if (alpha <= 0.01f) return;
        if (shownSinceMs < 0) shownSinceMs = nowMs;
        byte A(float v) => (byte)Math.Clamp(v * 255f * alpha, 0, 255);
        var r = new SKRect(cx - W / 2f, bottom - H, cx + W / 2f, bottom);
        float open = Math.Clamp((float)((nowMs - shownSinceMs) / 380.0), 0f, 1f); open = open * open * (3 - 2 * open);
        c.Save();
        c.ClipRect(new SKRect(r.MidX - r.Width / 2f * open, r.Top - 4, r.MidX + r.Width / 2f * open, r.Bottom + 4));
        // board + orange header band
        fill.Color = Board.WithAlpha(A(Board.Alpha / 255f)); c.DrawRoundRect(r, 6f, 6f, fill);
        var band = new SKRect(r.Left, r.Top, r.Right, r.Top + 44f);
        fill.Color = Orange.WithAlpha(A(1)); c.Save(); c.ClipRoundRect(new SKRoundRect(r, 6f, 6f)); c.DrawRect(band, fill); c.Restore();
        text.Typeface = Fonts.BarlowCondensed.Black; text.TextSize = 30f; text.Color = new SKColor(12, 12, 12, A(1)); text.TextAlign = SKTextAlign.Left;
        TextTracked.Draw(c, text, "PIT LANE BATTLE" + (battlePos > 0 ? $"  ·  P{battlePos}" : ""), band.Left + 26f, band.MidY + 11f, 2.4f);
        text.Typeface = Fonts.BarlowCondensed.Bold; text.TextSize = 18f; text.TextAlign = SKTextAlign.Right;
        TextTracked.Draw(c, text, "WHO'S OUT FIRST?", band.Right - 26f, band.MidY + 7f, 1.6f);
        text.TextAlign = SKTextAlign.Left;

        // the lane strip: ENTRY ---- [BOX] ---- EXIT, both cars on it
        float lx0 = r.Left + 110f, lx1 = r.Right - 110f, ly = band.Bottom + 34f;
        fill.Color = Lane.WithAlpha(A(Lane.Alpha / 255f)); c.DrawRoundRect(new SKRect(lx0, ly - 3f, lx1, ly + 3f), 3f, 3f, fill);
        text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f; text.Color = Dim.WithAlpha(A(1));   // #340: was 11
        text.TextAlign = SKTextAlign.Right; TextTracked.Draw(c, text, "ENTRY", lx0 - 14f, ly + 4f, 2f);
        text.TextAlign = SKTextAlign.Left; TextTracked.Draw(c, text, "EXIT", lx1 + 14f, ly + 4f, 2f);
        if (PitLaneMap.Box >= 0)
        {
            float bx = lx0 + (lx1 - lx0) * PitLaneMap.Along(PitLaneMap.Box);
            stroke.StrokeWidth = 1.5f; stroke.Color = Orange.WithAlpha(A(0.9f));
            c.DrawRect(new SKRect(bx - 16f, ly - 12f, bx + 16f, ly + 12f), stroke);
            text.TextAlign = SKTextAlign.Center; text.Typeface = Fonts.Inter.Bold; text.TextSize = 12.5f; text.Color = Orange.WithAlpha(A(1));
            TextTracked.Draw(c, text, "OUR BOX", bx, ly - 18f, 1.6f);
        }
        void Dot(Driver d, bool mine)
        {
            float x = lx0 + (lx1 - lx0) * (d.IsInPit ? PitLaneMap.Along(d.LapDistPct) : 1f);
            string num = d.CarNumber ?? "";
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 12f; text.TextAlign = SKTextAlign.Center;
            float w = Math.Max(26f, text.MeasureText(num) + 12f);
            var chip = new SKRect(x - w / 2f, ly - 10f, x + w / 2f, ly + 10f);
            fill.Color = (mine ? Blue : Ivory).WithAlpha(A(1)); c.DrawRoundRect(chip, 3f, 3f, fill);
            text.Color = (mine ? SKColors.White : new SKColor(10, 10, 12)).WithAlpha(A(1));
            TextTracked.Draw(c, text, num, x, ly + 4.5f, 0f);
        }
        Dot(rival, false); Dot(me, true);

        // the two rows
        float ry = ly + 44f;
        void Row(Driver d, bool mine, float y)
        {
            if (mine) { fill.Color = Blue.WithAlpha(A(0.16f)); c.DrawRect(new SKRect(r.Left + 16f, y - 22f, r.Right - 16f, y + 10f), fill);
                        fill.Color = Blue.WithAlpha(A(1)); c.DrawRect(new SKRect(r.Left + 16f, y - 22f, r.Left + 19f, y + 10f), fill); }
            text.TextAlign = SKTextAlign.Left; text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f;
            string num = d.CarNumber ?? ""; float w = Math.Max(30f, text.MeasureText(num) + 12f);
            fill.Color = Ivory.WithAlpha(A(0.95f)); c.DrawRect(new SKRect(r.Left + 32f, y - 17f, r.Left + 32f + w, y + 5f), fill);
            text.Color = new SKColor(10, 10, 12, A(1)); text.TextAlign = SKTextAlign.Center; TextTracked.Draw(c, text, num, r.Left + 32f + w / 2f, y, 0f);
            text.TextAlign = SKTextAlign.Left; text.Typeface = Fonts.BarlowCondensed.ExtraBold; text.TextSize = 27f; text.Color = (mine ? SKColors.White : Ivory).WithAlpha(A(1));
            TextTracked.Draw(c, text, NameFormat.Broadcast(d).ToUpperInvariant(), r.Left + 32f + w + 14f, y + 3f, 0.8f);
            string status = Status(d);
            text.Typeface = Fonts.BarlowCondensed.Bold; text.TextSize = 22f;
            text.Color = (status == "IN THE BOX" ? Orange : status == "LEAVING" ? Green : Dim).WithAlpha(A(1));
            TextTracked.Draw(c, text, status, r.Left + 520f, y + 2f, 1.4f);
            double stopped = StoppedFor(d, nowMs) / 1000.0;
            text.Typeface = Fonts.BarlowCondensed.ExtraBold; text.TextSize = 28f; text.Color = Ivory.WithAlpha(A(1)); text.TextAlign = SKTextAlign.Right;
            TextTracked.Draw(c, text, stopped > 0.05 ? stopped.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "-", r.Right - 150f, y + 3f, 0f);
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 12.5f; text.Color = Dim.WithAlpha(A(1));
            TextTracked.Draw(c, text, "STOPPED", r.Right - 150f, y - 22f + 4f, 1.2f);
            double lane = st.TryGetValue(d.CarIdx, out var s) && s.EnterMs >= 0 ? (nowMs - s.EnterMs) / 1000.0 : 0;
            text.Typeface = Fonts.BarlowCondensed.Bold; text.TextSize = 24f; text.Color = Dim.WithAlpha(A(1));
            TextTracked.Draw(c, text, lane > 0 ? lane.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "-", r.Right - 34f, y + 3f, 0f);
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 12.5f;
            TextTracked.Draw(c, text, "PIT LANE", r.Right - 34f, y - 22f + 4f, 1.2f);
            text.TextAlign = SKTextAlign.Left;
        }
        // the car further along the lane goes on top
        bool meFirst = (me.IsInPit ? PitLaneMap.Along(me.LapDistPct) : 1f) >= (rival.IsInPit ? PitLaneMap.Along(rival.LapDistPct) : 1f);
        Row(meFirst ? me : rival, meFirst, ry); Row(meFirst ? rival : me, !meFirst, ry + 40f);

        // who's out first
        string sm = Status(me), sr = Status(rival);
        Driver? first = null;
        if (!me.IsInPit && rival.IsInPit) first = me;
        else if (me.IsInPit && !rival.IsInPit) first = rival;
        else if (sm == "LEAVING" && sr != "LEAVING") first = me;
        else if (sr == "LEAVING" && sm != "LEAVING") first = rival;
        else first = meFirst ? me : rival;
        text.Typeface = Fonts.Inter.Bold; text.TextSize = 14f; text.Color = Dim.WithAlpha(A(1)); text.TextAlign = SKTextAlign.Left;
        float fy = r.Bottom - 16f;
        TextTracked.Draw(c, text, "OUT FIRST", r.Left + 32f, fy, 1.8f);
        text.Typeface = Fonts.BarlowCondensed.ExtraBold; text.TextSize = 22f; text.Color = (first == me ? Blue : Orange).WithAlpha(A(1));
        TextTracked.Draw(c, text, first == me ? "US" : "#" + first.CarNumber + "  " + NameFormat.Broadcast(first).ToUpperInvariant(), r.Left + 140f, fy + 2f, 1.2f);
        c.Restore();
    }

    public void Hidden() { shownSinceMs = -1; }
}
