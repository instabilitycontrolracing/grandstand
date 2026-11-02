using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

public enum StoryKind { None, RaceSoFar, YourRace, MoveOfRace, FastestLaps, PitStops, Halfway, WeatherWatch, DriverOfDay, CarContact, Limits, Cleanest }

public enum Partner { None, AB, Storm, GoFast, TradingPaints, Custom }

/// <summary>
/// Who presents which moment (2026-10-19, Angelo). A&amp;B General Contracting: Race So Far,
/// Move of the Race ("built by") and the Under Pressure battle tag; StormExhibit: Weather
/// Watch and the session-bar weather tag. The rest have an open slot for partners still to
/// be chosen - set them here and every card picks it up.
/// </summary>
public static class Presenters
{
    // 2026-10-23: who presents what now comes from SponsorPack (Documents\iRacing\Grandstand\sponsors.json).
    // Its defaults are exactly the old fixed list: A&B Race So Far / Move of the Race, StormExhibit
    // Weather Watch, GO FAST Fastest Laps / Limits, Trading Paints Car Contact / Cleanest.
    public static Partner For(StoryKind k) => SponsorPack.For(k.ToString());
    public static string Verb(StoryKind k) => k switch
    {
        StoryKind.MoveOfRace => "BUILT BY",
        StoryKind.FastestLaps or StoryKind.Limits or StoryKind.CarContact or StoryKind.Cleanest => "BY",
        _ => "PRESENTED BY",
    };
    public static Partner UnderPressure => SponsorPack.For("UnderPressure");
    public static Partner WeatherBar => SponsorPack.For("WeatherBar");

    // partner logos (assets\logos\sponsors). GO FAST and Trading Paints use a drawn wordmark
    // until their real logo files are dropped in under these names.
    static readonly Dictionary<Partner, SKBitmap?> logos = new();
    static int logosVersion = -1;
    static SKBitmap? Logo(Partner p)
    {
        // the control app saved a new sponsor pack: drop the cached logos (render thread only)
        int v = SponsorPack.Version;
        if (v != logosVersion) { logosVersion = v; logos.Clear(); }   // old bitmaps are left to the GC: a frame may still hold one
        if (logos.TryGetValue(p, out var b)) return b;
        b = null;
        try
        {
            var path = p == Partner.Storm ? null : SponsorPack.LogoPath(p);     // StormExhibit is a drawn wordmark
            if (path != null) { using var st = System.IO.File.OpenRead(path); b = Assets.Decode(st); }
        }
        catch { b = null; }
        return logos[p] = b;
    }

    // #604 perf pass 2: the mark's paints are kept per thread (the battle card's UNDER PRESSURE tab draws a mark every frame)
    [ThreadStatic] static SKPaint? wordPaint, markPaint, underPaint;
    static SKPaint WordPaint(Partner p, float h)
    {
        var wp = wordPaint ??= new SKPaint { IsAntialias = true };
        wp.Color = SKColors.White;
        wp.Typeface = p == Partner.GoFast ? Fonts.BarlowCondensed.Black : Fonts.BarlowCondensed.ExtraBold;
        wp.TextSize = p == Partner.GoFast ? h * 0.86f : h * 0.66f;
        return wp;
    }
    static string Word(Partner p) => SponsorPack.Info(p).Word is { Length: > 0 } w ? w
        : p == Partner.GoFast ? "GO FAST" : p == Partner.TradingPaints ? "TRADING PAINTS" : "";
    static bool HasWord(Partner p) => (p is Partner.GoFast or Partner.TradingPaints or Partner.Custom) && Word(p).Length > 0;

    /// <summary>Width of just the partner mark at height <paramref name="h"/>.</summary>
    public static float MarkWidth(Partner p, float h)
    {
        if (p == Partner.Storm) return PreRaceCards.WordmarkWidth(h * 0.62f);
        if (Logo(p) is { } b) return b.Width * h / b.Height;
        if (HasWord(p)) { var wp = WordPaint(p, h); return TextBlobs.Measure(Word(p), wp) + h * 0.3f; }
        return 0;
    }

    /// <summary>Draws the partner mark with its left edge at x, vertically centred on midY.</summary>
    public static void DrawMark(SKCanvas c, Partner p, float x, float midY, float h, float a)
    {
        a = Math.Clamp(a, 0, 1);
        if (p == Partner.Storm)
        {
            float size = h * 0.62f;
            PreRaceCards.StormWordmark(c, x, midY + size * 0.36f, size, a);
        }
        else if (Logo(p) is { } b)
        {
            float w = b.Width * h / b.Height;
            var lp = markPaint ??= new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            lp.Color = SKColors.White.WithAlpha((byte)(255 * a));
            BitmapImages.Draw(c, b, new SKRect(x, midY - h / 2, x + w, midY + h / 2), lp);
        }
        else if (HasWord(p))
        {
            var wp = WordPaint(p, h);
            wp.Color = wp.Color.WithAlpha((byte)(255 * a));
            string word = Word(p);
            float w = TextBlobs.Measure(word, wp);
            c.Save();
            c.Translate(x + h * 0.15f, midY + wp.TextSize * 0.36f);
            if (p == Partner.GoFast) c.Skew(-0.22f, 0);      // leaning forward
            TextBlobs.Draw(c, word, 0, 0, wp);
            var u = underPaint ??= new SKPaint { IsAntialias = true };
            u.Color = new SKColor(0xD8, 0x1E, 0x2A, (byte)(255 * a));
            c.DrawRect(new SKRect(0, h * 0.14f, w, h * 0.14f + Math.Max(2, h * 0.08f)), u);
            c.Restore();
        }
    }

    static SKPaint VerbPaint(float h, float a) => new()
    { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = Math.Max(9, h * 0.34f), Color = new SKColor(0x9A, 0xA4, 0xB4, (byte)(255 * Math.Clamp(a, 0, 1))) };

    /// <summary>"PRESENTED BY [mark]", right-aligned at <paramref name="right"/>. Returns its left edge.</summary>
    public static float DrawPresentedBy(SKCanvas c, Partner p, string verb, float right, float midY, float h, float a)
    {
        if (p == Partner.None || a <= 0.01f) return right;
        float mw = MarkWidth(p, h);
        if (mw <= 0) return right;
        using var lp = VerbPaint(h, a);
        float sp = lp.TextSize * 0.24f, tw = 0;
        foreach (char ch in verb) tw += lp.MeasureText(ch.ToString()) + sp;
        float x = right - mw - 12 - tw;
        TextTracked.Draw(c, lp, verb, x, midY + lp.TextSize * 0.36f, sp);
        DrawMark(c, p, right - mw, midY, h, a);
        return x;
    }

    /// <summary>"BY [mark]" starting at <paramref name="left"/> - sits right after a card's title
    /// (Angelo 2026-10-20: "built by A&amp;B right next to the words"). Returns its right edge.</summary>
    public static float DrawPresentedAfter(SKCanvas c, Partner p, string verb, float left, float midY, float h, float a)
    {
        if (p == Partner.None || a <= 0.01f) return left;
        float mw = MarkWidth(p, h);
        if (mw <= 0) return left;
        using var lp = VerbPaint(h, a);
        float sp = lp.TextSize * 0.24f, tw = 0;
        foreach (char ch in verb) tw += lp.MeasureText(ch.ToString()) + sp;
        TextTracked.Draw(c, lp, verb, left, midY + lp.TextSize * 0.36f, sp);
        DrawMark(c, p, left + tw + 10, midY, h, a);
        return left + tw + 10 + mw;
    }
}

/// <summary>
/// Decides WHEN a story card may take over half the screen (2026-10-19, Angelo's approved
/// "quiet moment" rule): racing, past lap 3, no same-class car within 2.5 s of the player
/// for 20 s, no battle card, not under caution / white / chequered, not in the pits, not in
/// the last 2 laps, at most one routine card every 5 minutes, ~9 s each, bowing out early if
/// anything kicks off. Routine cards rotate (Race So Far, Your Race, Move of the Race,
/// Fastest Laps, Pit Stops - skipping any without a story yet). Halfway and Weather Watch
/// are events: they jump the queue at the next calm moment. Driver of the Day plays once
/// after the chequered flag.
/// </summary>
public static partial class StoryDirector
{
    // CardMs and CooldownMs are settable from the Grandstand control app (2026-10-23).
    public static double CardMs = 9000, CooldownMs = 300000;
    // 2026-10-25 (Angelo: "they really don't hardly show up"): 10 s of quiet (was 20), nobody within 1.0 s (was 1.5),
    // events (Halfway, Weather, Contact) after 3 calm seconds (was 5) and 40 s after another card (was 60)
    public const double ExitMs = 750, QuietNeededMs = 10000;
    const double QuietGapS = 1.0, EventCalmMs = 3000, EventGapMs = 40000;

    public static StoryKind Current { get; private set; }
    public static double StartedMs { get; private set; } = -1;
    public static double ExitAtMs { get; private set; } = -1;
    /// <summary>Why the current Weather Watch is on (RAIN / DRYING / HEATING / COOLING).</summary>
    public static string WeatherHeadline { get; private set; } = "";
    /// <summary>The contact the current Car Contact card is about.</summary>
    public static RaceDiary.Contact? ContactShown { get; private set; }
    static double quietSinceMs = -1, lastEndMs = double.NegativeInfinity, lastRoutineEndMs = double.NegativeInfinity;
    static double postRaceSinceMs = -1;
    static bool halfwayShown, dodShown;
    static int session = int.MinValue;
    // 2026-10-25: partners alternate (A&B, GoFast, -, Trading Paints, ...) and Cleanest So Far joins the rotation
    static readonly StoryKind[] Rotation = { StoryKind.RaceSoFar, StoryKind.FastestLaps, StoryKind.PitStops, StoryKind.Cleanest, StoryKind.YourRace, StoryKind.MoveOfRace, StoryKind.Limits };
    // 2026-10-25 (Angelo: "the others need to find time to play too even if it takes away from a battle temporarily"):
    // a card that has waited this long past its turn plays through close racing (never through flags, the last
    // lap, pit stops or an onboard shot), and once it's up it stays for its full time
    const double PushThroughMs = 40000, EventPushMs = 20000;
    static double dueSinceMs = -1, eventDueSinceMs = -1;
    static bool firm;
    static int pitCardFor;   // the player's stop number the pit card last played for
    // 2026-10-26 (Angelo: "on pit exit it should play the Michelin pit card again"): leaving pit road plays it once more
    static bool wasInPit; static double pitExitAtMs = double.NegativeInfinity;
    static int rot;
    // 2026-10-24 (Angelo: "if graphics got interrupted by a battle, they come on at the first opportunity"):
    // a card that had to bow out early goes back in line with the same content and returns at the next
    // calm moment, instead of counting as shown.
    static StoryKind retryKind = StoryKind.None;
    static string retryWeather = "";
    static RaceDiary.Contact? retryContact;
    static double retryUntilMs = -1;
    static bool bowedOut;
    const double RetryCalmMs = 5000, RetryGapMs = 8000, RetryForMs = 10 * 60000;

    /// <summary>2026-10-27 (#338, Angelo): while he is DRIVING LIVE (in the car, not a replay - DrivingState.DrivingLive) and a
    /// battle is on, no story card plays at all - not even the ones that used to push through a fight (fastest lap, an
    /// overdue card, the pit cards); a card already up bows out. They wait in line and anything older than
    /// <see cref="BattleStaleMs"/> when the battle ends is dropped. Replays / spectating: unchanged. control.json
    /// "noCardsInBattleWhenDriving", ON by default (false = the old behaviour).</summary>
    public static bool NoCardsInBattleWhenDriving = true;
    public const double BattleStaleMs = 60000;
    static bool hushed;

    /// <summary>True while a card is on screen (including its exit).</summary>
    public static bool Visible => Current != StoryKind.None;

    /// <summary>Preview/test hook: show a card now regardless of the rules.</summary>
    public static void Force(StoryKind k, double nowMs, string weather = "RAIN")
    {
        Current = k; StartedMs = nowMs; WeatherHeadline = weather; pinned = true; exitCued = false;
        if (k == StoryKind.CarContact) ContactShown = RaceDiary.Contacts.Count > 0 ? RaceDiary.Contacts[^1] : null;
        ExitAtMs = nowMs + (k == StoryKind.DriverOfDay ? 12000 : CardMs) - ExitMs;
        StorySound.Play(StorySound.CueFor(k));
    }

    /// <summary>2026-10-27 (#393): a card that needs longer than the usual time (the paged GO FAST Fastest Laps: about 4 s
    /// per class) moves its exit later. Only ever later, and only while the card is still in its normal run.</summary>
    public static void ExtendExit(double exitAtMs, double nowMs)
    {
        if (Current == StoryKind.None || nowMs >= ExitAtMs || exitAtMs <= ExitAtMs) return;
        ExitAtMs = exitAtMs;
    }

    // a forced card (test trigger, preview, Driver of the Day) plays out in full
    static bool pinned, exitCued;
    // Live test (2026-10-20): drop a card name into this file (the desktop shortcut
    // "Show next story card" does it) and that card plays now, even in practice.
    const string TestTrigger = @"C:\SimRacing\Tools\story_test.txt";
    static double lastTriggerCheckMs = double.NegativeInfinity;
    static void CheckTestTrigger(double nowMs)
    {
        if (nowMs - lastTriggerCheckMs < 1000) return;
        lastTriggerCheckMs = nowMs;
        try
        {
            if (!System.IO.File.Exists(TestTrigger)) return;
            string name = System.IO.File.ReadAllText(TestTrigger).Trim();
            System.IO.File.Delete(TestTrigger);
            if (Enum.TryParse<StoryKind>(name, true, out var k) && k != StoryKind.None)
                Force(k, nowMs, k == StoryKind.WeatherWatch ? "HEATING" : "RAIN");
        }
        catch { }
    }

    // ---- the card queue (2026-10-25, Angelo: "all cards will wait until the battle is over and then show those cards,
    // hold them as long as needed even if they have to play back to back") ----
    // Things that happen put a card in line: halfway, weather, a contact, four or more class cars through the pits
    // since the last pit card, and the routine rotation's next card every few minutes. Nothing is dropped (a
    // contact goes stale after 6 minutes). The line plays in order - big moments first - whenever the battle card is
    // down and nobody's within a second of us, one after another with a short breather. Only after waiting
    // HoldMaxMs (a battle that never ends) does the front card play through the fight.
    sealed record Queued(StoryKind Kind, double At, string Weather, RaceDiary.Contact? Contact, int Prio);
    static readonly List<Queued> queue = new();
    static double lastRoutineQueuedMs = double.NegativeInfinity;
    static int classStopsAtPitCard;
    static int offsAtLimitsCard;
    static double lastLimitsMs = double.NegativeInfinity;
    static readonly Dictionary<string, float> classBest = new();
    static double lastFastestQueuedMs = double.NegativeInfinity;
    static bool fastestNow;
    static double fastestAtMs = double.NegativeInfinity;   // 2026-10-27 (#338): when the take-over was set (stale after a battle)
    const double BackToBackGapMs = 6000, CalmBeforeMs = 3500, HoldMaxMs = 240000;
    /// <summary>Cards waiting to play (for the log / control app).</summary>
    public static int Waiting => queue.Count;

    static readonly HashSet<int> dqSeen = new(); static bool dqSeeded;
    static void Enqueue(StoryKind k, double nowMs, int prio, string weather = "", RaceDiary.Contact? ct = null)
    {
        if (k != StoryKind.CarContact && queue.Any(q => q.Kind == k)) return;    // already in line
        queue.Add(new Queued(k, nowMs, weather, ct, prio));
    }

    static void FillQueue(RaceSnapshot snap, double nowMs)
    {
        if (RaceDiary.HalfwayReached && !halfwayShown) { halfwayShown = true; if (RaceDiary.Once("halfway")) Enqueue(StoryKind.Halfway, nowMs, 3); }   // once a race, kept across restarts
        if (RaceDiary.WeatherEvent != null) Enqueue(StoryKind.WeatherWatch, nowMs, 3, RaceDiary.TakeWeatherEvent()!);
        if (RaceDiary.TakeContactEvent() is { } ct) Enqueue(StoryKind.CarContact, nowMs, 2, "", ct);
        // 2026-10-25 (Angelo: "if a car has so many incidents they DQ, I want the Trading Paints card to show them each time
        // a car gets DQed and a big fat DQ goes over their car model"): every new disqualification puts the card in line,
        // ahead of the routine cards. Cars already out when we first look (joined late) don't count.
        foreach (var cl in snap.ClassLeaderboard)
            foreach (var d in cl.Drivers)
            {
                if (!d.Disqualified || !dqSeen.Add(d.CarIdx) || !dqSeeded) continue;
                OverlayLog.Write($"story: car #{d.CarNumber} {d.Name} disqualified ({d.Incidents}x) - Trading Paints DQ card");
                Enqueue(StoryKind.CarContact, nowMs, 3, "", new RaceDiary.Contact(d.CarIdx, NameFormat.Broadcast(d), d.CarBrand, d.ClassName,
                    d.LapsCompleted, -1, d.Incidents, -1, null, null, 0, 0, snap.CapturedAt, d.LapDistPct, Dq: true));
            }
        dqSeeded = true;
        // pit stats: four or more cars of our class have been through the pits since the last pit card
        var p = snap.Player;
        if (p != null)
        {
            int classStops = 0;
            foreach (var st in RaceDiary.Stops) if (string.Equals(st.Class, p.ClassName, StringComparison.OrdinalIgnoreCase)) classStops++;
            if (classStops < classStopsAtPitCard) classStopsAtPitCard = classStops;    // rewound replay
            if (classStops - classStopsAtPitCard >= 4) { classStopsAtPitCard = classStops; Enqueue(StoryKind.PitStops, nowMs, 2); }
        }
        // track limits (GoFast "On the Limit", 2026-10-25 - Angelo: "it should have been activated many times"): every
        // 6 new trips off the track across the field puts the card in line
        if (RaceDiary.TotalOffs < offsAtLimitsCard) offsAtLimitsCard = RaceDiary.TotalOffs;       // rewound replay / new race
        // 2026-10-25 (Angelo: "the On the Limit card has played so frequently"): 15 new offs, and 10 minutes since the last one
        if (RaceDiary.TotalOffs - offsAtLimitsCard >= 15 && RaceDiary.TotalOffs >= 3 && nowMs - lastLimitsMs >= 600000) { offsAtLimitsCard = RaceDiary.TotalOffs; lastLimitsMs = nowMs; Enqueue(StoryKind.Limits, nowMs, 2); }
        // 2026-10-25 (Angelo: "the fastest lap one hasn't come on when a new fastest lap change occurs"): a new fastest lap
        // in any class puts the GoFast Fastest Laps card in line (not just its turn in the rotation), at most every 3 min
        foreach (var cl in snap.ClassLeaderboard)
        {
            float best = float.MaxValue;
            foreach (var d in cl.Drivers) if (d.BestLapTime > 1 && d.BestLapTime < best) best = d.BestLapTime;
            if (best == float.MaxValue) continue;
            // 2026-10-25 (Angelo: "any time that triggers it should be the GoFast fastest lap one, and it should take over
            // and play fully no matter if a battle is happening"): plays straight away, in full (from the 2nd lap, 60 s apart)
            if (classBest.TryGetValue(cl.Name, out var was) && best < was - 0.001f && nowMs - lastFastestQueuedMs >= 60000
                && (snap.Leader?.LapsCompleted ?? 0) >= 2)
            { lastFastestQueuedMs = nowMs; fastestNow = true; fastestAtMs = nowMs; OverlayLog.Write($"story: new fastest lap in {cl.Name} - Fastest Laps card takes over"); }
            classBest[cl.Name] = best;
        }
        // the routine rotation: the next card with something to say, every CooldownMs (one routine card in line at a time);
        // 2026-10-25 (Angelo: "when there is a ton of space, like clean air, it can play the cards more frequently"):
        // after 45 s with nobody near us, every 100 s instead
        // 2026-10-25 (Angelo 16:56: "fill in more empty driving with nobody ahead or behind with cards"): after 20 s of
        // clean air a card every 40 s; otherwise at most every 3 min (was 5)
        double cooldown = quietSinceMs >= 0 && nowMs - quietSinceMs > 20000 ? 40000 : Math.Min(CooldownMs, 180000);
        if (nowMs - lastRoutineQueuedMs >= cooldown && !queue.Any(q => q.Prio == 1))
            for (int i = 0; i < Rotation.Length; i++)
            {
                var k = Rotation[(rot + i) % Rotation.Length];
                if (!HasStory(k, snap) || queue.Any(q => q.Kind == k)) continue;
                if (k == StoryKind.Limits && nowMs - lastLimitsMs < 600000) continue;   // On the Limit: 10 min apart
                if (k == StoryKind.Limits) lastLimitsMs = nowMs;
                rot = (rot + i + 1) % Rotation.Length;
                lastRoutineQueuedMs = nowMs;
                Enqueue(k, nowMs, 1);
                break;
            }
        queue.RemoveAll(q => q.Kind == StoryKind.CarContact && q.Contact is { } c && (snap.CapturedAt - c.At).TotalMinutes > 6);
    }

    public static void Tick(RaceSnapshot snap, double nowMs, bool battleActive)
    {
        if (snap.SessionNum != session)
        {
            dqSeen.Clear(); dqSeeded = false;
            session = snap.SessionNum; halfwayShown = dodShown = false; postRaceSinceMs = -1; pitCardFor = 0; wasInPit = false; pitExitAtMs = double.NegativeInfinity;
            lastEndMs = lastRoutineEndMs = lastRoutineQueuedMs = double.NegativeInfinity; rot = 0; queue.Clear(); classStopsAtPitCard = 0; offsAtLimitsCard = 0; lastLimitsMs = double.NegativeInfinity; classBest.Clear(); lastFastestQueuedMs = double.NegativeInfinity; fastestNow = false; fastestAtMs = double.NegativeInfinity;
            // the first routine card waits one gap after the start
            lastRoutineQueuedMs = nowMs;
            MiniReset();
        }
        CheckTestTrigger(nowMs);
        bool open = !Blocked(snap) && !PresentationState.OnboardSoon;   // no big cards over flags, pits, final laps or an onboard shot
        bool calm = open && !battleActive && IsCalm(snap, battleActive);
        if (!calm) quietSinceMs = -1; else if (quietSinceMs < 0) quietSinceMs = nowMs;
        bool isRace = (snap.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase);
        if (isRace && snap.Phase == RacePhase.Broadcast && snap.IsConnected) FillQueue(snap, nowMs);
        bool inPitNow = snap.Player is { IsInPit: true };
        if (wasInPit && !inPitNow && isRace && snap.Phase == RacePhase.Broadcast && RaceDiary.Stops.Count >= 1)
        { pitExitAtMs = nowMs; OverlayLog.Write("story: pit exit - the pit card plays again"); }
        wasInPit = inPitNow;
        bool pitExitDue = nowMs - pitExitAtMs < 60000 && !snap.Flags.Red && !snap.Flags.Checkered && !PresentationState.NewCardsHeld;   // #510

        // #338: driving live in a battle -> no cards at all; when it ends, anything that waited too long is dropped
        bool hush = NoCardsInBattleWhenDriving && battleActive && ApexHUD.Native.Common.DrivingState.DrivingLive;
        if (hush != hushed)
        {
            hushed = hush;
            if (hush) hushSinceMs = nowMs;
            if (hush) OverlayLog.Write("story: battle while you drive - cards held");
            else
            {
                int before = queue.Count;
                queue.RemoveAll(q => nowMs - q.At > BattleStaleMs);
                if (fastestNow && nowMs - fastestAtMs > BattleStaleMs) fastestNow = false;
                if (nowMs - pitExitAtMs > BattleStaleMs) pitExitAtMs = double.NegativeInfinity;
                OverlayLog.Write($"story: battle over - {queue.Count} card(s) still in line ({before - queue.Count} too old, dropped)");
            }
        }
        if (hush && Current != StoryKind.None && nowMs < ExitAtMs) { ExitAtMs = nowMs; bowedOut = true; pinned = false; }
        MiniTick(snap, nowMs, hush);   // #339 (preview, setting miniCardsInBattle): mini cards while the full ones are held

        if (Current != StoryKind.None)
        {
            if (pitExitDue && Current == StoryKind.PitStops)      // still up from pit entry: start it over with the stop counted
            { pitExitAtMs = double.NegativeInfinity; Start(StoryKind.PitStops, nowMs); pinned = true; return; }
            if (!pinned && (firm ? !open : !calm) && nowMs < ExitAtMs)
            {
                ExitAtMs = nowMs; bowedOut = true;   // something kicked off: bow out
                // #510 (2026-10-30): pulled by an onboard cut - it is already hidden (CardHideK), so it leaves silently and,
                // cut short, goes back to the front of the line (below) to play in full once the shot has settled
                if (PresentationState.OnboardSoon) exitCued = true;
            }
            if (nowMs >= ExitAtMs && !exitCued) { exitCued = true; StorySound.Play("story_exit"); }
            if (nowMs >= ExitAtMs + ExitMs)
            {
                bool cutShort = bowedOut && !pinned && (ExitAtMs - StartedMs) < CardMs * 0.6;
                if (cutShort && Current != StoryKind.DriverOfDay)
                    // interrupted before viewers really saw it: straight back to the front of the line, same content
                    queue.Insert(0, new Queued(Current, nowMs, WeatherHeadline, ContactShown, 9));
                else if (Current is StoryKind.RaceSoFar or StoryKind.YourRace or StoryKind.MoveOfRace or StoryKind.FastestLaps or StoryKind.PitStops or StoryKind.Limits or StoryKind.Cleanest)
                    lastRoutineEndMs = nowMs;
                Current = StoryKind.None; lastEndMs = nowMs; bowedOut = false;
            }
            return;
        }

        if (snap.Phase == RacePhase.PostRace && isRace) { queue.Clear(); return; }   // the outro (OutroDirector) runs the post-race cards
        if (hush) { WhyNot = $"{queue.Count} waiting - battle while you drive (#338)"; queue.RemoveAll(q => nowMs - q.At > BattleStaleMs * 3); return; }
        if (fastestNow && !PresentationState.NewCardsHeld && !snap.Flags.Red && !snap.Flags.Checkered)   // #510: not on a camera cut
        {
            fastestNow = false;
            queue.RemoveAll(q => q.Kind == StoryKind.FastestLaps);
            Start(StoryKind.FastestLaps, nowMs); pinned = true;    // takes over through a battle and plays its full time
            return;
        }
        if (pitExitDue && !PresentationState.CalloutActive)
        {
            pitExitAtMs = double.NegativeInfinity;
            queue.RemoveAll(q => q.Kind == StoryKind.PitStops);
            Start(StoryKind.PitStops, nowMs); pinned = true;
            return;
        }
        // 2026-10-25 (Angelo: "if nobody is in the pit with me ... play the pit stop card"): when we turn into the pits
        // the Pit Stops board plays (once per stop, if anyone has a timed stop yet) - it runs its full time
        if (isRace && snap.Phase == RacePhase.Broadcast && snap.Player is { IsInPit: true } pp && !PresentationState.CalloutActive
            && !snap.Flags.Red && !snap.Flags.Checkered && !PresentationState.NewCardsHeld)   // #510
        {
            int stopNo = RaceDiary.PitStops(pp.CarIdx);
            if (stopNo > pitCardFor && RaceDiary.Stops.Count >= 1 && !battleActive)
            {
                pitCardFor = stopNo;
                queue.RemoveAll(q => q.Kind == StoryKind.PitStops);
                Start(StoryKind.PitStops, nowMs); pinned = true;
                return;
            }
        }
        if (!open || queue.Count == 0) { if (queue.Count == 0) WhyNot = "nothing waiting"; return; }
        // #510 (2026-10-30, Angelo: "camera changes need a settle delay"): a card never starts on the same moment as a cut
        if (PresentationState.ShotSettling) { WhyNot = $"{queue.Count} waiting - the camera just cut"; return; }

        // the front of the line: highest priority, then oldest
        var next = queue.OrderByDescending(q => q.Prio).ThenBy(q => q.At).First();
        // 2026-10-25 (Angelo: "I have not seen the Trading Paints card in a while"): a battle that never ends held every
        // card for 4 minutes. Now: a DQ waits at most 20 s, a contact 45 s, anything else 90 s, then plays through the fight
        double holdMax = next.Prio >= 3 ? 20000 : next.Kind == StoryKind.CarContact ? 45000 : 90000;
        bool overdue = nowMs - next.At >= Math.Min(HoldMaxMs, holdMax);
        if (nowMs - lastEndMs < BackToBackGapMs) { WhyNot = $"{queue.Count} waiting - breather between cards"; return; }
        if (!calm && !overdue) { WhyNot = $"{queue.Count} waiting ({next.Kind}) - holding for the battle to end"; return; }
        double calmFor = calm ? nowMs - quietSinceMs : double.MaxValue;
        if (calmFor < CalmBeforeMs) { WhyNot = $"{queue.Count} waiting - calm for {calmFor / 1000:0.0} s"; return; }
        queue.Remove(next);
        firm = !calm;
        WeatherHeadline = next.Weather; ContactShown = next.Contact;
        Start(next.Kind, nowMs);
    }

