using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-29 (#494): the viewer cards, played one at a time from <see cref="ViewerFeed"/>.
/// <list type="bullet">
/// <item>Super Chat / tip / bits: a strip slides down from under the session bar (the standings-ticker look) and the
///   viewer's name, amount and message scroll through it once. From the "big" amount on, a short banner also covers the
///   top (series logo) of the timing tower.</item>
/// <item>Member / sub: a splash card in the pre-race card style, "NEW MEMBER · name", under the session bar.</item>
/// <item>Track fund (option): a slim goal bar under the session bar.</item>
/// </list>
/// Nothing ever plays over the race start (until the leader has done lap 1), a battle, an onboard, a replay, a story card,
/// a class transition or the finish (white flag on): events wait in the queue until the screen is clear; one that is
/// cut short by a battle (or the like) goes back to the front of the queue.
/// </summary>
public sealed class ViewerCardsRenderer : IDisposable
{
    public const float StripH = 44f, BadgeW = 172f, FundH = 20f, CardW = 640f, CardH = 104f;
    const double StripInMs = 450, StripOutMs = 380, ScrollPxPerSec = 150;
    const double CardInMs = 520, CardHoldMs = 6500, CardOutMs = 450;
    const double BannerAtMs = 350, BannerMs = 6500, BannerInMs = 420, BannerOutMs = 380;
    const double GapMs = 1500, AbortMs = 260;

    static readonly SKColor Glass = new(10, 15, 27, 214);
    static readonly SKColor CardGlass = new(0x0B, 0x0E, 0x14, 236);
    static readonly SKColor Ivory = new(252, 250, 245);
    static readonly SKColor Txt2 = new(0xD3, 0xD9, 0xE3);
    static readonly SKColor Txt3 = new(0xB4, 0xBE, 0xCE);
    static readonly SKColor Gold = new(0xE8, 0xC2, 0x5A);
    static readonly SKColor AmountGold = new(0xFF, 0xD8, 0x76);
    static readonly SKColor YouTubeRed = new(0xFF, 0x1F, 0x3D);
    static readonly SKColor TwitchPurple = new(0x91, 0x46, 0xFF);
    static readonly SKColor TipGreen = new(0x2E, 0xC4, 0x7A);

    readonly LinkedList<ViewerEvent> pending = new();
    ViewerEvent? cur;
    double curStart = -1, curLen, abortAt = -1, nextOkMs = -1;
    float chipW;
    float fundK; double lastFundMs = -1;

    /// <summary>A viewer card is playing right now.</summary>
    public static bool Playing { get; private set; }
    /// <summary>Why the next event is waiting (for the log / control app).</summary>
    public string WhyWaiting { get; private set; } = "";
    public int Queued => pending.Count + ViewerFeed.Waiting;
    public ViewerEvent? Current => cur;
    /// <summary>Paint while an event plays, the fund bar is on, or either is fading.</summary>
    public bool Visible => cur != null || FundWanted || fundK > 0.001f;
    /// <summary>Preview tools force the screen clear (the gates read live race state).</summary>
    public bool ForceClear;

    static bool FundWanted => ViewerFund.Enabled && ViewerFund.Target > 0 && PresentationState.BarVisible && !PresentationState.BarFullWidth
                              && PresentationState.Phase == RacePhase.Broadcast;

    /// <summary>Whether a viewer card may be on screen now (and why not).</summary>
    public static bool ScreenClear(RaceSnapshot snap, out string why) => ScreenClear(snap, out why, true);

    /// <summary><paramref name="raceControl"/>: #619 - a viewer look also waits while the race-control bar is up (it hangs
    /// in the same band under the session bar); the latest plate (top right) doesn't.</summary>
    public static bool ScreenClear(RaceSnapshot snap, out string why, bool raceControl)
    {
        why = "";
        if (!snap.IsConnected) { why = "iRacing not connected"; return false; }
        if (snap.Phase != RacePhase.Broadcast || OutroDirector.Waiting || OutroDirector.FullScreen) { why = "race start / finish"; return false; }
        if (PresentationState.IsRaceSession && !PresentationState.Lap1Complete) { why = "race start (lap 1)"; return false; }
        if (PresentationState.IsRaceSession && (snap.Flags.White || snap.Flags.Checkered)) { why = "the finish"; return false; }
        if (PresentationState.IsBattleActive || PresentationState.BumperBattleStyle) { why = "battle"; return false; }
        if (PresentationState.NewCardsHeld || PresentationState.ReplayHideK > 0.05f) { why = "onboard / camera settling / replay"; return false; }
        if (StoryDirector.Visible || StoryDirector.MiniVisible) { why = "story card"; return false; }
        if (!PresentationState.BarVisible || PresentationState.BarFullWidth) { why = "session bar busy"; return false; }
        if (raceControl && RaceControlBar.Up) { why = "race control"; return false; }
        return true;
    }

    /// <summary>Render thread, every tick: takes new events, starts the next one when the screen is clear, cuts the current
    /// one short (back to the queue) if a battle / the finish / an onboard comes along.</summary>
    public void Tick(RaceSnapshot snap, double nowMs)
    {
        // #576: with the tier looks on, ViewerTierRenderer takes the events (this one keeps only the track fund bar)
        while (!ViewerTiers.LooksOn && ViewerFeed.TryTake(out var e) && e != null)
        {
            pending.AddLast(e);
            while (pending.Count > ViewerFeed.MaxQueue) pending.RemoveFirst();
        }
        string why = "";
        bool clear = ForceClear || ScreenClear(snap, out why);
        WhyWaiting = clear ? "" : why;

        if (cur != null)
        {
            double t = nowMs - curStart;
            if (abortAt < 0 && !clear)
            {
                abortAt = nowMs;
                // cut short before most of it was seen: it plays again from the start once the screen is clear
                if (t < curLen * 0.6) pending.AddFirst(cur);
                ViewerFeed.Log?.Invoke($"viewer: {cur.Kind} from {cur.Name} paused for the {WhyWaiting}");
            }
            if ((abortAt >= 0 && nowMs - abortAt >= AbortMs) || t >= curLen)
            {
                cur = null; abortAt = -1; nextOkMs = nowMs + GapMs;
            }
        }
        if (cur == null && clear && pending.Count > 0 && nowMs >= nextOkMs)
        {
            cur = pending.First!.Value; pending.RemoveFirst();
            curStart = nowMs; abortAt = -1;
            curLen = LengthOf(cur);
            ViewerLatest.Note(cur);   // #608
        }
        Playing = cur != null;

        double dt = lastFundMs < 0 ? 16 : Math.Clamp(nowMs - lastFundMs, 0, 100);
        lastFundMs = nowMs;
        float target = FundWanted ? 1f : 0f;
        fundK += (target - fundK) * (float)Math.Min(1, dt / 160.0);
        if (Math.Abs(target - fundK) < 0.002f) fundK = target;
    }

    double LengthOf(ViewerEvent e)
    {
        if (!e.Strip) return CardInMs + CardHoldMs + CardOutMs;
        chipW = MeasureChip(e);
        // the chip enters at the strip's right edge and leaves past the badge (strip width as drawn: the normal bar)
        double travel = (SessionBarRenderer.BarWidth - BadgeW) + chipW + 60;
        double scroll = Math.Max(6000, travel / ScrollPxPerSec * 1000.0);
        double len = StripInMs + scroll + StripOutMs;
        return e.Big ? Math.Max(len, BannerAtMs + BannerMs) : len;
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>Draws everything this frame. <paramref name="bar"/> = the session bar plate on screen, <paramref name="towerTop"/>
    /// = the timing tower's top-left corner (the banner covers its 400 x 90 cap; null = the tower is not showing).</summary>
    public void Draw(SKCanvas c, int w, int h, SKRect bar, SKPoint? towerTop, double nowMs)
    {
        if (bar.Width < 10) bar = new SKRect((w - SessionBarRenderer.BarWidth) / 2f, 18f, (w + SessionBarRenderer.BarWidth) / 2f, 18f + SessionBarRenderer.BarHeight);
        float fundOff = 0;
        if (fundK > 0.001f)
        {
            DrawFund(c, bar, fundK, nowMs);
            fundOff = (FundH + 3f) * Ease(fundK);
        }
        var e = cur;
        if (e == null) return;
        double t = nowMs - curStart;
        float abortK = abortAt >= 0 ? 1f - (float)Math.Clamp((nowMs - abortAt) / AbortMs, 0, 1) : 1f;
        if (e.Strip)
        {
            DrawStrip(c, bar, bar.Bottom + fundOff, e, t, abortK, nowMs);
            if (e.Big && towerTop is SKPoint tt) DrawBanner(c, tt, e, t - BannerAtMs, abortK, nowMs);
        }
        else DrawMemberCard(c, w, bar.Bottom + fundOff, e, t, abortK, nowMs);
    }

    static SKColor PlatformColour(ViewerEvent e) => e.Platform switch
    {
        "youtube" => YouTubeRed,
        "twitch" => TwitchPurple,
        _ => TipGreen,
    };

    static string KindLabel(ViewerEvent e) => e.Kind switch
    {
        ViewerKind.SuperChat => "SUPER CHAT",
        ViewerKind.Tip => "TIP",
        ViewerKind.Bits => "BITS",
        ViewerKind.Member => "NEW MEMBER",
        ViewerKind.Sub => "NEW SUBSCRIBER",
        ViewerKind.Comment => "CHAT",
        _ => "",
    };

    static string PlatformLabel(ViewerEvent e) => e.Platform switch { "youtube" => "YOUTUBE", "twitch" => "TWITCH", _ => "THANK YOU" };

    readonly SKPaint namePaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 23f };
    readonly SKPaint amountPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 23f };
    readonly SKPaint msgPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 21f };
    readonly SKPaint smallPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 11f };
    readonly SKPaint labelPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 17f };
    readonly SKPaint fill = new() { IsAntialias = true };
    const float NameAmtGap = 14f, AmtMsgGap = 34f;

    float MeasureChip(ViewerEvent e)
    {
        float wN = namePaint.MeasureText(e.Name);
        float wA = e.Amount.Length > 0 ? NameAmtGap + amountPaint.MeasureText(e.Amount) : 0;
        float wM = e.Message.Length > 0 ? AmtMsgGap + msgPaint.MeasureText(e.Message) : 0;
        return 26f + wN + wA + wM + 40f;
    }

    void DrawStrip(SKCanvas c, SKRect bar, float top, ViewerEvent e, double t, float abortK, double nowMs)
    {
        double outAt = curLen - StripOutMs;
        float k = t < StripInMs ? Ease((float)(t / StripInMs)) : t > outAt ? 1f - Ease((float)Math.Clamp((t - outAt) / StripOutMs, 0, 1)) : 1f;
        k *= abortK;
        if (k <= 0.005f) return;
        var r = new SKRect(bar.Left, top, bar.Right, top + StripH);
        var accent = PlatformColour(e);
        int save = c.Save();
        c.ClipRect(new SKRect(r.Left, bar.Bottom, r.Right, r.Bottom + 8));
        c.Translate(0, -(1 - k) * (StripH + (top - bar.Bottom) + 4));
        float a = Math.Min(1f, k * 1.4f);

        fill.Shader = null;
        fill.Color = Glass.WithAlpha((byte)(Glass.Alpha * a));
        c.DrawRect(r, fill);
        Theme.Skin(c, r, ThemeSurface.Strip, nowMs, a);
        // the class-colour top hairline of the standings ticker, in the platform's colour
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0),
                   new[] { accent.WithAlpha(0), accent.WithAlpha((byte)(220 * a)), accent.WithAlpha((byte)(220 * a)), accent.WithAlpha(0) },
                   new[] { 0f, 0.2f, 0.8f, 1f }, SKShaderTileMode.Clamp))
        { fill.Shader = sh; c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + 1.5f), fill); fill.Shader = null; }

        // badge: platform colour -> transparent, with the platform and kind
        var badge = new SKRect(r.Left, r.Top, r.Left + BadgeW, r.Bottom);
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(badge.Left, 0), new SKPoint(badge.Right, 0),
                   new[] { accent.WithAlpha((byte)(200 * a)), accent.WithAlpha((byte)(80 * a)), accent.WithAlpha(0) },
                   new[] { 0f, 0.6f, 1f }, SKShaderTileMode.Clamp))
        { fill.Shader = sh; c.DrawRect(badge, fill); fill.Shader = null; }
        smallPaint.Color = Ivory.WithAlpha((byte)(215 * a));
        Tracked(c, smallPaint, PlatformLabel(e), badge.Left + 16, r.Top + 17, 1.6f);
        labelPaint.Color = Ivory.WithAlpha((byte)(255 * a));
        Tracked(c, labelPaint, KindLabel(e), badge.Left + 16, r.Top + 36, 1.2f);

        // the scrolling chip: name, amount, message - once, right to left
        float viewL = badge.Right - 6, viewR = r.Right;
        c.Save();
        c.ClipRect(new SKRect(viewL, r.Top, viewR, r.Bottom));
        float scrolled = (float)(Math.Max(0, t - StripInMs * 0.6) / 1000.0 * ScrollPxPerSec);
        float x = viewR - scrolled + 10f;
        namePaint.GetFontMetrics(out var fm);
        float base0 = r.MidY - (fm.Ascent + fm.Descent) / 2f;
        namePaint.Color = Ivory.WithAlpha((byte)(255 * a));
        c.DrawText(e.Name, x + 26f, base0, namePaint);
        float cx = x + 26f + namePaint.MeasureText(e.Name);
        if (e.Amount.Length > 0)
        {
            cx += NameAmtGap;
            amountPaint.Color = AmountGold.WithAlpha((byte)(255 * a));
            c.DrawText(e.Amount, cx, base0, amountPaint);
            cx += amountPaint.MeasureText(e.Amount);
        }
        if (e.Message.Length > 0)
        {
            fill.Color = Theme.AccentOr(Gold, ThemeSurface.Card).WithAlpha((byte)(200 * a));
            c.DrawCircle(cx + AmtMsgGap / 2f, r.MidY, 2.6f, fill);
            cx += AmtMsgGap;
            msgPaint.Color = Txt2.WithAlpha((byte)(245 * a));
            c.DrawText(e.Message, cx, base0 - 1f, msgPaint);
        }
        c.Restore();
        // soft edges where the chip enters and leaves
        EdgeFade(c, viewL, viewL + 26f, r, a, true);
        EdgeFade(c, viewR - 30f, viewR, r, a, false);
        c.RestoreToCount(save);
    }

    void EdgeFade(SKCanvas c, float x0, float x1, SKRect r, float a, bool left)
    {
        var g = Glass.WithAlpha((byte)(Glass.Alpha * a));
        using var sh = SKShader.CreateLinearGradient(new SKPoint(x0, 0), new SKPoint(x1, 0),
            left ? new[] { g, g.WithAlpha(0) } : new[] { g.WithAlpha(0), g }, null, SKShaderTileMode.Clamp);
        fill.Shader = sh; c.DrawRect(new SKRect(x0, r.Top + 1.5f, x1, r.Bottom), fill); fill.Shader = null;
    }

    readonly SKPaint bigPaint = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 46f };
    readonly SKPaint bannerName = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 19f };

    /// <summary>The big-amount banner over the tower's cap (its series logo), 400 x 90.</summary>
    void DrawBanner(SKCanvas c, SKPoint tt, ViewerEvent e, double t, float abortK, double nowMs)
    {
        if (t < 0 || t > BannerMs) return;
        float k = t < BannerInMs ? Ease((float)(t / BannerInMs)) : t > BannerMs - BannerOutMs ? 1f - Ease((float)((t - (BannerMs - BannerOutMs)) / BannerOutMs)) : 1f;
        k *= abortK;
        if (k <= 0.005f) return;
        var r = new SKRect(tt.X, tt.Y, tt.X + TimingTowerRenderer.TowerWidth, tt.Y + TimingTowerRenderer.CapHeight);
        var accent = PlatformColour(e);
        int save = c.Save();
        c.ClipRect(r);
        c.Translate(-(1 - k) * r.Width, 0);
        fill.Shader = null;
        fill.Color = CardGlass.WithAlpha(250);
        c.DrawRect(r, fill);
        Theme.Skin(c, r, ThemeSurface.Card, nowMs, 1f);
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(r.Left, 0), new SKPoint(r.Left + 260, 0),
                   new[] { accent.WithAlpha(90), accent.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
        { fill.Shader = sh; c.DrawRect(r, fill); fill.Shader = null; }
        fill.Color = accent; c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 5, r.Bottom), fill);
        // one shine sweep as it lands
        if (t < 1400)
        {
            float sx = r.Left + (float)(t / 1400.0) * (r.Width + 200) - 100;
            using var sh = SKShader.CreateLinearGradient(new SKPoint(sx - 60, 0), new SKPoint(sx + 60, 0),
                new[] { new SKColor(255, 255, 255, 0), new SKColor(255, 255, 255, 46), new SKColor(255, 255, 255, 0) }, null, SKShaderTileMode.Clamp);
            fill.Shader = sh; c.DrawRect(r, fill); fill.Shader = null;
        }
        smallPaint.Color = Theme.AccentOr(Gold, ThemeSurface.Card);
        smallPaint.TextSize = 13f;
        Tracked(c, smallPaint, KindLabel(e) + "  ·  " + PlatformLabel(e), r.Left + 22, r.Top + 27, 1.8f);
        smallPaint.TextSize = 11f;
        bigPaint.Color = Ivory;
        string amt = e.Amount.Length > 0 ? e.Amount : KindLabel(e);
        c.DrawText(amt, r.Left + 20, r.Top + 74, bigPaint);
        float ax = r.Left + 20 + bigPaint.MeasureText(amt) + 16;
        bannerName.Color = Txt2;
        string nm = Fit(bannerName, e.Name, r.Right - 18 - ax);
        c.DrawText(nm, r.Right - 18 - bannerName.MeasureText(nm), r.Top + 71, bannerName);
        c.RestoreToCount(save);
        fill.Color = new SKColor(255, 255, 255, (byte)(60 * k));
        c.DrawRect(new SKRect(r.Left, r.Bottom - 1, r.Right, r.Bottom), fill);
    }

    readonly SKPaint cardName = new() { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 46f };
    readonly SKPaint eyebrow = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 15f };

    /// <summary>Member / sub: a pre-race style card under the session bar.</summary>
    void DrawMemberCard(SKCanvas c, int w, float top, ViewerEvent e, double t, float abortK, double nowMs)
    {
        double outAt = CardInMs + CardHoldMs;
        float k = t < CardInMs ? Ease((float)(t / CardInMs)) : t > outAt ? 1f - Ease((float)Math.Clamp((t - outAt) / CardOutMs, 0, 1)) : 1f;
        k *= abortK;
        if (k <= 0.005f) return;
        float a = k;
        var r = new SKRect((w - CardW) / 2f, top + 10f, (w + CardW) / 2f, top + 10f + CardH);
        r.Offset(0, -(1 - k) * 34f);
        var accent = PlatformColour(e);
        using var shape = new SKPath(); shape.AddRoundRect(r, 12, 12);
        using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(110 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
        { c.Save(); c.Translate(0, 8); c.DrawPath(shape, sh); c.Restore(); }
        fill.Shader = null;
        fill.Color = CardGlass.WithAlpha((byte)(CardGlass.Alpha * a));
        c.DrawPath(shape, fill);
        Theme.Skin(c, r, ThemeSurface.Card, nowMs, a, shape);
        c.Save(); c.ClipPath(shape, antialias: true);
        using (var sh = SKShader.CreateRadialGradient(new SKPoint(r.Left + 60, r.MidY), 260,
                   new[] { accent.WithAlpha((byte)(70 * a)), accent.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
        { fill.Shader = sh; c.DrawRect(r, fill); fill.Shader = null; }
        if (t < 1600)   // one shine sweep as it arrives
        {
            float sx = r.Left + (float)(t / 1600.0) * (r.Width + 240) - 120;
            using var sh = SKShader.CreateLinearGradient(new SKPoint(sx - 70, 0), new SKPoint(sx + 70, 0),
                new[] { new SKColor(255, 255, 255, 0), new SKColor(255, 255, 255, (byte)(40 * a)), new SKColor(255, 255, 255, 0) }, null, SKShaderTileMode.Clamp);
            fill.Shader = sh; c.DrawRect(r, fill); fill.Shader = null;
        }
        c.Restore();
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(255, 255, 255, (byte)(70 * a)) })
            c.DrawPath(shape, edge);

        // badge: the platform's colour with a star, popping in
        float pop = Ease(Math.Clamp((float)((t - 120) / 380.0), 0, 1));
        float bcx = r.Left + 58, bcy = r.MidY, br = 30f * (0.6f + 0.4f * pop);
        fill.Color = accent.WithAlpha((byte)(255 * a * pop));
        c.DrawCircle(bcx, bcy, br, fill);
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = new SKColor(255, 255, 255, (byte)(90 * a * pop)) })
            c.DrawCircle(bcx, bcy, br + 4, ring);
        using (var star = Star(bcx, bcy, br * 0.55f, br * 0.24f))
        { fill.Color = Ivory.WithAlpha((byte)(255 * a * pop)); c.DrawPath(star, fill); }

        float tx = r.Left + 112, maxW = r.Right - 28 - tx;
        eyebrow.Color = Theme.AccentOr(Gold, ThemeSurface.Card).WithAlpha((byte)(255 * a));
        Tracked(c, eyebrow, KindLabel(e), tx, r.Top + 36, 2.6f);
        smallPaint.Color = Txt3.WithAlpha((byte)(230 * a));
        string pl = PlatformLabel(e);
        float plW = smallPaint.MeasureText(pl) + (pl.Length - 1) * 1.6f;
        Tracked(c, smallPaint, pl, r.Right - 26 - plW, r.Top + 34, 1.6f);
        cardName.TextSize = 46f;
        while (cardName.TextSize > 24f && cardName.MeasureText(e.Name) > maxW) cardName.TextSize -= 2f;
        cardName.Color = Ivory.WithAlpha((byte)(255 * a));
        // the name rises into place just after the card lands
        float nk = Ease(Math.Clamp((float)((t - 200) / 420.0), 0, 1));
        cardName.Color = cardName.Color.WithAlpha((byte)(255 * a * nk));
        c.DrawText(Fit(cardName, e.Name, maxW), tx, r.Top + 84 + (1 - nk) * 10f, cardName);
    }

    readonly SKPaint fundText = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 12f };

    void DrawFund(SKCanvas c, SKRect bar, float k, double nowMs)
    {
        float a = Ease(k);
        var r = new SKRect(bar.Left + 40, bar.Bottom + 2, bar.Right - 40, bar.Bottom + 2 + FundH);
        int save = c.Save();
        c.ClipRect(new SKRect(r.Left, bar.Bottom, r.Right, r.Bottom + 2));
        c.Translate(0, -(1 - a) * (FundH + 2));
        fill.Shader = null;
        fill.Color = Glass.WithAlpha((byte)(Glass.Alpha * a));
        c.DrawRect(r, fill);
        Theme.Skin(c, r, ThemeSurface.Strip, nowMs, a);
        var accent = Theme.AccentOr(Gold, ThemeSurface.Card);
        string goal = (ViewerFund.Goal.Length > 0 ? ViewerFund.Goal : "TRACK FUND").ToUpperInvariant();
        double cur0 = ViewerFund.Current, target = ViewerFund.Target;
        string right = ViewerFund.Money(cur0) + " / " + ViewerFund.Money(target);
        fundText.Color = Ivory.WithAlpha((byte)(240 * a));
        float base0 = r.MidY + 4.3f;
        float rw = fundText.MeasureText(right);
        goal = Fit(fundText, goal, r.Width * 0.42f);
        Tracked(c, fundText, goal, r.Left + 12, base0, 1.2f);
        float gl = r.Left + 12 + fundText.MeasureText(goal) + goal.Length * 1.2f + 14;
        c.DrawText(right, r.Right - 12 - rw, base0, fundText);
        // the track runs either side of the watch (it hangs over the middle of this bar): one goal, two segments
        float mid = bar.MidX, gapHalf = 58f;
        var segs = new[] { new SKRect(gl, r.MidY - 3, mid - gapHalf, r.MidY + 3), new SKRect(mid + gapHalf, r.MidY - 3, r.Right - 26 - rw, r.MidY + 3) };
        float total = segs.Sum(x => Math.Max(0, x.Width));
        float p = target > 0 ? (float)Math.Clamp(cur0 / target, 0, 1) : 0;
        float filled = total * p;
        foreach (var track in segs)
        {
            if (track.Width < 8) continue;
            fill.Color = new SKColor(0, 0, 0, (byte)(150 * a));
            c.DrawRoundRect(track, 3, 3, fill);
            float f = Math.Min(track.Width, filled);
            filled -= Math.Max(0, track.Width);
            if (f <= 0) continue;
            var fr = new SKRect(track.Left, track.Top, track.Left + Math.Max(6, f), track.Bottom);
            using var sh = SKShader.CreateLinearGradient(new SKPoint(gl, 0), new SKPoint(gl + total, 0),
                new[] { accent.WithAlpha((byte)(170 * a)), accent.WithAlpha((byte)(255 * a)) }, null, SKShaderTileMode.Clamp);
            fill.Shader = sh; c.DrawRoundRect(fr, 3, 3, fill); fill.Shader = null;
        }
        c.RestoreToCount(save);
    }

    // ---------------------------------------------------------------- helpers

    static float Ease(float k) { k = Math.Clamp(k, 0, 1); return 1 - (1 - k) * (1 - k) * (1 - k); }

    static void Tracked(SKCanvas c, SKPaint p, string s, float x, float y, float track)
    {
        foreach (char ch in s)
        {
            string g = ch.ToString();
            c.DrawText(g, x, y, p);
            x += p.MeasureText(g) + track;
        }
    }

    static string Fit(SKPaint p, string s, float maxW)
    {
        if (maxW <= 0 || p.MeasureText(s) <= maxW) return s;
        while (s.Length > 1 && p.MeasureText(s + "…") > maxW) s = s[..^1];
        return s.TrimEnd() + "…";
    }

    static SKPath Star(float cx, float cy, float ro, float ri)
    {
        var p = new SKPath();
        for (int i = 0; i < 10; i++)
        {
            double ang = -Math.PI / 2 + i * Math.PI / 5;
            float rr = i % 2 == 0 ? ro : ri;
            var pt = new SKPoint(cx + rr * (float)Math.Cos(ang), cy + rr * (float)Math.Sin(ang));
            if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
        }
        p.Close();
        return p;
    }

    public void Dispose()
    {
        namePaint.Dispose(); amountPaint.Dispose(); msgPaint.Dispose(); smallPaint.Dispose(); labelPaint.Dispose(); fill.Dispose();
        bigPaint.Dispose(); bannerName.Dispose(); cardName.Dispose(); eyebrow.Dispose(); fundText.Dispose();
    }
}
