using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>#619: what kind of race-control message a bar carries - picks its accent strip and tag.</summary>
public enum RcBarKind { Info, Yellow, Blue, TrackLimits, Penalty, BlackFlag }

/// <summary>
/// #619 (2026-10-30, Angelo: "race control ... shows up over the top of the radio frequently. Maybe make that a bar at
/// the top of the screen that plays under the session bar"): race control as a slim bar in the session bar's family -
/// the same deep glass, the same fading gold hairline, the bar's width - that slides down from under whatever hangs from
/// the top of the screen:
/// <list type="bullet">
/// <item>Regular: straight under the session bar plate (y 110 in 1080 units; the watch's bottom tip just rests on it).</item>
/// <item>Battle: under the standings ticker (y 154).</item>
/// <item>Onboard: under the corner / straight name (its live rect, <see cref="OnboardFullRenderer.PlaceBottom"/>).</item>
/// </list>
/// If the context changes while it is up, it glides to the new spot. A coloured strip at its left edge says what kind of
/// message it is (black flag, penalty, track limits, blue, yellow, info) next to "RACE CONTROL" and a tag; then the car
/// (number chip + driver) when there is one, the message, and either the car's incident points or race control's little
/// voice meter. Both race-control sources come here while it is on (control.json <c>raceControlBar</c>, ON by default):
/// the race engineer app's RACE CONTROL lines (POST /api/radio - same timing, live captions sentence by sentence, the
/// same pre-green exception) and the W12 decisions (same gates); the old radio-column cards are the fallback when off.
/// It is a separate queue from the team radio, so the two can be up together - the bar never reaches the radio column.
/// <para>Cost: one message is laid out when it starts (fitted size, cut text, widths); every frame draws kept paints,
/// kept shaders in the bar's own coordinates (fades go through the paint's alpha) and cached text blobs. One clip, no
/// layers, nothing allocated per frame.</para>
/// </summary>
public sealed class RaceControlBar : IDisposable
{
    /// <summary>control.json raceControlBar (ON by default). Off: the old popup / W12 card play as before.</summary>
    public static volatile bool On = true;
    public const float W = 976f, H = 44f;   // the session bar plate's width
    public const double InMs = 420, OutMs = 340, MinMs = 3800, MsPerChar = 55, MaxMs = 9500, DecisionMs = 8500;
    const float Pad = 18f, MsgMax = 16f, MsgMin = 12.5f;

    sealed class Msg
    {
        public RcBarKind Kind;
        public string Tag = "", Car = "", Driver = "", Right = "", ClassColor = "";
        public string[] Texts = Array.Empty<string>();
        public double[] At = Array.Empty<double>();
        public float[] Sizes = Array.Empty<float>();
        public double Hold;
        public bool Spoken;
        public string Raw = "";
        public float TagW, CarW, DriverSize, DriverW, RightW;
    }

    static readonly ConcurrentQueue<Msg> incoming = new();
    public static bool Waiting => !incoming.IsEmpty;
    /// <summary>A message is up or waiting (set by the compositor each tick): viewer looks wait for it.</summary>
    public static volatile bool Up;
    public static Action<string>? Log;

    // ---------------------------------------------------------------- feeding

    /// <summary>A RACE CONTROL line from the race engineer app (any thread). Cue tags "[[0]]...[[9]]...[[end=14.6]]" show
    /// each sentence as it is said (as the radio card did).</summary>
    public static void PushSpoken(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (incoming.Count > 6) incoming.TryDequeue(out _);
        var m = new Msg { Spoken = true, Raw = text.Trim() };
        var cues = ParseCues(m.Raw, out double end);
        if (cues != null)
        {
            m.Texts = cues.Select(q => q.Text).ToArray(); m.At = cues.Select(q => q.Ms).ToArray();
            m.Hold = end + 700;
        }
        else
        {
            m.Texts = new[] { m.Raw }; m.At = new[] { 0.0 };
            m.Hold = Math.Clamp(MinMs + m.Raw.Length * MsPerChar, MinMs, MaxMs);
        }
        m.Kind = Classify(string.Join(' ', m.Texts));
        m.Tag = TagOf(m.Kind);
        incoming.Enqueue(m);
    }

