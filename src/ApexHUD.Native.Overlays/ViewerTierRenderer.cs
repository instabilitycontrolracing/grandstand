using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-29 (#576), refined 2026-10-30 (#603, "the same level of refinement as the overlay intro"): the tiered viewer
/// looks and the end-of-race thank-you roll. Four escalating looks, one at a time from <see cref="ViewerFeed"/>'s queue:
/// <list type="bullet">
/// <item>Chat comment: a compact card that unrolls from under the right half of the session bar (~7 s, silent).</item>
/// <item>Tier 3 (and members / subs / milestones): a gold-edged card - the edge traces itself round, the medallion rings
///   in, the amount rolls up on its chip, a light sweep (~8.5 s; the light lower-third cue).</item>
/// <item>Tier 2 (and 5+ gifts): gold seams shoot out from the watch and a sleeve follows them round the session bar - name
///   and rolling amount on the left, the message (scrolling if long) on the right - then it folds back into the watch
///   (~8 s or longer; the Grandstand string swell).</item>
/// <item>Tier 1: a full-screen gold moment - a warm bloom, the rays fan open, the name with a shine, the amount rolling
///   up - that collapses into a "THANK YOU" band over the tower's cap (~7.6 s + 20 s; the string swell, a whoosh as it
///   collapses).</item>
/// </list>
/// Every look has its own exit (text first, then the glass), and the same exit plays, faster, when a look is cut short.
/// Same gates as the viewer cards (<see cref="ViewerCardsRenderer.ScreenClear"/>): never over the race start, a battle, an
/// onboard, a replay, a story card or the finish; a look cut short before 60 % goes back to the front of the queue.
/// A backlog plays tighter (shorter holds, chat older than 90 s dropped). The thank-you roll (<see cref="RequestRoll"/>)
/// is a full-screen credits sequence: everyone seen this stream, the supporters, then one page per tier.
/// <para>Drawn in two layers: <see cref="DrawUnder"/> (cards, band - under the session bar) and <see cref="DrawOver"/>
/// (bar sleeve, splash, roll - over it). Cost: the text of a look is laid out once when it starts (wrapped lines, fitted
/// names, amount cells), drawn through the shared text-blob cache; gradients are kept shaders (fades go through the
/// paint's alpha, moving sweeps through the canvas matrix); tier 1's big name and the amount's glow are pictures made once
/// per event at the screen's real size; paths are reused. No Skia objects or strings are made per frame on the steady
/// path; the only offscreen layers are the tier-2 message's soft edges and the tier-1 name shine, each just that big.</para>
/// <para>Sounds (<see cref="ViewerTiers.SoundsOn"/>): the existing broadcast cues through <see cref="StorySound"/> (the TV /
/// stream device, never the headset), only when no other cue is sounding (<see cref="StorySound.PlayIfQuiet"/>).</para>
/// <para>Themes: the gold follows <see cref="Theme.Current"/>'s accent on a non-default theme, and every glass surface is
/// skinned with <see cref="Theme.Skin"/>.</para>
/// </summary>
public sealed class ViewerTierRenderer : IDisposable
{
    // timings (ms)
    const double ChatLen = 7000, ChatOut = 520;
    const double T3Len = 8500, T3Out = 620;
    const double T2In = 820, T2MinLen = 8000, T2Out = 720, T2Scroll = 120;
    const double T1Len = 7600, T1Collapse = 1000, BandLen = 20000, BandIn = 620, BandOut = 460;
    const double GapMs = 1500, AbortMs = 340;
    // a backlog (3+ paid / member looks waiting) plays tighter
    const double ChatLenBusy = 5500, T3LenBusy = 6000, T2MinLenBusy = 6500, GapBusyMs = 900, ChatStaleMs = 90_000;

    static readonly SKColor CardGlass = new(0x0B, 0x0E, 0x14, 238);
    static readonly SKColor Ivory = new(252, 250, 245);
    static readonly SKColor Txt2 = new(0xD3, 0xD9, 0xE3);
    static readonly SKColor Txt3 = new(0xB4, 0xBE, 0xCE);
    static readonly SKColor YouTubeRed = new(0xFF, 0x1F, 0x3D);
    static readonly SKColor TwitchPurple = new(0x91, 0x46, 0xFF);
    static readonly SKColor TipGreen = new(0x2E, 0xC4, 0x7A);
    static readonly SKColor Warm = new(0xFF, 0xF4, 0xE0);
    static readonly SKColor Clear = new(0, 0, 0, 0);   // (SKColors.Transparent is transparent WHITE - it greys gradients)
    static readonly SKColor White = SKColors.White;

    // the session bar's gold (FFD876 / C9A961 / B8964C); on a theme, its accent
    static SKColor GoldHi => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xFF, 0xD8, 0x76) : Lighten(Theme.Current.Accent, 0.35f);
    static SKColor GoldMid => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xC9, 0xA9, 0x61) : Theme.Current.Accent;
    static SKColor GoldLo => !Theme.Applies(ThemeSurface.Card) ? new SKColor(0xB8, 0x96, 0x4C) : Darken(Theme.Current.Accent, 0.7f);
    static SKColor Lighten(SKColor c, float k) => new((byte)(c.Red + (255 - c.Red) * k), (byte)(c.Green + (255 - c.Green) * k), (byte)(c.Blue + (255 - c.Blue) * k), c.Alpha);
    static SKColor Darken(SKColor c, float k) => new((byte)(c.Red * k), (byte)(c.Green * k), (byte)(c.Blue * k), c.Alpha);
    static SKColor A(SKColor c, float a) => c.WithAlpha((byte)Math.Clamp(c.Alpha * a, 0, 255));
    static SKColor WA(float a) => new(255, 255, 255, (byte)Math.Clamp(255 * a, 0, 255));

    static readonly float[] S01 = { 0f, 1f }, S055 = { 0f, 0.55f, 1f }, S05 = { 0f, 0.5f, 1f }, S06 = { 0f, 0.6f, 1f },
        S045 = { 0f, 0.45f, 1f }, SLine = { 0f, 0.3f, 0.7f, 1f }, SSeam = { 0f, 0.25f, 0.5f, 0.75f, 1f }, SVig = { 0.5f, 1f }, SVig2 = { 0.45f, 1f };

    readonly LinkedList<ViewerEvent> pending = new();
    ViewerEvent? cur;
    ViewerTier curTier;
    Look? look;
    double curStart = -1, curLen, curOut, abortAt = -1, nextOkMs = -1;
    bool collapseCued;
    // tier 1's lingering band
    ViewerEvent? band;
    Look? bandLook;
    double bandStart = -1;
    float bandVis;
    bool lastClear;
    double lastFlushMs, lastBandMs = -1;

    /// <summary>Preview tools force the screen clear (the gates read live race state).</summary>
    public bool ForceClear;
    public ViewerEvent? Current => cur;
    public ViewerTier CurrentTier => curTier;
    public string WhyWaiting { get; private set; } = "";
    public int Queued => pending.Count;
    /// <summary>When the current look ends (its own clock, ms), or -1 (preview tools).</summary>
    public double CurrentEndsAt => cur != null ? curStart + curLen : -1;
    /// <summary>Paint while a look, the band or the roll is up.</summary>
    public bool Visible => cur != null || band != null || roll != null;
    /// <summary>The tier-1 splash or the roll owns the whole screen right now.</summary>
    public static bool FullScreen { get; private set; }
    public static bool RollActive { get; private set; }

    static volatile int rollRequest;   // 0 none, 1 the real list, 2 the sample list, 3 stop
    /// <summary>Starts the thank-you roll (control app button / POST /api/viewer/thanks). <paramref name="sample"/> = made-up names.</summary>
    public static void RequestRoll(bool sample) => rollRequest = sample ? 2 : 1;
    public static void StopRoll() => rollRequest = 3;
    bool autoRollDone;
    int autoSession = int.MinValue;

    // ================================================================ queue

    public void Tick(RaceSnapshot snap, double nowMs)
    {
        if (nowMs - lastFlushMs > 3000) { lastFlushMs = nowMs; ViewerRoll.Flush(); }
        while (ViewerTiers.LooksOn && ViewerFeed.TryTake(out var e) && e != null)
        {
            // paid / members first, chat at the back (chat is rate-limited anyway)
            if (e.Kind != ViewerKind.Comment)
            {
                var n = pending.First;
                while (n != null && n.Value.Kind != ViewerKind.Comment) n = n.Next;
                if (n == null) pending.AddLast(e); else pending.AddBefore(n, e);
            }
            else pending.AddLast(e);
            while (pending.Count > ViewerFeed.MaxQueue) pending.RemoveLast();
        }

        // the roll: asked for, or by itself after the outro (option)
        if (snap.SessionNum != autoSession) { autoSession = snap.SessionNum; autoRollDone = false; }
        int req = rollRequest;
        if (req != 0) { rollRequest = 0; if (req == 3) { roll = null; ViewerFeed.Log?.Invoke("thank-you roll: stopped"); } else StartRoll(snap, req == 2, nowMs); }
        if (ViewerTiers.AutoThanks && !autoRollDone && OutroDirector.Finished && roll == null)
        {
            autoRollDone = true;
            StartRoll(snap, false, nowMs);
        }
        if (roll != null)
        {
            double rt = nowMs - rollStart;
            int pi = RollPageAt(rt, out _);
            if (pi != rollPageCued) { rollPageCued = pi; if (pi > 0) Cue("lowerthird_light"); }
            if (rt >= rollTotalMs + 700) { roll = null; ViewerFeed.Log?.Invoke("thank-you roll: done"); }
        }
        RollActive = roll != null;

        string why = "";
        bool clear = roll == null && (ForceClear || ViewerFeed.TestBoost || ViewerCardsRenderer.ScreenClear(snap, out why));   // 2026-11-06
        if (roll != null) why = "thank-you roll";
        WhyWaiting = clear ? "" : why;
        lastClear = clear;

        if (cur != null)
        {
            double t = nowMs - curStart;
            if (abortAt < 0 && !clear)
            {
                abortAt = nowMs;
                if (t < curLen * 0.6) pending.AddFirst(cur);
                ViewerFeed.Log?.Invoke($"viewer: {cur.Kind} ({curTier}) from {cur.Name} paused for the {WhyWaiting}");
            }
            // tier 1: the band takes over as the splash collapses
            if (curTier == ViewerTier.Tier1 && abortAt < 0 && t >= curLen - T1Collapse && !collapseCued) { collapseCued = true; Cue("story_exit"); }
            if (curTier == ViewerTier.Tier1 && abortAt < 0 && t >= curLen - T1Collapse * 0.45 && band != cur)
            {
                if (bandLook != null && !ReferenceEquals(bandLook, look)) bandLook.Dispose();
                band = cur; bandLook = look; bandStart = nowMs;
            }
            if ((abortAt >= 0 && nowMs - abortAt >= AbortMs) || t >= curLen)
            {
                cur = null; abortAt = -1;
                nextOkMs = nowMs + (Busy ? GapBusyMs : GapMs);
                if (!ReferenceEquals(look, bandLook)) look?.Dispose();
                look = null;
            }
        }
        while (cur == null && clear && pending.Count > 0 && nowMs >= nextOkMs)
        {
            var next = pending.First!.Value; pending.RemoveFirst();
            // a chat comment that waited out a long battle is old news
            if (next.Kind == ViewerKind.Comment && next.At > 0 && Environment.TickCount64 - next.At > ChatStaleMs && !next.Test)
            { ViewerFeed.Log?.Invoke($"viewer: chat from {next.Name} dropped (waited over 90 s)"); continue; }
            cur = next;
            curTier = cur.Tier;
            curStart = nowMs; abortAt = -1; collapseCued = false;
            look = new Look(cur, curTier);
            edgeLen = 0;   // the tier-3 outline is measured again for this card
            curLen = LengthOf(cur, curTier, look);
            curOut = curTier switch { ViewerTier.Chat => ChatOut, ViewerTier.Tier3 => T3Out, ViewerTier.Tier2 => T2Out, _ => T1Collapse };
            ViewerFeed.Log?.Invoke($"viewer: playing {cur.Kind} from {cur.Name} as {curTier}{(Busy ? " (backlog: shorter)" : "")}");
            ViewerLatest.Note(cur);   // #608: the latest plate shows it once this look is over
            if (ViewerTiers.SoundsOn)
                switch (curTier)
                {
                    case ViewerTier.Tier3: Cue("lowerthird_light"); break;
                    case ViewerTier.Tier2: Cue("featured_in"); break;
                    case ViewerTier.Tier1: Cue("custom_in"); break;
                }
        }
        if (band != null && nowMs - bandStart >= BandLen)
        {
            band = null;
            if (!ReferenceEquals(bandLook, look)) bandLook?.Dispose();
            bandLook = null;
        }
        FullScreen = roll != null || (cur != null && curTier == ViewerTier.Tier1);
    }

    static void Cue(string cue) { if (ViewerTiers.SoundsOn) StorySound.PlayIfQuiet(cue); }

    /// <summary>3+ paid / member looks waiting: holds get shorter so the queue catches up.</summary>
    bool Busy
    {
        get
        {
            int n = 0;
            for (var x = pending.First; x != null; x = x.Next) if (x.Value.Kind != ViewerKind.Comment && ++n >= 3) return true;
            return false;
        }
    }

    double LengthOf(ViewerEvent e, ViewerTier tier, Look l) => tier switch
    {
        ViewerTier.Chat => Busy ? ChatLenBusy : ChatLen,
        ViewerTier.Tier3 => Busy ? T3LenBusy : T3Len,
        ViewerTier.Tier2 => T2Length(l),
        _ => T1Len,
    };

    // ================================================================ drawing entry points

    /// <summary>Under the session bar: the chat card, the tier-3 card and tier 1's band over the tower cap.
    /// <paramref name="towerTop"/> = the tower's top-left corner (null = the tower is away).</summary>
    public void DrawUnder(SKCanvas c, int w, int h, SKRect bar, SKPoint? towerTop, double nowMs)
    {
        if (bar.Width < 10) bar = DefaultBar(w);
        // the band: up while the tower is there and the screen is clear, fading otherwise (its clock keeps running)
        double dt = lastBandMs < 0 ? 16 : Math.Clamp(nowMs - lastBandMs, 0, 100);
        lastBandMs = nowMs;
        float target = band != null && towerTop != null && (lastClear || cur == band) && roll == null ? 1f : 0f;
        bandVis += (target - bandVis) * (float)Math.Min(1, dt / 90.0);
        if (Math.Abs(target - bandVis) < 0.01f) bandVis = target;
        if (band != null && bandLook != null && towerTop is SKPoint tt && bandVis > 0.01f) DrawBand(c, tt, bandLook, nowMs - bandStart, bandVis, nowMs);

        var l = look;
        if (cur == null || l == null) return;
        double t = nowMs - curStart;
        float xo = ExitK(t, nowMs);
        if (curTier == ViewerTier.Chat) DrawChat(c, bar, l, t, xo, nowMs);
        else if (curTier == ViewerTier.Tier3) DrawTier3(c, bar, l, t, xo, nowMs);
    }

    /// <summary>Over the session bar: tier 2's sleeve, tier 1's splash and the thank-you roll.</summary>
    public void DrawOver(SKCanvas c, int w, int h, SKRect bar, SKPoint? towerTop, RaceSnapshot? snap, double nowMs)
    {
        if (bar.Width < 10) bar = DefaultBar(w);
        var l = look;
        if (cur != null && l != null)
        {
            double t = nowMs - curStart;
            float xo = ExitK(t, nowMs);
            if (curTier == ViewerTier.Tier2) DrawSleeve(c, bar, l, t, xo, nowMs);
            else if (curTier == ViewerTier.Tier1) DrawSplash(c, w, h, towerTop, l, t, abortAt >= 0 ? 1f - xo : 1f, nowMs);
        }
        if (roll != null) DrawRoll(c, w, h, snap, nowMs - rollStart, nowMs);
    }

    /// <summary>Where <see cref="DrawUnder"/> draws right now (layout units), for the compositor's fade sheet. Empty = nothing.</summary>
    public SKRect UnderBounds(SKRect bar, SKPoint? towerTop)
    {
        var r = SKRect.Empty;
        if (cur != null && curTier is ViewerTier.Chat or ViewerTier.Tier3)
            r = new SKRect(bar.Left - 40, bar.Bottom - 4, bar.Right + 40, bar.Bottom + 300);
        if (band != null && towerTop is SKPoint tt)
        {
            var b = new SKRect(tt.X - 20, tt.Y - 20, tt.X + TimingTowerRenderer.TowerWidth + 40, tt.Y + TimingTowerRenderer.CapHeight + 30);
            r = r.IsEmpty ? b : SKRect.Union(r, b);
        }
        return r;
    }

    /// <summary>Where <see cref="DrawOver"/> draws right now: the bar (tier 2), or Empty = the whole screen (tier 1, the roll).</summary>
    public SKRect OverBounds(SKRect bar) =>
        roll == null && cur != null && curTier == ViewerTier.Tier2 ? new SKRect(bar.Left - 20, bar.Top - 20, bar.Right + 20, bar.Bottom + 90) : SKRect.Empty;

    static SKRect DefaultBar(int w) => new((w - SessionBarRenderer.BarWidth) / 2f, 18f, (w + SessionBarRenderer.BarWidth) / 2f, 18f + SessionBarRenderer.BarHeight);

    /// <summary>Exit progress 0..1: the look's own exit at its end, or the same exit played in <see cref="AbortMs"/> when cut short.</summary>
    float ExitK(double t, double nowMs)
    {
        if (abortAt >= 0) return Math.Max((float)Math.Clamp((nowMs - abortAt) / AbortMs, 0, 1), Raw(t - (curLen - curOut), curOut));
        return Raw(t - (curLen - curOut), curOut);
    }

    // ================================================================ easing

    static float Raw(double t, double len) => (float)Math.Clamp(t / len, 0, 1);
    static float Ease(float k) { k = Math.Clamp(k, 0, 1); return 1 - (1 - k) * (1 - k) * (1 - k); }
    static float Quint(float k) { k = Math.Clamp(k, 0, 1); float i = 1 - k; return 1 - i * i * i * i * i; }
    static float EaseIn(float k) { k = Math.Clamp(k, 0, 1); return k * k * k; }
    static float EaseInOut(float k) { k = Math.Clamp(k, 0, 1); return k < 0.5f ? 4 * k * k * k : 1 - (float)Math.Pow(-2 * k + 2, 3) / 2; }
    /// <summary>Eased progress of a step that starts at <paramref name="at"/> ms and lasts <paramref name="len"/> ms.</summary>
    static float Step(double t, double at, double len) => Ease((float)((t - at) / len));
    static float QStep(double t, double at, double len) => Quint((float)((t - at) / len));
    /// <summary>1 until the exit reaches <paramref name="from"/>, then down to 0 at <paramref name="to"/> (exit progress).</summary>
    static float Out(float xo, float from, float to) => 1f - Ease((xo - from) / Math.Max(0.001f, to - from));

    // ================================================================ shared bits

    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    readonly SKPaint text = new() { IsAntialias = true, SubpixelText = true };
    readonly SKPaint img = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    readonly SKPaint atop = new() { IsAntialias = true, BlendMode = SKBlendMode.SrcATop };
    readonly SKPaint dstIn = new() { BlendMode = SKBlendMode.DstIn };
    readonly SKPaint shadowPaint = new() { IsAntialias = true };
    readonly SKMaskFilter blur14 = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14), blur18 = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18);
    readonly SKPath shape = new(), path = new(), path2 = new();
    readonly SKPathMeasure edgeMeasure = new();
    SKSize edgeFor;
    float edgeLen, edgeTop;

    /// <summary>#604 perf pass 2: a solid wash, then a gradient faded by alpha A, then another faded by alpha B - as one
    /// shader (each source-over the one below, exactly the order the three full-screen draws had), rebuilt only when a
    /// colour, a fade or a gradient changes (every frame of a fade; held still otherwise).</summary>
    sealed class Stage : IDisposable
    {
        SKShader? solid, a1, a2, inner, outer; SKColorFilter? f1, f2;
        SKColor col; SKShader? s1, s2; byte al1, al2;
        public SKShader Get(SKColor wash, SKShader g1, byte alpha1, SKShader g2, byte alpha2)
        {
            if (outer != null && wash == col && ReferenceEquals(g1, s1) && ReferenceEquals(g2, s2) && alpha1 == al1 && alpha2 == al2) return outer;
            Dispose();
            col = wash; s1 = g1; s2 = g2; al1 = alpha1; al2 = alpha2;
            solid = SKShader.CreateColor(wash);
            f1 = SKColorFilter.CreateBlendMode(new SKColor(0, 0, 0, alpha1), SKBlendMode.DstIn);   // = the paint alpha the gradient was drawn with
            f2 = SKColorFilter.CreateBlendMode(new SKColor(0, 0, 0, alpha2), SKBlendMode.DstIn);
            // (Skia returns no filter for a no-op one - alpha 255 - and the gradient is then used as it is)
            a1 = f1 != null ? g1.WithColorFilter(f1) : null; a2 = f2 != null ? g2.WithColorFilter(f2) : null;
            inner = SKShader.CreateCompose(solid, a1 ?? g1);   // the first gradient over the wash
            return outer = SKShader.CreateCompose(inner, a2 ?? g2);
        }
        public void Dispose() { outer?.Dispose(); inner?.Dispose(); a1?.Dispose(); a2?.Dispose(); f1?.Dispose(); f2?.Dispose(); solid?.Dispose(); outer = null; }
    }
    readonly Stage splashStage = new(), rollStage = new();

    // kept gradients (#603 efficiency): built once per geometry / theme, faded through the paint's alpha
    readonly ShaderSlot[] sl = Enumerable.Range(0, 40).Select(_ => new ShaderSlot()).ToArray();
    const int SlChatWash = 0, SlT3Glow = 1, SlT3Edge = 2, SlRing = 3, SlChip = 4, SlHair = 5, SlSweep = 6, SlSweep2 = 7,
        SlSleeveBg = 8, SlPoolL = 9, SlPoolR = 10, SlSeam = 11, SlAmt2 = 12, SlMask = 13, SlHoleRing = 14,
        SlTop = 15, SlVig = 16, SlRay = 17, SlRuleL = 18, SlRuleR = 19, SlRule = 20, SlAmt1 = 21, SlShine = 22, SlBloom = 23,
        SlBandGlow = 24, SlBandBar = 25, SlBandLine = 26, SlBandFlare = 27, SlRollTop = 28, SlRollVig = 29, SlRollTab = 30, SlRollRule = 31,
        SlMedal = 32, SlChatLine = 33, SlSweepBand = 34, SlRollPlate = 35;

    static SKColor PlatformColour(string platform) => platform switch { "youtube" => YouTubeRed, "twitch" => TwitchPurple, _ => TipGreen };
    static string PlatformLabel(string platform) => platform switch { "youtube" => "YOUTUBE", "twitch" => "TWITCH", _ => "TIP" };

    static string KindLabel(ViewerEvent e) => e.Kind switch
    {
        ViewerKind.SuperChat => "SUPER CHAT",
        ViewerKind.Tip => "TIP",
        ViewerKind.Bits => "BITS",
        ViewerKind.Member => e.Gifts > 0 ? "GIFTED MEMBERSHIPS" : e.Months > 0 ? "MEMBER MILESTONE" : "NEW MEMBER",
        ViewerKind.Sub => e.Gifts > 0 ? "GIFTED SUBS" : e.Months > 0 ? "RESUBSCRIBED" : "NEW SUBSCRIBER",
        ViewerKind.Comment => "LIVE CHAT",
        _ => "",
    };

    /// <summary>The chip / big number: the event's own amount text, or the gift count, or the months, or the membership.</summary>
    static string AmountText(ViewerEvent e) => e.Amount.Length > 0 ? e.Amount
        : e.Gifts > 0 ? $"{e.Gifts} GIFTED"
        : e.Months > 0 ? $"{e.Months} {(e.Months == 1 ? "MONTH" : "MONTHS")}"
        : e.Kind == ViewerKind.Member ? "MEMBER" : e.Kind == ViewerKind.Sub ? "SUBSCRIBED" : "";

    void SetText(SKTypeface tf, float size, SKColor col) { text.Typeface = tf; text.TextSize = size; text.Color = col; text.Shader = null; text.MaskFilter = null; }
    void Txt(SKCanvas c, string s, float x, float y) => TextBlobs.Draw(c, s, x, y, text);
    float Meas(string s) => TextBlobs.Measure(s, text);
    void Trk(SKCanvas c, string s, float x, float y, float sp) => TextBlobs.DrawTracked(c, s, x, y, sp, text);
    float TrkW(string s, float sp) => TextBlobs.TrackedWidth(s, text, sp);

    /// <summary>A soft drop shadow under <see cref="shape"/>.</summary>
    void Shadow(SKCanvas c, float a, bool big)
    {
        shadowPaint.Color = new SKColor(0, 0, 0, (byte)Math.Clamp(120 * a, 0, 255));
        shadowPaint.MaskFilter = big ? blur18 : blur14;
        c.Save(); c.Translate(0, big ? 10 : 8); c.DrawPath(shape, shadowPaint); c.Restore();
    }

    /// <summary>A soft diagonal light sweep across <paramref name="r"/> (clip first). <paramref name="k"/> 0..1 = its travel. The
    /// gradient is a kept shader around x = 0, moved by the canvas.</summary>
    void Sweep(SKCanvas c, int slot, SKRect r, float k, float a, SKColor tint, float width)
    {
        if (k <= 0 || k >= 1 || a <= 0.01f) return;
        float sx = r.Left - width * 2 + k * (r.Width + width * 4);
        fill.Shader = sl[slot].Linear(new SKPoint(-width, r.Top), new SKPoint(width, r.Bottom), [tint.WithAlpha(0), tint.WithAlpha(70), tint.WithAlpha(0)], S05);
        fill.Color = WA(a);
        c.Save(); c.Translate(sx, 0);
        c.DrawRect(r.Left - sx, r.Top, r.Width, r.Height, fill);
        c.Restore();
        fill.Shader = null;
    }

    SKShader GoldV(int slot, float y0, float y1) => sl[slot].Linear(new SKPoint(0, y0), new SKPoint(0, y1), [GoldHi, GoldMid, GoldLo], S055);

    /// <summary>A gold hairline from <paramref name="x0"/> that has drawn <paramref name="k"/> of its length to <paramref name="x1"/>.</summary>
    void GoldRule(SKCanvas c, int slot, float x0, float x1, float y, float hgt, float k, float a)
    {
        if (k <= 0.005f || a <= 0.01f) return;
        fill.Shader = sl[slot].Linear(new SKPoint(x0, 0), new SKPoint(x1, 0), [GoldLo.WithAlpha(0), GoldHi, GoldMid, GoldLo.WithAlpha(0)], SLine);
        fill.Color = WA(a);
        c.DrawRect(x0, y, (x1 - x0) * k, hgt, fill);
        fill.Shader = null;
    }

    /// <summary>The platform mark drawn as simple shapes (no logo files): YouTube = red rounded plate with a play triangle,
    /// Twitch = purple speech bubble with two bars, tip = green coin with a dollar sign. <paramref name="s"/> = height.</summary>
    void Glyph(SKCanvas c, string platform, float cx, float cy, float s, float a)
    {
        if (a <= 0.01f) return;
        if (platform == "youtube")
        {
            fill.Color = A(YouTubeRed, a); c.DrawRoundRect(cx - s * 0.72f, cy - s / 2f, s * 1.44f, s, s * 0.26f, s * 0.26f, fill);
            path.Reset();
            path.MoveTo(cx - s * 0.17f, cy - s * 0.22f); path.LineTo(cx + s * 0.25f, cy); path.LineTo(cx - s * 0.17f, cy + s * 0.22f); path.Close();
            fill.Color = WA(a); c.DrawPath(path, fill);
        }
        else if (platform == "twitch")
        {
            float w = s * 0.86f, l = cx - w / 2f, t = cy - s / 2f;
            path.Reset();
            path.MoveTo(l + s * 0.12f, t); path.LineTo(l + w, t); path.LineTo(l + w, t + s * 0.62f); path.LineTo(l + w - s * 0.2f, t + s * 0.8f);
            path.LineTo(l + s * 0.42f, t + s * 0.8f); path.LineTo(l + s * 0.24f, t + s); path.LineTo(l + s * 0.24f, t + s * 0.8f); path.LineTo(l, t + s * 0.8f);
            path.LineTo(l, t + s * 0.14f); path.Close();
            fill.Color = A(TwitchPurple, a); c.DrawPath(path, fill);
            fill.Color = WA(a);
            float bw = s * 0.09f;
            c.DrawRect(l + s * 0.38f, t + s * 0.2f, bw, s * 0.28f, fill);
            c.DrawRect(l + s * 0.6f, t + s * 0.2f, bw, s * 0.28f, fill);
        }
        else
        {
            fill.Color = A(TipGreen, a); c.DrawCircle(cx, cy, s / 2f, fill);
            SetText(Fonts.Inter.Bold, s * 0.66f, WA(a));
            text.GetFontMetrics(out var fm);
            Txt(c, "$", cx - Meas("$") / 2f, cy - (fm.Ascent + fm.Descent) / 2f);
        }
    }

    // ================================================================ odometer amounts

    /// <summary>Draws an amount whose digits count up and land on their value, left digits landing first (a broadcast
    /// counter): every digit runs from the start, a running digit is a touch dimmer and lower, and settles up into place.
    /// <paramref name="k"/> 0..1 = the count's progress. Uses the cells laid out in <see cref="Amount"/>; the paint is set
    /// up by the caller (font, colour, shader).</summary>
    void Odometer(SKCanvas c, Amount am, float x, float y, float k)
    {
        if (am.Cells.Length == 0) return;
        if (am.Digits == 0) { Txt(c, am.Text, x, y); return; }   // (with digits: always cell by cell, so nothing jumps when it lands)
        float lift = text.TextSize * 0.16f;
        var baseCol = text.Color;
        int di = 0;
        for (int i = 0; i < am.Cells.Length; i++)
        {
            float cx = x + am.CellX[i];
            if (am.DigitVal[i] < 0) { Txt(c, am.Cells[i], cx, y); continue; }
            // each digit lands a little after the one to its left; the rightmost ones count furthest
            float end = 0.55f + 0.45f * di / Math.Max(1, am.Digits - 1);
            float p = Ease(k / end);
            int travel = 3 + 3 * di;
            int d = (am.DigitVal[i] + (int)Math.Ceiling(travel * (1 - p))) % 10;
            di++;
            if (p >= 1) { Txt(c, SmallStrings.Char((char)('0' + d)), cx + (am.CellW[i] - am.DigitW[d]) / 2f, y); continue; }
            text.Color = baseCol.WithAlpha((byte)(baseCol.Alpha * (0.55f + 0.45f * p)));
            Txt(c, SmallStrings.Char((char)('0' + d)), cx + (am.CellW[i] - am.DigitW[d]) / 2f, y + (1 - p) * lift);
            text.Color = baseCol;
        }
    }

    // ================================================================ chat comment: compact card under the bar's right half

    void DrawChat(SKCanvas c, SKRect bar, Look L, double t, float xo, double nowMs)
    {
        // in: a platform-coloured hairline draws along the top, the card unrolls down from under the bar, then the glyph,
        // the name and the lines arrive one after another. Out: the text goes first, then the card rolls back up.
        float hair = QStep(t, 0, 340) * Out(xo, 0.55f, 1f);
        float unroll = QStep(t, 80, 520) * (1 - EaseIn((xo - 0.25f) / 0.75f));
        float txtA = Out(xo, 0f, 0.4f);
        if (hair <= 0.005f && unroll <= 0.005f) return;
        float Lx = Math.Max(bar.Right - 500f, bar.MidX + 78f), W = bar.Right - Lx;
        L.PrepareChat(this, W);
        float H = L.ChatH;
        var r = new SKRect(Lx, bar.Bottom + 10, bar.Right, bar.Bottom + 10 + H);
        var accent = PlatformColour(L.E.Platform);
        int save = c.Save();
        // the card hangs from the bar: its visible part grows downwards
        c.ClipRect(new SKRect(r.Left - 30, r.Top - 2, r.Right + 30, r.Top + (H + 60) * unroll));
        c.Translate(0, -(1 - unroll) * 18f);
        shape.Reset(); shape.AddRoundRect(r, 10, 10);
        if (unroll > 0.005f)
        {
            Shadow(c, 0.8f * unroll * unroll * unroll, false);   // (faint while it unrolls: no hard shadow edge at the clip)
            fill.Color = A(CardGlass, unroll); c.DrawPath(shape, fill);
            Theme.Skin(c, r, ThemeSurface.Card, nowMs, unroll, shape);
            c.Save(); c.ClipPath(shape, antialias: true);
            fill.Shader = sl[SlChatWash].Linear(new SKPoint(r.Left, 0), new SKPoint(r.Left + 220, 0), [accent.WithAlpha(46), accent.WithAlpha(0)], S01);
            fill.Color = WA(unroll); c.DrawRect(r, fill); fill.Shader = null;
            fill.Color = A(accent, unroll); c.DrawRect(r.Left, r.Top, 3, r.Height, fill);
            c.Restore();
            stroke.StrokeWidth = 1f; stroke.Color = new SKColor(255, 255, 255, (byte)(46 * unroll)); stroke.Shader = null; c.DrawPath(shape, stroke);
        }
        c.RestoreToCount(save);
        // the hairline along the top edge (outside the unroll clip: it leads)
        if (hair > 0.005f)
        {
            fill.Shader = sl[SlChatLine].Linear(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0), [accent, accent.WithAlpha(160), accent.WithAlpha(0)], S06);
            fill.Color = WA(hair); c.DrawRect(r.Left + 6, r.Top, (r.Width - 12) * hair, 2f, fill); fill.Shader = null;
        }
        if (txtA <= 0.01f || unroll < 0.3f) return;
        save = c.Save();
        c.ClipRect(r);
        c.Translate(0, -(1 - unroll) * 18f);
        float gp = Step(t, 260, 300);
        float gs = 0.6f + 0.4f * gp;
        Glyph(c, L.E.Platform, r.Left + 30, r.Top + 27, 17f * gs, gp * txtA);
        float lk = Step(t, 380, 320);
        SetText(Fonts.Inter.Bold, 11f, A(Txt3, 0.88f * lk * txtA));
        Trk(c, "LIVE CHAT", r.Right - 18 - L.ChatLabelW, r.Top + 32, 1.8f);
        float nk = Step(t, 300, 420);
        if (nk > 0.01f)
        {
            SetText(L.NameFace, 19f, A(Ivory, txtA));
            c.Save(); c.ClipRect(new SKRect(r.Left + 46, r.Top, r.Left + 50 + (r.Width - 60) * nk, r.Top + 48));
            Txt(c, L.ChatName, r.Left + 50 + (1 - nk) * 10f, r.Top + 34);
            c.Restore();
        }
        SetText(L.MsgFace, 20f, Txt2);
        for (int i = 0; i < L.Lines.Length; i++)
        {
            float k = Step(t, 440 + 80 * i, 380);
            if (k <= 0.01f) continue;
            text.Color = A(Txt2, 0.96f * k * txtA);
            Txt(c, L.Lines[i], r.Left + 22, r.Top + 66 + i * 27 + (1 - k) * 7f);
        }
        c.RestoreToCount(save);
    }

    // ================================================================ tier 3: gold-edged card, centred under the bar

    void DrawTier3(SKCanvas c, SKRect bar, Look L, double t, float xo, double nowMs)
    {
        const float W = 720f;
        L.PrepareTier3(this, W);
        float H = L.T3H;
        float unroll = QStep(t, 0, 560) * (1 - EaseIn((xo - 0.35f) / 0.65f));
        float edgeK = Ease((float)((t - 120) / 820.0));
        float edgeA = Out(xo, 0.3f, 0.9f);
        if (unroll <= 0.005f && (edgeK <= 0.005f || edgeA <= 0.01f)) return;
        var r0 = new SKRect(0, 0, W, H);
        float ox = bar.MidX - W / 2f, oy = bar.Bottom + 26 - (1 - unroll) * 14f;   // clear of the watch
        int save = c.Save();
        c.Translate(ox, oy);
        shape.Reset(); shape.AddRoundRect(r0, 12, 12);
        if (unroll > 0.005f)
        {
            c.Save();
            c.ClipRect(new SKRect(-40, -4, W + 40, (H + 60) * unroll));
            Shadow(c, unroll * unroll * unroll, true);
            fill.Color = A(CardGlass, unroll); c.DrawPath(shape, fill);
            Theme.Skin(c, r0, ThemeSurface.Card, nowMs, unroll, shape);
            c.Save(); c.ClipPath(shape, antialias: true);
            // warm gold light from the medallion side
            fill.Shader = sl[SlT3Glow].Radial(new SKPoint(62, 54), 320, [GoldMid.WithAlpha(60), GoldMid.WithAlpha(0)], S01);
            fill.Color = WA(unroll); c.DrawRect(r0, fill); fill.Shader = null;
            Sweep(c, SlSweep, r0, (float)((t - 1000) / 1300.0), unroll, GoldHi, 110);
            Sweep(c, SlSweep, r0, (float)((t - curLen * 0.55) / 1600.0), unroll * 0.6f, GoldHi, 90);
            c.Restore();
            c.Restore();
        }
        // the gold edge traces itself round from the top centre, both ways
        if (edgeK > 0.005f && edgeA > 0.01f)
        {
            EdgeFor(W, H);
            stroke.Shader = sl[SlT3Edge].Linear(new SKPoint(0, 0), new SKPoint(W, H), [GoldHi.WithAlpha(240), GoldLo.WithAlpha(170), GoldHi.WithAlpha(200)], S06);
            stroke.Color = WA(edgeA); stroke.StrokeWidth = 1.6f;
            if (edgeK >= 0.999f) c.DrawPath(shape, stroke);
            else
            {
                path.Reset();
                float half = edgeLen * edgeK / 2f;
                Segment(edgeTop - half, edgeTop + half);
                c.DrawPath(path, stroke);
            }
            stroke.Shader = null;
        }
        float txtA = Out(xo, 0f, 0.45f);
        if (txtA > 0.01f && unroll > 0.3f)
        {
            c.Save(); c.ClipRect(new SKRect(-4, -4, W + 4, H + 4));
            // medallion: dark disc, a gold ring drawn round, the platform mark popping in
            float pop = Step(t, 200, 420) * Out(xo, 0.15f, 0.6f);
            float ring = Ease((float)((t - 220) / 520.0));
            float mx = 62, my = 54, mr = 32f * (0.7f + 0.3f * pop);
            fill.Color = new SKColor(0x06, 0x08, 0x0D, (byte)(255 * pop)); c.DrawCircle(mx, my, mr, fill);
            stroke.Shader = sl[SlRing].Linear(new SKPoint(0, my - 32), new SKPoint(0, my + 32), [GoldHi, GoldMid, GoldLo], S055);
            stroke.Color = WA(pop); stroke.StrokeWidth = 2.4f;
            c.DrawArc(new SKRect(mx - mr, my - mr, mx + mr, my + mr), -90, 360 * ring, false, stroke);
            stroke.Shader = null;
            Glyph(c, L.E.Platform, mx, my, 24f * (0.7f + 0.3f * pop), pop);

            float tx = 120;
            // amount chip, right: gold pill, dark numerals rolling up to the amount
            float ck = Step(t, 480, 460) * Out(xo, 0f, 0.35f);
            var chip = new SKRect(W - 22 - L.ChipW, 20, W - 22, 58);
            chip.Offset((1 - ck) * 26f, 0);
            if (ck > 0.01f)
            {
                fill.Shader = GoldV(SlChip, 20, 58); fill.Color = WA(ck);
                c.DrawRoundRect(chip, 19, 19, fill); fill.Shader = null;
                SetText(Fonts.BarlowCondensed.Bold, 28f, new SKColor(0x10, 0x0C, 0x04, (byte)(255 * ck)));
                Odometer(c, L.ChipAmount, chip.Left + 15, chip.MidY + 10f, (float)((t - 560) / 760.0));
            }
            // eyebrow wipes on, the name rises
            float ek = Step(t, 300, 420);
            SetText(Fonts.Inter.Bold, 13f, A(GoldHi, txtA));
            c.Save(); c.ClipRect(new SKRect(tx - 2, 0, tx + (L.EyebrowW + 6) * ek, 46));
            Trk(c, L.Eyebrow, tx, 36, 2.4f);
            c.Restore();
            float nk = Step(t, 360, 460);
            SetText(L.NameFace, L.T3NameSize, A(Ivory, nk * Out(xo, 0.1f, 0.45f)));
            Txt(c, L.T3Name, tx, 82 + (1 - nk) * 12f);
            if (L.Lines.Length > 0)
            {
                GoldRule(c, SlHair, tx, W - 24, 98, 1, Step(t, 650, 420), 0.55f * Out(xo, 0f, 0.3f));
                SetText(L.MsgFace, 20f, Txt2);
                for (int i = 0; i < L.Lines.Length; i++)
                {
                    float mk = Step(t, 760 + 90 * i, 420) * Out(xo, 0f, 0.3f);
                    if (mk <= 0.01f) continue;
                    text.Color = A(Txt2, 0.96f * mk);
                    Txt(c, L.Lines[i], tx, 128 + i * 26 + (1 - mk) * 6f);
                }
            }
            c.Restore();
        }
        c.RestoreToCount(save);
    }

    /// <summary>The tier-3 card's outline as a measured path (made when the card's size changes).</summary>
    void EdgeFor(float w, float h)
    {
        if (edgeFor.Width == w && edgeFor.Height == h && edgeLen > 0) return;
        edgeFor = new SKSize(w, h);
        edgeMeasure.SetPath(shape, true);
        edgeLen = edgeMeasure.Length;
        // where along the outline the top centre is
        float best = float.MaxValue; edgeTop = 0;
        for (int i = 0; i <= 256; i++)
        {
            float d = edgeLen * i / 256f;
            if (edgeMeasure.GetPosition(d, out var p))
            {
                float e = Math.Abs(p.X - w / 2) + Math.Abs(p.Y);
                if (e < best) { best = e; edgeTop = d; }
            }
        }
    }

    /// <summary>Appends the outline from <paramref name="a"/> to <paramref name="b"/> (wrapping round) to <see cref="path"/>.</summary>
    void Segment(float a, float b)
    {
        float L = edgeLen, len = b - a;
        if (len >= L - 0.5f) { edgeMeasure.GetSegment(0, L, path, true); return; }
        a = ((a % L) + L) % L;
        float e = a + len;
        if (e <= L) edgeMeasure.GetSegment(a, e, path, true);
        else { edgeMeasure.GetSegment(a, L, path, true); edgeMeasure.GetSegment(0, e - L, path, true); }
    }

    // ================================================================ tier 2: a sleeve wraps the session bar

    const float HoleR = 74f;

    double T2Length(Look l)
    {
        float areaW = SessionBarRenderer.BarWidth / 2f - HoleR - 20 - 28;
        l.PrepareTier2(this);
        double scroll = l.MsgW > areaW ? (l.MsgW - areaW) / T2Scroll * 1000 : 0;
        return Math.Max(Busy ? T2MinLenBusy : T2MinLen, T2In + 1600 + scroll + 1800 + T2Out);
    }

    void DrawSleeve(SKCanvas c, SKRect bar, Look L, double t, float xo, double nowMs)
    {
        double len = curLen;
        L.PrepareTier2(this);
        var r = bar;
        float cx = r.MidX, cy = r.MidY;
        // gold seams shoot out from the watch first, the dark panels follow them; at the end the panels fold back in,
        // the seams last
        float seam = Step(t, 0, 620) * (1 - EaseInOut((xo - 0.3f) / 0.7f));
        float panel = EaseInOut((float)((t - 160) / 700.0)) * (1 - EaseInOut((xo - 0.2f) / 0.65f));
        float ringK = Ease((float)(t / 700.0)) * Out(xo, 0.5f, 1f);
        if (seam <= 0.005f && panel <= 0.005f && ringK <= 0.005f) return;
        float pl = cx - panel * (cx - r.Left), pr = cx + panel * (r.Right - cx);
        float sL = cx - seam * (cx - r.Left), sR = cx + seam * (r.Right - cx);
        int save = c.Save();
        path2.Reset(); path2.AddCircle(cx, cy, HoleR);
        c.ClipPath(path2, SKClipOperation.Difference, true);
        if (panel > 0.005f)
        {
            var span = new SKRect(pl, r.Top, pr, r.Bottom);
            c.Save();
            c.ClipRect(span);
            fill.Shader = sl[SlSleeveBg].Linear(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), [new SKColor(0x12, 0x18, 0x28, 252), new SKColor(0x07, 0x0A, 0x12, 254)], S01);
            fill.Color = White; c.DrawRect(span, fill); fill.Shader = null;
            Theme.Skin(c, span, ThemeSurface.Bar, nowMs, 1f);
            // gold light pooled behind each half's text
            fill.Shader = sl[SlPoolL].Radial(new SKPoint(r.Left + 150, r.Bottom), 260, [GoldMid.WithAlpha(46), GoldMid.WithAlpha(0)], S01);
            c.DrawRect(span, fill);
            fill.Shader = sl[SlPoolR].Radial(new SKPoint(r.Right - 150, r.Bottom), 260, [GoldMid.WithAlpha(46), GoldMid.WithAlpha(0)], S01);
            c.DrawRect(span, fill); fill.Shader = null;
            Sweep(c, SlSweep2, span, (float)((t - 950) / 1100.0), 1f, GoldHi, 120);
            Sweep(c, SlSweep2, span, (float)((t - (len - 2300)) / 1100.0), 0.7f, GoldHi, 100);
            // time left: a slim gold line under the top seam that drains towards the watch
            float left = (float)Math.Clamp(1 - (t - T2In) / Math.Max(1, len - T2In - T2Out), 0, 1);
            fill.Color = A(GoldHi, 0.43f * Out(xo, 0f, 0.3f));
            float half = (r.Width / 2f - HoleR) * left;
            c.DrawRect(cx - HoleR - half, r.Top + 4f, half, 1f, fill);
            c.DrawRect(cx + HoleR, r.Top + 4f, half, 1f, fill);
            c.Restore();
        }
        // seams: gold hairlines along the top and bottom, ahead of the panels
        if (seam > 0.005f)
        {
            fill.Shader = sl[SlSeam].Linear(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0), [GoldLo, GoldHi, GoldMid, GoldHi, GoldLo], SSeam);
            fill.Color = White;
            c.DrawRect(sL, r.Top, sR - sL, 2f, fill);
            c.DrawRect(sL, r.Bottom - 2f, sR - sL, 2f, fill);
            fill.Shader = null;
            // the bright heads of the seams while they travel
            float head = (1 - seam) * seam * 4f * (1 - xo);
            if (head > 0.02f)
            {
                fill.Color = A(Warm, 0.9f * head);
                c.DrawCircle(sL, r.Top + 1, 3.2f, fill); c.DrawCircle(sR, r.Top + 1, 3.2f, fill);
                c.DrawCircle(sL, r.Bottom - 1, 3.2f, fill); c.DrawCircle(sR, r.Bottom - 1, 3.2f, fill);
            }
        }

        float ca = Step(t, T2In * 0.62, 420) * Out(xo, 0f, 0.3f);
        if (ca > 0.01f && panel > 0.2f)
        {
            c.Save(); c.ClipRect(new SKRect(pl, r.Top, pr, r.Bottom));
            // left half: who and how much (the name slides out from the watch side)
            float x0 = r.Left + 28, x1 = cx - HoleR - 20;
            Glyph(c, L.E.Platform, x0 + 10, r.Top + 27, 15f, Step(t, 520, 300) * ca);
            float ek = Step(t, 540, 360);
            SetText(Fonts.Inter.Bold, 13f, A(GoldHi, ca));
            c.Save(); c.ClipRect(new SKRect(x0 + 26, r.Top, x0 + 30 + (L.EyebrowW2 + 6) * ek, r.Top + 40));
            Trk(c, L.Eyebrow, x0 + 28, r.Top + 32, 2.2f);
            c.Restore();
            SetText(Fonts.BarlowCondensed.Black, 42f, White);
            text.Shader = sl[SlAmt2].Linear(new SKPoint(0, r.Top + 46), new SKPoint(0, r.Top + 80), [GoldHi, GoldMid, GoldLo], S055);
            text.Color = WA(Step(t, 640, 360) * ca);
            Odometer(c, L.SleeveAmount, x1 - L.SleeveAmount.Width, r.Top + 79, (float)((t - 660) / 760.0));
            text.Shader = null;
            float nk = Step(t, 580, 420);
            SetText(L.NameFace, L.T2NameSize, A(Ivory, ca * nk));
            Txt(c, L.T2Name, x0 + (1 - nk) * 24f, r.Top + 79);

            // right half: the message, scrolling if it's long
            float m0 = cx + HoleR + 20, m1 = r.Right - 28;
            float tk = Step(t, 600, 360);
            SetText(Fonts.Inter.Bold, 13f, A(GoldHi, ca));
            c.Save(); c.ClipRect(new SKRect(m0 - 2, r.Top, m0 + (L.ThanksW + 6) * tk, r.Top + 40));
            Trk(c, "THANK YOU", m0, r.Top + 32, 2.6f);
            c.Restore();
            float mk = Step(t, 720, 420);
            if (L.Msg.Length == 0)
            {
                SetText(Fonts.BarlowCondensed.Bold, 40f, A(Ivory, ca * mk));
                Txt(c, "FOR THE SUPPORT", m0 + (1 - mk) * 14f, r.Top + 79);
            }
            else
            {
                SetText(L.MsgFace, 23f, A(Txt2, 0.98f * ca * mk));
                float area = m1 - m0;
                float off = 0;
                bool scroll = L.MsgW > area;
                if (scroll) off = (float)Math.Clamp((t - T2In - 1600) / 1000.0 * T2Scroll, 0, L.MsgW - area);
                if (!scroll) Txt(c, L.Msg, m0 + (1 - mk) * 14f, r.Top + 76);
                else
                {
                    // soft edges where the text runs in / out (a mask on a message-sized layer, not a colour over it)
                    var mr = new SKRect(m0 - 2, r.Top + 40, m1 + 2, r.Bottom - 4);
                    int layer = c.SaveLayer(mr, null);
                    Txt(c, L.Msg, m0 - off + (1 - mk) * 14f, r.Top + 76);
                    byte fl = off > 1 ? (byte)0 : (byte)255, fr = off < L.MsgW - area - 1 ? (byte)0 : (byte)255;
                    dstIn.Shader = sl[SlMask].Linear(new SKPoint(mr.Left, 0), new SKPoint(mr.Right, 0),
                        [new SKColor(0, 0, 0, fl), SKColors.Black, SKColors.Black, new SKColor(0, 0, 0, fr)], L.MaskStops);
                    c.DrawRect(mr, dstIn); dstIn.Shader = null;
                    c.RestoreToCount(layer);
                }
            }
            c.Restore();
        }
        c.RestoreToCount(save);
        // the gold ring round the watch draws itself round from the top, both ways (clipped to the bar: the watch hangs below)
        if (ringK > 0.005f)
        {
            c.Save();
            c.ClipRect(r);
            stroke.Shader = sl[SlHoleRing].Linear(new SKPoint(0, cy - HoleR), new SKPoint(0, cy + HoleR), [GoldHi, GoldMid, GoldLo], S055);
            stroke.Color = White; stroke.StrokeWidth = 2.2f;
            var o = new SKRect(cx - HoleR, cy - HoleR, cx + HoleR, cy + HoleR);
            c.DrawArc(o, -90, 180 * ringK, false, stroke);
            c.DrawArc(o, -90, -180 * ringK, false, stroke);
            stroke.Shader = null;
            c.Restore();
        }
    }

    // ================================================================ tier 1: full-screen moment, then the band

    void DrawSplash(SKCanvas c, int w, int h, SKPoint? towerTop, Look L, double t, float abortK, double nowMs)
    {
        double len = curLen;
        float q = (float)Math.Clamp((t - (len - T1Collapse)) / T1Collapse, 0, 1);   // collapse progress
        float qe = EaseInOut(q);
        float dim = Ease((float)(t / 600.0)) * (1 - qe) * abortK;
        float S = w / 1920f;
        if (dim > 0.005f)
        {
            // the stage: deep broadcast navy over the race, a gold light from the top, a vignette
            // #604 perf pass 2: the three full-screen washes (navy, gold top light, vignette) in ONE pass - the same
            // layers composed in a shader instead of three 4K blends
            var top = sl[SlTop].Radial(new SKPoint(w / 2f, -h * 0.12f), w * 0.62f, [GoldMid.WithAlpha(70), new SKColor(0x12, 0x18, 0x26, 90), Clear], S05);
            var vig = sl[SlVig].Radial(new SKPoint(w / 2f, h * 0.5f), w * 0.7f, [Clear, new SKColor(0, 0, 0, 150)], SVig);
            fill.Color = SKColors.Black;
            fill.Shader = splashStage.Get(new SKColor(0x05, 0x07, 0x0D, (byte)(222 * dim)), top, WA(dim).Alpha, vig, WA(dim).Alpha);
            c.DrawRect(0, 0, w, h, fill); fill.Shader = null;
            Theme.Skin(c, new SKRect(0, 0, w, h), ThemeSurface.Card, nowMs, dim * 0.8f);
            // light rays fanning open from the top centre, then turning slowly
            float fan = 0.25f + 0.75f * QStep(t, 0, 1100);
            float rl = h * 1.25f;
            var o = new SKPoint(w / 2f, -h * 0.08f);
            fill.Shader = sl[SlRay].Linear(o, new SKPoint(o.X, o.Y + rl), [GoldHi.WithAlpha(17), GoldHi.WithAlpha(0)], S01);
            fill.Color = WA(dim);
            for (int i = 0; i < 9; i++)
            {
                double ang = -Math.PI / 2 + (i - 4) * 0.19 * fan + Math.Sin(nowMs / 3000.0 + i) * 0.02;
                path.Reset();
                path.MoveTo(o);
                path.LineTo(o.X + (float)Math.Cos(-ang - 0.035) * rl, o.Y + (float)Math.Sin(-ang - 0.035) * rl);
                path.LineTo(o.X + (float)Math.Cos(-ang + 0.035) * rl, o.Y + (float)Math.Sin(-ang + 0.035) * rl);
                path.Close();
                c.DrawPath(path, fill);
            }
            fill.Shader = null;
            GoldDust(c, w, h, nowMs, dim);
            // the warm bloom as the moment lands (the loading splash's hand-off flash, softer)
            float bloom = (float)(Math.Clamp((t - 120) / 260.0, 0, 1) * (1 - Math.Clamp((t - 380) / 900.0, 0, 1))) * abortK;
            if (bloom > 0.01f)
            {
                fill.Shader = sl[SlBloom].Radial(new SKPoint(w / 2f, h * 0.44f), w * 0.42f, [Warm.WithAlpha(120), GoldHi.WithAlpha(40), Clear], S045);
                fill.Color = WA(bloom); c.DrawRect(0, 0, w, h, fill); fill.Shader = null;
            }
        }

        float ca = Ease((float)((t - 250) / 500.0)) * abortK * (1 - Ease(q * 1.4f));
        if (ca <= 0.005f) return;
        int save = c.Save();
        // collapse: the whole moment shrinks into the band's spot on the tower
        var home = towerTop is SKPoint tt ? new SKPoint(tt.X + TimingTowerRenderer.TowerWidth / 2f, tt.Y + TimingTowerRenderer.CapHeight / 2f) : new SKPoint(w / 2f, h / 2f);
        if (qe > 0)
        {
            float sc = 1 - qe * 0.82f;
            float px = w / 2f + (home.X - w / 2f) * qe, py = h * 0.5f + (home.Y - h * 0.5f) * qe;
            c.Translate(px, py); c.Scale(sc); c.Translate(-w / 2f, -h * 0.5f);
        }
        c.Scale(S);
        const float CX = 960f;
        L.PrepareTier1(this);
        // corner brackets (the loading splash's frame) slide in
        float bk = Ease((float)((t - 100) / 700.0));
        float inset = 40 * (1 - bk);
        stroke.Shader = null; stroke.StrokeWidth = 2.4f; stroke.Color = A(Ivory, 0.59f * ca * bk);
        Brackets(c, new SKRect(330 - inset, 196 - inset, 1590 + inset, 900 + inset), 54);

        // eyebrow wipes open from the centre, gold rules reach out either side
        float ew = L.EyebrowW1;
        float ek = Step(t, 300, 520);
        SetText(Fonts.Inter.Bold, 22f, A(GoldHi, ca));
        c.Save(); c.ClipRect(new SKRect(CX - (ew / 2 + 8) * ek, 260, CX + (ew / 2 + 8) * ek, 310));
        Trk(c, L.Eyebrow, CX - ew / 2, 300, 7f);
        c.Restore();
        float rk = Step(t, 300, 700);
        if (rk > 0.005f)
        {
            float lx = CX - ew / 2 - 30, rx = CX + ew / 2 + 30;
            fill.Shader = sl[SlRuleL].Linear(new SKPoint(lx - 220, 0), new SKPoint(lx, 0), [GoldHi.WithAlpha(0), GoldHi.WithAlpha(220)], S01);
            fill.Color = WA(ca);
            c.Save(); c.Translate(lx, 0); c.Scale(rk, 1); c.Translate(-lx, 0); c.DrawRect(lx - 220, 292, 220, 1.5f, fill); c.Restore();
            fill.Shader = sl[SlRuleR].Linear(new SKPoint(rx, 0), new SKPoint(rx + 220, 0), [GoldHi.WithAlpha(220), GoldHi.WithAlpha(0)], S01);
            c.Save(); c.Translate(rx, 0); c.Scale(rk, 1); c.Translate(-rx, 0); c.DrawRect(rx, 292, 220, 1.5f, fill); c.Restore();
            fill.Shader = null;
        }

        // the name - the hero: a picture made once (glow + letters), settling from a touch larger; a gold shine runs through it once
        float nk = Ease((float)((t - 350) / 650.0));
        var hero = L.Hero(this, c);
        if (hero.Img != null && nk > 0.005f)
        {
            float ns = 1.06f - 0.06f * nk;
            c.Save();
            c.Translate(CX, 470); c.Scale(ns); c.Translate(-CX, -470);
            float nx = CX - L.HeroW / 2;
            var dst = new SKRect(nx + hero.Bounds.Left, 500 + hero.Bounds.Top, nx + hero.Bounds.Right, 500 + hero.Bounds.Bottom);
            img.Color = WA(ca * nk);
            float sk = (float)((t - 1100) / 1100.0);
            if (sk > 0 && sk < 1)
            {
                int layer = c.SaveLayer(dst, null);
                c.DrawImage(hero.Img, dst, img);
                float sx = nx - 200 + sk * (L.HeroW + 400);
                atop.Shader = sl[SlShine].Linear(new SKPoint(-120, 300), new SKPoint(120, 520), [GoldHi.WithAlpha(0), new SKColor(255, 246, 214, 235), GoldHi.WithAlpha(0)], S05);
                atop.Color = WA(ca);
                c.Save(); c.Translate(sx, 0); c.DrawRect(dst.Left - sx, dst.Top, dst.Width, dst.Height, atop); c.Restore();
                atop.Shader = null;
                c.RestoreToCount(layer);
            }
            else c.DrawImage(hero.Img, dst, img);
            c.Restore();
        }

        // gold rule with a diamond
        float gk = Ease((float)((t - 650) / 700.0));
        if (gk > 0.005f)
        {
            fill.Shader = sl[SlRule].Linear(new SKPoint(CX - 340, 0), new SKPoint(CX + 340, 0), [GoldLo.WithAlpha(0), GoldHi, GoldMid, GoldLo.WithAlpha(0)], SLine);
            fill.Color = WA(ca);
            c.Save(); c.Translate(CX, 0); c.Scale(gk, 1); c.Translate(-CX, 0); c.DrawRect(CX - 340, 552, 680, 2, fill); c.Restore();
            fill.Shader = null;
            path.Reset();
            path.MoveTo(CX, 545); path.LineTo(CX + 8, 553); path.LineTo(CX, 561); path.LineTo(CX - 8, 553); path.Close();
            fill.Color = A(GoldHi, ca * gk); c.DrawPath(path, fill);
        }

        // the amount, in gold, rolling up; its glow (a picture) comes up as it lands
        float ak = Ease((float)((t - 750) / 600.0));
        if (ak > 0.005f)
        {
            float ay = 680 + (1 - ak) * 16;
            var glow = L.AmountGlow(this, c);
            float ga = Step(t, 1350, 600);
            if (glow.Img != null && ga > 0.01f)
            {
                float gx = CX - L.Tier1Amount.Width / 2;
                img.Color = WA(ca * ga);
                c.DrawImage(glow.Img, new SKRect(gx + glow.Bounds.Left, ay + glow.Bounds.Top, gx + glow.Bounds.Right, ay + glow.Bounds.Bottom), img);
            }
            SetText(Fonts.BarlowCondensed.Black, 124f, White);
            text.Shader = sl[SlAmt1].Linear(new SKPoint(0, 590), new SKPoint(0, 682), [GoldHi, GoldMid, GoldLo], S055);
            text.Color = WA(ca * ak);
            Odometer(c, L.Tier1Amount, CX - L.Tier1Amount.Width / 2, ay, (float)((t - 780) / 900.0));
            text.Shader = null;
        }

        // the message, line by line
        if (L.Lines1.Length > 0)
        {
            SetText(L.MsgFace, 44f, Txt2);
            for (int i = 0; i < L.Lines1.Length; i++)
            {
                float mk = Ease((float)((t - 1100 - 110 * i) / 700.0));
                if (mk <= 0.01f) continue;
                text.Color = A(Txt2, 0.98f * ca * mk);
                Txt(c, L.Lines1[i], CX - L.Lines1W[i] / 2, 770 + i * 52 + (1 - mk) * 10);
            }
        }
        // a quiet "THANK YOU" signature at the bottom of the frame
        SetText(Fonts.Inter.Bold, 15f, A(Txt3, 0.78f * ca * Step(t, 1300, 600)));
        Trk(c, "THANK YOU FOR SUPPORTING THE STREAM", CX - L.SignatureW / 2, 942, 5f);
        c.RestoreToCount(save);
    }

    void Brackets(SKCanvas c, SKRect r, float len)
    {
        for (int i = 0; i < 4; i++)
        {
            float x = (i & 1) == 0 ? r.Left : r.Right, y = i < 2 ? r.Top : r.Bottom;
            int dx = (i & 1) == 0 ? 1 : -1, dy = i < 2 ? 1 : -1;
            c.DrawLine(x, y, x + dx * len, y, stroke);
            c.DrawLine(x, y, x, y + dy * len, stroke);
        }
    }

    void GoldDust(SKCanvas c, float w, float h, double nowMs, float a)
    {
        if (Perf.Light) return;
        double ts = nowMs / 1000.0;
        var gold = GoldHi;
        for (int i = 0; i < 70; i++)
        {
            uint hsh = Hash((uint)i * 2654435761u + 13u);
            float x0 = (hsh & 0xFFFF) / 65535f * w;
            float speed = 18f + ((hsh >> 16) & 0xFF) / 255f * 40f;
            float phase = ((hsh >> 24) & 0xFF) / 255f * (h + 40);
            float y = h + 20 - (float)((ts * speed + phase) % (h + 40));
            float x = x0 + (float)Math.Sin(ts * 0.6 + i) * 14f;
            float life = 1 - Math.Abs(y / h - 0.5f) * 2;
            float size = 1f + (Hash(hsh) & 0xFF) / 255f * 2.2f;
            fill.Color = gold.WithAlpha((byte)(170 * a * Math.Clamp(life, 0, 1)));
            c.DrawCircle(x, y, size, fill);
        }
    }

    static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }

    /// <summary>Tier 1's lingering band over the tower's cap (series logo), 400 x 90: slides in with a gold flare where it
    /// lands, a light sweep every 6 s, slides back out.</summary>
    void DrawBand(SKCanvas c, SKPoint tt, Look L, double t, float vis, double nowMs)
    {
        if (t < 0 || t > BandLen) return;
        float kin = QStep(t, 0, BandIn), kout = 1 - EaseIn((float)((t - (BandLen - BandOut)) / BandOut));
        float k = Math.Min(kin, kout) * vis;
        if (k <= 0.005f) return;
        L.PrepareBand(this);
        var r = new SKRect(tt.X, tt.Y, tt.X + TimingTowerRenderer.TowerWidth, tt.Y + TimingTowerRenderer.CapHeight);
        int save = c.Save();
        c.ClipRect(r);
        c.Translate(-(1 - k) * r.Width, 0);
        fill.Color = CardGlass.WithAlpha(250); c.DrawRect(r, fill);
        Theme.Skin(c, r, ThemeSurface.Card, nowMs, 1f);
        fill.Shader = sl[SlBandGlow].Radial(new SKPoint(r.Left + 40, r.MidY), 300, [GoldMid.WithAlpha(64), GoldMid.WithAlpha(0)], S01);
        fill.Color = White; c.DrawRect(r, fill); fill.Shader = null;
        double period = 6000, st = (t - 700) % period;
        if (t > 700) Sweep(c, SlSweepBand, r, (float)(st / 1300.0), 0.9f, GoldHi, 80);
        fill.Shader = GoldV(SlBandBar, r.Top, r.Bottom); fill.Color = White; c.DrawRect(r.Left, r.Top, 5, r.Height, fill); fill.Shader = null;
        GoldRule(c, SlBandLine, r.Left, r.Right, r.Bottom - 1.5f, 1.5f, 1f, 0.9f);
        // the flare where it lands
        float flare = (float)(Math.Clamp((t - BandIn * 0.6) / 160.0, 0, 1) * (1 - Math.Clamp((t - BandIn * 0.6 - 160) / 700.0, 0, 1)));
        if (flare > 0.01f)
        {
            fill.Shader = sl[SlBandFlare].Radial(new SKPoint(r.Left + 3, r.MidY), 140, [Warm.WithAlpha(150), GoldHi.WithAlpha(50), Clear], S045);
            fill.Color = WA(flare); c.DrawRect(r, fill); fill.Shader = null;
        }
        float tk = Step(t, 260, 380);
        SetText(Fonts.Inter.Bold, 13f, A(GoldHi, tk));
        Trk(c, "THANK YOU", r.Left + 22, r.Top + 28, 2.6f);
        SetText(Fonts.BarlowCondensed.Bold, 24f, A(GoldHi, tk));
        Txt(c, L.BandAmount, r.Right - 18 - L.BandAmountW, r.Top + 30);
        float nk = Step(t, 320, 460);
        SetText(L.BandFace, L.BandNameSize, Ivory);
        c.Save(); c.ClipRect(new SKRect(r.Left + 18, r.Top + 30, r.Left + 21 + (r.Width - 30) * nk, r.Bottom - 4));
        Txt(c, L.BandName, r.Left + 21, r.Top + 74);
        c.Restore();
        c.RestoreToCount(save);
    }

    // ================================================================ per-event layout (made once, on the render thread)

    /// <summary>An amount laid out once for the odometer: each character's string and x, which ones are digits.</summary>
    sealed class Amount
    {
        public string Text = "";
        public string[] Cells = Array.Empty<string>();
        public float[] CellX = Array.Empty<float>(), CellW = Array.Empty<float>();
        public int[] DigitVal = Array.Empty<int>();
        public readonly float[] DigitW = new float[10];
        public int Digits;
        public float Width;

        public static Amount Of(string s, SKPaint p)
        {
            var a = new Amount { Text = s, Width = p.MeasureText(s) };
            var cells = new List<string>(); var xs = new List<float>(); var ws = new List<float>(); var dv = new List<int>();
            int i = 0;
            while (i < s.Length)
            {
                int n = char.IsSurrogatePair(s, i) ? 2 : 1;
                string cell = s.Substring(i, n);
                xs.Add(p.MeasureText(s[..i]));
                ws.Add(p.MeasureText(cell));
                cells.Add(cell);
                dv.Add(n == 1 && s[i] is >= '0' and <= '9' ? s[i] - '0' : -1);
                i += n;
            }
            a.Cells = cells.ToArray(); a.CellX = xs.ToArray(); a.CellW = ws.ToArray(); a.DigitVal = dv.ToArray();
            foreach (int d in a.DigitVal) if (d >= 0) a.Digits++;
            for (int d = 0; d < 10; d++) a.DigitW[d] = p.MeasureText(((char)('0' + d)).ToString());
            return a;
        }
    }

    /// <summary>A text picture made once per event at the screen's real size (tier 1's big name, the amount's glow).</summary>
    sealed class Plate : IDisposable
    {
        public SKImage? Img; public SKRect Bounds; public float Scale;
        public void Dispose() { Img?.Dispose(); Img = null; }
    }

    static Plate MakePlate(string s, SKTypeface tf, float size, SKColor col, float glowSigma, byte glowA, bool glowOnly, float scale, SKShader? fillShader = null)
    {
        var pl = new Plate { Scale = scale };
        if (string.IsNullOrEmpty(s)) return pl;
        using var p = new SKPaint { IsAntialias = true, Typeface = tf, TextSize = size, Color = col };
        var b = new SKRect(); p.MeasureText(s, ref b);
        b.Inflate(glowSigma * 3 + 4, glowSigma * 3 + 4);
        int w = Math.Max(1, (int)Math.Ceiling(b.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(b.Height * scale));
        if ((long)w * h > 8_000_000) return pl;
        using var surf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surf == null) return pl;
        var c = surf.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(scale);
        c.Translate(-b.Left, -b.Top);
        if (glowSigma > 0)
        {
            using var gp = new SKPaint { IsAntialias = true, Typeface = tf, TextSize = size, Color = col.WithAlpha(glowA), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, glowSigma) };
            c.DrawText(s, 0, 0, gp);
        }
        if (!glowOnly)
        {
            p.Shader = fillShader;
            c.DrawText(s, 0, 0, p);
        }
        var snap = surf.Snapshot();
        pl.Img = GpuContext.Pin(snap);
        if (!ReferenceEquals(pl.Img, snap)) snap.Dispose();
        pl.Bounds = b;
        return pl;
    }

    static readonly Dictionary<int, SKTypeface?> fallbackFaces = new();

    /// <summary>The typeface for a name: <paramref name="pref"/>, or - when the name's letters aren't in it (Japanese, Korean,
    /// Cyrillic, Greek ...) - a font on this PC that has them, so a name never turns into "A viewer" just for its script.</summary>
    internal static SKTypeface NameFace(string name, SKTypeface pref, out string drawable)
    {
        drawable = ViewerTiers.Drawable(name, pref);
        int lost = 0, firstLost = 0;
        foreach (var r in name.EnumerateRunes())
            if (Rune.IsLetter(r) && !pref.ContainsGlyph(r.Value)) { if (lost++ == 0) firstLost = r.Value; }
        if (lost == 0) return pref;
        SKTypeface? fb;
        lock (fallbackFaces)
        {
            if (!fallbackFaces.TryGetValue(firstLost, out fb))
            {
                try { fb = SKFontManager.Default.MatchCharacter(firstLost); } catch { fb = null; }
                if (fb == null || !fb.ContainsGlyph(firstLost)) fb = FromFallbackFiles(firstLost);
                if (fallbackFaces.Count > 64) fallbackFaces.Clear();
                fallbackFaces[firstLost] = fb;
            }
        }
        if (fb == null) return pref;
        string d2 = ViewerTiers.Drawable(name, fb);
        if (d2.Length > drawable.Length) { drawable = d2; return fb; }
        return pref;
    }

    // fonts tried in order when the system's own fallback can't name one (Windows' own fonts first; the Linux ones are
    // for the preview tools). Loaded once each, on first need.
    static readonly string[] FallbackFiles =
    {
        @"C:\Windows\Fonts\segoeuib.ttf", @"C:\Windows\Fonts\segoeui.ttf", @"C:\Windows\Fonts\YuGothB.ttc", @"C:\Windows\Fonts\malgunbd.ttf",
        @"C:\Windows\Fonts\msyhbd.ttc", @"C:\Windows\Fonts\Nirmala.ttf", @"C:\Windows\Fonts\arialbd.ttf", @"C:\Windows\Fonts\seguisym.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    };
    static readonly Dictionary<string, SKTypeface?> fileFaces = new();

    static SKTypeface? FromFallbackFiles(int ch)
    {
        foreach (var f in FallbackFiles)
        {
            if (!fileFaces.TryGetValue(f, out var tf))
            {
                try { tf = System.IO.File.Exists(f) ? SKTypeface.FromFile(f) : null; } catch { tf = null; }
                fileFaces[f] = tf;
            }
            if (tf != null && tf.ContainsGlyph(ch)) return tf;
        }
        return null;
    }

    static readonly Regex Digits = new(@"\d[\d.,\s]*\d|\d", RegexOptions.Compiled);

    /// <summary>The amount as it came in, unless the font can't draw its currency sign ("₹100.00" in a font without ₹):
    /// then the number with the currency code ("100.00 INR"). Emoji and the like are dropped.</summary>
    static string SafeAmount(ViewerEvent e, SKTypeface tf)
    {
        string a = AmountText(e);
        string d = ViewerTiers.Drawable(a, tf);
        if (d.Replace(" ", "") == a.Replace(" ", "")) return a;
        var m = Digits.Match(a);
        if (m.Success && e.Currency.Length == 3) return m.Value.Trim() + " " + e.Currency;
        return d;
    }

    /// <summary>Everything a look draws that is text, laid out once when the look starts (strings, wrapped lines, fitted
    /// names, sizes, widths, amount cells); built lazily per tier on the render thread.</summary>
    sealed class Look : IDisposable
    {
        public readonly ViewerEvent E;
        public readonly ViewerTier Tier;
        public readonly string Eyebrow;
        public string[] Lines = Array.Empty<string>();
        public SKTypeface NameFace = Fonts.Inter.Bold;
        string drawName = "";

        public Look(ViewerEvent e, ViewerTier tier)
        {
            E = e; Tier = tier;
            string kind = KindLabel(e), plat = PlatformLabel(e.Platform);
            Eyebrow = kind == plat ? kind : kind + "  ·  " + plat;
        }

        /// <summary>The message as it can be drawn, and the face to draw it in (the look's own font, or a font on this PC
        /// that has the message's letters - a Cyrillic or Japanese comment isn't blanked out).</summary>
        public SKTypeface MsgFace = Fonts.Inter.Medium;
        string Msg0(SKTypeface tf) { MsgFace = ViewerTierRenderer.NameFace(E.Message, tf, out string d); return d; }

        // chat
        public float ChatH, ChatLabelW; public string ChatName = ""; bool chat;
        public void PrepareChat(ViewerTierRenderer v, float w)
        {
            if (chat) return; chat = true;
            NameFace = ViewerTierRenderer.NameFace(E.Name, Fonts.Inter.Bold, out drawName);
            if (drawName.Length == 0) drawName = "A viewer";
            var p = v.text;
            v.SetText(Fonts.Inter.Bold, 11f, Txt3); ChatLabelW = v.TrkW("LIVE CHAT", 1.8f);
            v.SetText(NameFace, 19f, Ivory); ChatName = Fit(p, drawName, w - 72 - ChatLabelW - 30);
            string cm = Msg0(Fonts.Inter.Medium);
            v.SetText(MsgFace, 20f, Txt2);
            Lines = Wrap(p, cm, w - 44, 3).ToArray();
            ChatH = 56 + Lines.Length * 27 + (Lines.Length > 0 ? 6 : -8);
        }

        // tier 3
        public float T3H, ChipW, EyebrowW, T3NameSize; public string T3Name = ""; public Amount ChipAmount = new(); bool t3;
        public void PrepareTier3(ViewerTierRenderer v, float w)
        {
            if (t3) return; t3 = true;
            NameFace = ViewerTierRenderer.NameFace(E.Name, Fonts.CormorantGaramond.Bold, out drawName);
            if (drawName.Length == 0) drawName = "A viewer";
            var p = v.text;
            string tm = Msg0(Fonts.Inter.Medium);
            v.SetText(MsgFace, 20f, Txt2);
            Lines = Wrap(p, tm, w - 120 - 30, 2).ToArray();
            T3H = Lines.Length > 0 ? 114 + Lines.Length * 26 + 12 : 108;
            v.SetText(Fonts.BarlowCondensed.Bold, 28f, Ivory);
            ChipAmount = Amount.Of(SafeAmount(E, Fonts.BarlowCondensed.Bold), p);
            ChipW = ChipAmount.Width + 30;
            v.SetText(Fonts.Inter.Bold, 13f, Ivory); EyebrowW = v.TrkW(Eyebrow, 2.4f);
            v.SetText(NameFace, 42f, Ivory);
            float maxN = (w - 22 - ChipW) - 16 - 120;
            ShrinkToFit(p, drawName, maxN, 28f);
            T3NameSize = p.TextSize;
            T3Name = Fit(p, drawName, maxN);
        }

        // tier 2
        public float MsgW, EyebrowW2, ThanksW, T2NameSize; public string Msg = "", T2Name = ""; public Amount SleeveAmount = new();
        public float[] MaskStops = { 0f, 0.03f, 0.96f, 1f }; bool t2;
        public void PrepareTier2(ViewerTierRenderer v)
        {
            if (t2) return; t2 = true;
            NameFace = ViewerTierRenderer.NameFace(E.Name, Fonts.BarlowCondensed.Bold, out drawName);
            if (drawName.Length == 0) drawName = "A viewer";
            var p = v.text;
            Msg = Msg0(Fonts.Inter.Medium);
            v.SetText(MsgFace, 23f, Txt2);
            MsgW = p.MeasureText(Msg);
            float barW = SessionBarRenderer.BarWidth, area = barW / 2f - HoleR - 20 - 28;
            float mrW = area + 4;
            MaskStops = new[] { 0f, Math.Max(0.001f, 30f / mrW), 1 - Math.Max(0.001f, 34f / mrW), 1f };
            v.SetText(Fonts.Inter.Bold, 13f, Ivory); EyebrowW2 = v.TrkW(Eyebrow, 2.2f); ThanksW = v.TrkW("THANK YOU", 2.6f);
            v.SetText(Fonts.BarlowCondensed.Black, 42f, Ivory);
            SleeveAmount = Amount.Of(SafeAmount(E, Fonts.BarlowCondensed.Black), p);
            float x0 = 28, x1 = barW / 2f - HoleR - 20;
            string up = drawName.ToUpperInvariant();
            v.SetText(NameFace, 42f, Ivory);
            float maxN = x1 - SleeveAmount.Width - 18 - x0;
            ShrinkToFit(p, up, maxN, 30f);
            T2NameSize = p.TextSize;
            T2Name = Fit(p, up, maxN);
        }

        // tier 1
        public float EyebrowW1, HeroW, SignatureW; public string HeroName = ""; public Amount Tier1Amount = new();
        public string[] Lines1 = Array.Empty<string>(); public float[] Lines1W = Array.Empty<float>(); float heroSize; bool t1;
        Plate? hero, glow;
        public void PrepareTier1(ViewerTierRenderer v)
        {
            if (t1) return; t1 = true;
            NameFace = ViewerTierRenderer.NameFace(E.Name, Fonts.BarlowCondensed.Black, out drawName);
            if (drawName.Length == 0) drawName = "A viewer";
            var p = v.text;
            v.SetText(Fonts.Inter.Bold, 22f, Ivory); EyebrowW1 = v.TrkW(Eyebrow, 7f);
            v.SetText(Fonts.Inter.Bold, 15f, Ivory); SignatureW = v.TrkW("THANK YOU FOR SUPPORTING THE STREAM", 5f);
            v.SetText(NameFace, 176f, Ivory);
            string up = drawName.ToUpperInvariant();
            ShrinkToFit(p, up, 1180, 60);   // (the longest names - 32 characters - fit whole at 60+)
            HeroName = Fit(p, up, 1180);
            heroSize = p.TextSize;
            HeroW = p.MeasureText(HeroName);
            v.SetText(Fonts.BarlowCondensed.Black, 124f, Ivory);
            Tier1Amount = Amount.Of(SafeAmount(E, Fonts.BarlowCondensed.Black), p);
            string msg = Msg0(Fonts.CormorantGaramond.SemiBold);
            if (msg.Length > 0)
            {
                v.SetText(MsgFace, 44f, Txt2);
                Lines1 = Wrap(p, "“" + msg + "”", 1180, 3).ToArray();
                Lines1W = Lines1.Select(l => p.MeasureText(l)).ToArray();
            }
        }

        static float DeviceScale(SKCanvas c)
        {
            var m = c.TotalMatrix;
            float s = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            return MathF.Round(Math.Clamp(s, 0.25f, 4f) * 10f) / 10f;
        }

        /// <summary>The big name with its soft glow, made once at the screen's size (re-made only if that changes a lot).</summary>
        public Plate Hero(ViewerTierRenderer v, SKCanvas c)
        {
            float s = DeviceScale(c);
            if (hero == null || Math.Abs(hero.Scale - s) > 0.35f && s >= hero.Scale)
            {
                hero?.Dispose();
                hero = MakePlate(HeroName, NameFace, heroSize, Ivory, 18, 70, false, s);
            }
            return hero;
        }

        /// <summary>The amount's warm glow (the numbers themselves roll live on top).</summary>
        public Plate AmountGlow(ViewerTierRenderer v, SKCanvas c)
        {
            float s = DeviceScale(c);
            if (glow == null || Math.Abs(glow.Scale - s) > 0.35f && s >= glow.Scale)
            {
                glow?.Dispose();
                glow = MakePlate(Tier1Amount.Text, Fonts.BarlowCondensed.Black, 124f, GoldMid, 16, 90, true, s);
            }
            return glow;
        }

        // band
        public string BandName = "", BandAmount = ""; public float BandAmountW, BandNameSize; public SKTypeface BandFace = Fonts.BarlowCondensed.Bold; bool bd;
        public void PrepareBand(ViewerTierRenderer v)
        {
            if (bd) return; bd = true;
            if (!t1) PrepareTier1(v);
            var p = v.text;
            v.SetText(Fonts.BarlowCondensed.Bold, 24f, Ivory);
            BandAmount = SafeAmount(E, Fonts.BarlowCondensed.Bold); BandAmountW = p.MeasureText(BandAmount);
            BandFace = ReferenceEquals(NameFace, Fonts.BarlowCondensed.Black) ? Fonts.BarlowCondensed.Bold : NameFace;
            v.SetText(BandFace, 40f, Ivory);
            string up = drawName.ToUpperInvariant();
            float maxW = TimingTowerRenderer.TowerWidth - 40;
            ShrinkToFit(p, up, maxW, 28);
            BandNameSize = p.TextSize;
            BandName = Fit(p, up, maxW);
        }

        public void Dispose() { hero?.Dispose(); glow?.Dispose(); hero = glow = null; }
    }

    static string Fit(SKPaint p, string s, float maxW)
    {
        if (maxW <= 0 || p.MeasureText(s) <= maxW) return s;
        while (s.Length > 1 && p.MeasureText(s + "…") > maxW) s = s[..^1];
        if (s.Length > 0 && char.IsHighSurrogate(s[^1])) s = s[..^1];
        return s.TrimEnd() + "…";
    }

    static void ShrinkToFit(SKPaint p, string s, float maxW, float minSize)
    {
        while (p.TextSize > minSize && p.MeasureText(s) > maxW) p.TextSize -= 2f;
    }

    /// <summary>Word wrap to at most <paramref name="maxLines"/> lines; the last line ends in "…" if text is left over.</summary>
    static List<string> Wrap(SKPaint p, string s, float maxW, int maxLines)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return lines;
        // split into pieces no wider than a line (a very long "word" is broken)
        var words = new List<string>();
        foreach (var w0 in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string w = w0;
            while (w.Length > 1 && p.MeasureText(w) > maxW)
            {
                int n = w.Length - 1;
                while (n > 1 && p.MeasureText(w[..n]) > maxW) n--;
                if (char.IsHighSurrogate(w[n - 1]) && n > 1) n--;
                words.Add(w[..n]); w = w[n..];
            }
            words.Add(w);
        }
        string line = "";
        int i = 0;
        for (; i < words.Count; i++)
        {
            string tryL = line.Length == 0 ? words[i] : line + " " + words[i];
            if (p.MeasureText(tryL) <= maxW) { line = tryL; continue; }
            if (line.Length > 0) lines.Add(line);
            line = words[i];
            if (lines.Count == maxLines) break;
        }
        if (lines.Count < maxLines) { if (line.Length > 0) lines.Add(line); }
        else lines[^1] = Fit(p, lines[^1] + "…", maxW);   // text left over: the last line ends in "…"
        return lines;
    }

    // ================================================================ the thank-you roll

    /// <summary>One page of the roll, its strings fitted once when the roll starts.</summary>
    sealed class RollPage
    {
        public string Eyebrow = "", Title = "", Sub = "";
        public string[] Names = Array.Empty<string>(), Right = Array.Empty<string>(), Rank = Array.Empty<string>();
        public float[] RightW = Array.Empty<float>();
        public SKTypeface[] Faces = Array.Empty<SKTypeface>();
        public int Cols, Rows, Part, Parts, ShownRows;
        public double Len;
        public float TitleSize, EyebrowW;
        public bool Ranked => Cols == 2;
        public List<(string Name, string Right)> Raw = new();
    }
    List<RollPage>? roll;
    double rollStart, rollTotalMs;
    int rollPageCued = -1;
    string rollTrack = "";

    void StartRoll(RaceSnapshot snap, bool sample, double nowMs)
    {
        var people = sample ? ViewerRoll.Sample() : ViewerRoll.Snapshot();
        roll = BuildRoll(people);
        LayoutRoll(roll);
        rollTotalMs = 0; foreach (var p in roll) rollTotalMs += p.Len * 1000;
        rollStart = nowMs;
        rollPageCued = 0;
        rollTrack = (snap.TrackDisplayName ?? "").ToUpperInvariant();
        if (cur != null && abortAt < 0) { abortAt = nowMs; pending.AddFirst(cur); }
        Cue("outro_results");
        ViewerFeed.Log?.Invoke($"thank-you roll: {(sample ? "sample" : "this stream")} - {people.Count} names, {roll.Count} pages");
    }

    /// <summary>Builds the pages: everyone, the supporters, then one page per tier (several pages when a list is long).</summary>
    public static List<(string Eyebrow, string Title, string Sub, int Count)> Outline(List<ViewerRoll.Person> people) =>
        BuildRoll(people).Select(p => (p.Eyebrow, p.Title, p.Sub, p.Raw.Count)).ToList();

    static ViewerTier PersonTier(ViewerRoll.Person p)
    {
        var t = p.Usd > 0 ? ViewerTiers.TierOfUsd(p.Usd) : ViewerTier.Tier3;
        if (p.Gifts >= ViewerTiers.GiftTier2 && t == ViewerTier.Tier3) t = ViewerTier.Tier2;
        return t;
    }

    static List<RollPage> BuildRoll(List<ViewerRoll.Person> people)
    {
        var pages = new List<RollPage>();
        void AddPages(string eyebrow, string title, string sub, List<(string, string)> names, int cols, int rows)
        {
            int per = cols * rows;
            int parts = Math.Max(1, (names.Count + per - 1) / per);
            for (int i = 0; i < parts; i++)
            {
                var chunk = names.Skip(i * per).Take(per).ToList();
                double len = Math.Clamp(6 + chunk.Count * 0.28, 10, 20);
                pages.Add(new RollPage { Eyebrow = eyebrow, Title = title, Sub = sub, Raw = chunk, Cols = cols, Rows = rows, Len = len, Part = i + 1, Parts = parts });
            }
        }
        var everyone = people.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => (p.Name, "")).ToList();
        if (everyone.Count == 0)
            pages.Add(new RollPage { Eyebrow = "IN THE GRANDSTAND TODAY", Title = "THANKS FOR WATCHING", Sub = "See you at the next race", Cols = 1, Rows = 1, Len = 10, Part = 1, Parts = 1 });
        else
            AddPages("IN THE GRANDSTAND TODAY", "THANKS FOR WATCHING", $"Everyone who chatted, joined, subscribed or supported today  ·  {everyone.Count} {(everyone.Count == 1 ? "name" : "names")}", everyone, 4, 11);
        var supporters = people.Where(p => p.Supporter).OrderByDescending(p => p.Usd).ThenByDescending(p => p.Gifts).ThenBy(p => p.First).ToList();
        if (supporters.Count > 0)
            AddPages("THANK YOU", "TO OUR SUPPORTERS", "Super Chats, tips, bits, members and subs", supporters.Select(p => (p.Name, "")).ToList(), 3, 11);
        foreach (var tier in new[] { ViewerTier.Tier1, ViewerTier.Tier2, ViewerTier.Tier3 })
        {
            var list = supporters.Where(p => PersonTier(p) == tier).ToList();
            if (list.Count == 0) continue;
            string label = tier switch { ViewerTier.Tier1 => "TIER 1", ViewerTier.Tier2 => "TIER 2", _ => "TIER 3" };
            AddPages("SUPPORTERS  ·  " + label, label, ViewerTiers.RangeText(tier), list.Select(p => (p.Name, RightText(p))).ToList(), 2, 9);
        }
        return pages;
    }

    /// <summary>Fits every string of the roll once (names, amounts, ranks, the page eyebrows).</summary>
    void LayoutRoll(List<RollPage> pages)
    {
        var p = text;
        foreach (var pg in pages)
        {
            pg.Eyebrow = pg.Eyebrow + (pg.Parts > 1 ? $"  ·  {pg.Part} OF {pg.Parts}" : "");
            SetText(Fonts.BarlowCondensed.Bold, 30f, Ivory); pg.EyebrowW = TrkW(pg.Eyebrow, 6f);
            SetText(Fonts.BarlowCondensed.Black, 124f, Ivory); ShrinkToFit(p, pg.Title, 1790, 70); pg.TitleSize = p.TextSize;
            SetText(Fonts.Inter.Medium, 24f, Txt2); pg.Sub = Fit(p, pg.Sub, 1790);
            int n = pg.Raw.Count;
            float colW = (1860 - 64) / (float)pg.Cols;
            pg.Names = new string[n]; pg.Right = new string[n]; pg.RightW = new float[n]; pg.Rank = new string[n]; pg.Faces = new SKTypeface[n];
            for (int i = 0; i < n; i++)
            {
                var (nm, right) = pg.Raw[i];
                var face = NameFace(nm, Fonts.BarlowCondensed.SemiBold, out string safe);
                pg.Faces[i] = face;
                pg.Right[i] = right;
                SetText(Fonts.BarlowCondensed.Bold, 26f, Ivory);
                pg.RightW[i] = right.Length > 0 ? p.MeasureText(right) : 0;
                pg.Rank[i] = ((pg.Part - 1) * pg.Cols * pg.Rows + i + 1).ToString(CultureInfo.InvariantCulture);
                SetText(face, 40f, Ivory);
                pg.Names[i] = safe.Length == 0 ? "" : pg.Ranked ? Fit(p, safe, colW - 40 - 70 - pg.RightW[i] - 30) : Fit(p, safe, colW - 50);
            }
            pg.ShownRows = pg.Ranked ? pg.Rows : Math.Max(1, Math.Min(pg.Rows, (n + pg.Cols - 1) / pg.Cols));   // short lists: even columns
        }
    }

    static string RightText(ViewerRoll.Person p)
    {
        var parts = new List<string>();
        if (p.Usd > 0 && ViewerRoll.TotalText(p) is { Length: > 0 } tot) parts.Add(tot);
        if (p.Gifts > 0) parts.Add($"{p.Gifts} GIFTED");
        else if (p.Member && p.Usd <= 0) parts.Add("MEMBER");
        return string.Join("  ·  ", parts);
    }

    int RollPageAt(double t, out double pageStartMs)
    {
        var pages = roll!;
        double acc = 0; int pi = 0;
        for (; pi < pages.Count - 1; pi++) { if (t < acc + pages[pi].Len * 1000) break; acc += pages[pi].Len * 1000; }
        pageStartMs = acc;
        return pi;
    }

    void DrawRoll(SKCanvas c, int w, int h, RaceSnapshot? snap, double t, double nowMs)
    {
        var pages = roll!;
        double total = rollTotalMs;
        float stageA = Ease((float)(t / 700.0)) * (1 - Ease((float)((t - total) / 700.0)));
        if (stageA <= 0.003f) return;
        float S = w / 1920f;
        c.Save();
        c.Scale(S);
        float W = 1920, H = h / S;
        // stage: the outro's navy with the soft gold light from the top (the light grows in)
        float lightK = Step(t, 150, 1200) * stageA;
        // #604 perf pass 2: navy stage, gold top light and vignette composed in one full-screen pass (was three)
        var rtop = sl[SlRollTop].Radial(new SKPoint(W / 2, -H * 0.15f), W * 0.75f, [GoldMid.WithAlpha(40), new SKColor(0x12, 0x18, 0x26, 120), Clear], S045);
        var rvig = sl[SlRollVig].Radial(new SKPoint(W / 2, H * 0.45f), W * 0.72f, [Clear, new SKColor(0, 0, 0, 150)], SVig2);
        fill.Color = SKColors.Black;
        fill.Shader = rollStage.Get(new SKColor(0x08, 0x0A, 0x10, (byte)(255 * stageA)), rtop, WA(lightK).Alpha, rvig, WA(stageA).Alpha);
        c.DrawRect(0, 0, W, H, fill); fill.Shader = null;
        Theme.Skin(c, new SKRect(0, 0, W, H), ThemeSurface.Card, nowMs, stageA * 0.85f);
        GoldDust(c, W, H, nowMs, stageA * 0.45f);

        // top bar: THANK YOU tab slides in from the left, track, GRANDSTAND; a hairline draws across
        float tabK = QStep(t, 200, 600) * stageA;
        c.Save(); c.Translate(-(1 - tabK) * 300, 0);
        path.Reset(); path.MoveTo(72, 40); path.LineTo(262, 40); path.LineTo(250, 86); path.LineTo(60, 86); path.Close();
        fill.Shader = GoldV(SlRollTab, 40, 86); fill.Color = WA(tabK); c.DrawPath(path, fill); fill.Shader = null;
        SetText(Fonts.BarlowCondensed.Black, 30f, new SKColor(0x0A, 0x0C, 0x11, (byte)(255 * tabK)));
        float tw = TrkW("THANK YOU", 2f);
        Trk(c, "THANK YOU", 161 - tw / 2, 74, 2f);
        c.Restore();
        float topK = Step(t, 500, 600) * stageA;
        SetText(Fonts.BarlowCondensed.Bold, 24f, A(Ivory, 0.94f * topK));
        if (rollTrack.Length > 0) Trk(c, rollTrack, 290, 72, 2f);
        SetText(Fonts.Inter.Bold, 15f, A(Txt3, 0.86f * topK));
        Trk(c, "GRANDSTAND", 1860 - TrkW("GRANDSTAND", 6f), 70, 6f);
        fill.Color = new SKColor(255, 255, 255, (byte)(28 * stageA)); c.DrawRect(60, 104, 1800 * QStep(t, 300, 900), 1, fill);

        // which page
        int pi = RollPageAt(t, out double acc);
        var pg = pages[pi];
        double lt = t - acc, plen = pg.Len * 1000;
        bool last = pi == pages.Count - 1;
        // a page leaves names first (bottom up, quickly), then its heading slides away
        float pout = last ? 1f : 1f - Ease((float)((lt - (plen - 420)) / 420.0));
        float pin = Ease((float)(lt / 600.0));
        float pa = Math.Min(pin, pout) * stageA;

        c.Save();
        c.Translate((1 - pin) * 60 - (1 - pout) * 60, 0);
        float ek = Step(lt, 80, 520);
        SetText(Fonts.BarlowCondensed.Bold, 30f, A(GoldHi, pa));
        c.Save(); c.ClipRect(new SKRect(60, 160, 64 + (pg.EyebrowW + 8) * ek, 210));
        Trk(c, pg.Eyebrow, 64, 196, 6f);
        c.Restore();
        // the title rises up out of a line (a wipe from below)
        float tk = QStep(lt, 150, 650);
        SetText(Fonts.BarlowCondensed.Black, pg.TitleSize, A(Ivory, Math.Min(pout, tk) * stageA));
        c.Save(); c.ClipRect(new SKRect(40, 200, 1880, 336));
        Txt(c, pg.Title, 56, 316 + (1 - tk) * 110);
        c.Restore();
        SetText(Fonts.Inter.Medium, 24f, A(Txt2, 0.92f * Step(lt, 420, 500) * pout * stageA));
        Txt(c, pg.Sub, 62, 366);
        GoldRule(c, SlRollRule, 40, 900, 394, 2, QStep(lt, 380, 800), 0.9f * pout * stageA);
        c.Restore();

        // names in columns, cascading in top to bottom, column by column; out in reverse, quickly
        float gx = 64, gy = 470, colW = (1860 - 64) / (float)pg.Cols, rowH = pg.Ranked ? 58f : 50f;
        int rows = pg.ShownRows, n = pg.Names.Length;
        for (int i = 0; i < n; i++)
        {
            if (pg.Names[i].Length == 0) continue;
            int col = i / rows, row = i % rows;
            float x = gx + col * colW, y = gy + row * rowH;
            float outK = last ? 1f : 1f - Ease((float)((lt - (plen - 700) - (n - 1 - i) * 8) / 260.0));
            float ik = Ease((float)((lt - 450 - i * 45) / 420.0)) * outK * stageA;
            if (ik <= 0.01f) continue;
            float yk = y + (1 - ik) * 14;
            if (pg.Ranked)
            {
                // tier pages: a rank, the name and the amount, on a faint plate
                fill.Color = new SKColor(255, 255, 255, (byte)(10 * ik)); c.DrawRoundRect(x, yk - 40, colW - 40, 52, 6, 6, fill);
                SetText(Fonts.BarlowCondensed.Bold, 26f, A(GoldHi, 0.9f * ik));
                Txt(c, pg.Rank[i], x + 14, yk - 4);
                if (pg.Right[i].Length > 0) { text.Color = A(GoldHi, ik); Txt(c, pg.Right[i], x + colW - 40 - 16 - pg.RightW[i], yk - 4); }
                SetText(pg.Faces[i], 40f, A(Ivory, ik));
                Txt(c, pg.Names[i], x + 62, yk - 1);
            }
            else
            {
                fill.Color = A(GoldMid, 0.78f * ik);
                c.DrawCircle(x + 6, yk - 13, 3.2f, fill);
                SetText(pg.Faces[i], 40f, A(Ivory, ik));
                Txt(c, pg.Names[i], x + 24, yk);
            }
        }

        // footer: page dots (the current one fills as it plays), and an honest note on who is listed
        float fk = Step(t, 700, 600) * stageA;
        float dx = W / 2 - (pages.Count - 1) * 14;
        for (int i = 0; i < pages.Count; i++)
        {
            float x = dx + i * 28;
            if (i == pi)
            {
                fill.Color = new SKColor(255, 255, 255, (byte)(50 * fk)); c.DrawRoundRect(x - 12, 1006, 24, 6, 3, 3, fill);
                float f = (float)Math.Clamp(lt / plen, 0, 1);
                fill.Color = A(GoldHi, fk); c.DrawRoundRect(x - 12, 1006, 24 * f, 6, 3, 3, fill);
            }
            else { fill.Color = (i < pi ? GoldMid : White).WithAlpha((byte)((i < pi ? 200 : 60) * fk)); c.DrawCircle(x, 1009, 3.5f, fill); }
        }
        SetText(Fonts.Inter.Bold, 13f, A(Txt3, 0.75f * fk));
        Trk(c, "NAMES FROM CHAT, MEMBERS, SUBS AND SUPPORT THIS STREAM", 64, 1014, 2.4f);
        c.Restore();
    }

    public void Dispose()
    {
        look?.Dispose(); bandLook?.Dispose();
        fill.Dispose(); stroke.Dispose(); text.Dispose(); img.Dispose(); atop.Dispose(); dstIn.Dispose(); shadowPaint.Dispose();
        blur14.Dispose(); blur18.Dispose(); shape.Dispose(); path.Dispose(); path2.Dispose(); edgeMeasure.Dispose();
        foreach (var s in sl) s.Dispose();
        splashStage.Dispose(); rollStage.Dispose();
    }
}
