using System.Globalization;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>2026-11-06: what the viewer-bet card shows (set by the host's bet engine, read on the render thread).</summary>
public sealed record BetView(
    int Id,
    string State,              // "open" | "locked" | "result" | "cancelled"
    string Question,           // "NEXT LAP UNDER 1:44.9?"
    string[] Options,          // "YES", "NO"
    int[] Pct,                 // share of the points on each option (0-100), empty = not known
    int Bettors,
    double SecsLeft,           // seconds until betting closes (open); &gt; 600 = open until the green flag
    float LeftFrac,            // share of the betting time still left (the countdown bar)
    int Winner,                // result: the winning option
    string How);               // "!bet 1 / !bet 2" or "Twitch Predictions"

public static class BetBoard
{
    static volatile BetView? current;
    public static BetView? Current { get => current; set => current = value; }
}

/// <summary>The viewer-bet card: top right under the latest-message plate. Open: the question, both options with how the
/// points are split, how to bet and a countdown bar. Locked: "BETS LOCKED" for a few seconds. Result: the winning option
/// lit gold for 9 s.</summary>
public sealed class BetCardRenderer
{
    public const float W = 420f, H = 128f, Right = 1896f, Top = 104f;
    public static SKRect Rect => new(Right - W, Top, Right, Top + H);
    /// <summary>2026-11-06: where the card wants its top edge (the compositor moves it below the latest-message plate and the
    /// battle map when they are up, so it never covers them); it glides there.</summary>
    public float WantTop = Top;
    float topNow = Top;
    float k; double lastMs = -1; BetView? shown;
    public bool Active => k > 0.002f;
    public bool Fading => k > 0.002f && k < 0.998f;
    public SKRect LastRect { get; private set; }
    string lastSig = ""; double busyUntil;
    /// <summary>Keep presenting: fading, betting open (the countdown moves), or something just changed.</summary>
    public bool NeedsFrames(double nowMs) => Fading || nowMs < busyUntil || (Active && shown?.State == "open");

    readonly SKPaint eyeP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 10f };
    readonly SKPaint qP = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 24f };
    readonly SKPaint optP = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 17f };
    readonly SKPaint pctP = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 17f, TextAlign = SKTextAlign.Right };
    readonly SKPaint smallP = new() { IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 11f };
    readonly SKPaint smallR = new() { IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 11f, TextAlign = SKTextAlign.Right };
    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint shaded = new() { IsAntialias = false };
    readonly SKPaint measureP = new() { IsAntialias = true };
    readonly ShaderSlot glassSh = new(), lineSh = new(), bloomSh = new();

    static SKColor GoldHi => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xFF, 0xD8, 0x76) : Theme.Current.Accent;
    static SKColor GoldMid => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xC9, 0xA9, 0x61) : Theme.Current.Accent;
    static SKColor GoldLo => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xB8, 0x96, 0x4C) : Theme.Current.Accent;
    static readonly float[] StopsLine = { 0f, 0.55f, 1f };
    static readonly SKColor Ivory = new(252, 250, 245), Txt2 = new(0xD3, 0xD9, 0xE3), Lane = new(255, 255, 255, 20);

    /// <summary>Advances the fade; <paramref name="wanted"/> = the screen has room (the compositor's gates).</summary>
    public void Tick(bool wanted, double nowMs)
    {
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;
        var v = BetBoard.Current;
        string sig = v == null ? "" : $"{v.Id}:{v.State}:{v.Bettors}:{string.Join(",", v.Pct)}";
        if (sig != lastSig) { lastSig = sig; busyUntil = nowMs + 900; }
        if (v != null && (shown == null || v.Id == shown.Id || k <= 0.002f)) shown = v;
        bool up = wanted && v != null && shown != null && v.Id == shown.Id && v.State != "cancelled";
        float target = up ? 1f : 0f;
        k += (target > k ? 1f : -1f) * (float)(dt / (target > k ? 420.0 : 320.0));
        k = Math.Clamp(k, 0f, 1f);
    }

    float Measure(string s, SKPaint p) { measureP.Typeface = p.Typeface; measureP.TextSize = p.TextSize; return measureP.MeasureText(s); }

    public void Draw(SKCanvas c, double nowMs)
    {
        var v = shown;
        if (k <= 0.002f || v == null) { LastRect = SKRect.Empty; return; }
        float e = k * k * (3f - 2f * k);
        if (k < 0.05f) topNow = WantTop;                        // appearing: start in the right place
        else topNow += (WantTop - topNow) * 0.18f;              // already up: glide
        if (Math.Abs(WantTop - topNow) < 0.5f) topNow = WantTop;
        else busyUntil = Math.Max(busyUntil, nowMs + 120);     // keep frames coming while it moves
        var r = new SKRect(Right - W, topNow, Right, topNow + H);
        LastRect = r;
        byte A(float x) => (byte)Math.Clamp(255f * x * e, 0f, 255f);
        int save = c.Save();
        c.Translate(r.Left + (1f - e) * 14f, r.Top);
        // deep glass, gold bloom, gold top line
        shaded.Shader = glassSh.Linear(new SKPoint(0, 0), new SKPoint(0, H), [new SKColor(14, 20, 34, 230), new SKColor(8, 12, 22, 240)], default);
        shaded.Color = new SKColor(0, 0, 0, A(1f));
        c.DrawRect(new SKRect(0, 0, W, H), shaded);
        shaded.Shader = bloomSh.Radial(new SKPoint(W, 0), 260f, [GoldMid.WithAlpha(30), GoldMid.WithAlpha(0)], default);
        c.DrawRect(new SKRect(0, 0, W, H), shaded);
        shaded.Shader = null;
        Theme.Skin(c, new SKRect(0, 0, W, H), ThemeSurface.Card, nowMs, e);
        shaded.Shader = lineSh.Linear(new SKPoint(0, 0), new SKPoint(W, 0), [GoldLo, GoldHi, GoldHi.WithAlpha(0)], StopsLine);
        c.DrawRect(new SKRect(0, 0, W, 2f), shaded);
        shaded.Shader = null;

        // eyebrow: VIEWER BET  ·  state on the right
        eyeP.Color = GoldHi.WithAlpha(A(0.95f));
        TextBlobs.DrawTracked(c, "VIEWER BET", 16f, 20f, 2f, eyeP);
        string right = v.State switch
        {
            "open" => v.SecsLeft > 600 ? "BETTING OPEN" : Math.Max(0, (int)Math.Ceiling(v.SecsLeft)).ToString(CultureInfo.InvariantCulture) + " S TO BET",
            "locked" => "BETS LOCKED",
            "result" => "RESULT",
            _ => "",
        };
        float rw = TextBlobs.TrackedWidth(right, eyeP, 2f);
        eyeP.Color = (v.State == "open" ? Ivory : GoldHi).WithAlpha(A(0.85f));
        TextBlobs.DrawTracked(c, right, W - 16f - rw, 20f, 2f, eyeP);

        // the question, fitted to one line
        qP.TextSize = 24f;
        string q = v.Question.ToUpperInvariant();
        float qw = Measure(q, qP);
        if (qw > W - 32f) qP.TextSize = Math.Max(16f, 24f * (W - 32f) / qw);
        qP.Color = Ivory.WithAlpha(A(1f));
        TextBlobs.Draw(c, q, 16f, 47f, qP);

        // the options: a lane each, filled to its share of the points, the winner lit gold
        int n = Math.Min(2, v.Options.Length);
        float laneW = (W - 32f - 10f) / 2f;
        for (int i = 0; i < n; i++)
        {
            float x = 16f + i * (laneW + 10f), y = 58f, h = 30f;
            bool win = v.State == "result" && v.Winner == i;
            bool lose = v.State == "result" && v.Winner != i;
            fill.Color = new SKColor(255, 255, 255, A(lose ? 0.04f : 0.08f));
            c.DrawRect(new SKRect(x, y, x + laneW, y + h), fill);
            int pct = i < v.Pct.Length ? v.Pct[i] : -1;
            if (pct > 0)
            {
                fill.Color = (win ? GoldHi : i == 0 ? new SKColor(0x3F, 0x8C, 0xFF) : new SKColor(0xE0, 0x4F, 0x8B)).WithAlpha(A(win ? 0.55f : lose ? 0.12f : 0.32f));
                c.DrawRect(new SKRect(x, y, x + laneW * pct / 100f, y + h), fill);
            }
            if (win) { fill.Color = GoldHi.WithAlpha(A(1f)); c.DrawRect(new SKRect(x, y + h - 2f, x + laneW, y + h), fill); }
            optP.Color = (win ? GoldHi : lose ? Txt2.WithAlpha(150) : Ivory).WithAlpha(A(lose ? 0.55f : 1f));
            string label = (v.State == "open" ? (i + 1).ToString(CultureInfo.InvariantCulture) + "  " : "") + v.Options[i].ToUpperInvariant();
            TextBlobs.Draw(c, label, x + 10f, y + 21f, optP);
            if (pct >= 0)
            {
                pctP.Color = (win ? GoldHi : Ivory).WithAlpha(A(lose ? 0.5f : 0.95f));
                TextBlobs.Draw(c, pct.ToString(CultureInfo.InvariantCulture) + "%", x + laneW - 10f, y + 21f, pctP);
            }
        }

        // how to bet / the result line
        string foot = v.State switch
        {
            "open" => v.How,
            "locked" => v.Bettors > 0 ? v.Bettors.ToString(CultureInfo.InvariantCulture) + (v.Bettors == 1 ? " viewer is in" : " viewers are in") : "Waiting for the result",
            "result" => v.Bettors > 0 && v.Winner < v.Pct.Length ? v.Pct[v.Winner].ToString(CultureInfo.InvariantCulture) + "% of the points called it" : "",
            _ => "",
        };
        smallP.Color = Txt2.WithAlpha(A(0.9f));
        TextBlobs.Draw(c, foot, 16f, 108f, smallP);
        if (v.State != "result" && v.Bettors > 0)
        {
            smallR.Color = Txt2.WithAlpha(A(0.7f));
            TextBlobs.Draw(c, v.Bettors.ToString(CultureInfo.InvariantCulture) + " IN", W - 16f, 108f, smallR);
        }
        // countdown bar along the bottom while betting is open
        if (v.State == "open")
        {
            float left = Math.Clamp(v.LeftFrac, 0f, 1f);
            fill.Color = GoldHi.WithAlpha(A(0.9f));
            c.DrawRect(new SKRect(0, H - 3f, W * left, H), fill);
        }
        c.RestoreToCount(save);
    }
}