    /// <summary>A W12 decision (render thread, from the visual pack): the car, its tag and the incident points.</summary>
    public static void Post(RcMessage r)
    {
        if (incoming.Count > 6) incoming.TryDequeue(out _);
        var kind = r.Category switch
        {
            RcCategory.Disqualified => RcBarKind.BlackFlag,
            RcCategory.Penalty => r.Text.Contains("BLACK FLAG", StringComparison.OrdinalIgnoreCase) ? RcBarKind.BlackFlag : RcBarKind.Penalty,
            RcCategory.Repair => RcBarKind.Penalty,
            RcCategory.TrackLimits or RcCategory.Warning => RcBarKind.TrackLimits,
            _ => RcBarKind.Info,
        };
        var m = new Msg
        {
            Kind = kind, Tag = r.Category switch
            {
                RcCategory.TrackLimits => "TRACK LIMITS", RcCategory.Warning => "WARNING", RcCategory.Penalty => kind == RcBarKind.BlackFlag ? "BLACK FLAG" : "PENALTY",
                RcCategory.Repair => "REPAIR", RcCategory.Disqualified => "DISQUALIFIED", RcCategory.Investigation => "INVESTIGATION",
                RcCategory.Cleared => "NO FURTHER ACTION", _ => "NOTED",
            },
            Car = r.CarNumber ?? "", Driver = (r.Driver ?? "").ToUpperInvariant(), ClassColor = r.ClassColor ?? "",
            Right = r.Incidents < 0 ? "" : r.IncidentLimit > 0 ? $"{r.Incidents}x / {r.IncidentLimit}x" : $"{r.Incidents}x",
            Texts = new[] { (r.Text ?? "").ToUpperInvariant() }, At = new[] { 0.0 }, Hold = DecisionMs, Raw = r.Text ?? "",
        };
        incoming.Enqueue(m);
    }

    /// <summary>The kind of a spoken line from its words (black flag, penalty, track limits, blue, yellow, else info).</summary>
    public static RcBarKind Classify(string text)
    {
        string t = text.ToLowerInvariant();
        if (t.Contains("black flag") || t.Contains("disqualif")) return RcBarKind.BlackFlag;
        if (t.Contains("penalt") || t.Contains("drive through") || t.Contains("drive-through") || t.Contains("stop and go") || t.Contains("stop-go") || t.Contains("stop & go")) return RcBarKind.Penalty;
        if (t.Contains("track limit") || t.Contains("off track") || t.Contains("off-track") || t.Contains("cut ") || t.Contains("incident point")) return RcBarKind.TrackLimits;
        if (t.Contains("blue flag")) return RcBarKind.Blue;
        if (t.Contains("yellow") || t.Contains("caution") || t.Contains("safety car") || t.Contains("pace car") || t.Contains("full course")) return RcBarKind.Yellow;
        return RcBarKind.Info;
    }

    static string TagOf(RcBarKind k) => k switch
    {
        RcBarKind.BlackFlag => "BLACK FLAG", RcBarKind.Penalty => "PENALTY", RcBarKind.TrackLimits => "TRACK LIMITS",
        RcBarKind.Blue => "BLUE FLAG", RcBarKind.Yellow => "YELLOW", _ => "INFO",
    };

    /// <summary>The accent strip's colour per kind (black flag: near-black with an ivory edge, drawn specially).</summary>
    public static SKColor AccentOf(RcBarKind k) => k switch
    {
        RcBarKind.BlackFlag => new SKColor(0x0A, 0x0B, 0x0E),
        RcBarKind.Penalty => Vp.LiveRed,
        RcBarKind.TrackLimits => new SKColor(0xFF, 0xB8, 0x70),
        RcBarKind.Blue => new SKColor(0x3B, 0x82, 0xF6),
        RcBarKind.Yellow => new SKColor(0xFF, 0xC1, 0x07),
        _ => Vp.RcBlue,
    };

    static List<(double Ms, string Text)>? ParseCues(string text, out double endMs)
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

    // ---------------------------------------------------------------- state

    Msg? cur;
    double startMs = -1;
    float curTop = float.NaN, curX = float.NaN, curBase = float.NaN; double lastMs = -1;

    /// <summary>Up (or fading out) right now.</summary>
    public bool IsActive(double nowMs) { Advance(nowMs); return cur != null; }
    /// <summary>The bar's rect as drawn last frame (1080 units), empty when it isn't up.</summary>
    public SKRect LastRect { get; private set; }
    /// <summary>Where the bar would sit right now if a message came (for the cam placer's look-ahead).</summary>
    public SKRect NextRect { get; private set; }

    void Advance(double nowMs)
    {
        if (cur != null && nowMs - startMs > cur.Hold + OutMs) cur = null;
        while (cur == null && incoming.TryDequeue(out var m))
        {
            if (m.Spoken && m.At.Length == 1 && Waiting) m.Hold = Math.Max(MinMs, m.Hold * 0.7);
            Prepare(m);
            cur = m; startMs = nowMs;
            Log?.Invoke($"race control bar: {m.Tag}: {m.Raw}");
        }
    }

