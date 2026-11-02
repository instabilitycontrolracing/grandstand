using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-26 (#235, Angelo: real broadcasts cut into and out of a replay with a quick branded wipe). A ~0.7 s
/// full-screen sweep - a slanted band in Grandstand's own colours (deep glass, gold edges, ivory type) with the word
/// REPLAY, or LIVE on the way back - that fully covers the picture for ~150 ms at its middle while the iRacing camera
/// settles, then clears. Plus a small "REPLAY" tag in the top-right corner while a replay plays behind live in a live
/// session.
///
/// <para>Triggers (fed from <see cref="ReplayState"/>, written by the data service every tick):
/// (a) REPLAY - a rewind / fast-forward / slow motion / jump ends and the replay plays at normal speed again at a
/// different moment (more than 2 s away from where it was) - not a plain pause;
/// (b) LIVE - in a live session, the replay was behind live (iRacing ReplayFrameNumEnd &gt; 2 s of frames) and is back at
/// the live end (&lt; 0.5 s), or you got back in the car (IsReplayPlaying false).
/// Never at start-up (6 s quiet after connecting or loading another session), never before the green (the grid /
/// opener sequences), never during the outro, never while scrubbing, and at most once every 3 s.</para>
/// </summary>
public sealed class ReplayWipe : IDisposable
{
    /// <summary>The whole sweep, ms.</summary>
    public const double DurationMs = 700;
    const double InEndMs = 275, OutStartMs = 425;     // fully covering between these (150 ms)
    const double DebounceMs = 3000, QuietAfterConnectMs = 6000;
    const int BehindFrames = 120, AtLiveFrames = 30;  // iRacing replays run at 60 frames a second
    const float Slant = 0.32f;                        // band lean: top edge this many screen heights right of the bottom

    static readonly SKColor Deep = SKColor.Parse("#04070B");
    static readonly SKColor Glass = SKColor.Parse("#0F131C");
    static readonly SKColor Gold = SKColor.Parse("#C5A572");
    static readonly SKColor Gold2 = SKColor.Parse("#E2C99A");
    static readonly SKColor Ivory = SKColor.Parse("#FCFAF5");
    static readonly SKColor Red = SKColor.Parse("#D81E2A");

    const string ReplayWord = "REPLAY", LiveWord = "LIVE", Kicker = "G R A N D S T A N D";

    readonly SKPath band = new(), stripe = new();
    readonly SKPaint bandPaint = new() { IsAntialias = true };
    readonly SKPaint goldPaint = new() { IsAntialias = true };
    readonly SKPaint wordPaint = new() { IsAntialias = true };
    readonly SKPaint kickerPaint = new() { IsAntialias = true };
    readonly SKPaint dotPaint = new() { IsAntialias = true };
    readonly SKPaint tagGlass = new() { IsAntialias = true };
    readonly SKPaint tagText = new() { IsAntialias = true };
    readonly SKPaint tagAccent = new() { IsAntialias = true };
    int shaderH = -1;
    float replayW = -1, liveW, kickerW, tagW;

    // the sweep
    double startMs = -1;
    bool live;

    // trigger state
    double armedAtMs = -1, lastFireMs = -1e9;
    string seenSessionKey = "";
    int seenJumps = int.MinValue, seenSessionNum = int.MinValue;
    bool disturbed, wasBehind;
    double steadyTime = double.NaN, disturbFrom = double.NaN;

    // the corner tag
    float tagK; double tagLastMs = -1;

    /// <summary>A sweep is playing.</summary>
    public bool Active(double nowMs) => startMs >= 0 && nowMs - startMs < DurationMs;

    /// <summary>The corner REPLAY tag is showing or fading.</summary>
    public bool TagShowing => tagK > 0.002f;

    /// <summary>The corner tag is fading in or out (keep presenting until it settles).</summary>
    public bool TagFading => tagK > 0.002f && tagK < 0.998f;

    /// <summary>The tag wants to be on (a replay behind live in a live session), whether or not it has room.</summary>
    public static bool BehindLiveNow =>
        !ReplayState.IsReplayFile && ReplayState.IsReplay && ReplayState.FramesBehindLive > BehindFrames;

    /// <summary>
    /// Watch the replay state once per tick. Returns a short reason when a wipe starts this tick (the host logs it and
    /// plays the stinger), else null. <paramref name="enabled"/> false keeps the bookkeeping going but never fires.
    /// </summary>
    public string? Tick(RaceSnapshot snap, double nowMs, bool enabled)
    {
        TickTag(nowMs, enabled);
        if (!snap.IsConnected) { armedAtMs = -1; disturbed = false; wasBehind = false; steadyTime = double.NaN; return null; }
        if (armedAtMs < 0 || !string.Equals(snap.SessionKey, seenSessionKey, StringComparison.Ordinal))
        {
            armedAtMs = nowMs; seenSessionKey = snap.SessionKey ?? "";
        }

        bool replay = ReplayState.IsReplay, scrub = ReplayState.Scrubbing;
        bool liveSession = !ReplayState.IsReplayFile;
        int behind = ReplayState.FramesBehindLive;
        double st = ReplayState.SessionTime;
        bool behindLive = liveSession && replay && behind > BehindFrames;
        bool atLive = liveSession && (!replay || (behind >= 0 && behind < AtLiveFrames));

        // anything that moves the replay to another moment: a scrub, a jump, another session of the event
        int jc = ReplayState.JumpCount;
        bool jumped = seenJumps != int.MinValue && jc != seenJumps;
        seenJumps = jc;
        bool sessionHop = seenSessionNum != int.MinValue && snap.SessionNum != seenSessionNum;
        seenSessionNum = snap.SessionNum;
        if (replay && (scrub || jumped || sessionHop) && !disturbed)
        {
            disturbed = true;
            disturbFrom = sessionHop ? double.NaN : steadyTime;
        }

        bool quiet = !enabled || nowMs - armedAtMs < QuietAfterConnectMs || OpenerAudio.IsPlaying
                     || PresentationState.Phase == RacePhase.PreRace || OutroDirector.FullScreen;
        string? fired = null;

        // (b) back to live
        if (wasBehind && atLive)
        {
            wasBehind = false;
            disturbed = false;
            if (!quiet && nowMs - lastFireMs >= DebounceMs) { Start(nowMs, true); fired = "back to live"; }
        }
        if (behindLive) wasBehind = true;

        // (a) a scrub / jump ended and the replay plays on at normal speed somewhere else
        if (disturbed && !scrub && (!replay || ReplayState.Speed == 1))
        {
            disturbed = false;
            bool moved = double.IsNaN(disturbFrom) || Math.Abs(st - disturbFrom) > 2.0;
            bool intoReplay = replay && (!liveSession || behind < 0 || behind > BehindFrames);
            if (moved && intoReplay && fired == null && !quiet && nowMs - lastFireMs >= DebounceMs)
            {
                Start(nowMs, false);
                fired = "into the replay";
            }
        }
        if (!disturbed && !scrub) steadyTime = st;
        if (quiet && fired == null) disturbed = false;
        return fired;
    }

    void Start(double nowMs, bool toLive) { startMs = nowMs; live = toLive; lastFireMs = nowMs; }

    void TickTag(double nowMs, bool enabled)
    {
        // room in the top-right corner: not while the session bar is full width (battle / pre-race), an onboard shot
        // (its gap readout lives up there), a story card or the outro holds the screen, or a sweep plays
        bool want = enabled && BehindLiveNow && !PresentationState.BarFullWidth && PresentationState.OnboardK < 0.5f
                    && !StoryDirector.Visible && !OutroDirector.FullScreen && !Active(nowMs);
        double dt = tagLastMs < 0 ? 16 : Math.Clamp(nowMs - tagLastMs, 0, 100);
        tagLastMs = nowMs;
        float target = want ? 1f : 0f;
        tagK += (target - tagK) * (float)Math.Min(1, dt / 160.0);
        if (Math.Abs(target - tagK) < 0.002f) tagK = target;
    }

    static float EaseInOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (float)(t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2);
    }

    void EnsurePaints(int h)
    {
        if (replayW < 0)
        {
            wordPaint.Typeface = Fonts.BarlowCondensed.Black; wordPaint.TextSize = 176f; wordPaint.Color = Ivory;
            kickerPaint.Typeface = Fonts.Inter.SemiBold; kickerPaint.TextSize = 22f; kickerPaint.Color = Gold2;
            tagText.Typeface = Fonts.BarlowCondensed.Bold; tagText.TextSize = 24f; tagText.Color = Ivory;
            replayW = wordPaint.MeasureText(ReplayWord);
            liveW = wordPaint.MeasureText(LiveWord);
            kickerW = kickerPaint.MeasureText(Kicker);
            tagW = tagText.MeasureText(ReplayWord);
            dotPaint.Color = Red;
            goldPaint.Color = Gold;
        }
        if (shaderH != h)
        {
            shaderH = h;
            bandPaint.Shader?.Dispose();
            bandPaint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
                new[] { Glass, Deep, Deep, Glass }, new[] { 0f, 0.35f, 0.65f, 1f }, SKShaderTileMode.Clamp);
        }
    }

    void Parallelogram(SKPath p, float xBottomLeft, float xBottomRight, float s, int h)
    {
        p.Rewind();
        p.MoveTo(xBottomLeft, h);
        p.LineTo(xBottomLeft + s, 0);
        p.LineTo(xBottomRight + s, 0);
        p.LineTo(xBottomRight, h);
        p.Close();
    }

    /// <summary>Draw the sweep and the corner tag (full screen, 1080p design space).</summary>
    public void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        EnsurePaints(h);
        if (tagK > 0.002f) DrawTag(c, w);
        if (!Active(nowMs)) return;

        double t = nowMs - startMs;
        float s = h * Slant;
        // leading edge sweeps in left to right; the trailing edge follows after the hold
        float xLead = -s + (w + s + 30f) * EaseInOut(t / InEndMs);
        float xTrail = -s - 30f + (w + s + 30f) * EaseInOut((t - OutStartMs) / (DurationMs - OutStartMs));
        if (xLead <= xTrail) return;

        Parallelogram(band, xTrail, xLead, s, h);
        c.DrawPath(band, bandPaint);

        // gold edges: a bold leading stripe with a thin echo ahead of it, and a trailing stripe on the way out
        goldPaint.Color = Gold;
        Parallelogram(stripe, xLead - 14f, xLead, s, h); c.DrawPath(stripe, goldPaint);
        goldPaint.Color = Gold2.WithAlpha(170);
        Parallelogram(stripe, xLead + 12f, xLead + 17f, s, h); c.DrawPath(stripe, goldPaint);
        if (t > OutStartMs)
        {
            goldPaint.Color = Gold;
            Parallelogram(stripe, xTrail, xTrail + 10f, s, h); c.DrawPath(stripe, goldPaint);
        }

        // the word, italic to the band's lean, drifting a little against the sweep; only where the band is
        int save = c.Save();
        c.ClipPath(band, SKClipOperation.Intersect, true);
        float drift = (float)(0.5 - t / DurationMs) * 70f;
        float cx = w / 2f + drift, cy = h / 2f;
        float wordW = live ? liveW : replayW;
        c.Translate(cx, cy + 60f);
        c.Skew(-0.2f, 0f);
        c.DrawText(Kicker, -kickerW / 2f, -150f, kickerPaint);
        goldPaint.Color = Gold;
        c.DrawRect(new SKRect(-wordW / 2f, -136f, wordW / 2f, -132f), goldPaint);
        if (live)
        {
            c.DrawCircle(-wordW / 2f - 44f, -62f, 20f, dotPaint);
            c.DrawText(LiveWord, -wordW / 2f + 10f, 0f, wordPaint);
        }
        else c.DrawText(ReplayWord, -wordW / 2f, 0f, wordPaint);
        c.RestoreToCount(save);
    }

    /// <summary>2026-10-27 (#397, Angelo: "the replay chip gets eaten by the battle card - near the top of the screen but
    /// below the corner / straight label"): where the tag's top sits, worked out by the host every frame.</summary>
    public float TagTop = 150f, TagRight = -1f;
    /// <summary>The tag's height and width (design px) for the host's placement.</summary>
    public const float TagH = 34f;

    void DrawTag(SKCanvas c, int w)
    {
        byte a = (byte)(255 * tagK);
        float pad = 14f, accent = 5f, hgt = 34f;
        float right = TagRight > 0 ? TagRight : w - 24f, top = TagTop - (1 - tagK) * 10f;
        float left = right - (accent + pad + tagW + pad);
        tagGlass.Color = new SKColor(9, 12, 18, (byte)(235 * tagK));
        c.DrawRoundRect(new SKRect(left, top, right, top + hgt), 4f, 4f, tagGlass);
        tagAccent.Color = Gold.WithAlpha(a);
        c.DrawRect(new SKRect(left, top, left + accent, top + hgt), tagAccent);
        tagText.Color = Ivory.WithAlpha(a);
        c.DrawText(ReplayWord, left + accent + pad, top + hgt - 9f, tagText);
    }

    public void Dispose()
    {
        band.Dispose(); stripe.Dispose();
        bandPaint.Shader?.Dispose();
        bandPaint.Dispose(); goldPaint.Dispose(); wordPaint.Dispose(); kickerPaint.Dispose(); dotPaint.Dispose();
        tagGlass.Dispose(); tagText.Dispose(); tagAccent.Dispose();
    }
}
