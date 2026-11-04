using System.Collections.Concurrent;
using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Team radio captions (2026-10-24, Angelo): what the engineer, strategist and race director say,
/// shown F1-TV style in Grandstand's own look - deep glass, class-colour top line, white number chip,
/// Cormorant driver name, Inter labels, manufacturer logo - and WHO the radio is to (the driver),
/// with a small live "voice" meter while it's on. The race engineer app sends each line to the
/// control server (POST /api/radio); lines queue and show one at a time.
/// </summary>
public static class RadioFeed
{
    public sealed record Line(string Who, string Text);
    static readonly ConcurrentQueue<Line> incoming = new();

    public static void Push(string who, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // #619: race control goes to its own bar at the top of the screen while that is on (the radio card stays team radio)
        if (RaceControlBar.On && string.Equals(who.Trim(), "RACE CONTROL", StringComparison.OrdinalIgnoreCase)) { RaceControlBar.PushSpoken(text); return; }
        if (incoming.Count > 6) incoming.TryDequeue(out _);    // never pile up a backlog
        incoming.Enqueue(new Line(who.Trim().ToUpperInvariant(), text.Trim()));
    }

    internal static bool TryTake(out Line? l) { bool ok = incoming.TryDequeue(out var x); l = x; return ok; }
    internal static bool Waiting => !incoming.IsEmpty;
}

public sealed class RadioPopupRenderer
{
    public const float Width = 330f;   // near-square (Angelo 2026-10-24: "way too long horizontally")
    const double InMs = 320, OutMs = 360, MinMs = 3800, MsPerChar = 55, MaxMs = 9500, BusyMinMs = 1600;

    static readonly SKColor Glass = Hex(BroadcastPalette.TowerGlassBase, 214);
    static readonly SKColor Ivory = Hex(BroadcastPalette.TextPrimary);
    static readonly SKColor Second = Hex(BroadcastPalette.TextSecondary);
    static readonly SKColor Tert = Hex(BroadcastPalette.TextTertiary);
    static readonly SKColor Gold = Hex(BroadcastPalette.Leader);
    static readonly SKColor Hair = Hex(BroadcastPalette.BorderHairline, 120);

    RadioFeed.Line? cur;
    double startMs = -1, holdMs;
    // live caption (2026-10-23, Angelo: "figure out what he says"): race control lines can carry timing cues
    // "[[0]]8, 7, 6...[[9]]Pit exit is now open.[[end=14.6]]" - each sentence shows as he says it
    List<(double Ms, string Text)>? cues;
    double cueEndMs;

    static List<(double, string)>? ParseCues(string text, out double endMs)
    {
        endMs = 0;
        if (!text.Contains("[[")) return null;
        var list = new List<(double, string)>();
        foreach (var part in text.Split("[[", StringSplitOptions.RemoveEmptyEntries))
        {
            int k = part.IndexOf("]]", StringComparison.Ordinal);
            if (k < 0) continue;
            string tag = part[..k].Trim(), body = part[(k + 2)..].Trim();
            if (tag.StartsWith("end=", StringComparison.Ordinal))
            { if (double.TryParse(tag[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var e)) endMs = e * 1000; continue; }
            if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && body.Length > 0)
                list.Add((sec * 1000, body));
        }
        if (list.Count == 0) return null;
        if (endMs <= list[^1].Item1) endMs = list[^1].Item1 + 2500;
        return list;
    }
    readonly Dictionary<string, SKBitmap?> logos = new(StringComparer.OrdinalIgnoreCase);

    public bool IsActive(double nowMs) { Advance(nowMs); return cur != null; }