    static void Start(StoryKind k, double nowMs)
    {
        Current = k; StartedMs = nowMs; ExitAtMs = nowMs + CardMs - ExitMs; pinned = false; exitCued = false;
        OverlayLog.Write($"story: playing {k} ({queue.Count} more waiting)");
        dueSinceMs = eventDueSinceMs = -1;
        StorySound.Play(StorySound.CueFor(k));
    }

    static bool HasStory(StoryKind k, RaceSnapshot s) => k switch
    {
        StoryKind.MoveOfRace => MoveOfRace(s) is { } m && RaceDiary.Gained(m) >= 2,
        StoryKind.FastestLaps => s.ClassLeaderboard.Any(c => RaceDiary.Fastest(c.Name) != null),
        StoryKind.PitStops => RaceDiary.Stops.Count >= 2,
        StoryKind.Limits => RaceDiary.TotalOffs >= 3,
        StoryKind.Cleanest => RaceDiary.HasIncidents && (s.Player?.LapsCompleted ?? 0) >= 3,
        _ => true,
    };

    /// <summary>The biggest climber in the field (ties: the better current place).</summary>
    public static Driver? MoveOfRace(RaceSnapshot s)
    {
        Driver? best = null; int bg = int.MinValue;
        foreach (var cls in s.ClassLeaderboard)
            foreach (var d in cls.Drivers)
            {
                int g = RaceDiary.Gained(d);
                if (g > bg || (g == bg && best != null && d.PositionInClass < best.PositionInClass)) { best = d; bg = g; }
            }
        return bg >= 1 ? best : null;
    }

    /// <summary>Driver of the Day: the biggest climber at the flag (ties: the better finish).</summary>
    public static Driver? DriverOfDay(RaceSnapshot s)
    {
        // Driver of the Day (2026-10-20): the best all-round race - places gained, where they
        // finished, a class fastest lap, and keeping it clean all count (Biggest Climber is its own award)
        Driver? best = null; double bs = double.MinValue;
        foreach (var cls in s.ClassLeaderboard)
        {
            var fl = RaceDiary.Fastest(cls.Name);
            foreach (var d in cls.Drivers)
            {
                if (d.PositionInClass <= 0) continue;
                int inc = RaceDiary.IncOf(d);
                double sc = RaceDiary.Gained(d) * 2.0 + Math.Max(0, 4 - d.PositionInClass) * 1.5
                          + (fl is { } f && f.CarIdx == d.CarIdx ? 3 : 0) - inc * 0.4;
                if (sc > bs || (sc == bs && best != null && d.PositionInClass < best.PositionInClass)) { bs = sc; best = d; }
            }
        }
        return best;
    }

    /// <summary>Cleanest driver among the finishers (fewest incident points; ties: better finish).</summary>
    public static Driver? Cleanest(IEnumerable<Driver> drivers)
    {
        var list = drivers.Where(d => d.PositionInClass > 0).ToList();
        if (list.Count == 0) return null;
        int maxLaps = list.Max(d => d.LapsCompleted);
        return list.Where(d => d.LapsCompleted >= maxLaps * 0.8)
                   .OrderBy(d => RaceDiary.IncOf(d)).ThenBy(d => d.PositionInClass).FirstOrDefault();
    }

    /// <summary>Times no card may show at all, however long it has waited.</summary>
    static bool Blocked(RaceSnapshot s)
    {
        if (!s.IsConnected || s.Phase != RacePhase.Broadcast) { WhyNot = "not racing"; return true; }
        // never on top of another graphic (2026-10-24, Angelo saw two stacked over the driver card)
        if (PresentationState.CalloutActive) { WhyNot = "another graphic is up"; return true; }
        if (!(s.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase)) return true;
        var f = s.Flags;
        if (f.Caution || f.Red || f.White || f.Checkered) { WhyNot = "flag"; return true; }   // local yellows no longer block (2026-10-24)
        var p = s.Player;
        if (p == null || p.IsInPit || p.LapsCompleted < 1) { WhyNot = "lap 1 / pits"; return true; }   // was 3 laps: short races never got a card
        float lap = p.BestLapTime > 30 ? p.BestLapTime : 120f;
        if (s.TotalLaps > 0 ? s.LapsRemaining <= 1 : s.SessionTimeRemaining < lap * 1.2) { WhyNot = "final lap(s)"; return true; }
        return false;
    }

    /// <summary>Nobody close to us: a card can show without hiding a fight.</summary>
    static bool IsCalm(RaceSnapshot s, bool battleActive)
    {
        var p = s.Player!;
        // nearest same-class car ahead / behind
        if (p.PositionInClass > 1 && p.IntervalSeconds is double ahead && ahead > 0 && ahead < QuietGapS) { WhyNot = "car close ahead"; return false; }
        foreach (var cls in s.ClassLeaderboard)
            for (int i = 0; i < cls.Drivers.Count; i++)
                if (cls.Drivers[i].CarIdx == p.CarIdx && i + 1 < cls.Drivers.Count
                    && cls.Drivers[i + 1].IntervalSeconds is double behind && behind > 0 && behind < QuietGapS)
                    return false;
        WhyNot = "calm";
        return true;
    }

    /// <summary>Why no story card can show right now (logged every 30 s for tuning).</summary>
    public static string WhyNot = "";
}

/// <summary>
/// The story cards (2026-10-19, Angelo: "make each one unique"). Each moment has its own
/// look, taken from what it is about, and its own entrance:
///   Race So Far   - a lap chart, every class car's line weaving across the laps (wipes in left to right)
///   Your Race     - the cockpit: your car's photo with HUD readouts and a telemetry trace (boots up)
///   Move of Race  - the climb: a tower of places built from A&amp;B's shingles, the car climbing it (rises)
///   Fastest Laps  - the stopwatch: the session-bar watch as a chronograph winding to the lap (spins up)
///   Pit Stops     - the pit board: black board, orange header, stencil numbers, lane bars (flips down)
///   Halfway       - the split screen: grid order | NOW, folded at the halfway line (splits open)
///   Weather Watch - the radar: the whole panel a StormExhibit radar scope (powers on like a CRT)
///   Driver of Day - the trophy moment: black and gold, spotlights, an engraved maker badge (fades up)
/// </summary>
public sealed partial class StoryCards : IDisposable
{
    static readonly SKColor Txt = new(0xF4, 0xF6, 0xFA), Txt2 = new(0xB8, 0xC0, 0xCE), Txt3 = new(0x7C, 0x86, 0x98);
    static readonly SKColor Red = new(0xD8, 0x1E, 0x2A), Gain = new(0x34, 0xD3, 0x99), Loss = new(0xF8, 0x71, 0x71);
    static readonly SKColor Blue = new(0x3B, 0x82, 0xF6), Purple = new(0xB0, 0x7C, 0xFF), Gold = new(0xE8, 0xC2, 0x6A);
    static readonly SKColor Storm = new(0x4F, 0xB3, 0xFF), Hud = new(0x5E, 0xE7, 0xFF), PitOrange = new(0xFF, 0x7A, 0x1A);
    static readonly SKColor Amber = new(0xFF, 0xB4, 0x1F);
    // partner themes (Angelo 2026-10-20: "their colour and their theme")
    static readonly SKColor GoGreen = new(0x24, 0xE0, 0x8A), GoGreenDeep = new(0x13, 0x7A, 0x49);    // gofast.gg
    static readonly SKColor TpBlue = new(0x27, 0x9B, 0xF0), TpRed = new(0xFF, 0x1F, 0x44);           // Trading Paints
    static readonly SKColor[] LinePalette =
    {
        new(0xF2, 0x8B, 0x82), new(0xFD, 0xD6, 0x63), new(0x81, 0xC9, 0x95), new(0x78, 0xD9, 0xEC), new(0xD7, 0xAE, 0xFB),
        new(0xFC, 0xAD, 0x70), new(0xA8, 0xDA, 0xB5), new(0xE6, 0xC9, 0xA8), new(0xF4, 0x8F, 0xB1), new(0xB0, 0xBE, 0xC5),
    };
    readonly Dictionary<string, SKBitmap?> bmps = new(StringComparer.OrdinalIgnoreCase);
    readonly WatchRenderer watch = new();

    public void Draw(SKCanvas c, float w, float h, RaceSnapshot snap, double nowMs)
    {
        var kind = StoryDirector.Current;
        if (kind == StoryKind.None || snap.Player == null) return;
        double t = (nowMs - StoryDirector.StartedMs) / 1000.0;
        double tx = (nowMs - StoryDirector.ExitAtMs) / 1000.0;
        float outK = tx > 0 ? Ease((float)(tx / (StoryDirector.ExitMs / 1000.0))) : 0;
        if (outK >= 0.999f) return;
        // 2026-10-27 (#349): the right-third layouts (StoryCardsColumn.cs); classic carries on below unchanged
        if (Layout != CardLayout.Classic) { DrawColumn(c, w, h, snap, kind, t, outK); return; }
        var r = new SKRect(w / 2f + 20, 172, w - 36, h - 36);   // top leaves room for the LIVE tab under the session bar
        var cls = ClassOf(snap, snap.Player);

        // shared exit (2026-10-20): a bar in the card's colour sweeps across, wiping it away
        var wipeCol = kind switch
        {
            StoryKind.WeatherWatch => Storm, StoryKind.DriverOfDay => Gold, StoryKind.FastestLaps => GoGreen,
            StoryKind.YourRace => Hud, StoryKind.PitStops => PitOrange, StoryKind.CarContact or StoryKind.Cleanest => TpBlue, StoryKind.Limits => GoGreen,
            _ => ParseHex(cls?.Color ?? snap.Player.ClassColor, Gold),
        };
        float bandX = r.Left - 60 + (r.Width + 140) * outK;
        c.Save();
        if (outK > 0) c.ClipRect(new SKRect(bandX, 0, w + 10, h + 10));
        float a = 1;
        double impact = ImpactAt(kind), ti = impact >= 0 ? t - impact : -1;
        if (ti > 0 && ti < 0.4)   // camera kick as the card lands, settling fast
        {
            float amp = 6f * (float)Math.Exp(-ti * 11);
            c.Translate((float)Math.Sin(ti * 70) * amp, (float)Math.Cos(ti * 57) * amp * 0.6f);
        }
        switch (kind)
        {
            case StoryKind.RaceSoFar: LapChart(c, r, snap, cls, t, a); break;
            case StoryKind.YourRace: Cockpit(c, r, snap, cls, t, a); break;
            case StoryKind.MoveOfRace: Climb(c, r, snap, t, a); break;
            case StoryKind.FastestLaps: Stopwatch(c, r, snap, cls, t, a); break;
            case StoryKind.PitStops: PitBoard(c, r, snap, t, a); break;
            case StoryKind.Halfway: Split(c, r, snap, cls, t, a); break;
            case StoryKind.WeatherWatch: Radar(c, r, snap, cls, t, a); break;
            case StoryKind.DriverOfDay: Trophy(c, r, snap, t, a); break;
            case StoryKind.CarContact: ContactCard(c, r, snap, t, a); break;
            case StoryKind.Limits: LimitsCard(c, r, snap, cls, t, a); break;
            case StoryKind.Cleanest: CleanCard(c, r, snap, t, a); break;
        }
        BroadcastLayer(c, r, kind, snap, t, ti, a);
        c.Restore();
        if (outK > 0 && outK < 1)
        {
            float bw = 26;
            using (var trail = SKShader.CreateLinearGradient(new SKPoint(bandX - 220, 0), new SKPoint(bandX, 0), new[] { wipeCol.WithAlpha(0), wipeCol.WithAlpha(120) }, null, SKShaderTileMode.Clamp))
            using (var tp = new SKPaint { Shader = trail }) c.DrawRect(new SKRect(Math.Max(r.Left - 60, bandX - 220), r.Top - 10, bandX, r.Bottom + 10), tp);
            using (var bp = new SKPaint { Color = wipeCol }) c.DrawRect(new SKRect(bandX, r.Top - 10, bandX + bw, r.Bottom + 10), bp);
            using (var hl = new SKPaint { Color = SKColors.White.WithAlpha(160) }) c.DrawRect(new SKRect(bandX + bw - 3, r.Top - 10, bandX + bw, r.Bottom + 10), hl);
        }
    }

