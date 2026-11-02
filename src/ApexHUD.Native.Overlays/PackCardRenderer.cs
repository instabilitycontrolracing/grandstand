using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-25 (Angelo: "if there are more cars in class within 2 seconds I want a battle of the pack ... and
/// their inputs too"). When we and at least two other cars of our class are within 2 s of each other, the
/// battle card gives way to this one: every car of the pack in running order - place, number, name, the gap
/// to the car ahead, and each car's steering (a small arc with a gold mark that turns with the wheel) and gear,
/// which iRacing sends for every car; our own row also gets slim throttle / brake bars. Same deep-glass,
/// serif-numeral look as the battle card. Render thread only.
/// </summary>
public sealed class PackCardRenderer
{
    public const float W = 1000f, RowH = 46f, HeadH = 20f, Pad = 14f;
    const double EnterGap = 2.0, LeaveGap = 2.4, LeaveHoldMs = 2500, FadeMs = 320;
    const int MaxRows = 6;

    static readonly SKColor Glass = Hex(BroadcastPalette.TowerGlassBase, 226);
    static readonly SKColor Ivory = Hex(BroadcastPalette.TextPrimary);
    static readonly SKColor Second = Hex(BroadcastPalette.TextSecondary);
    static readonly SKColor Tert = Hex(BroadcastPalette.TextTertiary);
    static readonly SKColor Gold = Hex(BroadcastPalette.Leader);
    static readonly SKColor Blue = Hex(BroadcastPalette.PlayerStripe);
    static readonly SKColor GreenHi = new(0x5C, 0xE6, 0x8E), RedHi = new(0xFF, 0x4A, 0x5F);

    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint text = new() { IsAntialias = true };
    readonly SKPath mark = new();
    readonly ShaderSlot sheen = new(), meBand = new();
    readonly Dictionary<int, float> steerS = new();

    readonly List<Driver> members = new();
    bool on; double offSinceMs = -1, flipMs = -1;
    public bool On => on;

    /// <summary>0..1 how visible the pack card is right now.</summary>
    public float Alpha(double nowMs)
    {
        if (flipMs < 0) return on ? 1f : 0f;
        float k = (float)Math.Clamp((nowMs - flipMs) / FadeMs, 0, 1);
        k = k * k * (3 - 2 * k);
        return on ? k : 1f - k;
    }

    /// <summary>Works out the pack around the player (call every frame the battle card is evaluated).</summary>
    public void Update(RaceSnapshot snap, double nowMs, bool battleOn)
    {
        var found = Find(snap, on ? LeaveGap : EnterGap);
        bool want = battleOn && found >= 3;
        if (want) { offSinceMs = -1; if (!on) { on = true; flipMs = nowMs; } }
        else if (on)
        {
            if (offSinceMs < 0) offSinceMs = nowMs;
            if (nowMs - offSinceMs >= LeaveHoldMs || !battleOn) { on = false; flipMs = nowMs; }
        }
    }

    int Find(RaceSnapshot s, double span)
    {
        var p = s.Player;
        if (p == null || !s.IsConnected) { if (!on) members.Clear(); return 0; }
        DriverClass? cls = null;
        foreach (var c in s.ClassLeaderboard) { foreach (var d in c.Drivers) if (d.CarIdx == p.CarIdx) { cls = c; break; } if (cls != null) break; }
        if (cls == null) return 0;
        // on the same lap as us and within `span` seconds of us (the class gap to the leader, both ways)
        double mine = p.GapToLeaderSeconds ?? 0;
        var list = new List<Driver>();
        foreach (var d in cls.Drivers)
        {
            if (d.LapDifference != p.LapDifference || d.IsInPit) continue;
            double g = d.GapToLeaderSeconds ?? (d.PositionInClass == 1 ? 0 : double.NaN);
            if (!double.IsFinite(g)) continue;
            if (Math.Abs(g - mine) <= span) list.Add(d);
        }
        list.Sort((a, b) => a.PositionInClass.CompareTo(b.PositionInClass));
        if (list.Count >= 3 || !on)
        {
            members.Clear();
            // keep it to six rows, centred on us
            int me = list.FindIndex(d => d.CarIdx == p.CarIdx);
            int start = Math.Clamp(me - MaxRows / 2, 0, Math.Max(0, list.Count - MaxRows));
            for (int i = start; i < Math.Min(list.Count, start + MaxRows); i++) members.Add(list[i]);
        }
        return list.Count;
    }

    public void Draw(SKCanvas c, float cx, float bottom, RaceSnapshot snap, double nowMs)
    {
        float a = Alpha(nowMs);
        if (a <= 0.01f || members.Count == 0) return;
        int n = members.Count;
        float h = Pad + n * RowH + Pad;
        var r = new SKRect(cx - W / 2f, bottom - h, cx + W / 2f, bottom);
        int save = c.Save();
        c.Translate(0, (1 - a) * 18f);
        byte A(float k) => (byte)Math.Clamp(k * 255f * a, 0, 255);

        // ---- the tab on top: BATTLE OF THE PACK · P4 – P7  [class]
        string range = members[0].PositionInClass > 0 ? $"P{members[0].PositionInClass} – P{members[^1].PositionInClass}" : "";
        text.Typeface = Fonts.Inter.Bold; text.TextSize = 17f; text.TextAlign = SKTextAlign.Left;
        const string head = "BATTLE OF THE PACK";
        float headW = text.MeasureText(head) + 0.42f * 15f * (head.Length - 1);
        text.TextSize = 19f; float rw = range.Length > 0 ? text.MeasureText(range) + 30f : 0f;
        float tw = headW + rw + 56f;
        var tab = new SKRect(r.MidX - tw / 2f, r.Top - 5f - 38f, r.MidX + tw / 2f, r.Top - 5f);
        fill.Shader = null; fill.Color = Glass.WithAlpha((byte)(Glass.Alpha * a)); c.DrawRoundRect(tab, 3, 3, fill);
        fill.Shader = sheen.Linear(new SKPoint(tab.Left, 0), new SKPoint(tab.Right, 0), [Gold.WithAlpha(0), Gold.WithAlpha(220), Gold.WithAlpha(0)], []);
        fill.Color = SKColors.White.WithAlpha(A(1)); c.DrawRect(new SKRect(tab.Left, tab.Top, tab.Right, tab.Top + 1.2f), fill); fill.Shader = null;
        float tx = tab.MidX - (headW + rw) / 2f, ty = tab.MidY + 6f;
        text.TextSize = 17f; text.Typeface = Fonts.Inter.Bold; text.Color = Tert.WithAlpha(A(1));
        TextTracked.Draw(c, text, head, tx, ty, 0.42f * 15f);
        if (range.Length > 0) { text.TextSize = 19f; text.Color = Ivory.WithAlpha(A(1)); TextTracked.Draw(c, text, range, tx + headW + 30f, ty, 1.6f); }

        // ---- glass + gold hairline
        fill.Color = Glass.WithAlpha((byte)(Glass.Alpha * a)); c.DrawRect(r, fill);
        fill.Shader = sheen.Linear(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0), [Gold.WithAlpha(0), Gold.WithAlpha(200), Gold.WithAlpha(0)], []);
        fill.Color = SKColors.White.WithAlpha(A(1)); c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + 1.2f), fill); fill.Shader = null;

        // column x positions
        float xPos = r.Left + 52f, xNum = r.Left + 84f, xName = r.Left + 150f, xGap = r.Left + 600f, xSteer = r.Left + 700f, xGear = r.Left + 776f, xPed = r.Left + 830f;
        text.Typeface = Fonts.Inter.SemiBold; text.TextSize = 12.5f; text.Color = Tert.WithAlpha(A(1f)); text.TextAlign = SKTextAlign.Center;   // #340: was 9.5 @0.8
        TextTracked.Draw(c, text, "GAP", xGap, r.Top + 12.5f, 1.6f);
        TextTracked.Draw(c, text, "STEER", xSteer, r.Top + 12.5f, 1.6f);
        TextTracked.Draw(c, text, "GEAR", xGear, r.Top + 12.5f, 1.6f);
        text.TextAlign = SKTextAlign.Left;

        int playerIdx = snap.PlayerCarIdx;
        for (int i = 0; i < n; i++)
        {
            var d = members[i];
            float y = r.Top + Pad + i * RowH, my = y + RowH / 2f;
            bool me = d.CarIdx == playerIdx;
            if (me)
            {
                fill.Shader = meBand.Linear(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0), [Blue.WithAlpha(46), Blue.WithAlpha(0)], []);
                fill.Color = SKColors.White.WithAlpha(A(1)); c.DrawRect(new SKRect(r.Left, y, r.Right, y + RowH), fill); fill.Shader = null;
                fill.Color = Blue.WithAlpha(A(1)); c.DrawRect(new SKRect(r.Left, y + 4, r.Left + 3, y + RowH - 4), fill);
            }
            if (i > 0) { fill.Color = new SKColor(255, 255, 255, A(0.06f)); c.DrawRect(new SKRect(r.Left + 20, y, r.Right - 20, y + 1), fill); }
            // place
            text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 30f; text.TextAlign = SKTextAlign.Center; text.Color = (i == 0 ? Gold : Ivory).WithAlpha(A(1));
            TextTracked.Draw(c, text, d.PositionInClass > 0 ? d.PositionInClass.ToString(CultureInfo.InvariantCulture) : "-", xPos, my + 10f, 0f);
            // number chip
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f;
            string num = d.CarNumber ?? "";
            float cw = Math.Max(30f, text.MeasureText(num) + 12f);
            fill.Color = SKColors.White.WithAlpha(A(0.95f)); c.DrawRect(new SKRect(xNum, my - 11f, xNum + cw, my + 11f), fill);
            text.Color = new SKColor(8, 10, 16, A(0.95f)); TextTracked.Draw(c, text, num, xNum + cw / 2f, my + 4.5f, 0f);
            text.TextAlign = SKTextAlign.Left;
            // name + car
            text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 26f; text.Color = (me ? SKColors.White : Ivory).WithAlpha(A(1));
            TextTracked.Draw(c, text, NameFormat.Broadcast(d).ToUpperInvariant(), xName, my + 8f, 0.4f);
            text.Typeface = Fonts.Inter.Medium; text.TextSize = 13f; text.Color = Tert.WithAlpha(A(1f));   // #340: was 10.5 @0.9
            TextTracked.Draw(c, text, (d.CarName ?? "").ToUpperInvariant(), xName + 300f, my + 4f, 0.6f);
            // gap to the car ahead
            text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 24f; text.TextAlign = SKTextAlign.Center;
            string gap = i == 0 ? "LEADS" : d.IntervalSeconds is double iv && iv > 0 ? "+" + iv.ToString("0.0", CultureInfo.InvariantCulture) : "-";
            if (i == 0) { text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f; text.Color = Gold.WithAlpha(A(1)); TextTracked.Draw(c, text, gap, xGap, my + 4f, 2f); }
            else { text.Color = Second.WithAlpha(A(1)); TextTracked.Draw(c, text, gap, xGap, my + 8f, 0f); }
            // steering arc + gear (every car)
            float steer = d.CarIdx < snap.CarSteer.Length ? snap.CarSteer[d.CarIdx] : 0f;
            steerS.TryGetValue(d.CarIdx, out var ss); ss += (steer - ss) * 0.4f; steerS[d.CarIdx] = ss;
            DrawSteer(c, xSteer, my, 14f, ss, a);
            int gear = d.CarIdx < snap.CarGear.Length ? snap.CarGear[d.CarIdx] : 0;
            text.Typeface = Fonts.CormorantGaramond.Bold; text.TextSize = 28f; text.Color = Ivory.WithAlpha(A(1));
            TextTracked.Draw(c, text, gear < 0 ? "R" : gear == 0 ? "N" : gear.ToString(CultureInfo.InvariantCulture), xGear, my + 9f, 0f);
            text.TextAlign = SKTextAlign.Left;
            // our pedals
            if (me && snap.Onboard.IsPlayerCar && snap.Onboard.HasPedals)
            {
                Bar(c, xPed, my - 6f, 120f, snap.Onboard.Throttle, GreenHi, a);
                Bar(c, xPed, my + 6f, 120f, snap.Onboard.Brake, RedHi, a);
            }
        }
        c.RestoreToCount(save);
    }

    void DrawSteer(SKCanvas c, float cx, float cy, float rr, float steerRad, float a)
    {
        stroke.Shader = null; stroke.StrokeWidth = 1.2f; stroke.Color = new SKColor(255, 255, 255, (byte)(60 * a));
        c.DrawArc(new SKRect(cx - rr, cy - rr, cx + rr, cy + rr), 200f, 140f, false, stroke);
        c.Save(); c.Translate(cx, cy); c.RotateDegrees((float)(-steerRad * 180.0 / Math.PI));
        mark.Reset(); mark.MoveTo(0, -rr - 3.5f); mark.LineTo(-3.4f, -rr + 3f); mark.LineTo(3.4f, -rr + 3f); mark.Close();
        fill.Shader = null; fill.Color = Gold.WithAlpha((byte)(255 * a)); c.DrawPath(mark, fill);
        c.Restore();
    }

    void Bar(SKCanvas c, float x, float y, float w, float v, SKColor col, float a)
    {
        fill.Shader = null; fill.Color = new SKColor(255, 255, 255, (byte)(22 * a));
        c.DrawRoundRect(new SKRect(x, y - 2f, x + w, y + 2f), 2f, 2f, fill);
        float fx = x + w * Math.Clamp(v, 0f, 1f);
        if (fx > x + 1f) { fill.Color = col.WithAlpha((byte)(255 * a)); c.DrawRoundRect(new SKRect(x, y - 2f, fx, y + 2f), 2f, 2f, fill); }
    }

    static SKColor Hex(string hex, byte alpha = 255)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) return new SKColor(255, 255, 255, alpha);
        return new SKColor(Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16), alpha);
    }
}