    void Advance(double nowMs)
    {
        // races: nothing before the green flag (Angelo 2026-10-24) - lines said on the grid are dropped,
        // not saved up; practice / qualifying: always (same gate as the other event callouts)
        // race control is the exception (2026-10-23): "go to the grid" and "formation lap" ARE the pre-green moments
        bool armed = PresentationState.NotificationsArmed;
        if (!armed && cur != null && cur.Who != "RACE CONTROL" && nowMs - startMs < holdMs) holdMs = Math.Max(0, nowMs - startMs);
        // #674 (2026-10-31, Angelo: "the audio from the radio calls being ahead of the text popup ... during heavy
        // moments"): each card held 3.8-9.5 s, so in a burst of calls every new card waited behind the last while its
        // voice was already playing (the engineer sends each line the moment it starts talking). Now, when the next
        // line is already waiting, the card on screen leaves after 1.6 s - the captions stay with the voice.
        if (cur != null && cues == null && RadioFeed.Waiting && nowMs - startMs > BusyMinMs && nowMs - startMs < holdMs)
            holdMs = nowMs - startMs;
        if (cur != null && nowMs - startMs > holdMs + OutMs) cur = null;
        while (cur == null && RadioFeed.TryTake(out var l) && l != null)
        {
            if (!armed && l.Who != "RACE CONTROL") continue;     // team radio said on the grid: dropped, not saved up
            cur = l; startMs = nowMs;
            cues = ParseCues(l.Text, out cueEndMs);
            if (cues != null) holdMs = cueEndMs + 700;          // stays up exactly as long as he talks
            else
            {
                holdMs = Math.Clamp(MinMs + l.Text.Length * MsPerChar, MinMs, MaxMs);
                if (RadioFeed.Waiting) holdMs = Math.Max(MinMs, holdMs * 0.7);
            }
        }
    }

    /// <summary>2026-10-27: height of the card drawn last frame (0 when nothing is up), so other cards can stack above it.</summary>
    public float LastHeight { get; private set; }

    public void Draw(SKCanvas c, float right, float bottom, RaceSnapshot snap, double nowMs)
    {
        Advance(nowMs);
        if (cur == null) { LastHeight = 0; return; }
        double t = nowMs - startMs;
        float a = (float)Math.Clamp(Math.Min(t / InMs, (holdMs + OutMs - t) / OutMs), 0, 1);
        a = a * a * (3 - 2 * a);
        if (a <= 0.01f) return;
        bool toAll = cur.Who == "RACE CONTROL";
        if (toAll) { DrawRaceControl(c, right, bottom, t, a, nowMs); return; }
        var d = toAll ? null : snap.Player;

        using var quoteP = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 17f, Color = Second.WithAlpha((byte)(255 * a)) };
        var rows = Wrap("\u201C" + cur.Text + "\u201D", quoteP, Width - 44f, 5);
        float headH = toAll ? 104f : 150f;   // race control: no driver row
        float h = headH + rows.Count * 23f + 16f;
        LastHeight = h;
        float slide = (1 - a) * 28f;
        var card = new SKRect(right - Width + slide, bottom - h, right + slide, bottom);

        var cls = d == null ? Gold : Hex(string.IsNullOrEmpty(d.ClassColor) ? BroadcastPalette.Class(d.ClassName) : d.ClassColor);
        if (cls.Red + cls.Green + cls.Blue < 60) cls = Ivory;   // a black class (GTP) can't be a line on dark glass

        // glass + class-colour top line with a soft glow (featured-card treatment)
        using (var g = new SKPaint { IsAntialias = true, Color = Glass.WithAlpha((byte)(Glass.Alpha * a)) }) c.DrawRect(card, g);
        Theme.Skin(c, card, ThemeSurface.Popup, nowMs, a);   // #601: the broadcast theme (nothing on Default)
        using (var glowSh = SKShader.CreateLinearGradient(new SKPoint(0, card.Top), new SKPoint(0, card.Top + 12f),
                   new[] { cls.WithAlpha((byte)(130 * a)), cls.WithAlpha(0) }, new[] { 0f, 1f }, SKShaderTileMode.Clamp))
        using (var glow = new SKPaint { Shader = glowSh }) c.DrawRect(new SKRect(card.Left, card.Top, card.Right, card.Top + 12f), glow);
        using (var line = new SKPaint { Color = cls.WithAlpha((byte)(255 * a)) }) c.DrawRect(new SKRect(card.Left, card.Top, card.Right, card.Top + 1.5f), line);