    // ================================================================== 1. RACE SO FAR - the lap chart
    void LapChart(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        var cc = ParseHex(cls?.Color, Gold);
        float wipe = K(t, 0, 0.7);
        c.Save();
        c.ClipRect(new SKRect(r.Left - 2, r.Top - 4, r.Left + (r.Width + 4) * wipe, r.Bottom + 4));
        using var shape = Slant(r);
        Glass(c, shape, r, new SKColor(9, 12, 18), new SKColor(15, 19, 28), cc, a);
        c.Save(); c.ClipPath(shape, antialias: true);
        // faint graph-paper grid
        using (var gp = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(7 * a)), StrokeWidth = 1 })
        {
            for (float gx = r.Left; gx < r.Right; gx += 32) c.DrawLine(gx, r.Top, gx, r.Bottom, gp);
            for (float gy = r.Top; gy < r.Bottom; gy += 32) c.DrawLine(r.Left, gy, r.Right, gy, gp);
        }
        float L = r.Left + 96, R = r.Right - 44, y = r.Top + 70;
        Eyebrow(c, L, y, "RACE SO FAR", cls, Red, t, a);
        Presenters.DrawPresentedBy(c, Presenters.For(StoryKind.RaceSoFar), "PRESENTED BY", R, r.Top + 64, 30, a * K(t, 0.45, 0.4));
        using (var tp = P(Fonts.BarlowCondensed.Black, 76, Txt, a * K(t, 0.3, 0.4)))
            c.DrawText(ToGo(s).ToUpperInvariant(), L, y + 82, tp);

        if (cls != null && cls.Drivers.Count > 0)
        {
            var area = new SKRect(L + 34, y + 130, R - 300, r.Bottom - 120);
            int n = cls.Drivers.Count;
            // 2026-10-25: positions from the order each lap was completed (RaceDiary.LapChart); the chart starts at
            // the first lap the diary actually saw (a replay jump no longer draws straight lines from the grid)
            // 2026-10-25 (Angelo: "I want it to show every change"): every change of place through the laps, when
            // the diary has it (RaceDiary.LiveChart); the lap-by-lap chart is the fallback
            if (RaceDiary.LiveChart(cls.Name) is { } lc && LiveLapChart(c, area, s, cls, lc.Chart, lc.Now, t, a)) goto order;
            var chart = RaceDiary.LapChart(cls.Drivers);
            int maxLap = 1, minLap = int.MaxValue;
            foreach (var kv in chart) foreach (var hp in kv.Value) { maxLap = Math.Max(maxLap, hp.Lap); minLap = Math.Min(minLap, hp.Lap); }
            if (minLap == int.MaxValue || minLap >= maxLap) minLap = Math.Max(0, maxLap - 1);
            float X(double lap) => area.Left + area.Width * (float)((lap - minLap) / Math.Max(1, maxLap - minLap));
            float Y(int pos) => area.Top + area.Height * (pos - 1) / Math.Max(1, n - 1);
            // lap ruler
            using (var lab = P(Fonts.Inter.SemiBold, 12, Txt3, a))
            using (var tick = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(22 * a)), StrokeWidth = 1 })
            {
                int step = Math.Max(1, (int)Math.Ceiling((maxLap - minLap) / 8.0));
                lab.TextAlign = SKTextAlign.Center;
                for (int lp = minLap; lp <= maxLap; lp += step)
                {
                    c.DrawLine(X(lp), area.Top - 10, X(lp), area.Bottom + 6, tick);
                    c.DrawText(lp == 0 ? "GRID" : "L" + lp, X(lp), area.Bottom + 28, lab);
                }
                lab.TextAlign = SKTextAlign.Right;
                for (int q = 1; q <= n; q += n > 10 ? 2 : 1) c.DrawText("P" + q, area.Left - 14, Y(q) + 4, lab);
            }
            double reveal = minLap + (maxLap - minLap) * Ease((float)((t - 0.45) / 1.7));
            // draw back to front: field, then leader, then you on top
            var order = cls.Drivers.Select((d, i) => (d, i)).OrderBy(x => x.d.CarIdx == s.PlayerCarIdx ? 2 : x.i == 0 ? 1 : 0).ToList();
            foreach (var (d, i) in order)
            {
                var hist = chart.TryGetValue(d.CarIdx, out var hl) ? hl : new List<(int Lap, int Pos)>();
                if (hist.Count < 2 || reveal <= minLap) continue;
                bool me = d.CarIdx == s.PlayerCarIdx;
                var col = me ? Blue : i == 0 ? Gold : LinePalette[d.CarIdx % LinePalette.Length];
                using var path = new SKPath();
                path.MoveTo(X(hist[0].Lap), Y(hist[0].Pos));
                SKPoint end = new(X(hist[0].Lap), Y(hist[0].Pos));
                for (int k = 1; k < hist.Count; k++)
                {
                    if (hist[k].Lap <= reveal) { end = new SKPoint(X(hist[k].Lap), Y(hist[k].Pos)); path.LineTo(end); continue; }
                    double f = (reveal - hist[k - 1].Lap) / Math.Max(1e-6, hist[k].Lap - hist[k - 1].Lap);
                    if (f > 0)
                    {
                        end = new SKPoint(X(hist[k - 1].Lap + (hist[k].Lap - hist[k - 1].Lap) * f),
                                          Y(hist[k - 1].Pos) + (Y(hist[k].Pos) - Y(hist[k - 1].Pos)) * (float)f);
                        path.LineTo(end);
                    }
                    break;
                }
                if (me)
                    using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 12, Color = Blue.WithAlpha((byte)(70 * a)), ImageFilter = SKImageFilter.CreateBlur(6, 6), StrokeJoin = SKStrokeJoin.Round })
                        c.DrawPath(path, glow);
                using (var lp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = me ? 5 : i == 0 ? 3.5f : 2.4f, Color = col.WithAlpha((byte)((me || i == 0 ? 255 : 170) * a)), StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round })
                    c.DrawPath(path, lp);
                using (var dp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * a)) }) c.DrawCircle(end, me ? 6 : 4, dp);
            }
            order:
            // the order at the line's end
            float lk = K(t, 1.9, 0.5);
            float fs = Math.Clamp(area.Height / Math.Max(1, n - 1) * 0.62f, 12, 21);
            for (int i = 0; i < n; i++)
            {
                var d = cls.Drivers[i];
                if (n > 14 && i >= 10 && d.CarIdx != s.PlayerCarIdx) continue;
                bool me = d.CarIdx == s.PlayerCarIdx;
                float ly = Y(d.PositionInClass > 0 ? d.PositionInClass : i + 1), lx = area.Right + 26 + (1 - lk) * 30, la = a * lk;
                var col = me ? Blue : i == 0 ? Gold : LinePalette[d.CarIdx % LinePalette.Length];
                using (var dp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * la)) }) c.DrawRect(new SKRect(lx - 14, ly - 2, lx - 4, ly + 2), dp);
                using (var pp = P(Fonts.BarlowCondensed.ExtraBold, fs * 1.2f, i == 0 ? Gold : Txt, la)) { pp.TextAlign = SKTextAlign.Right; c.DrawText((i + 1).ToString(CultureInfo.InvariantCulture), lx + 22, ly + fs * 0.36f, pp); }
                using (var np = P(Fonts.Inter.SemiBold, fs, me ? SKColors.White : Txt2, la)) c.DrawText(Fit(NameFormat.Broadcast(d), np, 196), lx + 32, ly + fs * 0.36f, np);
                int g = RaceDiary.Gained(d);
                if (g != 0) DrawDelta(c, R - 14, ly, g, la, fs * 0.85f);
            }
        }
        var fl = cls != null ? RaceDiary.Fastest(cls.Name) : null;
        float fk = K(t, 2.2, 0.5);
        using (var fp = P(Fonts.Inter.Bold, 13, Txt3, a * fk))
        {
            string line = $"LEAD CHANGES  {(cls != null ? RaceDiary.LeadChanges(cls.Name) : 0)}      PIT STOPS  {(cls != null ? RaceDiary.TotalPitStops(cls.Drivers) : 0)}"
                        + (fl is { } f ? $"      FASTEST  {LapTimeFormat.Lap(f.Time)}  {f.Name}" : "");
            TextTracked.Draw(c, fp, line, L, r.Bottom - 50, 2f);
        }
        c.Restore();
        c.Restore();
    }

    /// <summary>Race So Far's chart from every change of place (2026-10-25). False when there isn't enough yet.</summary>
    bool LiveLapChart(SKCanvas c, SKRect area, RaceSnapshot s, DriverClass cls, Dictionary<int, List<(float X, int Pos)>> chart, float now, double t, float a)
    {
        float minX = float.MaxValue;
        foreach (var d in cls.Drivers) if (chart.TryGetValue(d.CarIdx, out var l) && l.Count > 0) minX = Math.Min(minX, l[0].X);
        if (minX == float.MaxValue || now - minX < 0.5f) return false;
        int n = cls.Drivers.Count;
        float x0 = (float)Math.Floor(minX), x1 = Math.Max(now, x0 + 1);
        float X(double lap) => area.Left + area.Width * (float)((lap - x0) / Math.Max(0.001, x1 - x0));
        float Y(int pos) => area.Top + area.Height * (pos - 1) / Math.Max(1, n - 1);
        using (var lab = P(Fonts.Inter.SemiBold, 12, Txt3, a))
        using (var tick = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(22 * a)), StrokeWidth = 1 })
        {
            int span = (int)Math.Ceiling(x1 - x0), step = Math.Max(1, (int)Math.Ceiling(span / 8.0));
            lab.TextAlign = SKTextAlign.Center;
            for (int lp = (int)x0; lp <= (int)Math.Floor(x1); lp += step)
            {
                c.DrawLine(X(lp), area.Top - 10, X(lp), area.Bottom + 6, tick);
                c.DrawText(lp == 0 ? "START" : "L" + lp, X(lp), area.Bottom + 28, lab);
            }
            lab.TextAlign = SKTextAlign.Right;
            for (int q = 1; q <= n; q += n > 10 ? 2 : 1) c.DrawText("P" + q, area.Left - 14, Y(q) + 4, lab);
        }
        float reveal = x0 + (x1 - x0) * Ease((float)((t - 0.45) / 1.7));
        float ramp = Math.Clamp((x1 - x0) * 0.004f, 0.01f, 0.06f);       // a place change is a short slope, not a cliff
        var order = cls.Drivers.Select((d, i) => (d, i)).OrderBy(z => z.d.CarIdx == s.PlayerCarIdx ? 2 : z.i == 0 ? 1 : 0).ToList();
        c.Save();
        c.ClipRect(new SKRect(area.Left - 20, area.Top - 30, X(reveal) + 1, area.Bottom + 30));
        foreach (var (d, i) in order)
        {
            if (!chart.TryGetValue(d.CarIdx, out var h) || h.Count == 0) continue;
            bool me = d.CarIdx == s.PlayerCarIdx;
            var col = me ? Blue : i == 0 ? Gold : LinePalette[d.CarIdx % LinePalette.Length];
            using var path = new SKPath();
            path.MoveTo(X(h[0].X), Y(h[0].Pos));
            for (int k = 1; k < h.Count; k++)
            {
                path.LineTo(X(Math.Max(h[k - 1].X, h[k].X - ramp)), Y(h[k - 1].Pos));
                path.LineTo(X(h[k].X), Y(h[k].Pos));
            }
            path.LineTo(X(now), Y(h[^1].Pos));
            if (me)
                using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 12, Color = Blue.WithAlpha((byte)(70 * a)), ImageFilter = SKImageFilter.CreateBlur(6, 6), StrokeJoin = SKStrokeJoin.Round })
                    c.DrawPath(path, glow);
            using (var lp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = me ? 4 : i == 0 ? 3f : 1.8f, Color = col.WithAlpha((byte)((me || i == 0 ? 255 : 160) * a)), StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round })
                c.DrawPath(path, lp);
            // the dot rides the line as it draws in
            int posAt = h[0].Pos; foreach (var pt in h) { if (pt.X <= reveal) posAt = pt.Pos; else break; }
            using (var dp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * a)) }) c.DrawCircle(X(reveal), Y(posAt), me ? 6 : 4, dp);
        }
        c.Restore();
        return true;
    }

    // ================================================================== 2. YOUR RACE - the cockpit
    void Cockpit(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        var p = s.Player!;
        float boot = K(t, 0, 0.5);
        using var shape = new SKPath();
        float ch = SquareCorners ? 0 : 34;   // #412 preview: square corners
        shape.MoveTo(r.Left + ch, r.Top); shape.LineTo(r.Right, r.Top); shape.LineTo(r.Right, r.Bottom - ch);
        shape.LineTo(r.Right - ch, r.Bottom); shape.LineTo(r.Left, r.Bottom); shape.LineTo(r.Left, r.Top + ch); shape.Close();
        c.Save();
        float sc = 1.04f - 0.04f * boot;
        c.Scale(sc, sc, r.MidX, r.MidY);
        BlurPlates.Path(c, SquareCorners ? 4 : 3, r, shape, r, new SKColor(0, 0, 0, (byte)(120 * a * boot)), 18);   // #604: a picture while it holds
        c.Save(); c.ClipPath(shape, antialias: true);
        using (var bg = new SKPaint { Color = new SKColor(4, 7, 11, (byte)(250 * a)) }) c.DrawRect(r, bg);
        // 2026-10-25 (Angelo): our OWN picture of the player's car (iRacing's renderer with their livery) instead of a
        // press photo we have no rights to; it sits big on the right, lit, the way a broadcast shows the hero car
        var front = LiveryArt.GetFront(p);
        var own = front ?? LiveryArt.Get(p);   // the front photo from the sim when the photo booth has one
        var photo = own != null ? null : CarPhoto(p.CarName) ?? CarArt.Fallback(p.CarName, p.CarBrand, p.ClassName, p.ClassColor);   // user picture / drawn panel (2026-10-24)
        if (photo != null)
        {
            float s0 = Math.Max(r.Width / photo.Width, r.Height / photo.Height), pw = photo.Width * s0, ph = photo.Height * s0;
            using var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * K(t, 0.1, 0.9))) };
            c.DrawBitmap(photo, new SKRect(r.MidX - pw / 2, r.MidY - ph / 2, r.MidX + pw / 2, r.MidY + ph / 2), ip);
        }
        using (var dk = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                   new[] { new SKColor(2, 5, 9, (byte)(150 * a)), new SKColor(2, 5, 9, (byte)(205 * a)), new SKColor(2, 5, 9, (byte)(240 * a)) },
                   new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp))
        using (var dp = new SKPaint { Shader = dk }) c.DrawRect(r, dp);
        Halftone(c, r, a);
        if (own != null)   // drawn over the darkening so the car stays bright
        {
            float ch2 = front != null ? Math.Min(r.Height * 0.42f, own.Height * 1.6f) : Math.Min(r.Height * 0.62f, own.Height * 2.2f), cw2 = ch2 * own.Width / own.Height;
            if (cw2 > r.Width * 0.70f) { cw2 = r.Width * 0.70f; ch2 = cw2 * own.Height / own.Width; }
            float drift = (1 - K(t, 0.1, 1.2)) * 60f;    // eases in from the right
            var dst = new SKRect(r.Right - cw2 - r.Width * 0.03f + drift, r.Bottom - ch2 - r.Height * 0.14f, r.Right - r.Width * 0.03f + drift, r.Bottom - r.Height * 0.14f);
            using (var glow = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(dst.MidX, dst.MidY), cw2 * 0.6f,
                       new[] { new SKColor(70, 110, 170, (byte)(70 * a)), SKColors.Transparent }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(r, glow);
            using (var sh2 = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(150 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14f) })
                c.DrawOval(new SKRect(dst.Left + cw2 * 0.06f, dst.Bottom - ch2 * 0.10f, dst.Right - cw2 * 0.02f, dst.Bottom + ch2 * 0.05f), sh2);
            using var ip2 = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * K(t, 0.1, 0.9))) };
            c.DrawImage(own, dst, ip2);
        }
        // scanlines + boot flicker
        using (var sl = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(40 * a)) })
            for (float yy = r.Top; yy < r.Bottom; yy += 3) c.DrawRect(new SKRect(r.Left, yy, r.Right, yy + 1), sl);
        if (t < 0.45)
            using (var fl = new SKPaint { Color = Hud.WithAlpha((byte)(40 * (1 - t / 0.45) * ((int)(t * 30) % 2))) }) c.DrawRect(r, fl);

        float L = r.Left + 64, R = r.Right - 56, y = r.Top + 66;
        float hk = K(t, 0.25, 0.4);
        using (var ep = P(Fonts.BarlowCondensed.Bold, 22, Hud, a * hk)) TextTracked.Draw(c, ep, OutroDirector.Running ? "YOUR RACE  ·  FINAL" : "YOUR RACE", L, y, 3.4f);
        var clogo = cls != null ? ClassLogoBmp(cls.Name) : null;
        if (clogo != null) { float lh = 24, lw = clogo.Width * lh / clogo.Height; DrawLogo(c, clogo, R - lw / 2, y - 6, lw, lh, a * hk); }
        using (var np = P(Fonts.BarlowCondensed.Black, 64, SKColors.White, a * K(t, 0.35, 0.4))) c.DrawText(Fit(p.Name.ToUpperInvariant(), np, R - L), L, y + 58, np);
        using (var sp = P(Fonts.Inter.SemiBold, 16, Txt2, a * K(t, 0.4, 0.4))) TextTracked.Draw(c, sp, $"#{p.CarNumber}   ·   {p.CarName.ToUpperInvariant()}", L, y + 88, 1.8f);

        int start = RaceDiary.GridPos(p.CarIdx), now = p.PositionInClass, gained = start > 0 ? start - now : 0;
        float bw = (R - L - 32) / 3f, by = y + 120;
        Readout(c, new SKRect(L, by, L + bw, by + 150), "STARTED", start > 0 ? $"P{start}" : "-", SKColors.White, t, 0.55, a, 78);
        Readout(c, new SKRect(L + bw + 16, by, L + bw * 2 + 16, by + 150), "NOW", $"P{now}", now == 1 ? Gold : SKColors.White, t, 0.65, a, 78);
        Readout(c, new SKRect(L + bw * 2 + 32, by, R, by + 150), "PLACES", gained > 0 ? $"+{gained}" : gained.ToString(CultureInfo.InvariantCulture), gained > 0 ? Gain : gained < 0 ? Loss : Txt2, t, 0.75, a, 78);

        // telemetry trace
        var area = new SKRect(L + 44, by + 200, R - 8, r.Bottom - 200);
        using (var box = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = Hud.WithAlpha((byte)(90 * a * K(t, 0.8, 0.4))) })
            c.DrawRect(new SKRect(area.Left - 50, area.Top - 26, area.Right + 8, area.Bottom + 40), box);
        using (var tl = P(Fonts.Inter.Bold, 11, Hud, a * K(t, 0.8, 0.4))) TextTracked.Draw(c, tl, "POSITION TRACE", area.Left - 40, area.Top - 8, 3f);
        PositionTrace(c, area, RaceDiary.PlayerHistory, now, p.LapsCompleted, cls?.Drivers.Count ?? 0, t, a, Hud);

        float cb = ClassBest(cls);
        string best = p.BestLapTime > 0 ? LapTimeFormat.Lap(p.BestLapTime) : "-";
        string toBest = p.BestLapTime > 0 && cb > 0 ? (Math.Abs(p.BestLapTime - cb) < 0.0005f ? "CLASS FASTEST" : "+" + (p.BestLapTime - cb).ToString("0.000", CultureInfo.InvariantCulture)) : "-";
        string ahead = now <= 1 ? "LEADING" : p.IntervalSeconds is double ia && ia > 0 ? "+" + ia.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "-";
        float fy = r.Bottom - 130, fw = (R - L - 32) / 3f;
        Readout(c, new SKRect(L, fy, L + fw, fy + 96), "BEST LAP", best, SKColors.White, t, 1.2, a, 34);
        Readout(c, new SKRect(L + fw + 16, fy, L + fw * 2 + 16, fy + 96), "TO CLASS BEST", toBest, toBest == "CLASS FASTEST" ? Purple : SKColors.White, t, 1.3, a, toBest == "CLASS FASTEST" ? 26 : 34);
        Readout(c, new SKRect(L + fw * 2 + 32, fy, R, fy + 96), "TO CAR AHEAD", ahead, SKColors.White, t, 1.4, a, 34);
        c.Restore();
        // HUD corner brackets drawing in
        float bk = K(t, 0.05, 0.5), bl = 70 * bk;
        using (var bp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Hud.WithAlpha((byte)(230 * a)) })
        {
            c.DrawLine(r.Left + ch, r.Top, r.Left + ch + bl, r.Top, bp); c.DrawLine(r.Left, r.Top + ch, r.Left, r.Top + ch + bl, bp);
            c.DrawLine(r.Left, r.Top + ch, r.Left + ch, r.Top, bp);
            c.DrawLine(r.Right, r.Bottom - ch, r.Right, r.Bottom - ch - bl, bp); c.DrawLine(r.Right - ch, r.Bottom, r.Right - ch - bl, r.Bottom, bp);
            c.DrawLine(r.Right - ch, r.Bottom, r.Right, r.Bottom - ch, bp);
            c.DrawLine(r.Right - bl, r.Top, r.Right, r.Top, bp); c.DrawLine(r.Right, r.Top, r.Right, r.Top + bl, bp);
            c.DrawLine(r.Left, r.Bottom, r.Left + bl, r.Bottom, bp); c.DrawLine(r.Left, r.Bottom - bl, r.Left, r.Bottom, bp);
        }
        c.Restore();
    }

    void Readout(SKCanvas c, SKRect b, string label, string value, SKColor col, double t, double at, float a, float size)
    {
        float k = K(t, at, 0.35);
        if (k <= 0) return;
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(130 * a * k)) }) c.DrawRect(b, bg);
        using (var bd = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = Hud.WithAlpha((byte)(150 * a * k)) }) c.DrawRect(b, bd);
        using (var tk = new SKPaint { Color = Hud.WithAlpha((byte)(255 * a * k)) }) c.DrawRect(new SKRect(b.Left, b.Top, b.Left + 16, b.Top + 3), tk);
        using (var lp = P(Fonts.Inter.Bold, 12, Hud, a * k)) TextTracked.Draw(c, lp, label, b.Left + 14, b.Top + 24, 2.6f);
        using var vp = P(Fonts.BarlowCondensed.ExtraBold, size * 1.18f, col, a * k);
        while (vp.MeasureText(value) > b.Width - 24 && vp.TextSize > 14) vp.TextSize -= 1;
        DrawRolling(c, value, b.MidX, b.Bottom - (b.Height - 24) / 2 + vp.TextSize * 0.36f, vp, K(t, at, 0.9), SKTextAlign.Center);
    }

    /// <summary>Odometer-style text (2026-10-20): every digit rolls up through its numbers
    /// and lands on its value, left digits settling first. Digits sit in equal-width cells.</summary>
    static void DrawRolling(SKCanvas c, string text, float x, float baseline, SKPaint p, float k, SKTextAlign align)
    {
        var keep = p.TextAlign; p.TextAlign = SKTextAlign.Left;
        float cell = p.MeasureText("0"), total = 0;
        foreach (char ch in text) total += char.IsDigit(ch) ? cell : p.MeasureText(ch.ToString());
        float cx = align == SKTextAlign.Center ? x - total / 2 : align == SKTextAlign.Right ? x - total : x;
        var db = new SKRect(); p.MeasureText("0123456789", ref db);   // the digits' own ink box
        float lineH = db.Height + 14;
        int n = text.Length;
        for (int i = 0; i < n; i++)
        {
            char ch = text[i];
            if (!char.IsDigit(ch)) { c.DrawText(ch.ToString(), cx, baseline, p); cx += p.MeasureText(ch.ToString()); continue; }
            float ki = Ease(Math.Clamp((k - i * 0.05f) / Math.Max(0.2f, 1 - n * 0.05f), 0, 1));
            int d = ch - '0';
            float v = (d + 10) * ki;                  // one full turn, then land on the digit
            int lo = (int)Math.Floor(v);
            float frac = v - lo;
            c.Save();
            c.ClipRect(new SKRect(cx - 2, baseline + db.Top - 3, cx + cell + 2, baseline + db.Bottom + 3));
            string a0 = (lo % 10).ToString(), a1 = ((lo + 1) % 10).ToString();
            byte baseA = p.Color.Alpha;   // slide AND cross-fade, so half-rolled digits never read as clutter
            p.Color = p.Color.WithAlpha((byte)(baseA * (1 - frac)));
            c.DrawText(a0, cx + (cell - p.MeasureText(a0)) / 2, baseline - frac * lineH * 0.6f, p);
            if (frac > 0.001f)
            {
                p.Color = p.Color.WithAlpha((byte)(baseA * frac));
                c.DrawText(a1, cx + (cell - p.MeasureText(a1)) / 2, baseline + (1 - frac) * lineH * 0.6f, p);
            }
            p.Color = p.Color.WithAlpha(baseA);
            c.Restore();
            cx += cell;
        }
        p.TextAlign = keep;
    }

    /// <summary>Digits flicker through random values until the readout locks on.</summary>
    static string Scramble(string v, float k, double t)
    {
        if (k >= 0.999f) return v;
        var ch = v.ToCharArray();
        int seed = (int)(t * 24);
        for (int i = 0; i < ch.Length; i++)
            if (char.IsDigit(ch[i]) && (i + 1f) / ch.Length > k) ch[i] = (char)('0' + Math.Abs((seed * 31 + i * 17) % 10));
        return new string(ch);
    }

    // ================================================================== 3. MOVE OF THE RACE - the climb (A&B)
    void Climb(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        var d = StoryDirector.MoveOfRace(s);
        var dcls = d != null ? ClassOf(s, d) : null;
        var cc = ParseHex(dcls?.Color, Gold);
        float rise = K(t, 0, 0.7);
        c.Save();
        c.ClipRect(new SKRect(r.Left - 4, r.Bottom - (r.Height + 8) * rise, r.Right + 4, r.Bottom + 4));
        using var shape = Slant(r);
        Glass(c, shape, r, new SKColor(14, 12, 10), new SKColor(22, 19, 16), Gold, a);
        c.Save(); c.ClipPath(shape, antialias: true);
        if (d == null) { c.Restore(); c.Restore(); return; }
        int g = Math.Max(1, RaceDiary.GridPos(d.CarIdx)), now = Math.Max(1, d.PositionInClass), gained = g - now;
        int slots = Math.Min(14, Math.Max(g, now) + 1);
        // the tower of places, built from A&B's shingles
        var tower = new SKRect(r.Left + 110, r.Top + 60, r.Left + 340, r.Bottom - 60);
        float slotH = tower.Height / slots;
        var tile = Bmp("logos", "sponsors", "ab_roof_tile.png");
        if (tile != null)
        {
            float ts = 230f / tile.Width;
            using var shd = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateScale(ts, ts).PostConcat(SKMatrix.CreateTranslation(tower.Left, tower.Top)));
            using var tp = new SKPaint { Shader = shd, IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * a)) };
            c.DrawRect(tower, tp);
        }
        using (var dk = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(120 * a)) }) c.DrawRect(tower, dk);
        float climbK = K(t, 0.9, 1.7);
        float slotY(int pos) => tower.Top + slotH * (pos - 1);
        float markY = slotY(g) + (slotY(now) - slotY(g)) * climbK;
        for (int q = 1; q <= slots; q++)
        {
            var sr = new SKRect(tower.Left, slotY(q), tower.Right, slotY(q) + slotH);
            float lay = K(t, 0.25 + (slots - q) * 0.04, 0.3);   // courses laid bottom-up
            bool passed = q <= g && q >= now && markY <= sr.Top + 2;
            if (passed)
                using (var lit = new SKPaint { Color = Gold.WithAlpha((byte)(70 * a)) }) c.DrawRect(sr, lit);
            using (var cl = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(200 * a * lay)), StrokeWidth = 3 }) c.DrawLine(sr.Left, sr.Bottom, sr.Left + sr.Width * lay, sr.Bottom, cl);
            using (var pl = P(Fonts.BarlowCondensed.ExtraBold, Math.Min(28, slotH * 0.52f), q == now ? Gold : passed ? Txt : Txt2, a * lay))
                c.DrawText("P" + q, sr.Left + 14, sr.MidY + pl.TextSize * 0.36f, pl);
            if (q == g)
                using (var gt = P(Fonts.Inter.Bold, 11, Txt3, a * lay)) { gt.TextAlign = SKTextAlign.Right; c.DrawText("GRID", sr.Right - 12, sr.MidY + 4, gt); }
        }
        using (var edge = new SKPaint { Color = Gold.WithAlpha((byte)(200 * a)), StrokeWidth = 2, Style = SKPaintStyle.Stroke }) c.DrawRect(tower, edge);
        // the climbing car: number plate + maker badge
        var plate = new SKRect(tower.Left + 64, markY + 6, tower.Right - 10, markY + slotH - 6);
        using (var pg = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(60 * a)), ImageFilter = SKImageFilter.CreateBlur(8, 8) }) c.DrawRoundRect(plate, 8, 8, pg);
        using (var pb = new SKPaint { IsAntialias = true, Color = new SKColor(14, 15, 20, (byte)(245 * a)) }) c.DrawRoundRect(plate, 8, 8, pb);   // dark plate: logos keep their whites
        using (var pe = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = Gold.WithAlpha((byte)(255 * a)) }) c.DrawRoundRect(plate, 8, 8, pe);
        using (var nb = P(Fonts.BarlowCondensed.Black, Math.Min(38, plate.Height * 0.72f), SKColors.White, a)) c.DrawText("#" + d.CarNumber, plate.Left + 12, plate.MidY + nb.TextSize * 0.36f, nb);
        if (Maker(d.CarBrand) is { } mkb)
        {
            float bh = plate.Height * 0.66f;
            using var dark = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)) };
            float bw2 = mkb.Width * bh / mkb.Height; if (bw2 > 60) { bw2 = 60; bh = mkb.Height * bw2 / mkb.Width; }
            c.DrawBitmap(mkb, new SKRect(plate.Right - 10 - bw2, plate.MidY - bh / 2, plate.Right - 10, plate.MidY + bh / 2), dark);
        }

        // the story, to the right of the tower
        float L = tower.Right + 56, R = r.Right - 44, y = r.Top + 96;
        using (var ep = P(Fonts.BarlowCondensed.Bold, 22, Gold, a * K(t, 0.3, 0.4)))
        {
            string mt = OutroDirector.Running ? "BIGGEST CLIMBER" : "MOVES OF THE RACE";
            TextTracked.Draw(c, ep, mt, L, y, 3.4f);
            Presenters.DrawPresentedAfter(c, Presenters.For(StoryKind.MoveOfRace), Presenters.Verb(StoryKind.MoveOfRace),
                L + TrackedWidth(ep, mt, 3.4f) + 16, y - 8, 34, a * K(t, 0.45, 0.4));
        }
        if (dcls != null && ClassLogoBmp(dcls.Name) is { } cl2) { float lh = 24, lw = cl2.Width * lh / cl2.Height; DrawLogo(c, cl2, L + lw / 2, y + 30, lw, lh, a * K(t, 0.35, 0.4)); }
        using (var np = P(Fonts.BarlowCondensed.Black, 66, Txt, a * K(t, 0.4, 0.4)))
        {
            var parts = d.Name.ToUpperInvariant().Split(' ', 2);
            c.DrawText(Fit(parts[0], np, R - L), L, y + 104, np);
            if (parts.Length > 1) c.DrawText(Fit(parts[1], np, R - L), L, y + 156, np);
        }
        using (var sp = P(Fonts.Inter.SemiBold, 15, Txt2, a * K(t, 0.45, 0.4))) c.DrawText(Fit(d.CarName.ToUpperInvariant(), sp, R - L), L, y + 190, sp);
        float bigK = K(t, 1.2, 0.5);
        using (var gp = P(Fonts.BarlowCondensed.Black, 200, Gain, a * bigK)) c.DrawText($"+{gained}", L - 8, y + 384, gp);
        using (var lp = P(Fonts.Inter.Bold, 18, Gain, a * bigK)) TextTracked.Draw(c, lp, gained == 1 ? "PLACE GAINED" : "PLACES GAINED", L, y + 414, 4f);
        using (var rp = P(Fonts.BarlowCondensed.ExtraBold, 38, Txt, a * K(t, 1.6, 0.4)))
        {
            string ga = $"P{g}", na = $"P{now}";
            c.DrawText(ga, L, y + 480, rp);
            float x0 = L + rp.MeasureText(ga) + 14, x1 = x0 + 110;
            Arrow(c, x0, x1, y + 470, K(t, 1.7, 0.5), a, 3f);
            c.DrawText(na, x1 + 22, y + 480, rp);
        }
        // also climbing
        var others = new List<Driver>();
        foreach (var cl in s.ClassLeaderboard) foreach (var o in cl.Drivers) if (o.CarIdx != d.CarIdx && RaceDiary.Gained(o) >= 1) others.Add(o);
        others.Sort((p1, p2) => RaceDiary.Gained(p2).CompareTo(RaceDiary.Gained(p1)));
        float oy = y + 540;
        if (others.Count > 0)
            using (var hp = P(Fonts.Inter.Bold, 12, Txt3, a * K(t, 2.0, 0.4))) TextTracked.Draw(c, hp, "ALSO CLIMBING", L, oy, 3f);
        for (int i = 0; i < Math.Min(3, others.Count) && oy + 44 + i * 40 < r.Bottom - 110; i++)
        {
            var o = others[i];
            float ok = K(t, 2.05 + i * 0.08, 0.3);
            using (var on = P(Fonts.Inter.SemiBold, 19, o.CarIdx == s.PlayerCarIdx ? SKColors.White : Txt2, a * ok)) c.DrawText(Fit(NameFormat.Broadcast(o), on, R - L - 80), L, oy + 36 + i * 40, on);
            DrawDelta(c, R - 30, oy + 29 + i * 40, RaceDiary.Gained(o), a * ok, 18);
        }
        c.Restore();
        c.Restore();
    }

    // ================================================================== 4. FASTEST LAPS - the stopwatch
    // One watch face (the session bar's) stays put; it features each class's fastest lap in
    // turn, the chrono hand sweeping forward from one reading to the next (Angelo 2026-10-20).
    void Stopwatch(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? pcls, double t, float a)
    {
        float inK = K(t, 0, 0.6);
        c.Save();
        c.Translate((1 - inK) * (r.Width + 60), 0);
        using var shape = Slant(r);
        Glass(c, shape, r, new SKColor(6, 16, 12), new SKColor(10, 26, 20), GoGreen, a);
        c.Save(); c.ClipPath(shape, antialias: true);
        float L = r.Left + 96, R = r.Right - 44, y = r.Top + 70;
        float fx = Eyebrow(c, L, y, "FASTEST LAPS", null, GoGreen, t, a);
        Presenters.DrawPresentedAfter(c, Presenters.For(StoryKind.FastestLaps), Presenters.Verb(StoryKind.FastestLaps), fx + 14, y - 6, 38, a * K(t, 0.4, 0.4));
        var p = s.Player!;
        float cb0 = ClassBest(pcls);
        using (var yb = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 0.4, 0.4)))
        {
            yb.TextAlign = SKTextAlign.Right;
            string yours = p.BestLapTime > 0 ? $"YOUR BEST  {LapTimeFormat.Lap(p.BestLapTime)}" + (cb0 > 0 && p.BestLapTime - cb0 > 0.0005f ? $"   +{p.BestLapTime - cb0:0.000}" : "") : "";
            c.DrawText(yours, R, y, yb);
            using var sg = P(Fonts.BarlowCondensed.Bold, 17, GoGreen, a * K(t, 0.6, 0.4));
            const string Slogan = "DRIVE SMARTER, GO FASTER";
            TextTracked.Draw(c, sg, Slogan, R - TrackedWidth(sg, Slogan, 2.4f), y + 26, 2.4f);
        }
        // the classes with a fastest lap (ordered slowest class to quickest below)
        var list = new List<(DriverClass Cls, (float Time, int CarIdx, string Name, int Lap) F)>();
        if (pcls != null && RaceDiary.Fastest(pcls.Name) is { } pf) list.Add((pcls, pf));
        foreach (var cl in s.ClassLeaderboard)
            if (cl != pcls && RaceDiary.Fastest(cl.Name) is { } of) list.Add((cl, of));
        if (list.Count == 0) { c.Restore(); c.Restore(); return; }
        // 2026-10-25 (Angelo: "it should show the lowest class first and then the top class last"): slowest class's
        // fastest lap first, building to the quickest class at the end
        list = list.OrderByDescending(x => x.F.Time).ToList();

        // schedule: wind up to the first, then hold / sweep to the next
        const double First = 2.5, Hold = 1.7, Move = 0.8;
        int idx = 0; float tr = 1;
        double tt = t - First;
        if (tt >= 0 && list.Count > 1)
        {
            int k = (int)(tt / (Hold + Move));
            double within = tt - k * (Hold + Move);
            if (within < Hold) { idx = k; tr = 1; }
            else { idx = k + 1; tr = Ease((float)((within - Hold) / Move)); }
            if (idx >= list.Count) { idx = list.Count - 1; tr = 1; }
        }
        var cur = list[idx];
        var prev = idx > 0 ? list[idx - 1] : cur;

        float wr = 190, cx = r.MidX + 30, cy = y + 260;
        // GO FAST's own GT3 911 behind the watch, head-on: its headlights peek out either side and
        // flare on as the watch lands; their green horizon glows under the class watches
        // (the green horizon arc under the watch is gone - Angelo 2026-10-25: "that green line arc near the bottom needs to go away")
        if (Bmp("logos", "sponsors", "gofast_911_front.png") is { } car)
        {

            float ck = K(t, 0.15, 0.8), zoom = 1.06f - 0.06f * (float)Math.Min(1, t / 9);
            float cw = r.Width * 1.02f * zoom, chh = car.Height * cw / car.Width;
            var dst = new SKRect(cx - cw / 2, cy - chh * 0.52f, cx + cw / 2, cy + chh * 0.48f);
            // the car fades in from below the title and out at its edges - no hard box
            c.SaveLayer(dst, null);
            using (var cp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(105 * a * ck)) })
                c.DrawBitmap(car, dst, cp);
            using (var mk = SKShader.CreateLinearGradient(new SKPoint(0, y + 40), new SKPoint(0, y + 170), new[] { SKColors.Black.WithAlpha(0), SKColors.Black }, null, SKShaderTileMode.Clamp))
            using (var mp = new SKPaint { Shader = mk, BlendMode = SKBlendMode.DstIn }) c.DrawRect(dst, mp);
            using (var mk2 = SKShader.CreateRadialGradient(new SKPoint(cx, cy), cw * 0.5f, new[] { SKColors.Black, SKColors.Black, SKColors.Black.WithAlpha(0) }, new[] { 0f, 0.7f, 1f }, SKShaderTileMode.Clamp))
            using (var mp2 = new SKPaint { Shader = mk2, BlendMode = SKBlendMode.DstIn }) c.DrawRect(dst, mp2);
            c.Restore();
            if (Bmp("logos", "sponsors", "gofast_light.png") is { } fl)
            {
                double lt = t - 0.55;
                float lk = lt < 0 ? 0 : lt < 0.12 ? (float)(lt / 0.12) * 1.4f : 1f + 0.4f * (float)Math.Exp(-(lt - 0.12) * 4);
                // #508: both LED rows (53% / 57%) + the GT3 double high-beam flash every 3.2 s once lit
                // 2026-10-30 (#508 again, "only one row is lit"): the upper row was a smaller, dimmer copy hidden inside the
                // lower one's glow - now both rows are the same glow (the lower row's size and full strength)
                if (lt > 1.0) { double ph = (lt - 1.0) % 3.2; if (ph < 0.09 || (ph >= 0.18 && ph < 0.27)) lk = Math.Max(lk, 1.55f); }
                foreach (float hx in new[] { 0.255f, 0.745f })
                    foreach (float hy in HeadlightRows)
                    {
                        var hp = new SKPoint(dst.Left + dst.Width * hx, dst.Top + dst.Height * hy);
                        float fw = 420 * lk, fh = fw * fl.Height / fl.Width;
                        using var lp = new SKPaint { BlendMode = SKBlendMode.Plus, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * Math.Min(1, lk))) };
                        c.DrawBitmap(fl, new SKRect(hp.X - fw / 2, hp.Y - fh / 2, hp.X + fw / 2, hp.Y + fh / 2), lp);
                    }
            }
        }
        float wk = K(t, 0.2, 0.5);
        c.Save(); c.Scale(0.85f + 0.15f * wk, 0.85f + 0.15f * wk, cx, cy);
        using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(150 * a * wk)), ImageFilter = SKImageFilter.CreateBlur(16, 16) }) c.DrawCircle(cx, cy + 12, wr, sh);
        using (var lay = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a * wk)) })
        {
            c.SaveLayer(lay);
            if (tt < 0 || idx == 0 && tr >= 1)
                watch.DrawChrono(c, cx, cy, wr, cur.F.Time, K(t, 0.5, 2.0), GoGreen);
            else
            {
                var a0 = WatchRenderer.ChronoAngles(prev.F.Time);
                var a1 = WatchRenderer.ChronoAngles(cur.F.Time);
                double dSec = ((a1.Sec - a0.Sec) % 360 + 360) % 360;      // always sweep forward
                double dTen = ((a1.Tenth - a0.Tenth) % 360 + 360) % 360 + 360;
                watch.DrawChronoAngles(c, cx, cy, wr, a0.Sec + dSec * tr, a0.Min + (a1.Min - a0.Min) * tr, a0.Tenth + dTen * tr, GoGreen);
            }
            c.Restore();
        }
        c.Restore();

        // the reading: class logo, time, driver - cross-fading as the hands move on
        var shown = tr < 0.5f ? prev : cur;
        float xa = tr >= 1 ? 1 : Math.Abs(tr * 2 - 1);
        float baseY = cy + wr + 34;
        if (ClassLogoBmp(shown.Cls.Name) is { } clb)
        {
            float lh = 30, lw = clb.Width * lh / clb.Height;
            DrawLogo(c, clb, cx, baseY, lw, lh, a * K(t, 0.6, 0.4) * xa);
        }
        float dk = K(t, 0.8, 1.7);
        string lapTxt = LapTimeFormat.Lap(shown.F.Time);
        using (var tp = P(Fonts.BarlowCondensed.ExtraBold, 108, GoGreen, a * K(t, 0.6, 0.4) * xa))
        {
            if (idx == 0 && tr >= 1) DrawRolling(c, lapTxt, cx, baseY + 100, tp, dk, SKTextAlign.Center);
            else { tp.TextAlign = SKTextAlign.Center; c.DrawText(lapTxt, cx, baseY + 100, tp); }
        }
        Driver? fd = null; foreach (var d in shown.Cls.Drivers) if (d.CarIdx == shown.F.CarIdx) fd = d;
        using (var np = P(Fonts.BarlowCondensed.Bold, 34, shown.F.CarIdx == s.PlayerCarIdx ? SKColors.White : Txt, a * K(t, 2.4, 0.4) * xa))
        {
            np.TextAlign = SKTextAlign.Center;
            c.DrawText(shown.F.Name.ToUpperInvariant(), cx, baseY + 148, np);
        }
        using (var sp = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 2.5, 0.4) * xa))
        {
            sp.TextAlign = SKTextAlign.Center;
            c.DrawText($"LAP {shown.F.Lap}" + (fd != null ? "   ·   " + fd.CarName.ToUpperInvariant() : ""), cx, baseY + 174, sp);
        }

        // every class along the bottom; the one on the big watch is lit
        if (list.Count > 1)
        {
            float colW = (R - L) / list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                float ok = K(t, 1.4 + i * 0.15, 0.4);
                bool active = i == (tr < 0.5f ? Math.Max(0, idx - 1) : idx);
                float ox = L + colW * i + 50, oy = r.Bottom - 84;
                if (active)
                    using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = GoGreen.WithAlpha((byte)(255 * a * ok)) })
                        c.DrawCircle(ox, oy, 50, ring);
                using (var lay = new SKPaint { Color = SKColors.White.WithAlpha((byte)((active ? 255 : 150) * a * ok)) })
                {
                    c.SaveLayer(lay);
                    watch.DrawChrono(c, ox, oy, 44, list[i].F.Time, K(t, 1.5 + i * 0.15, 1.4), GoGreen);
                    c.Restore();
                }
                float ta = a * ok * (active ? 1f : 0.55f);
                if (ClassLogoBmp(list[i].Cls.Name) is { } cl) { float lh = 18, lw = cl.Width * lh / cl.Height; DrawLogo(c, cl, ox + 64 + lw / 2, oy - 22, lw, lh, ta); }
                using (var tp = P(Fonts.BarlowCondensed.ExtraBold, 30, GoGreen, ta)) c.DrawText(LapTimeFormat.Lap(list[i].F.Time), ox + 64, oy + 10, tp);
                using (var np = P(Fonts.Inter.SemiBold, 14, Txt2, ta)) c.DrawText(Fit(list[i].F.Name, np, colW - 120), ox + 64, oy + 32, np);
            }
        }
        c.Restore();
        c.Restore();
    }

    // ================================================================== 5. PIT STOPS - the pit board
    // 2026-10-25 (Angelo: "the pit stop should have Michelin branding, maybe the entire shape should be like that Michelin
    // tower thing at Le Mans"): the card IS the tower - a wide screen up top in a thick blue frame with a yellow inner
    // edge, and the leaning pylon coming down from its left end with MICHELIN running up it. The stops play on the screen.
    // The Michelin logo / word only appear when Angelo's own michelin.png is in corner_logos (his picture, never shipped);
    // without it the tower reads PIT LANE.
    static readonly SKColor MBlue = new(0x27, 0x50, 0x9B), MBlueDeep = new(0x1B, 0x3A, 0x75), MYellow = new(0xFC, 0xE5, 0x00);

    // ------------------------------------------------------------------ #604 perf pass 2: the Michelin tower's body
    /// <summary>What the tower body picture was drawn from (the path itself is not part of it: it is a function of the
    /// card rect and is drawn only while the picture is made).</summary>
    readonly struct TowerKey : IEquatable<TowerKey>
    {
        public readonly int Id; public readonly SKRect R; public readonly byte A; public readonly float BoxTop, Sigma, Dy, Stroke, BevDy;
        public readonly SKPath? Path;
        public TowerKey(int id, SKRect r, byte a, float boxTop, float sigma, float dy, float stroke, float bevDy, SKPath path)
        { Id = id; R = r; A = a; BoxTop = boxTop; Sigma = sigma; Dy = dy; Stroke = stroke; BevDy = bevDy; Path = path; }
        public bool Equals(TowerKey o) => Id == o.Id && R == o.R && A == o.A && BoxTop == o.BoxTop && Sigma == o.Sigma && Dy == o.Dy && Stroke == o.Stroke && BevDy == o.BevDy;
        public override bool Equals(object? obj) => obj is TowerKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(Id, R, A);
    }
    [ThreadStatic] static ScreenPlatePool<TowerKey>? towerPlates;

    /// <summary>The tower's soft shadow, its blue gradient body, the blurred bevel light inside it and the navy outline -
    /// exactly the draws the pit cards made every frame - as one screen-grid picture while the card holds still (the
    /// shadow blur, the bevel's blurred mask and the curved outline's coverage were re-made on every frame).</summary>
    static void TowerBody(SKCanvas c, int id, SKRect r, SKPath tower, float boxTop, float a, float sigma, float dy, float stroke, float bevDy)
    {
        var b = tower.Bounds;
        b.Inflate(sigma * 3f + 4f, sigma * 3f + 4f);
        b.Bottom += dy;
        var key = new TowerKey(id, r, (byte)(255 * a), boxTop, sigma, dy, stroke, bevDy, tower);
        if (GpuContext.Current == null) { DrawTowerBody(c, key, null!); return; }   // CPU raster: live, exactly as before
        (towerPlates ??= new ScreenPlatePool<TowerKey>(3, DrawTowerBody)).Draw(c, key, b);
    }

    static void DrawTowerBody(SKCanvas c, in TowerKey k, SKPaint unused)
    {
        var tower = k.Path!; var r = k.R; float a = k.A / 255f;
        using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(150 * a)), ImageFilter = SKImageFilter.CreateBlur(k.Sigma, k.Sigma) })
        { c.Save(); c.Translate(0, k.Dy); c.DrawPath(tower, sh); c.Restore(); }
        using (var bp = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, k.BoxTop), new SKPoint(0, r.Bottom),
                   new[] { new SKColor(0x33, 0x63, 0xB8, (byte)(255 * a)), MBlue.WithAlpha((byte)(255 * a)), MBlueDeep.WithAlpha((byte)(255 * a)) }, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawPath(tower, bp);
        c.Save(); c.ClipPath(tower, antialias: true);
        using (var bev = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = k.Stroke, Color = new SKColor(255, 255, 255, (byte)(34 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4) })
        { c.Save(); c.Translate(0, k.BevDy); c.DrawPath(tower, bev); c.Restore(); }
        c.Restore();
        using (var ol = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = new SKColor(0x10, 0x24, 0x50, (byte)(230 * a)) }) c.DrawPath(tower, ol);
    }

    /// <summary>The classic pit board's tower outline for card rect <paramref name="r"/> (the same sums as PitBoard).</summary>
    static SKPath PitBoardTowerPath(SKRect r)
    {
        var box = new SKRect(r.Left - 70, r.Top + 10, r.Right - 6, r.Top + Math.Max(568f, r.Height * 0.62f));
        float stemL = box.Left + 16, stemTopW = 176, stemBotW = 132, lean = 34, R = 46;
        float stemTR = stemL + stemTopW, stemBL = stemL + lean, stemBR = stemL + lean + stemBotW;
        var tower = new SKPath();
        tower.AddRoundRect(box, 30, 30);
        using (var stem = new SKPath())
        {
            float ex = (stemBR - stemTR) / (r.Bottom - box.Bottom);            // the right edge's slope
            stem.MoveTo(stemL, box.Bottom - 40);
            stem.LineTo(stemTR + R, box.Bottom - 40);
            stem.LineTo(stemTR + R, box.Bottom);
            stem.QuadTo(stemTR, box.Bottom, stemTR + ex * R, box.Bottom + R);  // the elbow
            stem.LineTo(stemBR, r.Bottom);
            stem.LineTo(stemBL, r.Bottom);
            stem.Close();
            using var u = tower.Op(stem, SKPathOp.Union);
            tower.Reset(); tower.AddPath(u);
            tower.Transform(SKMatrix.CreateScale(-1, 1, box.MidX, 0));        // the pylon on the right
        }
        return tower;
    }

    void PitBoard(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        var mlogo = CornerBrand.User("michelin");
        float rise = OutBack(Math.Clamp((float)(t / 0.75), 0, 1));
        const float F = 28f;                                                  // frame thickness (the real one is thick)
        var box = new SKRect(r.Left - 70, r.Top + 10, r.Right - 6, r.Top + Math.Max(568f, r.Height * 0.62f));   // 100 px wider (2026-10-25) for Bibendum's panel
        // proportions taken from the real tower: the pylon leaves the screen's left end with a curved elbow on its inner
        // side, narrows a little and leans as it goes down; the frame is thick, bevelled Michelin blue
        // 2026-10-25 (Angelo: "make the arm come from the right edge of the screen"): built on the left as before and
        // mirrored about the screen's centre, so the pylon drops from the right end
        float stemL = box.Left + 16, stemTopW = 176, stemBotW = 132, lean = 34, R = 46;
        float stemTR = stemL + stemTopW, stemBL = stemL + lean, stemBR = stemL + lean + stemBotW;
        // the tower grows up from the ground: pylon first, then the screen
        c.Save();
        c.Scale(1, Math.Max(0.001f, rise), box.MidX, r.Bottom);
        // #604 perf pass 2: the tower's outline (a path union) is made once per card size, its soft shadow and the
        // blurred bevel are screen-grid pictures while the card holds still
        using var tower = PathMemo.Get(1, r, PitBoardTowerPath);
        // shadow, body, bevel (light along the top edges) and the navy outline all round: one picture while it holds
        TowerBody(c, 1, r, tower, box.Top, a, 22, 14, 10, 4);
        // the yellow edge down the pylon's inner side
        using (var yp = new SKPaint { IsAntialias = true, Color = MYellow.WithAlpha((byte)(255 * a)) })
        {
            float ex = (stemBR - stemTR) / (r.Bottom - box.Bottom);
            using var edge = new SKPath();
            edge.MoveTo(stemTR + ex * R - 14, box.Bottom + R); edge.LineTo(stemTR + ex * R - 4, box.Bottom + R);
            edge.LineTo(stemBR - 4, r.Bottom - 2); edge.LineTo(stemBR - 14, r.Bottom - 2); edge.Close();
            edge.Transform(SKMatrix.CreateScale(-1, 1, box.MidX, 0));
            c.DrawPath(edge, yp);
        }
        // the screen: a broad yellow band down its left end (like the real one), Bibendum's white panel at the right end
        var screen = new SKRect(box.Left + F + 4, box.Top + F, box.Right - F, box.Bottom - F);
        using (var sp = new SKPaint { IsAntialias = true, Color = new SKColor(9, 11, 16, (byte)(250 * a)) }) c.DrawRoundRect(screen, 10, 10, sp);
        c.Save(); c.ClipRoundRect(new SKRoundRect(screen, 10, 10), antialias: true);
        using (var yp2 = new SKPaint { Color = MYellow.WithAlpha((byte)(255 * a)) }) c.DrawRect(new SKRect(screen.Left, screen.Top, screen.Left + 16, screen.Bottom), yp2);
        float panelL = screen.Right;
        var man = CornerBrand.User("michelin man");
        if (man != null)
        {
            float pw = Math.Min(screen.Width * 0.27f, 270f);
            panelL = screen.Right - pw;
            using (var wp = new SKPaint { Color = new SKColor(0xF4, 0xF6, 0xFA, (byte)(255 * a)) }) c.DrawRect(new SKRect(panelL, screen.Top, screen.Right, screen.Bottom), wp);
            using (var yc = new SKPaint { IsAntialias = true, Color = MYellow.WithAlpha((byte)(255 * a)) })
            {
                using var tri = new SKPath();                                   // the yellow corner, bottom right
                tri.MoveTo(screen.Right, screen.Bottom - pw * 0.55f); tri.LineTo(screen.Right, screen.Bottom); tri.LineTo(screen.Right - pw * 0.55f, screen.Bottom); tri.Close();
                c.DrawPath(tri, yc);
            }
            // Bibendum leans in from the bottom of his panel, waving (his picture runs off the panel's bottom edge)
            // 2026-10-25 (Angelo: "take up more of that negative white space"): sized by the panel's HEIGHT so he stands
            // the full height of it, centred across the panel, only his feet running off the bottom
            // his face (about 69% across the picture) sits in the middle of the panel; the waving arm comes in from the left
            float mh = screen.Height * 1.0f, mw = man.Width * (mh / man.Height);
            float mx = panelL + pw * 0.56f - mw * 0.69f;
            c.Save(); c.ClipRect(new SKRect(panelL, screen.Top, screen.Right, screen.Bottom));
            using (var mp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)) })
                c.DrawBitmap(man, new SKRect(mx, screen.Bottom - mh * 0.97f, mx + mw, screen.Bottom + mh * 0.03f), mp);
            c.Restore();
            using (var sep = new SKPaint { Color = MBlueDeep.WithAlpha((byte)(255 * a)) }) c.DrawRect(new SKRect(panelL - 6, screen.Top, panelL, screen.Bottom), sep);
        }
        c.Restore();
        // 2026-10-25 (Angelo: "on the bottom where the word Michelin is typed it's actually just the Michelin Man"): with his
        // picture, Bibendum stands on the pylon, big, clipped to its shape; without it, the word as before
        // Angelo: "the whole man, not just the face" - his full-body picture ("michelin man full.png") when he has one,
        // else the whole of the picture he gave (never cropped), fitted to the pylon
        var bib = CornerBrand.User("michelin man full") ?? CornerBrand.User("michelin man");
        // 2026-10-27 (#216, Angelo): the pylon went back to the typed MICHELIN lettering; #401 brought the Michelin Man back
        // 2026-10-27 (#409, Angelo): the RUNNING Michelin Man - his frame sequence (corner_logos\michelin_run) plays once up
        // the stem as the card comes in, then holds on the finished logo; white panel, stem width, clipped to the tower
        bool ran = false;
        if (MichelinRun.Count > 0)
        {
            // the white panel sits at the top of the stem, upright, at least 200 wide so the final logo reads (wider than
            // the stem where it has to be, like the real tower's panel), kept inside the card
            float cxTop = 2 * box.MidX - (stemL + stemTR) / 2f;
            float pw = Math.Max(200, stemTopW - 12);
            cxTop = Math.Min(cxTop, box.Right - pw / 2);
            ran = MichelinRun.Draw(c, cxTop, box.Bottom + 12, pw, r.Bottom - box.Bottom - 22, t, a, RunPaint, MBlueDeep);
        }
        if (ran) { }
        else if (bib != null && mlogo != null)   // 2026-10-27 (#401, Angelo): the Michelin Man back on the stem
        {
            float midTop = (stemL + stemTR) / 2f, midBot = (stemBL + stemBR) / 2f;
            float pylTop = box.Bottom + R + 8, pylBot = r.Bottom - 14, pylH = pylBot - pylTop;
            float cy = (pylTop + pylBot) / 2f;
            float cx = midTop + (midBot - midTop) * ((cy - box.Bottom) / (r.Bottom - box.Bottom));
            cx = 2 * box.MidX - cx;                                                // the pylon is on the right
            float pylW = Math.Min(stemTopW, stemBotW) - 22;
            float bsc = Math.Min(pylW / bib.Width, pylH / bib.Height);
            float mw = bib.Width * bsc, mh = bib.Height * bsc;
            c.Save();
            c.ClipPath(tower, antialias: true);
            using (var bp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)) })
                c.DrawBitmap(bib, new SKRect(cx - mw / 2, cy - mh / 2, cx + mw / 2, cy + mh / 2), bp);
            c.Restore();
        }
        else
        // the word running up the pylon (bottom to top, slanted like the real lettering)
        {
            string word = mlogo != null ? "MICHELIN" : "PIT LANE";
            float midTop = (stemL + stemTR) / 2f, midBot = (stemBL + stemBR) / 2f;
            float sy = (box.Bottom + 30 + r.Bottom) / 2f, sx = midTop + (midBot - midTop) * ((sy - box.Bottom) / (r.Bottom - box.Bottom)) - 6;
            sx = 2 * box.MidX - sx;                                               // mirrored to the right-hand pylon
            c.Save();
            c.Translate(sx, sy); c.RotateDegrees(-90 - MathF.Atan2(midBot - midTop, r.Bottom - box.Bottom) * 180 / MathF.PI);
            using var wp = P(Fonts.BarlowCondensed.Black, 92, SKColors.White, a);
            wp.TextSkewX = -0.22f; wp.TextAlign = SKTextAlign.Center;
            float maxLen = (r.Bottom - box.Bottom) - 70;
            while (wp.MeasureText(word) > maxLen && wp.TextSize > 30) wp.TextSize -= 2;
            while (wp.MeasureText(word) < maxLen * 0.85f && wp.TextSize < stemBotW * 0.62f) wp.TextSize += 2;
            c.DrawText(word, 0, wp.TextSize * 0.36f, wp);
            c.Restore();
        }

        // ---- on the screen: the quickest stops
        c.Save(); c.ClipRoundRect(new SKRoundRect(screen, 10, 10), antialias: true);
        var sc = new SKRect(screen.Left + 16, screen.Top, panelL - 6, screen.Bottom);
        var band = new SKRect(sc.Left, sc.Top, sc.Right, sc.Top + 86);
        using (var hb = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(10 * a)) }) c.DrawRect(band, hb);
        float hx = band.Left + 28;
        if (mlogo != null) hx += CornerBrand.Draw(c, mlogo, hx, band.MidY, 250, 56, a) + 24;
        using (var ht = P(Fonts.BarlowCondensed.Black, mlogo != null ? 44 : 60, SKColors.White, a)) TextTracked.Draw(c, ht, mlogo != null ? "PIT STOPS" : "PIT LANE", hx, band.MidY + (mlogo != null ? 16 : 21), 3f);
        using (var hs = P(Fonts.BarlowCondensed.Bold, 22, MYellow, a)) { hs.TextAlign = SKTextAlign.Right; c.DrawText("QUICKEST STOPS", band.Right - 28, band.MidY + 8, hs); }
        using (var hr = new SKPaint { Color = MYellow.WithAlpha((byte)(200 * a)) }) c.DrawRect(new SKRect(band.Left + 28, band.Bottom - 2, band.Right - 28, band.Bottom), hr);
        var list = RaceDiary.Stops.OrderBy(x => x.Seconds).ToList();
        double worst = list.Count > 0 ? list.Max(x => x.Seconds) : 1;
        float footH = 64, rowY = band.Bottom + 16, rowH = 98;
        int n = Math.Min(list.Count, (int)((sc.Bottom - footH - rowY) / rowH));
        for (int i = 0; i < n; i++)
        {
            var st = list[i];
            float rk = K(t, 0.7 + i * 0.12, 0.35);
            float ry = rowY + i * rowH, ra = a * rk;
            c.Save(); c.Translate(0, (1 - rk) * -20);
            bool me = st.CarIdx == s.PlayerCarIdx;
            var nbox = new SKRect(sc.Left + 28, ry, sc.Left + 120, ry + 72);
            using (var nb = new SKPaint { IsAntialias = true, Color = (me ? Blue : MYellow).WithAlpha((byte)(255 * ra)) }) c.DrawRoundRect(nbox, 6, 6, nb);
            using (var nt = P(Fonts.BarlowCondensed.Black, 60, me ? SKColors.White : MBlueDeep, ra))
            {
                nt.TextAlign = SKTextAlign.Center;
                c.DrawText((i + 1).ToString(CultureInfo.InvariantCulture), nbox.MidX, nbox.MidY + 21, nt);
            }
            float x = nbox.Right + 22;
            using (var nm = P(Fonts.BarlowCondensed.Bold, 36, SKColors.White, ra)) c.DrawText(Fit(st.Name.ToUpperInvariant(), nm, sc.Right - 220 - x), x, ry + 30, nm);
            using (var lp = P(Fonts.Inter.Bold, 13, new SKColor(170, 178, 192), ra))
                TextTracked.Draw(c, lp, $"{ClassLogo.DisplayName(st.Class)}   ·   LAP {st.Lap}", x, ry + 52, 2f);
            float lx0 = x, lx1 = sc.Right - 200, ly = ry + 66;
            using (var lane = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(40 * ra)), StrokeWidth = 2 }) c.DrawLine(lx0, ly, lx1, ly, lane);
            float frac = (float)(st.Seconds / worst) * K(t, 0.9 + i * 0.12, 0.6);
            using (var fill = new SKPaint { Color = (i == 0 ? MYellow : new SKColor(120, 160, 230)).WithAlpha((byte)(255 * ra)), StrokeWidth = 6, StrokeCap = SKStrokeCap.Round })
                c.DrawLine(lx0, ly, lx0 + (lx1 - lx0) * frac, ly, fill);
            using (var tm = P(Fonts.BarlowCondensed.ExtraBold, 66, i == 0 ? MYellow : SKColors.White, ra))
            {
                tm.TextAlign = SKTextAlign.Right;
                c.DrawText(st.Seconds.ToString("0.0", CultureInfo.InvariantCulture), sc.Right - 54, ry + 54, tm);
            }
            using (var sx = P(Fonts.Inter.Bold, 20, new SKColor(170, 178, 192), ra)) c.DrawText("s", sc.Right - 48, ry + 54, sx);
            c.Restore();
        }
        var p = s.Player!;
        double mine = RaceDiary.Stops.Where(x => x.CarIdx == p.CarIdx).Select(x => x.Seconds).DefaultIfEmpty(0).Min();
        float fk = K(t, 1.4, 0.4);
        using (var fl = new SKPaint { Color = MYellow.WithAlpha((byte)(200 * a * fk)) }) c.DrawRect(new SKRect(sc.Left + 28, sc.Bottom - footH, sc.Right - 28, sc.Bottom - footH + 2), fl);
        using (var ft = P(Fonts.BarlowCondensed.Bold, 26, SKColors.White, a * fk))
        {
            string line = $"STOPS  {RaceDiary.Stops.Count}       YOURS  {RaceDiary.PitStops(p.CarIdx)}       YOUR QUICKEST  " + (mine > 0 ? mine.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "-");
            TextTracked.Draw(c, ft, line, sc.Left + 30, sc.Bottom - 22, 2.4f);
        }
        c.Restore();
        c.Restore();
    }

    // ================================================================== 6. HALFWAY - the split screen
    void Split(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        var cc = ParseHex(cls?.Color, Gold);
        float open = K(t, 0, 0.7), mid = r.MidX + 20;
        c.Save();
        c.ClipRect(new SKRect(mid - (r.Width / 2 + 60) * open, r.Top - 4, mid + (r.Width / 2 + 60) * open, r.Bottom + 4));
        using var shape = Slant(r);
        Glass(c, shape, r, new SKColor(10, 13, 20), new SKColor(14, 18, 27), cc, a);
        c.Save(); c.ClipPath(shape, antialias: true);
        float y = r.Top + 70;
        using (var ep = P(Fonts.BarlowCondensed.Black, 44, Txt, a * K(t, 0.3, 0.4))) { ep.TextAlign = SKTextAlign.Center; c.DrawText("HALFWAY", mid, y + 4, ep); }
        using (var sp = P(Fonts.Inter.SemiBold, 16, Txt2, a * K(t, 0.4, 0.4))) { sp.TextAlign = SKTextAlign.Center; c.DrawText(ToGo(s).ToUpperInvariant(), mid, y + 38, sp); }
        // the halfway line
        float top = y + 120, bottom = r.Bottom - 110;
        using (var hl = new SKPaint { Color = SKColors.White.WithAlpha((byte)(230 * a)), StrokeWidth = 3 }) c.DrawLine(mid, top - 30, mid, bottom + 20, hl);
        var pill = new SKRect(mid - 46, top - 50, mid + 46, top - 18);
        using (var pb = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * a)) }) c.DrawRoundRect(pill, 16, 16, pb);
        using (var pt = P(Fonts.Inter.Bold, 16, new SKColor(10, 12, 18), a)) { pt.TextAlign = SKTextAlign.Center; c.DrawText("50%", mid, pill.MidY + 6, pt); }
        using (var ch = P(Fonts.Inter.Bold, 12, Txt3, a * K(t, 0.5, 0.4)))
        {
            ch.TextAlign = SKTextAlign.Right; c.DrawText("AT THE START", mid - 60, top - 28, ch);
            ch.TextAlign = SKTextAlign.Left; c.DrawText("NOW", mid + 60, top - 28, ch);
        }
        if (cls != null)
        {
            var ds = cls.Drivers.Take(12).ToList();
            int n = Math.Max(ds.Count, ds.Max(d => Math.Max(1, RaceDiary.GridPos(d.CarIdx))));
            n = Math.Min(n, 14);
            float rowH = Math.Min(66, (bottom - top) / Math.Max(1, n));
            float fs = Math.Clamp(rowH * 0.42f, 13, 21);
            float Yp(int pos) => top + rowH * (pos - 0.5f);
            float ck = K(t, 0.8, 1.0);
            foreach (var d in ds)
            {
                int g = RaceDiary.GridPos(d.CarIdx), nw = d.PositionInClass;
                if (g <= 0 || g > n || nw > n) continue;
                bool me = d.CarIdx == s.PlayerCarIdx;
                int gained = g - nw;
                var col = me ? Blue : gained > 0 ? Gain : gained < 0 ? Loss : new SKColor(120, 128, 140);
                float y0 = Yp(g), y1 = Yp(nw), x0 = mid - 52, x1 = mid + 52;
                using var path = new SKPath();
                path.MoveTo(x0, y0);
                path.CubicTo(mid - 4, y0, mid + 4, y1, x1, y1);
                using var meas = new SKPathMeasure(path);
                using var part = new SKPath();
                meas.GetSegment(0, meas.Length * ck, part, true);
                using (var lp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = me ? 4.5f : 2.5f, Color = col.WithAlpha((byte)((me ? 255 : 200) * a)), StrokeCap = SKStrokeCap.Round })
                    c.DrawPath(part, lp);
                float la = a * K(t, 0.6 + g * 0.03, 0.3), ra = a * K(t, 1.4 + nw * 0.03, 0.3);
                using (var pl = P(Fonts.Inter.Bold, fs, Txt3, la)) { pl.TextAlign = SKTextAlign.Left; c.DrawText("P" + g, r.Left + 96, y0 + fs * 0.36f, pl); }
                using (var nl = P(Fonts.Inter.SemiBold, fs, me ? SKColors.White : Txt2, la)) { nl.TextAlign = SKTextAlign.Right; c.DrawText(Fit(NameFormat.Broadcast(d), nl, mid - 70 - (r.Left + 150)), x0 - 12, y0 + fs * 0.36f, nl); }
                using (var nr = P(Fonts.Inter.SemiBold, fs, me ? SKColors.White : Txt, ra)) c.DrawText(Fit(NameFormat.Broadcast(d), nr, r.Right - 120 - (x1 + 12)), x1 + 12, y1 + fs * 0.36f, nr);
                using (var pr = P(Fonts.Inter.Bold, fs, nw == 1 ? Gold : Txt, ra)) { pr.TextAlign = SKTextAlign.Right; c.DrawText("P" + nw, r.Right - 44, y1 + fs * 0.36f, pr); }
            }
        }
        var p = s.Player!;
        float fk = K(t, 2.0, 0.4);
        using (var ft = P(Fonts.Inter.Bold, 15, Txt2, a * fk))
        {
            string gap = p.PositionInClass <= 1 ? "YOU LEAD THE CLASS" : p.GapToLeaderSeconds is double gl && gl > 0 ? $"YOU: P{p.PositionInClass}, +{gl:0.0}s TO THE LEAD" : $"YOU: P{p.PositionInClass}";
            string fline = $"LEAD CHANGES {RaceDiary.AllLeadChanges}     PIT STOPS {RaceDiary.Stops.Count}     {gap}";
            float fw0 = 0; foreach (char chx in fline) fw0 += ft.MeasureText(chx.ToString()) + 1.6f;
            ft.TextAlign = SKTextAlign.Left;
            TextTracked.Draw(c, ft, fline, mid - fw0 / 2, r.Bottom - 56, 1.6f);
        }
        c.Restore();
        c.Restore();
    }

    // ================================================================== 7. WEATHER WATCH - the radar (StormExhibit)
    void Radar(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? pcls, double t, float a)
    {
        // CRT power-on: a bright line, then the picture opens vertically
        float line = K(t, 0, 0.18), open = K(t, 0.15, 0.45);
        float halfH = Math.Max(2, r.Height / 2 * open);
        c.Save();
        c.ClipRect(new SKRect(r.Left - 2, r.MidY - halfH, r.Right + 2, r.MidY + halfH));
        var rr = new SKRoundRect(r, 26, 26);
        BlurPlates.Rect(c, 5, r, 26, new SKColor(0, 0, 0, (byte)(130 * a)), 18);   // #604: a picture while it holds
        using (var bg = SKShader.CreateRadialGradient(new SKPoint(r.MidX, r.MidY), r.Height * 0.8f, new[] { new SKColor(6, 26, 44, (byte)(250 * a)), new SKColor(2, 10, 18, (byte)(252 * a)) }, null, SKShaderTileMode.Clamp))
        using (var bp = new SKPaint { Shader = bg }) c.DrawRoundRect(rr, bp);
        c.Save(); c.ClipRoundRect(rr, antialias: true);
        var ctr = new SKPoint(r.MidX + 150, r.MidY + 60);
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = Storm.WithAlpha((byte)(70 * a)) })
        {
            for (float rad = 110; rad < 1100; rad += 110) c.DrawCircle(ctr, rad, ring);
            c.DrawLine(r.Left, ctr.Y, r.Right, ctr.Y, ring); c.DrawLine(ctr.X, r.Top, ctr.X, r.Bottom, ring);
        }
        using (var rl = P(Fonts.Inter.SemiBold, 11, Storm, a * 0.6f)) for (int k = 1; k <= 4; k++) c.DrawText($"{k * 5} KM", ctr.X + 6, ctr.Y - 110 * k - 4, rl);
        // the circuit itself, glowing on the scope (2026-10-20)
        bool raining = s.IsRaining || s.Extras.PrecipNow > 0.01f;
        float wet = raining ? 1f : Math.Clamp(s.Extras.RainChancePct / 100f, 0.1f, 1f);
        float sweepDeg = (float)((t * 110) % 360);
        var trackPts = TrackOnScope(s, new SKPoint(ctr.X - 30, ctr.Y - 10), 270f);
        if (trackPts != null)
        {
            float tk = K(t, 0.5, 1.2);
            using var tpath = new SKPath();
            tpath.MoveTo(trackPts[0]); foreach (var tp0 in trackPts) tpath.LineTo(tp0); tpath.Close();
            using (var tg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 12, Color = Storm.WithAlpha((byte)(60 * a * tk)), ImageFilter = SKImageFilter.CreateBlur(7, 7), StrokeJoin = SKStrokeJoin.Round })
                c.DrawPath(tpath, tg);
            using (var tl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(0xBF, 0xE8, 0xFF, (byte)(220 * a * tk)), StrokeJoin = SKStrokeJoin.Round })
                c.DrawPath(tpath, tl);
        }
        // rain cells: more of them the wetter it gets; drifting across the circuit, glowing as
        // the sweep passes. (iRacing reports how wet it is, not where - the cells show intensity.)
        var rnd = new Random(447);
        int cells = 26;
        for (int i = 0; i < cells; i++)
        {
            float ang = (float)(rnd.NextDouble() * 360), dist = 120 + (float)rnd.NextDouble() * 760, size = 22 + (float)rnd.NextDouble() * 70, heavy = (float)rnd.NextDouble();
            int onTrack = rnd.Next(1 << 20);
            if (i >= cells * wet) continue;
            float rad = ang * (float)Math.PI / 180f;
            var pc = new SKPoint(ctr.X + dist * (float)Math.Cos(rad), ctr.Y + dist * (float)Math.Sin(rad));
            if (trackPts != null && i % 3 != 2) { var q = trackPts[onTrack % trackPts.Count]; pc = new SKPoint(q.X + (float)Math.Cos(rad) * 40, q.Y + (float)Math.Sin(rad) * 40); }
            pc = new SKPoint(pc.X + (float)(t * 14), pc.Y - (float)(t * 6));    // the weather drifts
            float behind = ((sweepDeg - ang) % 360 + 360) % 360;
            float glow = (0.25f + 0.75f * (float)Math.Exp(-behind / 70.0)) * open;
            var core = heavy > 0.75f ? new SKColor(255, 255, 255) : new SKColor(0x7F, 0xD4, 0xFF);
            using var cg = SKShader.CreateRadialGradient(pc, size, new[] { core.WithAlpha((byte)(170 * glow * a)), Storm.WithAlpha((byte)(90 * glow * a)), Storm.WithAlpha(0) }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp);
            using var cp = new SKPaint { Shader = cg };
            c.DrawCircle(pc, size, cp);
        }
        // sweep
        c.Save(); c.Translate(ctr.X, ctr.Y); c.RotateDegrees(sweepDeg);
        using (var sw = new SKPaint { IsAntialias = true, Shader = SKShader.CreateSweepGradient(SKPoint.Empty, new[] { Storm.WithAlpha(0), Storm.WithAlpha(0), Storm.WithAlpha((byte)(110 * a)) }, new[] { 0f, 0.82f, 1f }) })
            c.DrawCircle(0, 0, 1200, sw);
        using (var sl = new SKPaint { IsAntialias = true, Color = new SKColor(0xBF, 0xE8, 0xFF, (byte)(200 * a)), StrokeWidth = 2 }) c.DrawLine(0, 0, 1200, 0, sl);
        c.Restore();
        // scanlines
        using (var sc = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(55 * a)) })
            for (float yy = r.Top; yy < r.Bottom; yy += 3) c.DrawRect(new SKRect(r.Left, yy, r.Right, yy + 1), sc);

        float L = r.Left + 52, R = r.Right - 44, y = r.Top + 66;
        using (var ep = P(Fonts.BarlowCondensed.Bold, 22, Storm, a * K(t, 0.5, 0.4))) TextTracked.Draw(c, ep, "WEATHER WATCH", L, y, 3.4f);
        Presenters.DrawPresentedBy(c, Presenters.For(StoryKind.WeatherWatch), "PRESENTED BY", R, y - 6, 34, a * K(t, 0.55, 0.4));
        int wetLvl = s.Extras.TrackWetness;
        string head = StoryDirector.WeatherHeadline;
        string title = head switch
        {
            "RAIN" => "Rain is falling",
            "WET" => "Water on the track",
            "DRYING" => wetLvl >= 3 ? "The track is drying" : "The rain has stopped",
            "HEATING" => "The track is heating up",
            "COOLING" => "The track is cooling down",
            _ => raining ? "Rain is falling" : "Conditions update",
        };
        using (var tp = P(Fonts.BarlowCondensed.Black, 72, SKColors.White, a * K(t, 0.6, 0.4))) c.DrawText(title.ToUpperInvariant(), L, y + 80, tp);
        // the big number tells THIS story (Angelo 2026-10-20): the temperature swing when the
        // track heats or cools, the surface when it's wet or drying, the rain chance otherwise
        float nk = K(t, 0.8, 0.4);
        bool f = UnitPrefs.Fahrenheit(s);
        float dT = float.IsNaN(RaceDiary.StartTrackC) ? 0 : s.TrackTempC - RaceDiary.StartTrackC;
        float dTu = f ? dT * 9 / 5 : dT;
        string unit = f ? "°F" : "°C";
        string big, bigLabel; SKColor bigCol = SKColors.White;
        switch (head)
        {
            case "HEATING" or "COOLING":
                big = (dTu >= 0 ? "+" : "-") + Math.Abs(dTu).ToString("0", CultureInfo.InvariantCulture) + unit;
                bigLabel = $"TRACK TEMP SINCE THE START  ·  NOW {Temp(s.TrackTempC, f)}";
                bigCol = dTu >= 0 ? new SKColor(0xFF, 0x8A, 0x3D) : Storm;
                break;
            case "WET" or "DRYING" when wetLvl > 0:
                big = SessionExtras.WetnessName(wetLvl);
                bigLabel = head == "WET" ? "TRACK SURFACE  ·  GRIP IS DOWN" : "TRACK SURFACE  ·  A DRY LINE IS COMING";
                bigCol = head == "WET" ? Storm : SKColors.White;
                break;
            case "RAIN":
                big = "RAIN";
                bigLabel = wetLvl > 0 ? $"FALLING NOW  ·  TRACK {SessionExtras.WetnessName(wetLvl)}" : "FALLING NOW";
                bigCol = Storm;
                break;
            default:
                big = raining ? "WET" : $"{s.Extras.RainChancePct:0}%";
                bigLabel = raining ? "RAIN FALLING NOW" : "CHANCE OF RAIN";
                break;
        }
        using (var bp2 = P(Fonts.BarlowCondensed.Black, 156, bigCol, a * nk))
        {
            while (bp2.MeasureText(big) > R - L - 20 && bp2.TextSize > 60) bp2.TextSize -= 4;
            DrawRolling(c, big, L, y + 240, bp2, K(t, 0.8, 1.4), SKTextAlign.Left);
        }
        using (var bl = P(Fonts.Inter.Bold, 15, Storm, a * nk)) TextTracked.Draw(c, bl, bigLabel, L + 4, y + 272, 3f);
        // instrument readouts along the bottom
        double early = RaceDiary.ClassPaceEarly, nowPace = pcls != null ? RaceDiary.ClassPace(pcls) : 0;
        string pace = early > 0 && nowPace > 0 ? (Math.Abs(nowPace - early) < 0.05 ? "SAME" : (nowPace > early ? "+" : "-") + Math.Abs(nowPace - early).ToString("0.0", CultureInfo.InvariantCulture) + "s") : "-";
        var cellsTxt = new (string L, string V, string? S)[]
        {
            ("TRACK", Temp(s.TrackTempC, f), DeltaTemp(dT, f)),
            ("AIR", Temp(s.AirTempC, f), s.Extras.Humidity >= 0 ? $"HUMIDITY {s.Extras.Humidity * 100:0}%" : null),
            wetLvl > 1 ? ("SURFACE", SessionExtras.WetnessName(wetLvl), s.Extras.Skies >= 0 ? Sky(s.Extras.Skies).ToUpperInvariant() : null) :
            ("SKIES", Sky(s.Extras.Skies), s.Extras.WindMs > 0.05f ? (f ? $"WIND {s.Extras.WindMs * 2.237f:0} MPH" : $"WIND {s.Extras.WindMs * 3.6f:0} KM/H") : "CALM"),
            ("LAP TIMES", pace, early > 0 && nowPace > 0 ? "VS EARLY RACE" : null),
        };
        float iw = (R - L - 36) / 4f, iy = r.Bottom - 170;
        for (int i = 0; i < cellsTxt.Length; i++)
        {
            float ik = K(t, 1.0 + i * 0.1, 0.35);
            var b = new SKRect(L + i * (iw + 12), iy, L + i * (iw + 12) + iw, iy + 128);
            using (var ib = new SKPaint { IsAntialias = true, Color = new SKColor(2, 10, 18, (byte)(200 * a * ik)) }) c.DrawRoundRect(b, 10, 10, ib);
            using (var ie = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = Storm.WithAlpha((byte)(150 * a * ik)) }) c.DrawRoundRect(b, 10, 10, ie);
            using (var il = P(Fonts.Inter.Bold, 12, Storm, a * ik)) TextTracked.Draw(c, il, cellsTxt[i].L, b.Left + 16, b.Top + 26, 2.6f);
            using (var iv = P(Fonts.BarlowCondensed.ExtraBold, 42, SKColors.White, a * ik))
            {
                while (iv.MeasureText(cellsTxt[i].V) > iw - 28 && iv.TextSize > 18) iv.TextSize -= 1;
                c.DrawText(Fit(cellsTxt[i].V, iv, iw - 28), b.Left + 16, b.Top + 74, iv);
            }
            if (cellsTxt[i].S != null)
                using (var isub = P(Fonts.Inter.Bold, 11, i == 0 && dT > 0.3f ? Loss : i == 0 && dT < -0.3f ? Storm : Txt2, a * ik)) c.DrawText(Fit(cellsTxt[i].S!, isub, iw - 28), b.Left + 16, b.Top + 104, isub);
        }
        c.Restore();
        using (var frame = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Storm.WithAlpha((byte)(200 * a)) }) c.DrawRoundRect(rr, frame);
        c.Restore();
        if (line < 1 || open < 0.2f)
            using (var crt = new SKPaint { Color = SKColors.White.WithAlpha((byte)(230 * (1 - open) * line * a)) })
                c.DrawRect(new SKRect(r.Left + r.Width * (1 - line) / 2, r.MidY - 2, r.Right - r.Width * (1 - line) / 2, r.MidY + 2), crt);
    }

    List<SKPoint>? scopeTrack; string? scopeTrackKey;
    /// <summary>The current circuit's outline (assets\maps json), fitted into a box of
    /// size <paramref name="fit"/> around the scope centre. Null when no map is recorded yet.</summary>
    List<SKPoint>? TrackOnScope(RaceSnapshot s, SKPoint ctr, float fit)
    {
        var key = TrackMapCache.Resolve(s.TrackConfigName, s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName);
        if (key != scopeTrackKey)
        {
            scopeTrackKey = key; scopeTrack = null;
            try
            {
                var json = key == null ? null : Assets.Resolve("maps", key + ".json");
                if (json != null && System.IO.File.Exists(json))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(json));
                    var list = new List<SKPoint>();
                    foreach (var pt in doc.RootElement.GetProperty("points").EnumerateArray()) list.Add(new SKPoint(pt[0].GetSingle(), pt[1].GetSingle()));
                    if (list.Count >= 20) scopeTrack = list;
                }
            }
            catch { scopeTrack = null; }
        }
        if (scopeTrack == null) return null;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in scopeTrack) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        float sc = fit * 2 / Math.Max(maxX - minX, maxY - minY);
        float mx = (minX + maxX) / 2, my = (minY + maxY) / 2;
        var outPts = new List<SKPoint>(scopeTrack.Count);
        foreach (var p in scopeTrack) outPts.Add(new SKPoint(ctr.X + (p.X - mx) * sc, ctr.Y + (p.Y - my) * sc));
        return outPts;
    }

    // ================================================================== 8. DRIVER OF THE DAY - the trophy moment
    void Trophy(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        var d = StoryDirector.DriverOfDay(s);
        float up = K(t, 0, 0.9);
        a *= up;
        c.Save();
        float sc = 1.03f - 0.03f * up; c.Scale(sc, sc, r.MidX, r.MidY);
        BlurPlates.Rect(c, 6, r, 0, new SKColor(0, 0, 0, (byte)(160 * a)), 22);   // #604: a picture while it holds
        using (var bg = new SKPaint { Color = new SKColor(4, 4, 5, (byte)(250 * a)) }) c.DrawRect(r, bg);
        c.Save(); c.ClipRect(r);
        // spotlights from the top corners, gently sweeping
        float sw = (float)Math.Sin(t * 0.9) * 6;
        foreach (var (ox, dir) in new[] { (r.Left + 40, 1f), (r.Right - 40, -1f) })
        {
            c.Save(); c.Translate(ox, r.Top - 20); c.RotateDegrees(dir * (28 + sw));
            using var beam = new SKPath(); beam.MoveTo(-8, 0); beam.LineTo(8, 0); beam.LineTo(170, 1100); beam.LineTo(-170, 1100); beam.Close();
            using var bs = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1100), new[] { new SKColor(255, 240, 200, (byte)(70 * a * K(t, 0.4, 0.8))), new SKColor(255, 240, 200, 0) }, null, SKShaderTileMode.Clamp);
            using var bp = new SKPaint { Shader = bs, IsAntialias = true, BlendMode = SKBlendMode.Plus };
            c.DrawPath(beam, bp);
            c.Restore();
        }
        // double gold frame
        using (var gf = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = Gold.WithAlpha((byte)(230 * a)) })
        {
            c.DrawRect(new SKRect(r.Left + 18, r.Top + 18, r.Right - 18, r.Bottom - 18), gf);
            gf.StrokeWidth = 1; gf.Color = Gold.WithAlpha((byte)(120 * a));
            c.DrawRect(new SKRect(r.Left + 28, r.Top + 28, r.Right - 28, r.Bottom - 28), gf);
        }
        float cx = r.MidX, y = r.Top + 100;
        using (var ep = P(Fonts.CormorantGaramond.Bold, 42, Gold, a * K(t, 0.5, 0.5)))
        {
            ep.TextAlign = SKTextAlign.Center;
            c.DrawText("Driver of the Day", cx, y + 20, ep);
            float tw = ep.MeasureText("Driver of the Day") / 2 + 26;
            using var rule = new SKPaint { Color = Gold.WithAlpha((byte)(180 * a * K(t, 0.6, 0.5))), StrokeWidth = 1.5f };
            c.DrawLine(cx - tw - 110, y + 6, cx - tw, y + 6, rule); c.DrawLine(cx + tw, y + 6, cx + tw + 110, y + 6, rule);
        }
        if (d == null) { c.Restore(); c.Restore(); return; }
        // the maker badge, engraved in gold
        if (Maker(d.CarBrand) is { } mk)
        {
            float bk = K(t, 0.7, 0.8);
            float bh = 230, bw = mk.Width * bh / mk.Height; if (bw > 420) { bw = 420; bh = mk.Height * bw / mk.Width; }
            var dst = new SKRect(cx - bw / 2, y + 70, cx + bw / 2, y + 70 + bh);
            using (var halo = SKShader.CreateRadialGradient(new SKPoint(cx, dst.MidY), 300, new[] { Gold.WithAlpha((byte)(60 * a * bk)), Gold.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
            using (var hp = new SKPaint { Shader = halo }) c.DrawCircle(cx, dst.MidY, 300, hp);
            using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(150 * a * bk)), ImageFilter = SKImageFilter.CreateBlur(14, 14) })
                c.DrawOval(new SKRect(dst.Left + 20, dst.Bottom - 16, dst.Right - 20, dst.Bottom + 16), shadow);
            using (var orig = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * bk)) })
                c.DrawBitmap(mk, dst, orig);
            // a glint travelling across the badge
            float gx = dst.Left - 120 + (dst.Width + 240) * K(t, 1.6, 1.2);
            c.Save(); c.ClipRect(dst);
            using (var gl = SKShader.CreateLinearGradient(new SKPoint(gx - 60, 0), new SKPoint(gx + 60, 0), new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha((byte)(150 * a)), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
            using (var layp = new SKPaint { BlendMode = SKBlendMode.Plus })
            using (var glp = new SKPaint { Shader = gl, BlendMode = SKBlendMode.SrcIn })
            {
                c.SaveLayer(dst, layp); c.DrawBitmap(mk, dst); c.DrawRect(dst, glp); c.Restore();
            }
            c.Restore();
        }
        float ny = y + 400;
        using (var np = P(Fonts.CormorantGaramond.Bold, 76, SKColors.White, a * K(t, 1.0, 0.6))) { np.TextAlign = SKTextAlign.Center; c.DrawText(Fit(d.Name, np, r.Width - 120), cx, ny, np); }
        using (var cp = P(Fonts.Inter.SemiBold, 16, Txt2, a * K(t, 1.1, 0.6))) { cp.TextAlign = SKTextAlign.Center; c.DrawText($"#{d.CarNumber}   ·   {d.CarName.ToUpperInvariant()}", cx, ny + 40, cp); }
        int g = RaceDiary.GridPos(d.CarIdx), fin = d.PositionInClass, gained = RaceDiary.Gained(d);
        float fk = K(t, 1.4, 0.6);
        using (var lp = P(Fonts.Inter.Bold, 15, Txt3, a * fk))
        {
            lp.TextAlign = SKTextAlign.Center;
            c.DrawText("STARTED", cx - 190, ny + 120, lp); c.DrawText("FINISHED", cx + 190, ny + 120, lp); c.DrawText("GAINED", cx, ny + 120, lp);
        }
        using (var vp = P(Fonts.CormorantGaramond.Bold, 96, SKColors.White, a * fk))
        {
            vp.TextAlign = SKTextAlign.Center;
            c.DrawText($"P{g}", cx - 190, ny + 210, vp);
            vp.Color = (fin == 1 ? Gold : SKColors.White).WithAlpha((byte)(255 * a * fk)); c.DrawText($"P{fin}", cx + 190, ny + 210, vp);
            vp.Color = Gold.WithAlpha((byte)(255 * a * fk)); c.DrawText($"+{gained}", cx, ny + 210, vp);
        }
        var dcls = ClassOf(s, d);
        if (dcls != null && ClassLogoBmp(dcls.Name) is { } cl) { float lh = 26, lw = cl.Width * lh / cl.Height; DrawLogo(c, cl, cx, r.Bottom - 120, lw, lh, a * K(t, 1.7, 0.5)); }
        if (d.BestLapTime > 0)
            using (var bp = P(Fonts.Inter.Bold, 14, Txt3, a * K(t, 1.8, 0.5))) { bp.TextAlign = SKTextAlign.Center; c.DrawText($"BEST LAP  {LapTimeFormat.Lap(d.BestLapTime)}", cx, r.Bottom - 70, bp); }
        c.Restore();
        c.Restore();
    }

    // ================================================================== 9. CAR CONTACT - the collision (Trading Paints)
    // Two plates slam together in the middle; the incident-points sheet underneath
    // (Angelo 2026-10-20: "show who crashed and their incident points").
    void ContactCard(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        float inK = K(t, 0, 0.35);
        c.Save();
        c.ClipRect(new SKRect(r.Left - 4, r.Top - 4, r.Left + (r.Width + 30) * inK, r.Bottom + 4));
        using var shape = Slant(r);
        Glass(c, shape, r, new SKColor(16, 17, 28), new SKColor(28, 30, 46), TpBlue, a);
        c.Save(); c.ClipPath(shape, antialias: true);
        // 2026-10-25 (Angelo: "instead of the barbershop wheel ... tesselate its logo and then add scratches"): the band
        // along the top is the Trading Paints mark tiled in its own colours, keyed by contact - scratched and chipped
        TpBand(c, new SKRect(r.Left, r.Top + 4, r.Right, r.Top + 40), a);
        Scratches(c, r, a * 0.4f, 7, 20);
        TpPattern(c, r, t, a);
        float L = r.Left + 96, R = r.Right - 44, y = r.Top + 74;
        bool dq = StoryDirector.ContactShown?.Dq == true;
        float ex = Eyebrow(c, L, y, dq ? "DISQUALIFIED" : "CAR CONTACT", null, dq ? TpRed : TpBlue, t, a);
        Presenters.DrawPresentedAfter(c, Presenters.For(StoryKind.CarContact), Presenters.Verb(StoryKind.CarContact), ex + 14, y - 6, 34, a * K(t, 0.4, 0.4));

        var ct = StoryDirector.ContactShown;
        float py = y + 40, ph = 226;     // 2026-10-25: taller (was 176) so each plate shows the real car
        if (ct != null)
        {
            var d1 = Find(s, ct.CarIdx); var d2 = ct.OtherCarIdx >= 0 ? Find(s, ct.OtherCarIdx) : null;
            float hit = OutBack(Math.Clamp((float)((t - 0.15) / 0.45), 0, 1));
            double ti = t - 0.6;
            float recoil = ti > 0 ? 14f * (float)Math.Exp(-ti * 6) * (float)Math.Sin(ti * 18) : 0;
            if (d2 != null || ct.OtherName != null)
            {
                float pw = (R - L - 40) / 2;
                var lr = new SKRect(L, py, L + pw, py + ph); var rr = new SKRect(R - pw, py, R, py + ph);
                lr.Offset(-(1 - hit) * (pw + 260) - recoil, 0); rr.Offset((1 - hit) * (pw + 260) + recoil, 0);
                Plate(c, lr, d1, ct.Name, ct.Brand, ct.Points, RaceDiary.IncOf(ct.CarIdx, ct.Total), ct.CarIdx == s.PlayerCarIdx, a, true, TpBlue, TpRed, t);
                Plate(c, rr, d2, ct.OtherName ?? "", ct.OtherBrand, ct.OtherPoints, RaceDiary.IncOf(ct.OtherCarIdx, ct.OtherTotal), ct.OtherCarIdx == s.PlayerCarIdx, a, false, TpRed, TpBlue, t);
                PaintBurst(c, (L + R) / 2, py + ph / 2, t, a, TpBlue, TpRed);
            }
            else
            {
                var solo = LiveryArt.Get(d1);
                float pw = (R - L) * (solo != null ? 0.48f : 0.62f);    // narrower plate: the car gets the room
                var lr = new SKRect(L, py, L + pw, py + ph);
                lr.Offset(-(1 - hit) * (pw + 260) - recoil, 0);
                Plate(c, lr, d1, ct.Name, ct.Brand, ct.Dq ? -2 : ct.Points, RaceDiary.IncOf(ct.CarIdx, ct.Total), ct.CarIdx == s.PlayerCarIdx, a, true, ct.Dq ? TpRed : TpBlue, SKColors.White, t, drawCar: solo == null);   // DQ: red plate, DQ badge
                PaintBurst(c, lr.Right + 10, py + ph / 2, t, a, TpBlue, SKColors.White);
                // 2026-10-25 (Angelo: "make the car much larger if there is only one on there"): the car fills the space
                // beside its plate, big, sliding in with it
                if (solo != null)
                {
                    var room = new SKRect(lr.Right + 10, py - 70, R + 24, py + ph + 10);
                    float cw = room.Width, chh = cw * solo.Height / solo.Width;
                    if (chh > room.Height) { chh = room.Height; cw = chh * solo.Width / solo.Height; }
                    float cx = room.MidX + (1 - hit) * 300 + (ct.Dq ? DqShake(t) : 0), by = room.Bottom + (ct.Dq ? Math.Abs(DqShake(t)) * 0.6f : 0);   // the car jolts when the DQ lands
                    using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(150 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f) })
                        c.DrawOval(new SKRect(cx - cw * 0.44f, by - chh * 0.10f, cx + cw * 0.48f, by + chh * 0.05f), sh);
                    using var ip = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * a)) };
                    c.DrawImage(solo, new SKRect(cx - cw / 2, by - chh, cx + cw / 2, by), ip);
                    if (ct.Dq) DqStamp(c, new SKPoint(cx, by - chh * 0.52f), Math.Min(cw, chh * 2.2f), t, a, ct);
                }
                else if (ct.Dq) DqStamp(c, new SKPoint(lr.MidX, lr.MidY), lr.Width * 0.8f, t, a, ct);
            }
            using var lp = P(Fonts.Inter.Bold, 14, Txt2, a * K(t, 0.9, 0.4));
            string what = ct.Dq ? $"DISQUALIFIED   ·   {Math.Max(0, ct.Total)}x INCIDENT POINTS" : ct.Points >= 4 ? "HEAVY CONTACT" : "CONTACT";
            // 2026-10-25 (Angelo: "and what corner or straight the contact occurred at")
            string where = "";
            if (ct.Pct >= 0)
            {
                var cn = TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, ct.Pct, s.TrackBroadcastName);
                var st = cn == null ? TrackCorners.Straight(s.TrackRawName, s.Extras.TrackLength, ct.Pct) : null;
                where = cn != null ? "AT " + cn.ToUpperInvariant() : st != null ? "ON " + st.ToUpperInvariant() : "";
            }
            if (where.Length > 0 && !ct.Dq) what += "   ·   " + where;
            TextTracked.Draw(c, lp, $"LAP {ct.Lap}   ·   {ClassLogo.DisplayName(ct.Class)}   ·   {what}" + (ct.Points < 0 ? "" : $"   ·   +{ct.Points}x" + (ct.OtherName != null ? $" / +{ct.OtherPoints}x" : "")), L, py + ph + 40, 2.2f);
        }
        else
            using (var np = P(Fonts.BarlowCondensed.Black, 64, Txt, a * K(t, 0.3, 0.4))) c.DrawText("A CLEAN RACE SO FAR", L, py + 100, np);

        // the incident-points sheet
        float ty = py + ph + 96;
        using (var hp = P(Fonts.BarlowCondensed.Bold, 22, TpBlue, a * K(t, 1.0, 0.4))) TextTracked.Draw(c, hp, "DAMAGE REPORT  ·  INCIDENT POINTS", L, ty, 3f);
        var all = new List<Driver>();
        foreach (var cl in s.ClassLeaderboard) foreach (var d in cl.Drivers) if (Inc(d) > 0) all.Add(d);
        all.Sort((p1, p2) => Inc(p2).CompareTo(Inc(p1)));
        int rows = Math.Min(all.Count, Math.Max(0, (int)((r.Bottom - 110 - ty) / 46)));
        var shown = all.Take(rows).ToList();
        var me = s.Player;
        if (me != null && Inc(me) > 0 && !shown.Any(x => x.CarIdx == me.CarIdx) && shown.Count > 0) shown[^1] = me;
        int max = Math.Max(1, all.Count > 0 ? Inc(all[0]) : 1);
        for (int i = 0; i < shown.Count; i++)
        {
            var d = shown[i]; bool mine = d.CarIdx == s.PlayerCarIdx;
            float rk = K(t, 1.1 + i * 0.08, 0.35), ra = a * rk, ry = ty + 22 + i * 46;
            c.Save(); c.Translate((1 - rk) * 40, 0);
            if (mine) using (var hl = new SKPaint { Color = Blue.WithAlpha((byte)(46 * ra)) }) c.DrawRect(new SKRect(L - 10, ry, R, ry + 40), hl);
            if (Maker(d.CarBrand) is { } mk) { float bh = 26, bw = Math.Min(44, mk.Width * bh / mk.Height); DrawLogo(c, mk, L + 22, ry + 20, bw, bh, ra); }
            using (var np = P(Fonts.BarlowCondensed.Bold, 27, mine ? SKColors.White : Txt, ra)) c.DrawText(Fit(NameFormat.Broadcast(d).ToUpperInvariant(), np, 230), L + 56, ry + 29, np);
            float bx0 = L + 300, bx1 = R - 80;
            using (var tr = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(22 * ra)) }) c.DrawRect(new SKRect(bx0, ry + 12, bx1, ry + 28), tr);
            float frac = Inc(d) / (float)max * K(t, 1.2 + i * 0.08, 0.6);
            ScuffStrip(c, new SKRect(bx0, ry + 12, bx0 + (bx1 - bx0) * frac, ry + 28), i % 2 == 0 ? TpBlue : TpRed, Inc(d), d.CarIdx, ra);
            using (var vp = P(Fonts.BarlowCondensed.ExtraBold, 32, mine ? SKColors.White : TpBlue, ra)) { vp.TextAlign = SKTextAlign.Right; c.DrawText($"{Inc(d)}x", R, ry + 31, vp); }
            c.Restore();
        }
        using (var fp = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 1.6, 0.4)))
        {
            TextTracked.Draw(c, fp, $"CONTACTS THIS RACE  {RaceDiary.Contacts.Count}      YOUR INCIDENTS  {(me != null ? Math.Max(0, Inc(me)) : 0)}x", L, r.Bottom - 46, 2f);
            using var sg = P(Fonts.Inter.Bold, 11, new SKColor(0x8A, 0x93, 0xA8), a * K(t, 1.8, 0.4));
            const string Slogan = "THE CUSTOM CAR PAINTING PLATFORM FOR IRACING";
            TextTracked.Draw(c, sg, Slogan, R - TrackedWidth(sg, Slogan, 1.6f), r.Bottom - 72, 1.6f);
        }
        c.Restore();
        c.Restore();
    }

    // ================================================================== 11. CLEANEST DRIVER - the outro award (Trading Paints)
    // Paint without a scratch: the cleanest finisher as a pristine body panel, their (near) zero,
    // then the cleanest driver in each class on an un-scuffed paint strip.
    void CleanCard(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        float inK = K(t, 0, 0.35);
        c.Save();
        c.ClipRect(new SKRect(r.Left - 4, r.Top - 4, r.Left + (r.Width + 30) * inK, r.Bottom + 4));
        using var shape = Slant(r);
        Glass(c, shape, r, new SKColor(16, 17, 28), new SKColor(28, 30, 46), TpBlue, a);
        c.Save(); c.ClipPath(shape, antialias: true);
        TpPattern(c, r, t, a);
        float L = r.Left + 96, R = r.Right - 44, y = r.Top + 74;
        float ex = Eyebrow(c, L, y, OutroDirector.Running ? "CLEANEST DRIVER" : "CLEANEST SO FAR", null, TpBlue, t, a);
        Presenters.DrawPresentedAfter(c, Presenters.For(StoryKind.Cleanest), Presenters.Verb(StoryKind.Cleanest), ex + 14, y - 6, 34, a * K(t, 0.4, 0.4));
        var all = s.ClassLeaderboard.SelectMany(x => x.Drivers);
        var best = StoryDirector.Cleanest(all);
        if (best != null)
        {
            int inc = Inc(best);
            float hk = OutBack(Math.Clamp((float)((t - 0.15) / 0.5), 0, 1));
            var plate = new SKRect(L, y + 40, R, y + 230);
            plate.Offset(-(1 - hk) * (plate.Width + 200), 0);
            Plate(c, plate, best, NameFormat.Broadcast(best), best.CarBrand, inc, inc, best.CarIdx == s.PlayerCarIdx, a, true, TpBlue, SKColors.White, -1);
            // the big number: their incident points
            float nk = K(t, 0.7, 0.4);
            using (var np = P(Fonts.BarlowCondensed.Black, 170, TpBlue, a * nk)) DrawRolling(c, inc + "x", L - 4, y + 392, np, K(t, 0.7, 1.0), SKTextAlign.Left);
            using (var lp = P(Fonts.BarlowCondensed.Bold, 24, Txt2, a * nk))
            {
                TextTracked.Draw(c, lp, OutroDirector.Running ? "INCIDENT POINTS ALL RACE" : "INCIDENT POINTS SO FAR", L + 2, y + 426, 3f);
                var bcls = ClassOf(s, best);
                TextTracked.Draw(c, lp, $"P{best.PositionInClass}  ·  {(bcls != null ? ClassLogo.DisplayName(bcls.Name) : "")}  ·  {best.LapsCompleted} LAPS", L + 2, y + 456, 3f);
            }
            // the cleanest in each class, on paint strips (no scratches = clean)
            float ty = y + 516;
            using (var hp = P(Fonts.BarlowCondensed.Bold, 22, TpBlue, a * K(t, 1.1, 0.4))) TextTracked.Draw(c, hp, "CLEANEST IN EACH CLASS", L, ty, 3f);
            int i = 0;
            foreach (var cl in s.ClassLeaderboard)
            {
                var cb = StoryDirector.Cleanest(cl.Drivers);
                if (cb == null) continue;
                float rk = K(t, 1.2 + i * 0.1, 0.35), ra = a * rk, ry = ty + 22 + i * 50;
                if (ry > r.Bottom - 120) break;
                c.Save(); c.Translate((1 - rk) * 40, 0);
                if (ClassLogoBmp(cl.Name) is { } lg) DrawLogo(c, lg, L + 40, ry + 20, 80, 24, ra);
                using (var nm = P(Fonts.BarlowCondensed.Bold, 28, cb.CarIdx == s.PlayerCarIdx ? SKColors.White : Txt, ra)) c.DrawText(Fit(NameFormat.Broadcast(cb).ToUpperInvariant(), nm, 220), L + 96, ry + 30, nm);
                int ci = Inc(cb);
                ScuffStrip(c, new SKRect(L + 340, ry + 12, R - 90, ry + 28), i % 2 == 0 ? TpBlue : TpRed, ci, cb.CarIdx, ra);
                using (var vp = P(Fonts.BarlowCondensed.ExtraBold, 32, TpBlue, ra)) { vp.TextAlign = SKTextAlign.Right; c.DrawText(ci + "x", R, ry + 31, vp); }
                c.Restore();
                i++;
            }
        }
        using (var sg = P(Fonts.Inter.Bold, 11, new SKColor(0x8A, 0x93, 0xA8), a * K(t, 1.8, 0.4)))
        {
            const string Slogan = "THE CUSTOM CAR PAINTING PLATFORM FOR IRACING";
            TextTracked.Draw(c, sg, Slogan, R - TrackedWidth(sg, Slogan, 1.6f), r.Bottom - 46, 1.6f);
        }
        c.Restore();
        c.Restore();
    }

    /// <summary>Each circuit's own kerb paint (Angelo 2026-10-20): Le Mans blue + yellow, Watkins Glen
    /// yellow / red / blue; everywhere else the classic red + white. Add tracks here as we race them.</summary>
    static SKColor[] KerbColours(RaceSnapshot s)
    {
        string n = ((s.TrackRawName ?? "") + " " + (s.TrackDisplayName ?? "") + " " + (s.TrackBroadcastName ?? "")).ToLowerInvariant();
        SKColor kBlue = new(0x1F, 0x4F, 0xC2), kYellow = new(0xFF, 0xD1, 0x00), kRed = new(0xD8, 0x1E, 0x2A);
        if (n.Contains("lemans") || n.Contains("le mans") || n.Contains("sarthe")) return new[] { kBlue, kYellow };
        if (n.Contains("watkins") || n.Contains("glen")) return new[] { kYellow, kRed, kBlue };
        // 2026-10-28 (round 2): every other circuit's own paint from its track pack ("kerbs" in TrackData/tracks.json,
        // filled from the kerb-colour research); none listed = the classic red + white
        var pk = ApexHUD.Native.Common.TrackPacks.Kerbs(s.TrackRawName);
        if (pk.Count >= 2)
        {
            var cols = new List<SKColor>();
            foreach (var hx in pk) if (SKColor.TryParse(hx, out var kc)) cols.Add(kc);
            if (cols.Count >= 2) return cols.ToArray();
        }
        return new[] { kRed, SKColors.White };
    }

    static int Inc(Driver d) => RaceDiary.IncOf(d);

    static Driver? Find(RaceSnapshot s, int carIdx)
    {
        foreach (var cl in s.ClassLeaderboard) foreach (var d in cl.Drivers) if (d.CarIdx == carIdx) return d;
        return null;
    }

    /// <summary>One car's plate on the contact card, painted as a body panel in one of Trading Paints'
    /// two arrow colours: maker badge, a race-number panel, name, the points it just took - and, once
    /// they've touched, the OTHER car's paint scraped onto it along the impact edge.</summary>
    void Plate(SKCanvas c, SKRect b, Driver? d, string name, string? brand, int pts, int total, bool me, float a, bool left, SKColor body, SKColor other, double t, bool drawCar = true, bool compact = false)
    {
        c.Save(); c.Translate(10, 10);
        using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(150 * a)) }) c.DrawRect(b, sh);
        c.Restore();
        // the panel: dark with a painted swoosh in the car's colour, like a livery
        using (var bg = new SKPaint { Color = new SKColor(20, 21, 30, (byte)(250 * a)) }) c.DrawRect(b, bg);
        c.Save(); c.ClipRect(b);
        using (var sw = new SKPath())
        {
            float x0 = left ? b.Left : b.Right, dir = left ? 1 : -1;
            sw.MoveTo(x0, b.Bottom); sw.LineTo(x0, b.Top + b.Height * 0.62f);
            sw.CubicTo(x0 + dir * b.Width * 0.35f, b.Top + b.Height * 0.58f, x0 + dir * b.Width * 0.55f, b.Top + b.Height * 0.95f, x0 + dir * b.Width * 1.02f, b.Top + b.Height * 0.70f);
            sw.LineTo(x0 + dir * b.Width * 1.02f, b.Bottom); sw.Close();
            using var sp = new SKPaint { IsAntialias = true, Color = body.WithAlpha((byte)(235 * a)) };
            c.DrawPath(sw, sp);
            using var pin = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = SKColors.White.WithAlpha((byte)(200 * a)) };
            using var line = new SKPath();
            line.MoveTo(x0, b.Top + b.Height * 0.62f - 8);
            line.CubicTo(x0 + dir * b.Width * 0.35f, b.Top + b.Height * 0.58f - 8, x0 + dir * b.Width * 0.55f, b.Top + b.Height * 0.95f - 8, x0 + dir * b.Width * 1.02f, b.Top + b.Height * 0.70f - 8);
            c.DrawPath(line, pin);
        }
        // paint transfer: the other car's colour scraped along the edge that took the hit
        double ti = t - 0.6;
        if (ti > 0 && other != SKColors.White)
        {
            float ek = Ease((float)Math.Min(1, ti / 0.35));
            float ex = left ? b.Right : b.Left, dir = left ? -1 : 1;
            var rnd = new Random(left ? 11 : 23);
            using var scr = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round };
            for (int k = 0; k < 9; k++)
            {
                float yy = b.Top + 20 + (float)rnd.NextDouble() * (b.Height - 40), len = (40 + (float)rnd.NextDouble() * 110) * ek;
                scr.StrokeWidth = 1.5f + (float)rnd.NextDouble() * 4;
                scr.Color = other.WithAlpha((byte)((150 + rnd.Next(100)) * a));
                c.DrawLine(ex, yy, ex + dir * len, yy + (float)(rnd.NextDouble() - 0.5) * 16, scr);
            }
        }
        c.Restore();
        using (var bd = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = body.WithAlpha((byte)(255 * a)) }) c.DrawRect(b, bd);
        // 2026-10-25 (Angelo): the real car, drawn by iRacing with its Trading Paints livery (LiveryArt). On the contact
        // card each car sits bottom-left of its plate (never mirrored: sponsors must read the right way); on the wide Cleanest
        // plate it sits mid-right.
        if (drawCar && LiveryArt.Get(d) is { } car)
        {
            bool wide = b.Width > 640;
            float ch = wide ? b.Height * 0.60f : b.Height - 154f;
            if (ch > 40)
            {
                float cw = ch * car.Width / car.Height;
                float cx = wide ? b.MidX + b.Width * 0.10f : b.Left + (left ? 22 : 30) + cw / 2f, by = b.Bottom - (wide ? 18 : 12);
                c.Save(); c.ClipRect(b);
                using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(130 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f) })
                    c.DrawOval(new SKRect(cx - cw * 0.44f, by - ch * 0.10f, cx + cw * 0.48f, by + ch * 0.04f), sh);
                using (var ip = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * a)) })
                    c.DrawImage(car, new SKRect(cx - cw / 2f, by - ch, cx + cw / 2f, by), ip);
                c.Restore();
            }
        }
        float x = b.Left + (left ? 22 : 30);
        // 2026-10-27 (#399, Angelo: "a two digit Px gets overlapped"): the maker badge, P-number and IN CLASS are laid out
        // from their measured widths against the race-number panel on the right: IN CLASS goes first if there's no room,
        // then the badge narrows, then only the P-number shrinks (never below 28)
        float panelL = d != null ? b.Right - (left ? 30 : 20) - PlateNumberWidth(d.CarNumber) - 10 : b.Right - 20;
        int pos = d != null && t >= 0 ? (d.PositionInClass > 0 ? d.PositionInClass : d.Position) : 0;
        string ptxt = pos > 0 ? "P" + pos.ToString(CultureInfo.InvariantCulture) : "";
        float pw = pos > 0 ? PlateMeasure(ptxt, 40) : 0;
        var mk = Maker(brand);
        float logoW = 0;
        if (mk != null)
        {
            float bh = 46, bw = Math.Min(compact ? 56 : 80, mk.Width * bh / mk.Height);
            float room = panelL - x - (pw > 0 ? pw + 16 : 0);
            if (bw > room) { bw = Math.Max(Math.Min(bw, 36), room); bh = Math.Min(bh, mk.Height * bw / mk.Width); }
            DrawLogo(c, mk, x + bw / 2, b.Top + 40, bw, bh, a); logoW = bw;
        }
        // 2026-10-25 (Angelo: "show what position the car I hit is in at the moment the card comes on"): class position
        if (pos > 0)
        {
            using var posP = P(Fonts.BarlowCondensed.Black, 40, SKColors.White, a);
            float px = x + (logoW > 0 ? logoW + 16 : 0);
            while (px + posP.MeasureText(ptxt) > panelL && posP.TextSize > 28) posP.TextSize -= 1;
            c.DrawText(ptxt, px, b.Top + 56, posP);
            using var cp = P(Fonts.Inter.Bold, 11, new SKColor(0x8A, 0x93, 0xA8), a);
            float ix = px + posP.MeasureText(ptxt) + 8;
            if (!compact && ix + cp.MeasureText("IN CLASS") + 1.6f * 8 <= panelL) TextTracked.Draw(c, cp, "IN CLASS", ix, b.Top + 54, 1.6f);   // the column's narrow plates (#349) drop it
        }
        // race-number panel, the way it's printed on the car
        if (d != null)
        {
            using var np = P(Fonts.BarlowCondensed.Black, 50, new SKColor(14, 14, 18), a);
            float nw = Math.Max(70, np.MeasureText(d.CarNumber) + 30);
            var panel = new SKRect(b.Right - (left ? 30 : 20) - nw, b.Top + 14, b.Right - (left ? 30 : 20), b.Top + 74);
            using (var pp = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * a)) }) c.DrawRoundRect(panel, 8, 8, pp);
            using (var pe = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = body.WithAlpha((byte)(255 * a)) }) c.DrawRoundRect(panel, 8, 8, pe);
            np.TextAlign = SKTextAlign.Center;
            c.DrawText(d.CarNumber, panel.MidX, panel.Bottom - 12, np);
        }
        using (var nm = P(Fonts.BarlowCondensed.Bold, 34, SKColors.White, a)) c.DrawText(Fit(name.ToUpperInvariant(), nm, b.Width - 50), x, b.Top + 108, nm);
        if (me)
        {
            var you = new SKRect(x, b.Top + 118, x + 44, b.Top + 136);
            using (var yp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a)) }) c.DrawRect(you, yp);
            using (var yt = P(Fonts.BarlowCondensed.Black, 16, new SKColor(14, 14, 18), a)) { yt.TextAlign = SKTextAlign.Center; c.DrawText("YOU", you.MidX, you.Bottom - 3, yt); }
        }
        var badge = new SKRect(b.Right - (left ? 30 : 20) - 92, b.Bottom - 48, b.Right - (left ? 30 : 20), b.Bottom - 12);
        using (var bb = new SKPaint { Color = new SKColor(14, 14, 18, (byte)(235 * a)) }) c.DrawRect(badge, bb);
        bool award = t < 0;   // the Cleanest Driver award: just their points, no "+"
        // pts < 0: contact seen from the cars' motion (a replay: no incident points) - say CONTACT instead of "+Nx"
        using (var bt = P(Fonts.BarlowCondensed.Black, pts < 0 ? 24 : 32, SKColors.White, a)) { bt.TextAlign = SKTextAlign.Center; c.DrawText(pts == -2 ? "DQ" : pts < 0 ? "CONTACT" : award ? $"{pts}x" : $"+{pts}x", badge.MidX, badge.Bottom - (pts < 0 ? 10 : 7), bt); }
        if (!award && pts >= 0) using (var tt = P(Fonts.Inter.Bold, 13, SKColors.White, a)) { tt.TextAlign = SKTextAlign.Right; c.DrawText($"TOTAL {total}x", badge.Right, badge.Top - 8, tt); }   // above the badge (the car sits to its left)
    }

    /// <summary>Width of a plate's race-number panel (#399: measured before the P-number is placed).</summary>
    /// <summary>#508: the GO FAST car picture's two LED rows (fractions down the picture), each lit with the same glow.</summary>
    internal static readonly float[] HeadlightRows = { 0.533f, 0.57f };

    static readonly SKPaint RunPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };   // #409 frames (render thread only)
    static SKPaint? plateNumPaint;   // one measuring paint, made once (render thread only)
    static float PlateNumberWidth(string number) => Math.Max(70, PlateMeasure(number, 50) + 30);
    static float PlateMeasure(string text, float size)
    {
        plateNumPaint ??= new SKPaint { Typeface = Fonts.BarlowCondensed.Black };
        plateNumPaint.TextSize = size;
        return plateNumPaint.MeasureText(text);
    }

    /// <summary>The impact (Trading Paints): a white flash, then flecks of both cars' paint flying out
    /// of the contact point, tumbling and falling away.</summary>
    static void PaintBurst(SKCanvas c, float cx, float cy, double t, float a, SKColor c1, SKColor c2)
    {
        double ti = t - 0.58;
        if (ti < 0) return;
        if (ti < 0.22)
        {
            float k = (float)(ti / 0.22);
            using var fl = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * (1 - k) * a)) };
            c.DrawCircle(cx, cy, 20 + 60 * k, fl);
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Color = SKColors.White.WithAlpha((byte)(220 * (1 - k) * a)) };
            c.DrawCircle(cx, cy, 60 + (float)ti * 620, ring);
        }
        var rnd = new Random(77);
        float life = 1.6f;
        for (int i = 0; i < 46; i++)
        {
            double ang = rnd.NextDouble() * Math.PI * 2, spd = 160 + rnd.NextDouble() * 420, rot0 = rnd.NextDouble() * 360, rotV = (rnd.NextDouble() - 0.5) * 900;
            float size = 4 + (float)rnd.NextDouble() * 10, delay = (float)rnd.NextDouble() * 0.08f;
            double tt = ti - delay;
            if (tt < 0 || tt > life) continue;
            float drag = (float)(1 - Math.Exp(-tt * 3.2)) / 3.2f;
            float px = cx + (float)(Math.Cos(ang) * spd) * drag, py = cy + (float)(Math.Sin(ang) * spd) * drag + 260f * (float)(tt * tt);
            float fade = (float)Math.Clamp(1 - tt / life, 0, 1);
            var col = i % 2 == 0 ? c1 : c2;
            if (i % 9 == 0) col = SKColors.White;
            c.Save(); c.Translate(px, py); c.RotateDegrees((float)(rot0 + rotV * tt));
            using var fp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * fade * a)) };
            using var shard = new SKPath();
            shard.MoveTo(-size, -size * 0.4f); shard.LineTo(size * 0.7f, -size * 0.6f); shard.LineTo(size, size * 0.3f); shard.LineTo(-size * 0.3f, size * 0.5f); shard.Close();
            c.DrawPath(shard, fp);
            c.Restore();
        }
    }

    /// <summary>A strip of scuffed paint: the more incident points, the more scratched it is.</summary>
    static void ScuffStrip(SKCanvas c, SKRect b, SKColor col, int points, int seed, float a)
    {
        if (b.Width < 2) return;
        using (var path = new SKPath())
        {
            path.MoveTo(b.Left, b.Top); path.LineTo(b.Right - 6, b.Top); path.LineTo(b.Right, b.Top + b.Height * 0.3f);
            path.LineTo(b.Right - 4, b.Top + b.Height * 0.55f); path.LineTo(b.Right + 2, b.Bottom - 3); path.LineTo(b.Right - 8, b.Bottom); path.LineTo(b.Left, b.Bottom); path.Close();
            using var fp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * a)) };
            c.DrawPath(path, fp);
        }
        var rnd = new Random(seed * 31 + 5);
        using var sp = new SKPaint { IsAntialias = true, StrokeWidth = 1.4f, StrokeCap = SKStrokeCap.Round };
        int n = Math.Min(40, points * 3);
        c.Save(); c.ClipRect(b);
        for (int i = 0; i < n; i++)
        {
            float x0 = b.Left + (float)rnd.NextDouble() * b.Width, len = 6 + (float)rnd.NextDouble() * 26;
            sp.Color = (i % 3 == 0 ? new SKColor(14, 14, 18) : SKColors.White).WithAlpha((byte)((90 + rnd.Next(110)) * a));
            c.DrawLine(x0, b.Top + (float)rnd.NextDouble() * b.Height, x0 + len, b.Top + (float)rnd.NextDouble() * b.Height, sp);
        }
        c.Restore();
    }

    // ---- ultra-real kerbs (Angelo 2026-10-20): built once into a texture at the screen's real
    // resolution - raised, rounded blocks with joints, rumble ribs, worn and chipped paint, tyre
    // rubber laid down across them, a white track line and grainy asphalt along the inside.
    static SKBitmap? kerbTex; static string kerbKey = "";

    static void DrawKerb(SKCanvas c, SKRect b, SKColor[] cols, float a)
    {
        var tm = c.TotalMatrix;
        float scale = Math.Max(1f, MathF.Sqrt(tm.ScaleX * tm.ScaleX + tm.SkewY * tm.SkewY));   // also right when drawn turned (#411)
        string key = $"{b.Width:0}x{b.Height:0}@{scale:0.0}:" + string.Join(",", cols.Select(x => x.ToString()));
        if (kerbTex == null || key != kerbKey)
        {
            kerbTex?.Dispose();
            kerbTex = BuildKerb((int)Math.Ceiling(b.Width * scale), (int)Math.Ceiling(b.Height * scale), scale, cols);
            kerbKey = key;
        }
        using var p = new SKPaint { FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(a, 0, 1))) };
        c.DrawBitmap(kerbTex, b, p);
    }

    static SKBitmap BuildKerb(int W, int H, float s, SKColor[] cols)
    {
        var bmp = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        c.Scale(s);
        float w = W / s, h = H / s;
        float kh = h * 0.62f, lineH = h * 0.11f;          // kerb | white line | asphalt
        var rnd = new Random(2026);
        // asphalt: dark, grainy, with stones catching the light
        var asph = new SKRect(0, kh + lineH, w, h);
        using (var ap = new SKPaint { Color = new SKColor(38, 39, 42) }) c.DrawRect(asph, ap);
        using (var n = SKShader.CreatePerlinNoiseFractalNoise(1.6f / s, 1.6f / s, 3, 5))
        using (var np = new SKPaint { Shader = n, BlendMode = SKBlendMode.Overlay, Color = SKColors.White.WithAlpha(150) }) c.DrawRect(asph, np);
        using (var st = new SKPaint { IsAntialias = true })
            for (int i = 0; i < (int)(w * asph.Height / 9); i++)
            {
                st.Color = (rnd.Next(3) == 0 ? new SKColor(120, 120, 116) : new SKColor(18, 18, 20)).WithAlpha((byte)(40 + rnd.Next(70)));
                c.DrawCircle((float)rnd.NextDouble() * w, asph.Top + (float)rnd.NextDouble() * asph.Height, 0.25f + (float)rnd.NextDouble() * 0.4f, st);
            }
        using (var fade = SKShader.CreateLinearGradient(new SKPoint(0, asph.Top), new SKPoint(0, asph.Bottom), new[] { SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(255) }, new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp))
        using (var fp = new SKPaint { Shader = fade, BlendMode = SKBlendMode.DstOut }) c.DrawRect(asph, fp);
        // white track line, slightly scuffed
        var wl = new SKRect(0, kh, w, kh + lineH);
        using (var lp = new SKPaint { Color = new SKColor(236, 236, 230) }) c.DrawRect(wl, lp);
        using (var n2 = SKShader.CreatePerlinNoiseFractalNoise(0.9f / s, 2.5f / s, 2, 9))
        using (var np2 = new SKPaint { Shader = n2, BlendMode = SKBlendMode.Multiply, Color = SKColors.White.WithAlpha(90) }) c.DrawRect(wl, np2);
        // kerb blocks
        float blk = kh * 2.3f;
        int bi = 0;
        for (float x = -blk * 0.35f; x < w; x += blk, bi++)
        {
            var br = new SKRect(x, 0, x + blk, kh);
            var col = cols[((bi % cols.Length) + cols.Length) % cols.Length];
            using (var bp = new SKPaint { Color = col }) c.DrawRect(br, bp);
            // rounded, raised profile: light catching the top, falling into shadow toward the track
            using (var g = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, kh),
                       new[] { SKColors.White.WithAlpha(40), SKColors.White.WithAlpha(85), SKColors.White.WithAlpha(0), SKColors.Black.WithAlpha(60), SKColors.Black.WithAlpha(150) },
                       new[] { 0f, 0.18f, 0.45f, 0.8f, 1f }, SKShaderTileMode.Clamp))
            using (var gp = new SKPaint { Shader = g }) c.DrawRect(br, gp);
            // rumble ribs across the block
            using (var rib = new SKPaint { IsAntialias = true, StrokeWidth = 0.9f })
                for (float rx = br.Left + 3; rx < br.Right - 2; rx += 4.2f)
                {
                    rib.Color = SKColors.Black.WithAlpha(34); c.DrawLine(rx, 1, rx, kh - 1, rib);
                    rib.Color = SKColors.White.WithAlpha(22); c.DrawLine(rx + 1.1f, 1, rx + 1.1f, kh - 1, rib);
                }
            // the joint between blocks
            using (var jp = new SKPaint { StrokeWidth = 1.6f, Color = new SKColor(10, 10, 12, 190) }) c.DrawLine(br.Left, 0, br.Left, kh, jp);
            using (var jh = new SKPaint { StrokeWidth = 0.8f, Color = SKColors.White.WithAlpha(70) }) c.DrawLine(br.Left + 1.4f, 0, br.Left + 1.4f, kh, jh);
            // chipped paint showing concrete along the outer edge and corners
            using (var chip = new SKPaint { IsAntialias = true })
                for (int k = 0; k < 3; k++)
                {
                    chip.Color = new SKColor((byte)(96 + rnd.Next(24)), (byte)(94 + rnd.Next(24)), (byte)(90 + rnd.Next(24)), (byte)(170 + rnd.Next(70)));
                    bool corner = k == 0;
                    float cx = corner ? br.Left + 1.5f + (float)rnd.NextDouble() * 3 : br.Left + (float)rnd.NextDouble() * blk;
                    float cy = corner ? (float)rnd.NextDouble() * kh : 0.6f + (float)rnd.NextDouble() * 1.4f;   // worn along the outer edge and at the joints
                    float cr = 0.5f + (float)rnd.NextDouble() * 1.6f;
                    using var cp = new SKPath();
                    for (int v = 0; v < 6; v++) { double an = v * Math.PI / 3 + rnd.NextDouble() * 0.6; var pt = new SKPoint(cx + cr * (float)Math.Cos(an) * (0.6f + (float)rnd.NextDouble()), cy + cr * (float)Math.Sin(an) * (0.6f + (float)rnd.NextDouble())); if (v == 0) cp.MoveTo(pt); else cp.LineTo(pt); }
                    cp.Close(); c.DrawPath(cp, chip);
                }
        }
        var kr = new SKRect(0, 0, w, kh);
        // weathered paint: mottled fade + fine grain
        using (var n3 = SKShader.CreatePerlinNoiseFractalNoise(0.06f / s, 0.22f / s, 3, 3))
        using (var np3 = new SKPaint { Shader = n3, BlendMode = SKBlendMode.Overlay, Color = SKColors.White.WithAlpha(55) }) c.DrawRect(kr, np3);
        using (var n4 = SKShader.CreatePerlinNoiseFractalNoise(1.4f / s, 1.4f / s, 2, 4))
        using (var np4 = new SKPaint { Shader = n4, BlendMode = SKBlendMode.Overlay, Color = SKColors.White.WithAlpha(80) }) c.DrawRect(kr, np4);
        // rubber laid down by cars riding the kerb: long dark smears, heavier toward the track side
        using (var rub = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.6f) })
            for (int i = 0; i < (int)(w / 14); i++)
            {
                float lane = i % 3 == 0 ? 0.32f : 0.74f;   // where tyres actually ride the kerb
                float x0 = (float)rnd.NextDouble() * w, len = 80 + (float)rnd.NextDouble() * 260, yy = kh * (lane + ((float)rnd.NextDouble() - 0.5f) * 0.22f);
                rub.StrokeWidth = 2f + (float)rnd.NextDouble() * 5f;
                rub.Color = new SKColor(14, 14, 16, (byte)(45 + rnd.Next(80)));
                c.DrawLine(x0, yy, x0 + len, yy + ((float)rnd.NextDouble() - 0.5f) * 5, rub);
            }
        using (var lip = new SKPaint { Color = SKColors.White.WithAlpha(70) }) c.DrawRect(new SKRect(0, 0, w, 1.2f), lip);
        using (var seam = new SKPaint { Color = new SKColor(8, 8, 10, 200) }) c.DrawRect(new SKRect(0, kh - 1.2f, w, kh), seam);
        // the kerb's own lip casting onto the white line
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, kh), new SKPoint(0, kh + lineH * 0.8f), new[] { SKColors.Black.WithAlpha(120), SKColors.Black.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
        using (var shp = new SKPaint { Shader = sh }) c.DrawRect(new SKRect(0, kh, w, kh + lineH), shp);
        return bmp;
    }

    /// <summary>One small kerb block (the "most times off" counters): raised profile, ribs, a scuff.</summary>
    static void KerbBlock(SKCanvas c, SKRect b, SKColor col, float a, int seed)
    {
        if (a <= 0.01f) return;
        byte A = (byte)(255 * Math.Clamp(a, 0, 1));
        using (var bp = new SKPaint { Color = col.WithAlpha(A) }) c.DrawRect(b, bp);
        using (var g = SKShader.CreateLinearGradient(new SKPoint(0, b.Top), new SKPoint(0, b.Bottom),
                   new[] { SKColors.White.WithAlpha((byte)(80 * a)), SKColors.White.WithAlpha(0), SKColors.Black.WithAlpha((byte)(140 * a)) }, new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp))
        using (var gp = new SKPaint { Shader = g }) c.DrawRect(b, gp);
        using (var rib = new SKPaint { StrokeWidth = 0.8f, Color = SKColors.Black.WithAlpha((byte)(40 * a)) })
            for (float rx = b.Left + 3; rx < b.Right - 1; rx += 4) c.DrawLine(rx, b.Top + 1, rx, b.Bottom - 1, rib);
        var rnd = new Random(seed);
        using (var sc = new SKPaint { IsAntialias = true, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round, Color = new SKColor(16, 16, 18, (byte)((40 + rnd.Next(60)) * a)) })
        { float yy = b.Top + b.Height * (0.5f + 0.4f * (float)rnd.NextDouble()); c.DrawLine(b.Left, yy, b.Right, yy - 1.5f, sc); }
        using (var jp = new SKPaint { StrokeWidth = 1.2f, Color = new SKColor(10, 10, 12, (byte)(180 * a)) }) c.DrawLine(b.Right, b.Top, b.Right, b.Bottom, jp);
    }

    /// <summary>GO FAST's green horizon: a wide glowing arc low on the card, like the curve on gofast.gg.</summary>
    static void GoFastHorizon(SKCanvas c, SKRect r, double t, float a)
    {
        float k = K(t, 0.3, 0.9);
        float rad = r.Width * 0.9f;
        var ctr = new SKPoint(r.MidX, r.Bottom + rad - 150);   // the arc crests just above the class watches
        using (var g = SKShader.CreateRadialGradient(ctr, rad + 140, new[] { GoGreen.WithAlpha(0), GoGreen.WithAlpha(0), GoGreenDeep.WithAlpha((byte)(80 * a * k)), GoGreen.WithAlpha(0) }, new[] { 0f, (rad - 60) / (rad + 140), rad / (rad + 140), 1f }, SKShaderTileMode.Clamp))
        using (var gp = new SKPaint { Shader = g }) c.DrawCircle(ctr, rad + 140, gp);
        using (var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = GoGreen.WithAlpha((byte)(170 * a * k)) })
            c.DrawCircle(ctr, rad, arc);
    }

    /// <summary>Trading Paints' blue/red arrow mark, repeated faintly across the card like a livery pattern.</summary>
    /// <summary>A rubber stamp: DQ in heavy red letters inside a thick red frame, tilted, SLAMMED down over the car
    /// (2026-10-25, Angelo: "make sure the DQ slams down, with a sound effect"): it falls from high above, speeding up, its
    /// shadow tightening under it; on impact a ring of dust bursts out, the car jolts, the stamp sound hits; the ink is
    /// worn by specks.</summary>
    const double DqStart = 0.8, DqFall = 0.2;
    static float DqShake(double t)
    {
        double ti = t - DqStart - DqFall;
        return ti > 0 ? 9f * (float)Math.Exp(-ti * 10) * (float)Math.Sin(ti * 46) : 0;
    }
    static RaceDiary.Contact? dqSounded;
    static void DqStamp(SKCanvas c, SKPoint at, float width, double t, float a, RaceDiary.Contact ct)
    {
        double ts = t - DqStart;
        if (ts < 0) return;
        float u = (float)Math.Clamp(ts / DqFall, 0, 1);
        float fall = u * u;                                                   // speeding up as it comes down
        if (u >= 1 && !ReferenceEquals(dqSounded, ct)) { dqSounded = ct; StorySound.Play("story_dq_stamp"); }
        float scale = 1f + (1 - fall) * 2.4f;
        float sa = a * Math.Min(1f, u * 3f);
        float w = width * 0.62f, h = w * 0.52f;
        var red = new SKColor(0xE5, 0x1A, 0x2E);
        float shake = DqShake(t);
        // its shadow on the car: wide and soft while it's high, tight and dark at impact
        using (var shp = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(150 * a * (0.3f + 0.7f * fall))), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4 + (1 - fall) * 26) })
        {
            c.Save(); c.Translate(at.X + (1 - fall) * 40 + 6, at.Y + (1 - fall) * 50 + 8); c.RotateDegrees(-11);
            float ss = 1f + (1 - fall) * 0.6f;
            c.DrawRoundRect(new SKRect(-w / 2 * ss, -h / 2 * ss, w / 2 * ss, h / 2 * ss), h * 0.12f, h * 0.12f, shp);
            c.Restore();
        }
        // impact: a ring of dust and paint bursting out
        double ti = ts - DqFall;
        if (ti > 0 && ti < 0.45)
        {
            float k = (float)(ti / 0.45), ease = 1 - (1 - k) * (1 - k);
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = h * 0.12f * (1 - k), Color = new SKColor(255, 236, 220, (byte)(160 * a * (1 - k))), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) };
            c.DrawOval(at, new SKSize(w * (0.55f + ease * 0.6f), h * (0.6f + ease * 0.7f)), ring);
            var rnd = new Random(31);
            using var bit = new SKPaint { IsAntialias = true };
            for (int i = 0; i < 26; i++)
            {
                double ang = rnd.NextDouble() * Math.PI * 2; float dd = (float)(0.5 + rnd.NextDouble() * 0.8);
                float px = at.X + (float)Math.Cos(ang) * w * (0.4f + ease * 0.7f * dd), py = at.Y + (float)Math.Sin(ang) * h * (0.45f + ease * 0.9f * dd);
                bit.Color = (i % 3 == 0 ? red : new SKColor(235, 230, 222)).WithAlpha((byte)(200 * a * (1 - k)));
                c.DrawCircle(px, py, 1.5f + (float)rnd.NextDouble() * 3f * (1 - k), bit);
            }
        }
        c.Save();
        c.Translate(at.X + shake, at.Y + (1 - fall) * -30);
        c.RotateDegrees(-11 + (1 - fall) * -6);
        c.Scale(scale);
        int layer = c.SaveLayer(new SKRect(-w, -h, w, h), null);
        var box = new SKRect(-w / 2, -h / 2, w / 2, h / 2);
        using (var fr = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = h * 0.09f, Color = red.WithAlpha((byte)(255 * sa)) })
        {
            c.DrawRoundRect(box, h * 0.12f, h * 0.12f, fr);
            fr.StrokeWidth = h * 0.025f;
            var inner = box; inner.Inflate(-h * 0.11f, -h * 0.11f);
            c.DrawRoundRect(inner, h * 0.07f, h * 0.07f, fr);
        }
        using (var tp = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Black, TextSize = h * 0.92f, Color = red.WithAlpha((byte)(255 * sa)), TextAlign = SKTextAlign.Center })
            c.DrawText("DQ", 0, h * 0.32f, tp);
        var rnd2 = new Random(77);
        using (var ko = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.DstOut, Color = SKColors.Black })
            for (int i = 0; i < 140; i++)
            {
                float x = (float)(rnd2.NextDouble() - 0.5) * w, y = (float)(rnd2.NextDouble() - 0.5) * h, rr = (float)(0.6 + rnd2.NextDouble() * rnd2.NextDouble() * h * 0.05);
                ko.Color = new SKColor(0, 0, 0, (byte)(120 + rnd2.Next(135)));
                c.DrawCircle(x, y, rr, ko);
            }
        c.RestoreToCount(layer);
        c.Restore();
    }

    /// <summary>A strip of the Trading Paints mark tiled edge to edge (alternate marks turned half round, like a wrap
    /// pattern), then scuffed: scratches through the paint and a few chips knocked out of it. Static, no crawl.</summary>
    void TpBand(SKCanvas c, SKRect band, float a)
    {
        using (var bp = new SKPaint { Color = new SKColor(0x12, 0x2C, 0x52, (byte)(255 * a)) }) c.DrawRect(band, bp);
        var logo = Bmp("logos", "sponsors", "tradingpaints_logo.png");
        c.Save(); c.ClipRect(band);
        if (logo != null)
        {
            var src = new SKRect(0, 0, logo.Height * 1.08f, logo.Height);
            float h = band.Height * 0.8f, w = h * 1.08f, step = w * 1.3f;
            using var mp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(235 * a)) };
            int n = 0;
            for (float x = band.Left - w * 0.4f; x < band.Right + w; x += step, n++)
            {
                float cy = band.MidY;
                c.Save();
                c.RotateDegrees(n % 2 == 0 ? 0 : 180, x + w / 2, cy);
                c.DrawBitmap(logo, src, new SKRect(x, cy - h / 2, x + w, cy + h / 2), mp);
                c.Restore();
            }
        }
        // a little gloss along the top, then the damage
        using (var gl = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, band.Top), new SKPoint(0, band.Bottom),
                   new[] { new SKColor(255, 255, 255, (byte)(60 * a)), SKColors.Transparent, new SKColor(0, 0, 0, (byte)(70 * a)) }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(band, gl);
        var rnd = new Random(1923);
        using (var chip = new SKPaint { IsAntialias = true, Color = new SKColor(0xB8, 0xBE, 0xC8, (byte)(220 * a)) })    // bare metal showing
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = new SKColor(0, 0, 0, (byte)(150 * a)) })
            for (int i = 0; i < 9; i++)
            {
                float cx = band.Left + (float)rnd.NextDouble() * band.Width, cy = band.Top + (float)rnd.NextDouble() * band.Height;
                float rr = 2.5f + (float)rnd.NextDouble() * 5f;
                using var pth = new SKPath();
                for (int v = 0; v < 7; v++)
                {
                    double ang = v / 7.0 * Math.PI * 2;
                    float rv = rr * (0.55f + (float)rnd.NextDouble() * 0.6f);
                    var pt = new SKPoint(cx + (float)Math.Cos(ang) * rv * 1.5f, cy + (float)Math.Sin(ang) * rv);
                    if (v == 0) pth.MoveTo(pt); else pth.LineTo(pt);
                }
                pth.Close();
                c.DrawPath(pth, chip); c.DrawPath(pth, rim);
            }
        c.Restore();
        Scratches(c, band, a, 1923, 18);
    }

    /// <summary>Scratches through paint: thin bright gouges with a dark lip, mostly running one way (a car's slide),
    /// plus a few fine crossing scuffs. Seeded, so the same card always wears the same marks.</summary>
    static void Scratches(SKCanvas c, SKRect r, float a, int seed, int count)
    {
        var rnd = new Random(seed);
        c.Save(); c.ClipRect(r);
        using var lit = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        using var lip = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        for (int i = 0; i < count; i++)
        {
            bool main = i % 4 != 3;
            float len = (float)(main ? 30 + rnd.NextDouble() * 160 : 8 + rnd.NextDouble() * 40);
            double ang = (main ? -9 + rnd.NextDouble() * 8 : 25 + rnd.NextDouble() * 50) * Math.PI / 180;
            float x = r.Left + (float)rnd.NextDouble() * r.Width, y = r.Top + (float)rnd.NextDouble() * r.Height;
            float dx = (float)Math.Cos(ang) * len, dy = (float)Math.Sin(ang) * len;
            using var pth = new SKPath();
            pth.MoveTo(x, y);
            float bend = (float)(rnd.NextDouble() - 0.5) * len * 0.08f;
            pth.QuadTo(x + dx / 2 - dy / len * bend, y + dy / 2 + dx / len * bend, x + dx, y + dy);
            float wdt = (float)(0.6 + rnd.NextDouble() * (main ? 1.4 : 0.6));
            byte al = (byte)(Math.Clamp(a, 0, 1) * (120 + rnd.Next(110)));
            lip.StrokeWidth = wdt + 1.4f; lip.Color = new SKColor(0, 0, 0, (byte)(al * 0.55f));
            lit.StrokeWidth = wdt; lit.Color = new SKColor(0xE8, 0xEC, 0xF2, al);
            c.Save(); c.Translate(0, 0.8f); c.DrawPath(pth, lip); c.Restore();
            c.DrawPath(pth, lit);
        }
        c.Restore();
    }

    void TpPattern(SKCanvas c, SKRect r, double t, float a)
    {
        var logo = Bmp("logos", "sponsors", "tradingpaints_logo.png");
        if (logo == null) return;
        var src = new SKRect(0, 0, logo.Height * 1.08f, logo.Height);   // the mark is the square at the left of the logo
        float sz = 74, drift = (float)(t * 12 % (sz * 2.2f));
        using var mp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium, Color = SKColors.White.WithAlpha((byte)(16 * a)) };
        c.Save();
        c.RotateDegrees(-14, r.MidX, r.MidY);
        int row = 0;
        for (float yy = r.Top - 300; yy < r.Bottom + 300; yy += sz * 1.6f, row++)
            for (float xx = r.Left - 400 + (row % 2) * sz * 1.1f + drift; xx < r.Right + 300; xx += sz * 2.2f)
                c.DrawBitmap(logo, src, new SKRect(xx, yy, xx + sz * 1.08f, yy + sz), mp);
        c.Restore();
    }

    // ================================================================== 10. DRIVING ON THE LIMIT - track limits (GO FAST)
    // Where the field runs out of road: every excursion as a dot on the circuit, the hotspot
    // ringed, and who has gone off the most (Angelo 2026-10-20).
    void LimitsCard(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        float inK = K(t, 0, 0.55);
        c.Save();
        c.ClipRect(new SKRect(r.Left - 4, r.Top - 4, r.Right + 4, r.Top + (r.Height + 8) * inK));
        using var shape = Slant(r);
        Glass(c, shape, r, new SKColor(6, 16, 12), new SKColor(10, 26, 20), GoGreen, a);
        c.Save(); c.ClipPath(shape, antialias: true);
        // a kerb along the top, painted in THIS circuit's kerb colours (Angelo 2026-10-20)
        var kerb = KerbColours(s);
        LimitsTheme(c, r, s, t, a);   // #424: the hotspot's corner theme takes the whole card as the map zooms
        KerbLeft(c, r, kerb, a, 30, SquareCorners ? 0 : 70);   // #411: down the card's left edge (was along the top)
        float L = r.Left + 96, R = r.Right - 44, y = r.Top + 72;
        float ex = Eyebrow(c, L, y, "DRIVING ON THE LIMIT", null, GoGreen, t, a);
        Presenters.DrawPresentedAfter(c, Presenters.For(StoryKind.Limits), Presenters.Verb(StoryKind.Limits), ex + 14, y - 6, 38, a * K(t, 0.4, 0.4));

        var spots = RaceDiary.OffSpots;
        // the circuit with every excursion on it
        var mapBox = new SKRect(L - 10, y + 30, L + 470, y + 430);
        var pts = TrackOnScope(s, new SKPoint(mapBox.MidX, mapBox.MidY), mapBox.Width * 0.46f);
        const int Bins = 40;
        var bins = new int[Bins];
        foreach (var sp in spots) bins[Math.Clamp((int)(sp.Pct * Bins), 0, Bins - 1)]++;
        int hot = 0; for (int i = 1; i < Bins; i++) if (bins[i] > bins[hot]) hot = i;
        // lap fraction -> point by DISTANCE along the recorded line (its points are spaced in time, not distance)
        float[]? cumL = null;
        if (pts != null)
        {
            cumL = new float[pts.Count + 1];
            for (int i = 1; i <= pts.Count; i++) cumL[i] = cumL[i - 1] + SKPoint.Distance(pts[i - 1], pts[i % pts.Count]);
            float tot = Math.Max(1e-3f, cumL[^1]); for (int i = 0; i < cumL.Length; i++) cumL[i] /= tot;
        }
        SKPoint OnTrack(float pct)
        {
            if (pts == null || cumL == null) return new SKPoint(mapBox.Left + mapBox.Width * pct, mapBox.MidY);
            pct -= MathF.Floor(pct);
            int lo = 0, hi = cumL.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (cumL[mid] <= pct) lo = mid; else hi = mid; }
            float span = cumL[hi] - cumL[lo], fr = span > 0 ? (pct - cumL[lo]) / span : 0;
            var p0 = pts[lo % pts.Count]; var p1 = pts[hi % pts.Count];
            return new SKPoint(p0.X + (p1.X - p0.X) * fr, p0.Y + (p1.Y - p0.Y) * fr);
        }
        // 2026-10-27 (#402, Angelo: "start with the full track outline but then zoom into the part of the track with the
        // most offs"): the full circuit while the offs drop in, then an ease-in-out zoom onto the densest stretch, named
        float hotC = HotWindowCentre(spots);
        var zm = MapZoom(mapBox, pts, cumL, hotC, spots.Count, t, out float zs);
        c.Save();
        float clipPad = zs > 1.001f ? 4 : 40;   // zoomed: nothing spills past the map onto the numbers / list
        c.ClipRect(new SKRect(mapBox.Left - clipPad, mapBox.Top - clipPad, mapBox.Right + clipPad, mapBox.Bottom + clipPad));
        c.Concat(ref zm);
        float tk = K(t, 0.4, 1.0);
        using (var tl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 9 / zs, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, Color = new SKColor(70, 76, 88, (byte)(255 * a * tk)) })
        {
            using var path = new SKPath();
            if (pts != null) { path.MoveTo(pts[0]); foreach (var q in pts) path.LineTo(q); path.Close(); }
            else { path.MoveTo(mapBox.Left, mapBox.MidY); path.LineTo(mapBox.Right, mapBox.MidY); }
            using var meas = new SKPathMeasure(path, false);
            using var part = new SKPath(); meas.GetSegment(0, meas.Length * tk, part, true);
            c.DrawPath(part, tl);
            tl.StrokeWidth = 2.5f / zs; tl.Color = new SKColor(200, 206, 216, (byte)(255 * a * tk)); c.DrawPath(part, tl);
        }
        // start/finish
        var sf = OnTrack(0);
        using (var sfp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a * tk)) }) c.DrawRect(new SKRect(sf.X - 3 / zs, sf.Y - 11 / zs, sf.X + 3 / zs, sf.Y + 11 / zs), sfp);
        // excursions drop in one by one
        var jit = new Random(91);
        for (int i = 0; i < spots.Count; i++)
        {
            float dk = K(t, 0.9 + i * (1.2 / Math.Max(1, spots.Count)), 0.25);
            if (dk <= 0) break;
            var q = OnTrack(spots[i].Pct);
            q = new SKPoint(q.X + (float)(jit.NextDouble() - 0.5) * 14, q.Y + (float)(jit.NextDouble() - 0.5) * 14);
            bool mine = spots[i].CarIdx == s.PlayerCarIdx;
            using var dp = new SKPaint { IsAntialias = true, Color = (mine ? Blue : GoGreen).WithAlpha((byte)(230 * a * dk)) };
            c.DrawCircle(q, 6.5f * (0.6f + 0.4f * OutBack(dk)) / zs, dp);
        }
        if (spots.Count > 0)
        {
            var hq = OnTrack((hot + 0.5f) / Bins);
            float hk = K(t, 2.2, 0.4), pulse = 1 + 0.08f * (float)Math.Sin(t * 5);
            using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f / zs, Color = SKColors.White.WithAlpha((byte)(255 * a * hk)) })
                c.DrawCircle(hq, 30 * pulse * (0.6f + 0.4f * hk) / zs, ring);
        }
        c.Restore();
        HotLabel(c, s, mapBox, pts, cumL, hotC, spots.Count, zm, t, a);

        // the big number, right of the map
        float bxL = mapBox.Right + 30;
        // GO FAST's 911 sweeps across the card on its side and parks under the numbers, green underglow on
        if (Bmp("logos", "sponsors", "gofast_911_side.png") is { } side)
        {
            float sw = R - bxL + 30, sh = side.Height * sw / side.Width;
            float restX = bxL - 10, travel = Ease(Math.Clamp((float)((t - 0.25) / 1.1), 0, 1));
            float sx = r.Left - sw - 40 + (restX - (r.Left - sw - 40)) * travel, sy = y + 404;
            var dst = new SKRect(sx, sy, sx + sw, sy + sh);
            using (var glow = SKShader.CreateRadialGradient(new SKPoint(dst.MidX, dst.Bottom - 4), sw * 0.55f, new[] { GoGreen.WithAlpha((byte)(110 * a)), GoGreen.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
            using (var gp = new SKPaint { Shader = glow })
            { c.Save(); c.Scale(1, 0.18f, dst.MidX, dst.Bottom - 4); c.DrawCircle(dst.MidX, dst.Bottom - 4, sw * 0.55f, gp); c.Restore(); }
            if (travel < 0.98f)   // speed streaks while it's moving
                using (var st = new SKPaint { IsAntialias = true, StrokeWidth = 2, Color = GoGreen.WithAlpha((byte)(120 * a * (1 - travel))) })
                    for (int k = 0; k < 5; k++) c.DrawLine(dst.Left - 30 - k * 40, dst.Top + sh * (0.35f + k * 0.1f), dst.Left - 140 - k * 60, dst.Top + sh * (0.35f + k * 0.1f), st);
            using (var cp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)) })
                c.DrawBitmap(side, dst, cp);
        }
        float nk = K(t, 0.7, 0.4);
        using (var bp = P(Fonts.BarlowCondensed.Black, 150, SKColors.White, a * nk)) DrawRolling(c, RaceDiary.TotalOffs.ToString(CultureInfo.InvariantCulture), bxL, y + 190, bp, K(t, 0.7, 1.2), SKTextAlign.Left);
        using (var bl = P(Fonts.BarlowCondensed.Bold, 24, GoGreen, a * nk)) { TextTracked.Draw(c, bl, "TIMES OFF", bxL + 4, y + 226, 2.6f); TextTracked.Draw(c, bl, "THE TRACK", bxL + 4, y + 254, 2.6f); }
        if (spots.Count > 0)
            using (var hp = P(Fonts.Inter.Bold, 13, Txt2, a * K(t, 2.3, 0.4)))
            {
                TextTracked.Draw(c, hp, "HOTSPOT", bxL + 4, y + 320, 2.4f);
                using var hv = P(Fonts.BarlowCondensed.ExtraBold, 34, SKColors.White, a * K(t, 2.3, 0.4));
                // 2026-10-25 (Angelo): name the corner. 2026-10-27 (r4): the SAME place the zoomed map's label names - the
                // densest stretch of offs (Hotspot), the busiest single corner only when that stretch has no name
                var (hn, hc) = Hotspot(s);
                string cn = hn ?? $"{HotWindowCentre(spots) * 100f:0}% INTO THE LAP";
                float maxW = r.Right - bxL - 30;
                while (hv.TextSize > 20 && hv.MeasureText(cn) > maxW) hv.TextSize -= 2;
                c.DrawText(cn, bxL + 4, y + 356, hv);
                c.DrawText($"{hc} OFF{(hc == 1 ? "" : "S")} THERE", bxL + 4, y + 392, hp);
            }

        // who runs wide the most
        float ty = mapBox.Bottom + 56;
        using (var hp = P(Fonts.BarlowCondensed.Bold, 22, GoGreen, a * K(t, 1.2, 0.4))) TextTracked.Draw(c, hp, "MOST TIMES OFF", L, ty, 3f);
        var field = new List<Driver>();
        foreach (var cl in s.ClassLeaderboard) foreach (var d in cl.Drivers) field.Add(d);
        var worst = field.Where(d => RaceDiary.OffsOf(d.CarIdx) > 0).OrderByDescending(d => RaceDiary.OffsOf(d.CarIdx)).ToList();
        int rows = Math.Min(worst.Count, Math.Max(0, (int)((r.Bottom - 110 - ty) / 44)));
        var shown = worst.Take(rows).ToList();
        var me = s.Player;
        if (me != null && RaceDiary.OffsOf(me.CarIdx) > 0 && !shown.Any(x => x.CarIdx == me.CarIdx) && shown.Count > 0) shown[^1] = me;
        int max = Math.Max(1, worst.Count > 0 ? RaceDiary.OffsOf(worst[0].CarIdx) : 1);
        for (int i = 0; i < shown.Count; i++)
        {
            var d = shown[i]; bool mine = d.CarIdx == s.PlayerCarIdx; int n = RaceDiary.OffsOf(d.CarIdx);
            float rk = K(t, 1.3 + i * 0.08, 0.35), ra = a * rk, ry = ty + 20 + i * 44;
            c.Save(); c.Translate((1 - rk) * 40, 0);
            if (mine) using (var hl = new SKPaint { Color = Blue.WithAlpha((byte)(46 * ra)) }) c.DrawRect(new SKRect(L - 10, ry, R, ry + 38), hl);
            var dcls = ClassOf(s, d);
            if (dcls != null && ClassLogoBmp(dcls.Name) is { } cl0) DrawLogo(c, cl0, L + 30, ry + 19, 60, 20, ra);
            using (var np = P(Fonts.BarlowCondensed.Bold, 27, mine ? SKColors.White : Txt, ra)) c.DrawText(Fit(NameFormat.Broadcast(d).ToUpperInvariant(), np, 220), L + 72, ry + 28, np);
            // one kerb block per excursion
            float bx0 = L + 310, bw = Math.Min(26, (R - 80 - bx0) / max);
            for (int k = 0; k < n; k++)
                KerbBlock(c, new SKRect(bx0 + k * bw, ry + 10, bx0 + (k + 1) * bw - 1, ry + 28),
                          mine && k % 2 == 0 ? Blue : mine ? SKColors.White : kerb[k % kerb.Length], ra * K(t, 1.4 + i * 0.08 + k * 0.03, 0.2), d.CarIdx * 17 + k);
            using (var vp = P(Fonts.BarlowCondensed.ExtraBold, 32, mine ? SKColors.White : GoGreen, ra)) { vp.TextAlign = SKTextAlign.Right; c.DrawText(n.ToString(CultureInfo.InvariantCulture), R, ry + 30, vp); }
            c.Restore();
        }
        // the cleanest in the player's class
        if (cls != null)
        {
            var clean = cls.Drivers.Where(d => d.LapsCompleted >= 3).OrderBy(d => RaceDiary.OffsOf(d.CarIdx)).ThenBy(d => d.PositionInClass).FirstOrDefault();
            if (clean != null)
                using (var fp = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 1.8, 0.4)))
                    TextTracked.Draw(c, fp, $"CLEANEST IN {ClassLogo.DisplayName(cls.Name)}  {NameFormat.Broadcast(clean).ToUpperInvariant()} ({RaceDiary.OffsOf(clean.CarIdx)})      YOURS  {(me != null ? RaceDiary.OffsOf(me.CarIdx) : 0)}", L, r.Bottom - 46, 2f);
        }
        using (var sg = P(Fonts.BarlowCondensed.Bold, 17, GoGreen, a * K(t, 1.9, 0.4)))
        {
            const string Slogan = "YOUR PERSONAL RACE ENGINEER";
            TextTracked.Draw(c, sg, Slogan, R - TrackedWidth(sg, Slogan, 2.4f), r.Bottom - 46, 2.4f);
        }
        c.Restore();
        c.Restore();
    }

    // ================================================================== shared pieces
    static SKPath Slant(SKRect r)
    {
        var shape = new SKPath();
        float sl = SquareCorners ? 0 : 70;   // #412 preview: a plain rectangle
        shape.MoveTo(r.Left + sl, r.Top); shape.LineTo(r.Right, r.Top); shape.LineTo(r.Right, r.Bottom); shape.LineTo(r.Left, r.Bottom); shape.Close();
        return shape;
    }

    /// <summary>The card's body (2026-10-20 restyle, "less AI"): a print-style hard shadow instead
    /// of a soft glow, a flat panel with carbon weave and grain, a solid colour slab down the
    /// slanted edge with a white keyline, and a livery stripe (colour / white / red) along the foot.</summary>
    static void Glass(SKCanvas c, SKPath shape, SKRect r, SKColor c0, SKColor c1, SKColor edge, float a)
    {
        c.Save(); c.Translate(12, 12);
        using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(150 * a)) }) c.DrawPath(shape, sh);
        c.Restore();
        using (var g = SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Left, r.Bottom),
                   new[] { c1.WithAlpha((byte)(246 * a)), c0.WithAlpha((byte)(250 * a)) }, null, SKShaderTileMode.Clamp))
        using (var gp = new SKPaint { IsAntialias = true, Shader = g }) c.DrawPath(shape, gp);
        c.Save(); c.ClipPath(shape, antialias: true);
        Texture(c, r, a);
        Theme.SkinNow(c, r, ThemeSurface.Story, a);   // #601: the broadcast theme (nothing on Default)
        // slab down the slanted edge + white keyline
        using (var slab = new SKPath())
        {
            float sl = SquareCorners ? 0 : 70;
            slab.MoveTo(r.Left + sl, r.Top); slab.LineTo(r.Left + sl + 18, r.Top); slab.LineTo(r.Left + 18, r.Bottom); slab.LineTo(r.Left, r.Bottom); slab.Close();
            using var sp = new SKPaint { IsAntialias = true, Color = edge.WithAlpha((byte)(255 * a)) };
            c.DrawPath(slab, sp);
        }
        using (var kl = new SKPaint { IsAntialias = true, StrokeWidth = 3, Color = SKColors.White.WithAlpha((byte)(210 * a)) })
            c.DrawLine(r.Left + (SquareCorners ? 26 : 96), r.Top, r.Left + 26, r.Bottom, kl);
        using (var top = new SKPaint { Color = edge.WithAlpha((byte)(255 * a)) }) c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + 4), top);
        Livery(c, r, edge, a);
        c.Restore();
    }

    /// <summary>Livery stripe along the foot of a card in the card's colour (the white + red cuts were removed, #422).</summary>
    static void Livery(SKCanvas c, SKRect r, SKColor col, float a)
    {
        float h = 9, y1 = r.Bottom, y0 = y1 - h;
        void Cut(float x0, float x1, SKColor cc)
        {
            using var path = new SKPath();
            path.MoveTo(x0 + h, y0); path.LineTo(x1 + h, y0); path.LineTo(x1, y1); path.LineTo(x0, y1); path.Close();
            using var p = new SKPaint { IsAntialias = true, Color = cc.WithAlpha((byte)(255 * a)) };
            c.DrawPath(path, p);
        }
        // 2026-10-27 (#422, Angelo: "why is there that white and red thing? Can we remove that part?"): the card's colour
        // only, the whole width (the white + red cuts at the right end are gone)
        Cut(r.Left - 20, r.Right + 20, col);
    }

    static SKBitmap? weave, dots;
    /// <summary>Carbon twill (16px tile) + a fixed film grain over a card's panel.</summary>
    static void Texture(SKCanvas c, SKRect r, float a)
    {
        if (weave == null)
        {
            var b = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var cv = new SKCanvas(b);
            cv.Clear(SKColors.Transparent);
            for (int by = 0; by < 2; by++)
                for (int bx = 0; bx < 2; bx++)
                {
                    bool down = (bx + by) % 2 == 0;
                    var rc = new SKRect(bx * 8, by * 8, bx * 8 + 8, by * 8 + 8);
                    using var g = SKShader.CreateLinearGradient(new SKPoint(rc.Left, rc.Top), down ? new SKPoint(rc.Left, rc.Bottom) : new SKPoint(rc.Right, rc.Top),
                        new[] { new SKColor(255, 255, 255, 30), new SKColor(255, 255, 255, 4), new SKColor(0, 0, 0, 60) }, null, SKShaderTileMode.Clamp);
                    using var gp = new SKPaint { Shader = g };
                    cv.DrawRect(rc, gp);
                }
            b.SetImmutable();   // #604: an immutable bitmap is uploaded to the graphics card once, not copied every frame
            weave = b;
        }
        var tx = texKit ??= new TexKit();
        tx.Weave.Shader = tx.WeaveShader ??= SKShader.CreateBitmap(weave, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        tx.Weave.Color = SKColors.White.WithAlpha((byte)(150 * a));
        c.DrawRect(r, tx.Weave);
        // weave fades out toward the bottom so the data area stays clean
        tx.Cols[0] = new SKColor(0, 0, 0, 0); tx.Cols[1] = new SKColor(0, 0, 0, (byte)(120 * a));
        tx.Fade.Shader = tx.FadeSlot.Linear(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), tx.Cols, default);
        c.DrawRect(r, tx.Fade);
        // #604 perf pass 2: the film grain (fractal noise - the costliest thing on a card: ~0.1 s a frame on the CPU at 4K,
        // and a noise program over the whole panel on the graphics card) is drawn once into a screen-grid picture and
        // blended (Overlay) from it while the card holds still - the same grain pixels
        // (on the graphics card the picture blends to the very same pixels; the CPU raster blends the noise in float, so
        // there - the preview tools, a PC without a usable graphics card - it stays live and exactly as before)
        if (GpuContext.Current != null) tx.Grain.Draw(c, new GrainKey(r, (byte)(26 * a)), r);
        else DrawGrain(c, new GrainKey(r, (byte)(26 * a)), tx.Weave2);
    }

    readonly record struct GrainKey(SKRect R, byte Alpha);
    sealed class TexKit
    {
        public readonly SKPaint Weave = new(), Fade = new(), Weave2 = new();
        public SKShader? WeaveShader;
        public readonly ShaderSlot FadeSlot = new();
        public readonly SKColor[] Cols = new SKColor[2];
        public readonly ScreenPlatePool<GrainKey> Grain = new(3, DrawGrain, SKBlendMode.Overlay);
    }
    [ThreadStatic] static TexKit? texKit;
    static SKShader? grainShader;
    static void DrawGrain(SKCanvas c, in GrainKey k, SKPaint p)
    {
        // the same noise and paint as before; into the picture it goes on its own (SrcOver onto clear = the grain's own
        // pixels), and the picture is put down with Overlay exactly as this draw was
        if (ScreenPlates.Building) { using var g = SKShader.CreatePerlinNoiseFractalNoise(0.85f, 0.85f, 2, 7); Grain(c, k, p, g, SKBlendMode.SrcOver); }
        else Grain(c, k, p, grainShader ??= SKShader.CreatePerlinNoiseFractalNoise(0.85f, 0.85f, 2, 7), SKBlendMode.Overlay);
    }
    static void Grain(SKCanvas c, in GrainKey k, SKPaint p, SKShader g, SKBlendMode mode)
    {
        p.Shader = g; p.Color = SKColors.White.WithAlpha(k.Alpha); p.BlendMode = mode;
        c.DrawRect(k.R, p);
        p.Shader = null; p.BlendMode = SKBlendMode.SrcOver;
    }

    /// <summary>Print halftone over a photo: dots on a 30-degree screen, heavier toward the bottom.</summary>
    static void Halftone(SKCanvas c, SKRect r, float a)
    {
        if (dots == null)
        {
            var b = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var cv = new SKCanvas(b);
            cv.Clear(SKColors.Transparent);
            using var dp = new SKPaint { IsAntialias = true, Color = SKColors.Black };
            cv.DrawCircle(4, 4, 2.3f, dp);
            b.SetImmutable();
            dots = b;
        }
        c.SaveLayer(r, null);
        using (var ds = SKShader.CreateBitmap(dots, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateRotationDegrees(30)))
        using (var dpaint = new SKPaint { Shader = ds, Color = SKColors.White.WithAlpha((byte)(170 * a)) })
            c.DrawRect(r, dpaint);
        using (var m = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                   new[] { SKColors.Black.WithAlpha(40), SKColors.Black.WithAlpha(255) }, null, SKShaderTileMode.Clamp))
        using (var mp = new SKPaint { Shader = m, BlendMode = SKBlendMode.DstIn })
            c.DrawRect(r, mp);
        c.Restore();
    }

    /// <summary>When each card "lands" (seconds in): the shake and flash fire then. -1 = no impact.</summary>
    static double ImpactAt(StoryKind k) => k switch
    {
        StoryKind.RaceSoFar => 0.62, StoryKind.YourRace => 0.42, StoryKind.MoveOfRace => 0.62, StoryKind.FastestLaps => 0.52,
        StoryKind.PitStops => 0.42, StoryKind.Halfway => 0.6, StoryKind.WeatherWatch => 0.55,
        StoryKind.CarContact => 0.6, StoryKind.Limits => 0.62, StoryKind.Cleanest => 0.5, _ => -1,
    };

    /// <summary>What every card shares on top (2026-10-20): the landing flash and light streak, and the
    /// LIVE tab hanging off the top edge - LIVE · lap · sim clock, or FINAL for Driver of the Day.</summary>
    void BroadcastLayer(SKCanvas c, SKRect r, StoryKind kind, RaceSnapshot s, double t, double ti, float a)
    {
        if (ImpactAt(kind) >= 0 && ti >= 0 && ti < 0.5)
        {
            c.Save(); c.ClipRect(r);
            if (ti < 0.2)
                using (var fl = new SKPaint { Color = SKColors.White.WithAlpha((byte)(70 * (1 - ti / 0.2) * a)), BlendMode = SKBlendMode.Plus }) c.DrawRect(r, fl);
            float sx = r.Left - 160 + (r.Width + 320) * Ease((float)(ti / 0.5));
            using (var band = new SKPath())
            {
                band.MoveTo(sx + 90, r.Top); band.LineTo(sx + 150, r.Top); band.LineTo(sx + 60, r.Bottom); band.LineTo(sx, r.Bottom); band.Close();
                using var bp = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(46 * a)), BlendMode = SKBlendMode.Plus };
                c.DrawPath(band, bp);
            }
            c.Restore();
        }
        // the tab on top: only FINAL (Driver of the Day / the outro) - 2026-10-25, Angelo: the LIVE dot and the
        // clock aren't needed, and in battle mode the tab ran into the session bar
        float k = K(t, 0.3, 0.35);
        if (k <= 0) return;
        bool final = kind == StoryKind.DriverOfDay || OutroDirector.Running;
        if (!final) return;
        float th = 34, ty0 = r.Top - th - 6, ty1 = r.Top - 6, x0 = r.Left + (final ? 0 : 60);
        using var lt = P(Fonts.BarlowCondensed.Black, 25, SKColors.White, a);
        using var it = P(Fonts.BarlowCondensed.Bold, 23, Txt, a);
        string tag = final ? "FINAL" : "LIVE";
        string lap = s.TotalLaps > 0 ? $"LAP {Math.Max(1, s.Leader?.CurrentLap ?? s.CurrentLap)}/{s.TotalLaps}" : LapTimeFormat.SessionTimer(s.SessionTimeRemaining) + " TO GO";
        if (final) lap = "CHEQUERED FLAG";
        double tod = s.SessionTimeOfDay;
        string clock = tod > 0 ? $"{(int)(tod / 3600) % 24:00}:{(int)(tod / 60) % 60:00}" : "";
        string info = lap + (clock.Length > 0 && !final ? "   " + clock : "");
        float dotW = final ? 0 : 22, tagW = dotW + lt.MeasureText(tag) + 34, infoW = it.MeasureText(info) + 40;
        c.Save();
        c.ClipRect(new SKRect(x0 - 20, ty0 - 4, x0 + (tagW + infoW + 30) * k, ty1 + 4));
        SKPath Para(float l, float rr) { var pp = new SKPath(); pp.MoveTo(l + 12, ty0); pp.LineTo(rr + 12, ty0); pp.LineTo(rr, ty1); pp.LineTo(l, ty1); pp.Close(); return pp; }
        using (var p1 = Para(x0 + tagW, x0 + tagW + infoW))
        using (var ip = new SKPaint { IsAntialias = true, Color = new SKColor(10, 12, 17, (byte)(240 * a)) }) c.DrawPath(p1, ip);
        using (var p0 = Para(x0, x0 + tagW))
        using (var rp = new SKPaint { IsAntialias = true, Color = (final ? Gold : Red).WithAlpha((byte)(255 * a)) }) c.DrawPath(p0, rp);
        float by = ty1 - 9;
        if (!final)
        {
            bool on = (int)(t * 1.6) % 2 == 0;
            using var dp = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)((on ? 255 : 90) * a)) };
            c.DrawCircle(x0 + 24, (ty0 + ty1) / 2, 6, dp);
        }
        if (final) lt.Color = new SKColor(12, 10, 6, (byte)(255 * a));
        c.DrawText(tag, x0 + 16 + dotW, by, lt);
        c.DrawText(info, x0 + tagW + 22, by, it);
        c.Restore();
    }

    float Eyebrow(SKCanvas c, float L, float y, string text, DriverClass? cls, SKColor col, double t, float a)
    {
        float k = K(t, 0.2, 0.35), x = L;
        if (cls != null && ClassLogoBmp(cls.Name) is { } logo)
        {
            float lh = 26, lw = logo.Width * lh / logo.Height;
            DrawLogo(c, logo, x + lw / 2, y - 7, lw, lh, a * k);
            x += lw + 18;
        }
        using var ep = P(Fonts.BarlowCondensed.Bold, 23, col, a * k);
        TextTracked.Draw(c, ep, text, x, y + 2, 3.2f);
        return x + TrackedWidth(ep, text, 3.2f);
    }

    static float TrackedWidth(SKPaint p, string text, float sp)
    {
        float w = 0; foreach (char ch in text) w += p.MeasureText(ch.ToString()) + sp;
        return w;
    }

    static string Temp(float cDeg, bool f) => f ? $"{cDeg * 9 / 5 + 32:0}°F" : $"{cDeg:0}°C";
    static string DeltaTemp(float dC, bool f)
    {
        float v = f ? dC * 9 / 5 : dC;
        if (Math.Abs(v) < 0.5f) return "SAME AS THE START";
        return (v > 0 ? "+" : "-") + Math.Abs(v).ToString("0", CultureInfo.InvariantCulture) + (f ? "°F" : "°C") + " SINCE THE START";
    }
    static string Sky(int skies) => skies switch { 0 => "Clear", 1 => "Partly cloudy", 2 => "Mostly cloudy", 3 => "Overcast", _ => "-" };

    void PositionTrace(SKCanvas c, SKRect area, IReadOnlyList<(int Lap, int Pos)> hist, int nowPos, int nowLap, int fieldSize, double t, float a, SKColor lineCol)
    {
        var pts = new List<(int Lap, int Pos)>(hist);
        if (pts.Count == 0 || pts[^1].Lap != nowLap) pts.Add((nowLap, nowPos));
        int maxPos = Math.Max(3, Math.Max(fieldSize > 0 ? Math.Min(fieldSize, 12) : 0, pts.Max(q => q.Pos)));
        int lapMax = Math.Max(1, pts.Max(q => q.Lap));
        float gk = K(t, 0.8, 0.4);
        using (var lab = P(Fonts.Inter.SemiBold, 12, lineCol, a * gk * 0.8f))
        using (var gl = new SKPaint { Color = lineCol.WithAlpha((byte)(22 * a * gk)), StrokeWidth = 1 })
        {
            int step = maxPos > 8 ? 2 : 1;
            lab.TextAlign = SKTextAlign.Right;
            for (int q = 1; q <= maxPos; q += step)
            {
                float yy = Y(q);
                c.DrawLine(area.Left, yy, area.Right, yy, gl);
                c.DrawText("P" + q, area.Left - 10, yy + 4, lab);
            }
            lab.TextAlign = SKTextAlign.Center;
            c.DrawText("START", area.Left, area.Bottom + 26, lab);
            c.DrawText("LAP " + nowLap, area.Right - 20, area.Bottom + 26, lab);
        }
        if (pts.Count < 2) return;
        var line = new List<SKPoint>();
        for (int i = 0; i < pts.Count; i++)
        {
            var pt = new SKPoint(X(pts[i].Lap), Y(pts[i].Pos));
            if (i > 0) line.Add(new SKPoint(pt.X, line[^1].Y));
            line.Add(pt);
        }
        float total = 0; for (int i = 1; i < line.Count; i++) total += SKPoint.Distance(line[i - 1], line[i]);
        float reveal = total * Ease((float)((t - 1.0) / 1.8));
        if (reveal <= 1) return;
        using var path = new SKPath(); path.MoveTo(line[0]);
        float run = 0; SKPoint head = line[0], dir = new(1, 0);
        for (int i = 1; i < line.Count; i++)
        {
            float seg = SKPoint.Distance(line[i - 1], line[i]);
            if (run + seg >= reveal)
            {
                float k = seg > 0 ? (reveal - run) / seg : 0;
                head = new SKPoint(line[i - 1].X + (line[i].X - line[i - 1].X) * k, line[i - 1].Y + (line[i].Y - line[i - 1].Y) * k);
                if (seg > 0) dir = new SKPoint((line[i].X - line[i - 1].X) / seg, (line[i].Y - line[i - 1].Y) / seg);
                path.LineTo(head); break;
            }
            run += seg; path.LineTo(line[i]); head = line[i];
            if (seg > 0) dir = new SKPoint((line[i].X - line[i - 1].X) / seg, (line[i].Y - line[i - 1].Y) / seg);
        }
        using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10, Color = lineCol.WithAlpha((byte)(60 * a)), StrokeJoin = SKStrokeJoin.Round, ImageFilter = SKImageFilter.CreateBlur(6, 6) })
            c.DrawPath(path, glow);
        using (var lp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = lineCol.WithAlpha((byte)(255 * a)), StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round })
            c.DrawPath(path, lp);
        using (var dp = new SKPaint { IsAntialias = true, Color = Txt.WithAlpha((byte)(200 * a)) }) c.DrawCircle(line[0], 5, dp);
        float ang = (float)Math.Atan2(dir.Y, dir.X);
        c.Save(); c.Translate(head.X, head.Y); c.RotateRadians(ang);
        using (var hp = new SKPath())
        {
            hp.MoveTo(16, 0); hp.LineTo(-5, -9); hp.LineTo(-1, 0); hp.LineTo(-5, 9); hp.Close();
            using var hf = new SKPaint { IsAntialias = true, Color = Red.WithAlpha((byte)(255 * a)) };
            c.DrawPath(hp, hf);
        }
        c.Restore();

        float X(int lap) => area.Left + area.Width * lap / lapMax;
        float Y(int pos) => area.Top + area.Height * (pos - 1) / Math.Max(1, maxPos - 1);
    }

    static string ToGo(RaceSnapshot s) => s.TotalLaps > 0
        ? $"Lap {Math.Max(1, s.Leader?.CurrentLap ?? s.CurrentLap)} of {s.TotalLaps}"
        : $"{LapTimeFormat.SessionTimer(s.SessionTimeRemaining)} to go";

    static float ClassBest(DriverClass? cls)
    {
        float best = 0;
        if (cls != null) foreach (var d in cls.Drivers) if (d.BestLapTime > 0 && (best == 0 || d.BestLapTime < best)) best = d.BestLapTime;
        return best;
    }

    /// <summary>A red IMSA arrow from x0 to x1 (revealed by k).</summary>
    static void Arrow(SKCanvas c, float x0, float x1, float y, float k, float a, float thick)
    {
        if (k <= 0.01f) return;
        float hx = x0 + (x1 - x0) * k;
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(x0, 0), new SKPoint(hx, 0), new[] { Red.WithAlpha(0), Red.WithAlpha((byte)(255 * a)) }, null, SKShaderTileMode.Clamp))
        using (var rp = new SKPaint { IsAntialias = true, Shader = sh })
        using (var rib = new SKPath())
        {
            rib.MoveTo(x0, y - 0.6f); rib.LineTo(hx - 8, y - thick); rib.LineTo(hx - 8, y + thick); rib.LineTo(x0, y + 0.6f); rib.Close();
            c.DrawPath(rib, rp);
        }
        using var head = new SKPath();
        float s = thick * 2.6f;
        head.MoveTo(hx + s * 1.1f, y); head.LineTo(hx - s * 0.8f, y - s); head.LineTo(hx - s * 0.35f, y); head.LineTo(hx - s * 0.8f, y + s); head.Close();
        using var hp = new SKPaint { IsAntialias = true, Color = Red.WithAlpha((byte)(255 * a)) };
        c.DrawPath(head, hp);
    }

    /// <summary>Places gained / lost: a drawn triangle (the fonts have no arrow glyphs) + the count.</summary>
    static void DrawDelta(SKCanvas c, float cx, float cy, int n, float a, float size = 20)
    {
        var col = n > 0 ? Gain : n < 0 ? Loss : Txt3;
        using var tp = P(Fonts.BarlowCondensed.ExtraBold, size * 1.15f, col, a);
        if (n == 0) { tp.TextAlign = SKTextAlign.Center; c.DrawText("-", cx, cy + size * 0.35f, tp); return; }
        string s = Math.Abs(n).ToString(CultureInfo.InvariantCulture);
        float tri = size * 0.7f, tw = tp.MeasureText(s), w = tri + 6 + tw, x = cx - w / 2;
        using (var path = new SKPath())
        {
            if (n > 0) { path.MoveTo(x + tri / 2, cy - tri * 0.5f); path.LineTo(x + tri, cy + tri * 0.36f); path.LineTo(x, cy + tri * 0.36f); }
            else { path.MoveTo(x, cy - tri * 0.36f); path.LineTo(x + tri, cy - tri * 0.36f); path.LineTo(x + tri / 2, cy + tri * 0.5f); }
            path.Close();
            using var fp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * Math.Clamp(a, 0, 1))) };
            c.DrawPath(path, fp);
        }
        c.DrawText(s, x + tri + 6, cy + size * 0.35f, tp);
    }

    static void DrawLogo(SKCanvas c, SKBitmap b, float cx, float cy, float bw, float bh, float a)
    {
        float s = Math.Min(bw / b.Width, bh / b.Height), w = b.Width * s, h = b.Height * s;
        using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(235 * Math.Clamp(a, 0, 1))) };
        c.DrawBitmap(b, new SKRect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), lp);
    }

    SKBitmap? CarPhoto(string? carName)
    {
        if (string.IsNullOrWhiteSpace(carName)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (char ch in carName.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        string slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        var found = CarPhotos.Find(carName);
        if (found != null) return Bmp("logos", "sponsors", System.IO.Path.GetFileName(found));
        return Bmp("logos", "sponsors", "car_" + slug + "_panel.png") ?? Bmp("logos", "sponsors", "car_" + slug + ".png");
    }

    SKBitmap? Maker(string? brand)
    {
        var slug = ManufacturerBrand.Resolve(brand ?? "");
        return slug == null ? null : Bmp("logos", "manufacturers", slug + ".png");
    }

    SKBitmap? ClassLogoBmp(string? cls)
    {
        var slug = ClassLogo.Resolve(cls);
        return slug == null ? null : Bmp("logos", "classes", slug + ".png");
    }

    SKBitmap? Bmp(params string[] parts)
    {
        string key = string.Join("/", parts);
        if (bmps.TryGetValue(key, out var b)) return b;
        try
        {
            var path = Assets.Resolve(parts);
            if (System.IO.File.Exists(path)) { using var st = System.IO.File.OpenRead(path); b = Assets.Decode(st); }
        }
        catch { b = null; }
        return bmps[key] = b;
    }

    static DriverClass? ClassOf(RaceSnapshot s, Driver p)
    {
        foreach (var cl in s.ClassLeaderboard) foreach (var d in cl.Drivers) if (d.CarIdx == p.CarIdx) return cl;
        return null;
    }

    static float Ease(float x) { x = Math.Clamp(x, 0, 1); return 1 - (1 - x) * (1 - x) * (1 - x); }
    static float OutBack(float x) { const float c1 = 1.4f, c3 = c1 + 1; return 1 + c3 * (float)Math.Pow(x - 1, 3) + c1 * (float)Math.Pow(x - 1, 2); }
    static float K(double t, double start, double len) => Ease((float)((t - start) / len));

    static SKPaint P(SKTypeface tf, float size, SKColor col, float a) => new()
    { IsAntialias = true, Typeface = tf, TextSize = size, Color = col.WithAlpha((byte)(col.Alpha * Math.Clamp(a, 0, 1))) };

    static string Fit(string s, SKPaint p, float maxW)
    {
        if (string.IsNullOrEmpty(s) || p.MeasureText(s) <= maxW) return s ?? "";
        for (int n = s.Length - 1; n > 1; n--) { var t = s[..n].TrimEnd() + "…"; if (p.MeasureText(t) <= maxW) return t; }
        return s[..1];
    }

    static SKColor ParseHex(string? hex, SKColor fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        var h = hex.TrimStart('#');
        return h.Length == 6 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v) : fallback;
    }

    public void Dispose() { foreach (var b in bmps.Values) b?.Dispose(); bmps.Clear(); watch.Dispose(); }
}