    /// <summary>A message about to be cut short (the screen is being cleared): plays its exit now.</summary>
    public void Hide(double nowMs) { if (cur != null && nowMs - startMs < cur.Hold) cur.Hold = Math.Max(0, nowMs - startMs); }

    // ---------------------------------------------------------------- layout (once per message)

    readonly SKPaint labelP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 11.5f };
    readonly SKPaint tagP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 10.5f };
    readonly SKPaint msgP = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = MsgMax };
    readonly SKPaint carP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 12.5f };
    readonly SKPaint drvP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };
    readonly SKPaint rightP = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 17f };
    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint shaded = new() { IsAntialias = false };
    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f };
    readonly ShaderSlot glassSh = new(), hairSh = new(), glowSh = new(), shadowSh = new();
    static readonly float[] StopsHair = { 0f, 0.18f, 0.82f, 1f };   // (a span of constants made per call allocates; a kept array doesn't)
    const string Label = "RACE CONTROL";
    const float LabelTrack = 2.2f, TagTrack = 1.8f;
    float labelW;

    void Prepare(Msg m)
    {
        if (labelW <= 0) labelW = TextBlobs.TrackedWidth(Label, labelP, LabelTrack);
        m.TagW = TextBlobs.TrackedWidth(m.Tag, tagP, TagTrack) + 18f;
        float x = Pad + labelW + 14f + m.TagW + 16f;
        if (m.Car.Length > 0)
        {
            m.CarW = Math.Max(30f, TextBlobs.Measure(m.Car, carP) + 14f);
            m.DriverSize = 13f;
            float dw = Measure(m.Driver, Fonts.Inter.Bold, 13f);
            if (dw > 170f) { m.DriverSize = Math.Max(10f, 13f * 170f / dw); }
            m.DriverW = Measure(m.Driver, Fonts.Inter.Bold, m.DriverSize);
            x += m.CarW + 8f + 3f + 8f + m.DriverW + 16f;
        }
        m.RightW = m.Right.Length > 0 ? Measure(m.Right, Fonts.BarlowCondensed.Bold, 17f) + Pad : 5 * 5f + Pad;
        float avail = W - x - m.RightW - 14f;
        m.Sizes = new float[m.Texts.Length];
        for (int i = 0; i < m.Texts.Length; i++)
        {
            string s = m.Texts[i];
            float size = MsgMax, w = Measure(s, Fonts.Inter.SemiBold, size);
            if (w > avail) { size = Math.Max(MsgMin, size * avail / w); w = Measure(s, Fonts.Inter.SemiBold, size); }
            if (w > avail)
            {
                // still too long at the smallest size: cut at a word and add an ellipsis
                var words = s.Split(' ');
                string t = "";
                foreach (var wd in words)
                {
                    string test = t.Length == 0 ? wd : t + " " + wd;
                    if (Measure(test + "…", Fonts.Inter.SemiBold, size) > avail) break;
                    t = test;
                }
                s = (t.Length > 0 ? t : s[..Math.Min(s.Length, 20)]) + "…";
            }
            m.Texts[i] = s; m.Sizes[i] = size;
        }
    }

    readonly SKPaint measureP = new() { IsAntialias = true };
    float Measure(string s, SKTypeface tf, float size) { measureP.Typeface = tf; measureP.TextSize = size; return measureP.MeasureText(s); }

    // ---------------------------------------------------------------- drawing

    /// <summary>Draws the bar. <paramref name="anchorTop"/> = the bottom of whatever it hangs from (session bar plate, ticker
    /// or onboard place label); <paramref name="cx"/> its centre line; <paramref name="regularK"/> 1 while the session bar's
    /// watch rests on it (the text sits a touch lower so the watch's tip never touches a letter); <paramref name="maxRight"/>
    /// = the bar never reaches past this (the radio column).</summary>
    public void Draw(SKCanvas c, float cx, float anchorTop, float regularK, float maxRight, double nowMs)
    {
        Advance(nowMs);
        float wantX = cx - W / 2f;
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;
        NextRect = new SKRect(wantX, anchorTop, wantX + W, anchorTop + H);
        if (cur == null) { LastRect = SKRect.Empty; curTop = float.NaN; return; }
        double t = nowMs - startMs;
        // position: glide to a new context (session bar -> ticker -> onboard label) instead of jumping
        float baseTarget = 26f + 4f * regularK;
        if (float.IsNaN(curTop) || t < 1) { curTop = anchorTop; curX = wantX; curBase = baseTarget; }
        else
        {
            float k = (float)Math.Min(1.0, dt / 120.0);
            curTop += (anchorTop - curTop) * k; curX += (wantX - curX) * k; curBase += (baseTarget - curBase) * k;
            if (Math.Abs(curTop - anchorTop) < 0.05f) curTop = anchorTop;
            if (Math.Abs(curX - wantX) < 0.05f) curX = wantX;
        }
        float inK = Vp.EaseOutExpo(Vp.Seg(t, 0, InMs));
        float outK = Vp.EaseIn(Vp.Seg(t, cur.Hold, cur.Hold + OutMs));
        float a = Math.Min(1f, inK * 1.25f) * (1f - outK);
        if (a <= 0.004f) { LastRect = SKRect.Empty; return; }
        float slide = (1f - inK) * H + outK * H * 0.6f;
        float x0 = curX, top = curTop;
        float w = Math.Min(W, maxRight - x0);
        if (w < 200f) { LastRect = SKRect.Empty; return; }
        LastRect = new SKRect(x0, top, x0 + w, top + H);

        int save = c.Save();
        c.ClipRect(new SKRect(x0 - 2f, top, x0 + w + 2f, top + H + 24f));   // slides down out of whatever it hangs from
        c.Translate(x0, top - slide);
        byte A(float k) => (byte)Math.Clamp(255f * k * a, 0f, 255f);

        // shadow under the bar (a short gradient, no blur)
        shaded.Shader = shadowSh.Linear(new SKPoint(0, H), new SKPoint(0, H + 10f), [new SKColor(0, 0, 0, 90), new SKColor(0, 0, 0, 0)], default);
        shaded.Color = new SKColor(0, 0, 0, A(1f));
        c.DrawRect(new SKRect(0, H, w, H + 10f), shaded);
        // glass: the session bar's deep glass, a touch darker towards the bottom
        shaded.Shader = glassSh.Linear(new SKPoint(0, 0), new SKPoint(0, H), [new SKColor(14, 20, 34, 230), new SKColor(8, 12, 22, 236)], default);
        c.DrawRect(new SKRect(0, 0, w, H), shaded);
        shaded.Shader = null;
        Theme.Skin(c, new SKRect(0, 0, w, H), ThemeSurface.Popup, nowMs, a);   // #601: the broadcast theme (nothing on Default)
        // the kind's colour: a strip at the left edge and a soft wash from it
        var acc = AccentOf(cur.Kind);
        var wash = cur.Kind == RcBarKind.BlackFlag ? Vp.Ivory : acc;
        shaded.Shader = glowSh.Linear(new SKPoint(0, 0), new SKPoint(220f, 0), [wash.WithAlpha(46), wash.WithAlpha(0)], default);
        shaded.Color = new SKColor(0, 0, 0, A(1f));
        c.DrawRect(new SKRect(0, 0, 220f, H), shaded);
        shaded.Shader = null;
        fill.Color = acc.WithAlpha(A(1f));
        c.DrawRect(new SKRect(0, 0, 5f, H), fill);
        if (cur.Kind == RcBarKind.BlackFlag)
        {
            fill.Color = Vp.Ivory.WithAlpha(A(0.85f));
            c.DrawRect(new SKRect(5f, 0, 6.2f, H), fill);
        }
        // the session bar's bottom hairline (gold, fading at both ends), on this bar's bottom edge
        var gold = Theme.AccentOr(new SKColor(0xC5, 0xA5, 0x72), ThemeSurface.Popup);
        shaded.Shader = hairSh.Linear(new SKPoint(0, 0), new SKPoint(w, 0),
            [gold.WithAlpha(0), gold.WithAlpha(150), gold.WithAlpha(150), gold.WithAlpha(0)], StopsHair);
        c.DrawRect(new SKRect(0, H - 1f, w, H), shaded);
        shaded.Shader = null;

        // text: fades in a beat after the glass
        float ta = a * Vp.Seg(t, 120, 360);
        float by = curBase;
        float x = Pad;
        labelP.Color = Vp.Ivory.WithAlpha(A(0.92f * ta / Math.Max(a, 0.001f)));
        TextBlobs.DrawTracked(c, Label, x, by, LabelTrack, labelP);
        x += labelW + 14f;
        // the tag pill
        var pill = new SKRect(x, by - 13.5f, x + cur.TagW, by + 5.5f);
        float tk = ta / Math.Max(a, 0.001f);
        if (cur.Kind == RcBarKind.Info)
        {
            stroke.Color = acc.WithAlpha(A(tk)); c.DrawRoundRect(pill, 3f, 3f, stroke);
            tagP.Color = new SKColor(0x9F, 0xB6, 0xFF, A(tk));
        }
        else if (cur.Kind == RcBarKind.BlackFlag)
        {
            fill.Color = new SKColor(0x05, 0x05, 0x07, A(tk)); c.DrawRoundRect(pill, 3f, 3f, fill);
            stroke.Color = Vp.Ivory.WithAlpha(A(0.9f * tk)); c.DrawRoundRect(pill, 3f, 3f, stroke);
            tagP.Color = Vp.Ivory.WithAlpha(A(tk));
        }
        else
        {
            fill.Color = acc.WithAlpha(A(tk)); c.DrawRoundRect(pill, 3f, 3f, fill);
            bool dark = acc.Red + acc.Green + acc.Blue > 450;
            tagP.Color = (dark ? new SKColor(10, 12, 16) : SKColors.White).WithAlpha(A(tk));
        }
        TextBlobs.DrawTracked(c, cur.Tag, x + 9f, by, TagTrack, tagP);
        x += cur.TagW + 16f;
        // the car: number chip, class line, driver
        if (cur.Car.Length > 0)
        {
            fill.Color = SKColors.White.WithAlpha(A(tk));
            c.DrawRoundRect(new SKRect(x, by - 14f, x + cur.CarW, by + 5f), 2f, 2f, fill);
            carP.Color = new SKColor(8, 10, 16, A(tk)); carP.TextAlign = SKTextAlign.Center;
            TextBlobs.Draw(c, cur.Car, x + cur.CarW / 2f, by, carP);
            carP.TextAlign = SKTextAlign.Left;
            x += cur.CarW + 8f;
            fill.Color = Vp.ClassLine(cur.ClassColor).WithAlpha(A(tk));
            c.DrawRect(new SKRect(x, by - 13f, x + 3f, by + 4f), fill);
            x += 3f + 8f;
            drvP.TextSize = cur.DriverSize; drvP.Color = Vp.Ivory.WithAlpha(A(tk));
            TextBlobs.Draw(c, cur.Driver, x, by, drvP);
            x += cur.DriverW + 16f;
        }
        // the message (a spoken line shows the sentence being said; each new one fades in)
        int idx = 0;
        for (int i = 0; i < cur.At.Length; i++) if (t >= cur.At[i]) idx = i;
        float sa = idx == 0 ? 1f : Vp.Seg(t, cur.At[idx], cur.At[idx] + 220);
        msgP.TextSize = cur.Sizes[idx];
        msgP.Color = Vp.Ivory.WithAlpha(A(tk * sa));
        TextBlobs.Draw(c, cur.Texts[idx], x, by + (1f - sa) * 4f, msgP);
        // right: incident points, or the voice meter while a spoken line is on air
        float rx = w - Pad;
        if (cur.Right.Length > 0)
        {
            rightP.Color = Vp.Ivory.WithAlpha(A(tk)); rightP.TextAlign = SKTextAlign.Right;
            TextBlobs.Draw(c, cur.Right, rx, by + 0.5f, rightP);
            rightP.TextAlign = SKTextAlign.Left;
        }
        else
        {
            bool on = t < cur.Hold;
            fill.Color = Vp.Ivory.WithAlpha(A(0.88f * tk));
            for (int i = 0; i < 5; i++)
            {
                double ph = t / 1000.0 * (6.0 + i * 1.7) + i * 1.3;
                float k = on && cur.Spoken ? (float)(0.35 + 0.65 * Math.Abs(Math.Sin(ph))) : 0.25f;
                float bh = 3f + 9f * k;
                c.DrawRoundRect(new SKRect(rx - 24f + i * 5f, by - bh, rx - 21f + i * 5f, by), 1f, 1f, fill);
            }
        }
        c.RestoreToCount(save);
    }

    /// <summary>Preview tools: drop what is up and waiting.</summary>
    public void Reset() { cur = null; while (incoming.TryDequeue(out _)) { } curTop = float.NaN; }

    public void Dispose()
    {
        labelP.Dispose(); tagP.Dispose(); msgP.Dispose(); carP.Dispose(); drvP.Dispose(); rightP.Dispose(); fill.Dispose(); shaded.Dispose();
        stroke.Dispose(); measureP.Dispose(); glassSh.Dispose(); hairSh.Dispose(); glowSh.Dispose(); shadowSh.Dispose();
    }
}