        // Team radio's own signature (2026-10-24, "distinct but at that level"): a warm gold bloom from the
        // top-left, the car number huge and ghosted behind the name, a class-colour spine down the left edge,
        // and a live voice waveform running along the card while the line is on air.
        using (var bSh = SKShader.CreateRadialGradient(new SKPoint(card.Left + 30f, card.Top + 20f), Width * 0.9f,
                   new[] { Gold.WithAlpha((byte)(70 * a)), Gold.WithAlpha((byte)(18 * a)), Gold.WithAlpha(0) },
                   new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp))
        using (var bp = new SKPaint { Shader = bSh }) c.DrawRect(card, bp);
        if (d != null && !string.IsNullOrEmpty(d.CarNumber))
        {
            c.Save(); c.ClipRect(card);
            using var gp = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.ExtraBold, TextSize = 150f,
                Color = SKColors.White.WithAlpha((byte)(16 * a)), TextAlign = SKTextAlign.Right };
            BigText.Draw(c, d.CarNumber, card.Right + 8f, card.Top + 132f, gp);   // drawn once, reused (4K perf)
            c.Restore();
        }
        using (var sp = new SKPaint { Color = cls.WithAlpha((byte)(255 * a)) })
            c.DrawRect(new SKRect(card.Left, card.Top, card.Left + 4f, card.Bottom), sp);
        DrawWave(c, card.Left + 22f, card.Right - 20f, card.Top + headH - 18f, t, holdMs, a, cls);

        float x = card.Left + 22f;
        // row 1: who is speaking (gold, Angelo: "ENGINEER" / "RACE CONTROL") with the live voice meter at the right
        // row 2 (team radio only): "TEAM RADIO" - race control talks to everyone, so its card is shorter
        // 2026-10-25 (Angelo: "when I say something I want those words to go on the team radio thing on the broadcast"):
        // the driver's own lines - "DRIVER" in his class colour (the pit wall's lines stay gold), same card
        string role = cur.Who switch { "STRATEGIST" => "STRATEGIST", "RACE CONTROL" => "RACE CONTROL", "DRIVER" => "DRIVER", _ => "ENGINEER" };
        var roleCol = cur.Who == "DRIVER" ? cls : Gold;
        using (var rp = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14f, Color = roleCol.WithAlpha((byte)(255 * a)) })   // #340: was 12
            TextTracked.Draw(c, rp, role, x, card.Top + 27f, 2.0f);
        DrawMeter(c, card.Right - 20f - 23f, card.Top + 27f, t, holdMs, a, cls);
        if (!toAll)
            using (var eb = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 13f, Color = Tert.WithAlpha((byte)(255 * a)) })   // #340: was 10
                TextTracked.Draw(c, eb, "TEAM RADIO", x, card.Top + 46f, 1.7f);

        // who it's to: number chip + maker logo on one line, the name underneath (shrinks to fit)
        float chipY = card.Top + 74f;
        if (d != null)
        {
            string num = string.IsNullOrEmpty(d.CarNumber) ? "—" : d.CarNumber;
            using var nt = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 15f, Color = new SKColor(8, 10, 16, (byte)(242 * a)) };
            float cw = Math.Max(40f, nt.MeasureText(num) + 16f);
            var chip = new SKRect(x, chipY - 13f, x + cw, chipY + 12f);
            using (var cb = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a)) }) c.DrawRect(chip, cb);
            nt.GetFontMetrics(out var nm);
            c.DrawText(num, chip.MidX - nt.MeasureText(num) / 2f, chip.MidY - (nm.Ascent + nm.Descent) / 2f, nt);
            var logo = Logo(d.CarBrand);
            if (logo != null)
            {
                float sc = Math.Min(52f / logo.Width, 30f / logo.Height);
                var dst = SKRect.Create(card.Right - 20f - logo.Width * sc, chipY - logo.Height * sc / 2f, logo.Width * sc, logo.Height * sc);
                using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High,
                    ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(255, 255, 255, (byte)(235 * a)), SKBlendMode.DstIn) };
                c.DrawBitmap(logo, dst, lp);
            }
        }
        string name = d == null ? "ALL CARS" : NameFormat.Broadcast(d).ToUpperInvariant();
        using (var np = new SKPaint { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 30f, Color = Ivory.WithAlpha((byte)(255 * a)) })
        {
            float maxW = Width - 44f;
            float nw = np.MeasureText(name);
            if (nw > maxW) np.TextSize = Math.Max(18f, 30f * maxW / nw);   // long names shrink instead of widening the card
            c.DrawText(name, x, card.Top + (d == null ? 70f : 120f), np);
        }

        // the words (the waveform above them replaces the hairline)
        float y = card.Top + headH + 4f;
        foreach (var r in rows) { c.DrawText(r, card.Left + 22f, y, quoteP); y += 23f; }
    }

    // Race control (2026-10-24, Angelo: "very uniquely identifiable as not a team radio - an iRacing logo,
    // colours like the splash screen"): the tower-splash surface - deep glass, a soft bloom, a slow
    // light sweep, hairlines top and bottom - with the iRacing wordmark and "RACE CONTROL".
    SKBitmap? irLogo; bool irTried;
    static readonly SKColor RcBlue = new(46, 92, 255);
    void DrawRaceControl(SKCanvas c, float right, float bottom, double t, float a, double nowMs)
    {
        using var txtP = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 19f, Color = Ivory.WithAlpha((byte)(255 * a)) };
        List<string> rows;
        float textA = 1f;
        int rowSlots;
        if (cues != null)
        {
            // the sentence he is saying right now; the card keeps the height of the longest one so it never jumps
            int idx = 0;
            for (int i = 0; i < cues.Count; i++) if (t >= cues[i].Ms) idx = i;
            rows = Wrap(cues[idx].Text, txtP, Width - 44f, 4);
            rowSlots = 1;
            foreach (var q in cues) rowSlots = Math.Max(rowSlots, Wrap(q.Text, txtP, Width - 44f, 4).Count);
            textA = (float)Math.Clamp((t - cues[idx].Ms) / 220.0, 0, 1);     // each new sentence fades in
            if (idx == 0) textA = 1f;
        }
        else { rows = Wrap(cur!.Text, txtP, Width - 44f, 4); rowSlots = rows.Count; }
        float h = 78f + rowSlots * 26f + 16f;
        LastHeight = h;
        float slide = (1 - a) * 28f;
        var card = new SKRect(right - Width + slide, bottom - h, right + slide, bottom);
        using (var g = new SKPaint { Color = Hex(BroadcastPalette.TowerGlassBase, (byte)(240 * a)) }) c.DrawRect(card, g);
        Theme.Skin(c, card, ThemeSurface.Popup, nowMs, a);   // #601
        using (var bloomSh = SKShader.CreateRadialGradient(new SKPoint(card.MidX, card.Top + 20f), Width * 0.75f,
                   new[] { RcBlue.WithAlpha((byte)(110 * a)), RcBlue.WithAlpha((byte)(30 * a)), RcBlue.WithAlpha(0) },
                   new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp))
        using (var bloom = new SKPaint { Shader = bloomSh }) c.DrawRect(card, bloom);
        // slow light sweep, as on the splash (not in light mode)
        float ph = (float)((nowMs % 4500) / 4500.0), sy = Perf.Light ? -10000f : card.Top + ph * card.Height, sh = card.Height * 0.35f;
        c.Save(); c.ClipRect(card);
        using (var swSh = SKShader.CreateLinearGradient(new SKPoint(0, sy - sh), new SKPoint(0, sy + sh),
                   new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha((byte)(20 * a)), SKColors.White.WithAlpha(0) },
                   new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp))
        using (var sw = new SKPaint { Shader = swSh }) c.DrawRect(new SKRect(card.Left, sy - sh, card.Right, sy + sh), sw);
        c.Restore();
        using (var hl = new SKPaint { Color = RcBlue.WithAlpha((byte)(200 * a)) })
        {
            c.DrawRect(new SKRect(card.Left, card.Top, card.Right, card.Top + 2f), hl);
            c.DrawRect(new SKRect(card.Left, card.Bottom - 2f, card.Right, card.Bottom), hl);
        }
        float x = card.Left + 22f;
        if (!irTried)
        {
            irTried = true;
            try { var p = Assets.Resolve("logos", "iracing-brand", "iRacing-Inline-Color-White.png"); if (System.IO.File.Exists(p)) irLogo = Assets.Decode(p); }
            catch { irLogo = null; }
        }
        float lx = x;
        if (irLogo != null)
        {
            float lh = 22f, lw = irLogo.Width * lh / irLogo.Height;
            using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High,
                ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(255, 255, 255, (byte)(255 * a)), SKBlendMode.DstIn) };
            c.DrawBitmap(irLogo, SKRect.Create(x, card.Top + 16f, lw, lh), lp);
            lx = x;
        }
        using (var rp = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14f, Color = Ivory.WithAlpha((byte)(245 * a)) })   // #340: was 12 @235
            TextTracked.Draw(c, rp, "RACE CONTROL", lx, card.Top + 60f, 2.2f);
        DrawMeter(c, card.Right - 20f - 23f, card.Top + 34f, t, holdMs, a, Ivory);
        float y = card.Top + 96f + (1 - textA) * 6f;
        txtP.Color = Ivory.WithAlpha((byte)(255 * a * textA));
        foreach (var r in rows) { c.DrawText(r, x, y, txtP); y += 26f; }
    }

    // a thin voice waveform: lively while the line is on air, settling to a flat hairline after
    static void DrawWave(SKCanvas c, float x0, float x1, float y, double t, double hold, float a, SKColor col)
    {
        bool on = t < hold;
        float amp = on && !Perf.Light ? 6f : 0f;      // light mode: a still line
        if (on && t < 300) amp *= (float)(t / 300);
        using var path = new SKPath();
        int n = 64;
        for (int i = 0; i <= n; i++)
        {
            float u = i / (float)n, x = x0 + (x1 - x0) * u;
            float env = MathF.Sin(MathF.PI * u);                          // fades in and out at the ends
            double ph = t / 1000.0;
            float v = (float)(Math.Sin(u * 21 + ph * 9) * 0.55 + Math.Sin(u * 47 - ph * 13) * 0.3 + Math.Sin(u * 9 + ph * 5) * 0.25);
            float yy = y - v * amp * env;
            if (i == 0) path.MoveTo(x, yy); else path.LineTo(x, yy);
        }
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f,
            Color = (on ? col : Hair).WithAlpha((byte)((on ? 220 : 160) * a)) };
        c.DrawPath(path, p);
    }

    // five little bars that move while the line is "on air" and settle when it ends
    static void DrawMeter(SKCanvas c, float x, float baseline, double t, double hold, float a, SKColor col)
    {
        bool on = t < hold;
        using var p = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(230 * a)) };
        for (int i = 0; i < 5; i++)
        {
            double ph = t / 1000.0 * (6.0 + i * 1.7) + i * 1.3;
            float k = on ? (float)(0.35 + 0.65 * Math.Abs(Math.Sin(ph))) : 0.25f;
            float bh = 3f + 9f * k;
            c.DrawRoundRect(new SKRect(x + i * 5f, baseline - bh, x + i * 5f + 3f, baseline), 1f, 1f, p);
        }
    }

    SKBitmap? Logo(string? brand)
    {
        var slug = ManufacturerBrand.Resolve(brand);
        if (slug == null) return null;
        if (logos.TryGetValue(slug, out var b)) return b;
        try
        {
            var path = Assets.Resolve("logos", "manufacturers", slug + ".png");
            b = System.IO.File.Exists(path) ? Assets.Decode(path) : null;
        }
        catch { b = null; }
        logos[slug] = b;
        return b;
    }

    static List<string> Wrap(string text, SKPaint p, float max, int maxRows)
    {
        var rows = new List<string>();
        string line = "";
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string test = line.Length == 0 ? w : line + " " + w;
            if (p.MeasureText(test) <= max) { line = test; continue; }
            if (line.Length > 0) rows.Add(line);
            line = w;
            if (rows.Count == maxRows) break;
        }
        if (rows.Count < maxRows && line.Length > 0) rows.Add(line);
        return rows;
    }

    static SKColor Hex(string hex, byte alpha = 255)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) return new SKColor(255, 255, 255, alpha);
        return new SKColor(byte.Parse(s[..2], NumberStyles.HexNumber), byte.Parse(s[2..4], NumberStyles.HexNumber),
                           byte.Parse(s[4..6], NumberStyles.HexNumber), alpha);
    }
}
