using System.Globalization;
using System.IO;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The post-race outro (2026-10-20, Angelo: "we need an updated outro"). After the chequered
/// flag the broadcast takes the whole screen for the big moments, then hands back to half-screen
/// cards so the cool-down lap stays visible:
///   FULL SCREEN  chequered flag + class winners -> one podium per class -> final results per class
///   HALF SCREEN  your race (final) -> fastest lap (GO FAST) -> cleanest driver (Trading Paints)
///                -> biggest climber (A&amp;B) -> Driver of the Day
/// then everything clears and the game is left on its own (no closing slate, no old results board).
/// Classes run smallest first so the top class closes the show. Results say PROVISIONAL until
/// iRacing marks them official.
/// </summary>
public static class OutroDirector
{
    public enum Seg { Flag, Podium, Results, YourRace, Fastest, Clean, Climber, DriverOfDay }
    public sealed record Segment(Seg Kind, int ClassIndex, double Start, double Length);

    static readonly List<Segment> plan = new();
    static double startMs = -1;
    static int session = int.MinValue;
    static bool seenRace;
    // 2026-10-24 (Angelo: "the finish animation should come when the target driver crosses the line"):
    // the chequered flag goes out when the LEADER finishes; the featured car may still be on its last lap.
    // Wait for it to cross the line (or pull off / 4 minutes), keeping the race graphics up meanwhile.
    static int waitCar = -1, waitLaps = -1;
    static double waitSinceMs = -1;
    /// <summary>Chequered flag is out but the featured car hasn't crossed the line yet.</summary>
    public static bool Waiting { get; private set; }
    static readonly HashSet<int> forced = new();

    /// <summary>The outro has started for this session (it replaces the old post-race board for good).</summary>
    public static bool Started => startMs >= 0;
    /// <summary>A full-screen part is on now.</summary>
    public static bool FullScreen { get; private set; }
    public static Segment? Current { get; private set; }
    public static double Local { get; private set; }
    public static IReadOnlyList<DriverClass> Classes { get; private set; } = Array.Empty<DriverClass>();

    /// <summary>Preview / test: start the outro now.</summary>
    public static void Force(RaceSnapshot s, double nowMs) { Build(s); startMs = nowMs; forced.Clear(); }

    public static void Tick(RaceSnapshot s, double nowMs)
    {
        if (s.SessionNum != session) { session = s.SessionNum; startMs = -1; seenRace = false; plan.Clear(); forced.Clear(); waitCar = -1; waitSinceMs = -1; Waiting = false; }
        bool isRace = (s.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase);
        if (isRace && s.Phase == RacePhase.Broadcast) seenRace = true;
        if (startMs < 0 && seenRace && s.Phase == RacePhase.PostRace)
        {
            var target = s.CameraTarget ?? s.Player;
            if (waitSinceMs < 0)
            {
                waitSinceMs = nowMs;
                bool leaderIsTarget = target == null || s.Leader == null || s.Leader.CarIdx == target.CarIdx;
                waitCar = leaderIsTarget ? -1 : target!.CarIdx;
                waitLaps = target?.LapsCompleted ?? -1;
            }
            Driver? w = null;
            if (waitCar >= 0)
                foreach (var dc in s.ClassLeaderboard) foreach (var d in dc.Drivers) if (d.CarIdx == waitCar) w = d;
            bool crossed = waitCar < 0 || w == null || w.LapsCompleted > waitLaps || w.IsInPit || !w.IsConnected
                           || nowMs - waitSinceMs > 240000;
            Waiting = !crossed;
            if (crossed) { Waiting = false; Build(s); startMs = nowMs + 1500; }   // a breath after they cross
        }
        if (startMs < 0) { FullScreen = false; Current = null; Finished = false; return; }
        double t = (nowMs - startMs) / 1000.0;
        Finished = plan.Count > 0 && t >= plan[^1].Start + plan[^1].Length;   // #576
        Current = null; FullScreen = false;
        for (int i = 0; i < plan.Count; i++)
        {
            var sg = plan[i];
            if (t < sg.Start || t >= sg.Start + sg.Length) continue;
            Current = sg; Local = t - sg.Start;
            FullScreen = sg.Kind is Seg.Flag or Seg.Podium or Seg.Results;
            if (FullScreen && forced.Add(i))
                StorySound.Play(sg.Kind switch { Seg.Flag => "outro_flag", Seg.Podium => "outro_podium", _ => "outro_results" });
            if (!FullScreen && forced.Add(i))
                StoryDirector.Force(sg.Kind switch
                {
                    Seg.YourRace => StoryKind.YourRace, Seg.Fastest => StoryKind.FastestLaps, Seg.Clean => StoryKind.Cleanest,
                    Seg.Climber => StoryKind.MoveOfRace, _ => StoryKind.DriverOfDay,
                }, nowMs);
            break;
        }
    }

    static void Build(RaceSnapshot s)
    {
        plan.Clear();
        // smallest class first, the top class last
        var cls = s.ClassLeaderboard.Where(c => c.Drivers.Count > 0).Reverse().ToList();
        Classes = cls;
        double t = 0;
        void Add(Seg k, int ci, double len) { plan.Add(new Segment(k, ci, t, len)); t += len; }
        Add(Seg.Flag, -1, 6.0);
        // 2026-10-28 (#486, Angelo: "the post race stuff needs to bring the same exact design language as the pre race"):
        // one RESULT page per class, built exactly like the pre-race grid pages (GridHeroBoard in results mode): the winner
        // and P2 as hero cards with their cars, the rest cascading in. The podium is the hero row, so no separate podium page.
        for (int i = 0; i < cls.Count; i++) Add(Seg.Results, i, 9.0 + Math.Max(0, cls[i].Drivers.Count - 12) * 0.45);
        t += 0.4;
        Add(Seg.YourRace, -1, 9.0);
        if (s.ClassLeaderboard.Any(c => RaceDiary.Fastest(c.Name) != null)) Add(Seg.Fastest, -1, 9.0);
        if (RaceDiary.HasIncidents || s.ClassLeaderboard.Any(c => c.Drivers.Any(d => d.Incidents >= 0))) Add(Seg.Clean, -1, 9.0);
        if (StoryDirector.MoveOfRace(s) is { } m && RaceDiary.Gained(m) >= 1) Add(Seg.Climber, -1, 9.0);
        Add(Seg.DriverOfDay, -1, 12.0);
    }

    /// <summary>The outro is somewhere in its run (used to keep the old post-race board away).</summary>
    public static bool Running => startMs >= 0 && plan.Count > 0;
    /// <summary>#576: every part of the outro has played (the thank-you roll may follow).</summary>
    public static bool Finished { get; private set; }
}

/// <summary>Draws the outro's full-screen parts: the flag, the podiums, the results.
/// 2026-10-21 (Angelo: "they are very AI"): rebuilt in the story cards' broadcast language -
/// carbon stage, slanted slabs with a white keyline, heavy condensed type, timing numerals,
/// the class colour as an edge, a FINAL tab. No clip-art flag, no podium steps, no confetti.
/// Laid out on a 1920x1080 design grid and scaled to the screen.</summary>
public sealed class OutroRenderer : IDisposable
{
    const float DW = 1920f, DH = 1080f;
    // #340 legibility: Dim was 7E8AA0 (~55%), too dim on the TV across the room
    static readonly SKColor Ink = new(0xF4, 0xF6, 0xFA), Ink2 = new(0xC8, 0xCE, 0xDA), Dim = new(0xB4, 0xBE, 0xCE);
    static readonly SKColor Bg = new(0x0A, 0x0C, 0x11), SlabC = new(0x14, 0x17, 0x1F), Slab2 = new(0x1C, 0x20, 0x2A), SlabAlt = new(0x10, 0x13, 0x1A);
    static readonly SKColor Gold = new(0xE8, 0xC2, 0x5A), Purple = new(0xBE, 0x5A, 0xFF), Gain = new(0x28, 0xDC, 0x82), Loss = new(0xFF, 0x3C, 0x50);
    static readonly SKColor Amber = new(0xFF, 0xAA, 0x3C), CleanBlue = new(0x28, 0x96, 0xFF), MeRow = new(0x24, 0x28, 0x38), GoGreen = new(0x24, 0xE0, 0x8A);
    readonly Dictionary<string, SKBitmap?> bmps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<SKBitmap, bool> mono = new();
    SKShader? carbon, grain;
    SKTypeface? timing;

    public void Draw(SKCanvas c, float w, float h, RaceSnapshot s, double nowMs)
    {
        var sg = OutroDirector.Current;
        if (sg == null || !OutroDirector.FullScreen) return;
        double t = OutroDirector.Local;
        float fin = Ease(t / 0.30), fout = 1 - Ease((t - (sg.Length - 0.35)) / 0.35), a = Math.Min(fin, fout);
        bool first = sg.Kind == OutroDirector.Seg.Flag;
        float stageA = first ? Ease(t / 0.4) : 1;
        float k = w / DW, H = h / k;
        c.Save();
        c.Scale(k);
        Stage(c, DW, H, stageA);   // stays solid between parts so the game never flashes through
        var cls = OutroDirector.Classes;
        switch (sg.Kind)
        {
            case OutroDirector.Seg.Flag: WinnersPage(c, H, s, t, a); break;   // #486: pre-race language (old: Flag)
            case OutroDirector.Seg.Podium: if (sg.ClassIndex < cls.Count) Podium(c, H, s, Live(s, cls[sg.ClassIndex]), t, a); break;
            case OutroDirector.Seg.Results: if (sg.ClassIndex < cls.Count) ResultPage(c, H, s, sg.ClassIndex, t, a); break;
        }
        c.Restore();
    }

    /// <summary>The class as it stands right now (positions can still settle after the flag).</summary>
    static DriverClass Live(RaceSnapshot s, DriverClass c) => s.ClassLeaderboard.FirstOrDefault(x => x.Name == c.Name) ?? c;

    // ================================================================== stage + top bar
    void Stage(SKCanvas c, float w, float h, float a)
    {
        carbon ??= BuildCarbon();
        grain ??= BuildGrain();
        byte al = (byte)(255 * Math.Clamp(a, 0, 1));
        // 2026-10-28 (#486): the pre-race splash backdrop - deep broadcast navy, a soft gold light from the top - instead of
        // the story cards' carbon weave, so the outro reads as the same show as the opener
        using (var p = new SKPaint { Color = new SKColor(0x08, 0x0A, 0x10, al) }) c.DrawRect(0, 0, w, h, p);
        using (var gl = SKShader.CreateRadialGradient(new SKPoint(w / 2, -h * 0.15f), w * 0.75f, new[] { new SKColor(0xC5, 0xA5, 0x72, (byte)(34 * a)), new SKColor(0x12, 0x18, 0x26, (byte)(120 * a)), new SKColor(0, 0, 0, 0) }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { Shader = gl }) c.DrawRect(0, 0, w, h, p);
        using (var vg = SKShader.CreateRadialGradient(new SKPoint(w / 2, h * 0.45f), w * 0.72f, new[] { new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, (byte)(150 * a)) }, new[] { 0.45f, 1f }, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { Shader = vg }) c.DrawRect(0, 0, w, h, p);
    }

    static SKShader BuildCarbon()
    {
        const int cell = 6, n = cell * 4;   // twill weave: 2x2 blocks on a diagonal, with a soft sheen across each strand
        var bmp = new SKBitmap(n, n, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int cx = x / cell, cy = y / cell;
                float tw = (cx + cy) % 4 < 2 ? 1f : 0f;
                float sh = (float)(Math.Sin((x + y) / (double)cell * Math.PI) * 0.5 + 0.5);
                float g = 11 + 5 * tw * sh;
                bmp.SetPixel(x, y, new SKColor((byte)g, (byte)(g * 1.06f), (byte)Math.Min(255, g * 1.32f)));
            }
        var sh2 = SKShader.CreateBitmap(bmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        return sh2;
    }

    static SKShader BuildGrain()
    {
        const int n = 256;
        var bmp = new SKBitmap(n, n, SKColorType.Rgba8888, SKAlphaType.Premul);
        var rnd = new Random(7);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int v = rnd.Next(0, 2) == 0 ? 0 : 255;
                bmp.SetPixel(x, y, new SKColor((byte)v, (byte)v, (byte)v, (byte)rnd.Next(0, 9)));
            }
        return SKShader.CreateBitmap(bmp, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
    }

    void TopBar(SKCanvas c, RaceSnapshot s, float a)
    {
        Slab(c, 60, 40, 150, 46, Ink, a, slant: 12, keyline: false, shadow: false);
        Txt(c, "FINAL", 135, 63, Fonts.BarlowCondensed.Black, 30, Bg, a, Align.Center, 2, middle: true);
        string track = (s.TrackDisplayName ?? "").ToUpperInvariant();
        float x = 232 + Txt(c, track, 232, 63, Fonts.BarlowCondensed.Bold, 24, Ink, a, Align.Left, 2, middle: true);
        string? series = SeriesNames.ResolveName(s.SeriesId);
        if (!string.IsNullOrEmpty(series)) Txt(c, "·  " + series.ToUpperInvariant(), x + 14, 63, Fonts.BarlowCondensed.SemiBold, 24, Dim, a, Align.Left, 2, middle: true);
        int cars = s.ClassLeaderboard.Sum(cl => cl.Drivers.Count);
        string right = (s.TotalLaps > 0 ? $"{s.TotalLaps} LAPS  ·  " : "") + $"{cars} CARS";
        Txt(c, right, 1860, 63, Fonts.BarlowCondensed.Bold, 24, Dim, a, Align.Right, 2, middle: true);
        using var ln = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(28 * a)) };
        c.DrawRect(new SKRect(60, 100, 1860, 101), ln);
    }

    // ================================================================== 1. CHEQUERED FLAG
    void Flag(SKCanvas c, float H, RaceSnapshot s, double t, float a)
    {
        // a chequered wipe crosses the whole screen, then leaves a band behind it top-right
        float wipe = Ease(t / 0.9);
        if (wipe < 1)
        {
            float front = -300 + 2700 * wipe;
            using var clip = new SKPath();
            clip.MoveTo(front - 760, 0); clip.LineTo(front, 0); clip.LineTo(front - 330, H); clip.LineTo(front - 1090, H); clip.Close();
            c.Save(); c.ClipPath(clip, antialias: true);
            Checker(c, -200, 0, DW + 400, H, 90, 0.35f, a, 1f, t * 40);
            c.Restore();
        }
        float band = Ease((t - 0.45) / 0.6);
        if (band > 0)
        {
            using var clip = new SKPath();
            float l0 = 1180 + (1 - band) * 760, l1 = 1060 + (1 - band) * 880;
            clip.MoveTo(l0, 104); clip.LineTo(DW, 104); clip.LineTo(DW, 436); clip.LineTo(l1, 436); clip.Close();
            c.Save(); c.ClipPath(clip, antialias: true);
            Checker(c, 900, 104, 1060, 332, 55, 0.35f, a, 0f, t * 18);
            c.Restore();
            using var edge = new SKPaint { IsAntialias = true, StrokeWidth = 4, Color = Ink.WithAlpha((byte)(255 * a * band)) };
            c.DrawLine(l0, 104, l1, 436, edge);
        }
        float tk = Ease((t - 0.55) / 0.4);
        TopBar(c, s, a * tk);
        c.Save(); c.Translate((1 - tk) * -60, 0);
        Txt(c, "CHEQUERED FLAG", 56, 250, Fonts.BarlowCondensed.Black, 168, Ink, a * tk);
        float sx = 62 + Txt(c, "RACE WINNERS", 62, 312, Fonts.BarlowCondensed.Bold, 34, Gold, a * tk, Align.Left, 6);
        var classes = s.ClassLeaderboard.Where(cl => cl.Drivers.Count > 0).ToList();
        int cars = classes.Sum(cl => cl.Drivers.Count);
        string sub = $"{classes.Count} CLASS{(classes.Count == 1 ? "" : "ES")}" + (s.TotalLaps > 0 ? $"  ·  {s.TotalLaps} LAPS" : "") + $"  ·  {cars} STARTERS";
        Txt(c, sub, sx + 20, 312, Fonts.BarlowCondensed.SemiBold, 30, Dim, a * tk, Align.Left, 3);
        c.Restore();

        // one winner card per class, top class first
        int n = Math.Max(1, classes.Count);
        float gap = 30, cw = Math.Min(700, (1800 - (n - 1) * gap) / n), total = n * cw + (n - 1) * gap, x0 = (DW - total) / 2;
        for (int i = 0; i < classes.Count; i++)
        {
            var cl = classes[i];
            var win = Sorted(cl)[0];
            double land = 1.2 + i * 0.22;
            float rk = OutBack((t - land) / 0.5), ra = a * Ease((t - land) / 0.25);
            if (ra <= 0.01f) continue;
            var col = ParseHex(cl.Color, Gold);
            float x = x0 + i * (cw + gap), y = 480 + (1 - rk) * 120, ch = 480, sc = cw / 580f;
            Slab(c, x, y, cw, ch, SlabC, ra, slant: 22, top: col);
            var (fn, ln) = SplitName(win.Name);
            Txt(c, $"{win.CarNumber}", x + cw - 24, y + 345, Fonts.BarlowCondensed.Black, 330 * Math.Min(1, sc + 0.1f), col.WithAlpha(34), ra, Align.Right);
            if (ClassLogoBmp(cl.Name) is { } lg) LogoMid(c, lg, x + 52, y + 62, 220 * sc, 34, ra, white: false);
            if (Maker(win.CarBrand) is { } mk) LogoBox(c, mk, new SKRect(x + cw - 145, y + 54, x + cw - 35, y + 116), ra);
            Txt(c, "WINNER", x + 52, y + 140, Fonts.BarlowCondensed.Bold, 26, col, ra, Align.Left, 6);
            Txt(c, fn, x + 50, y + 200, Fonts.BarlowCondensed.SemiBold, 46, Ink2, ra, Align.Left, 1, maxW: cw - 100);
            Txt(c, ln, x + 46, y + 300, Fonts.BarlowCondensed.Black, 112, Ink, ra, Align.Left, 0, maxW: cw - 90, shrink: true);
            Line(c, x + 40, y + 352, x + cw - 40, ra);
            var runner = Sorted(cl).Skip(1).FirstOrDefault();
            string margin = runner == null ? "-" : GapTo(runner, win);
            string best = win.BestLapTime > 0 ? LapTimeFormat.Lap(win.BestLapTime) : "-";
            float[] cols = { 0, 110 * sc, 290 * sc };
            string[] labs = { "CAR", "BEST LAP", "MARGIN" }, vals = { "#" + win.CarNumber, best, margin };
            for (int j = 0; j < 3; j++)
            {
                Txt(c, labs[j], x + 50 + cols[j], y + 398, Fonts.BarlowCondensed.Bold, 20, Dim, ra, Align.Left, 3);
                if (j == 0) Txt(c, vals[j], x + 50 + cols[j], y + 446, Fonts.BarlowCondensed.Black, 44, Ink, ra);
                else Txt(c, vals[j], x + 50 + cols[j], y + 446, Timing, 30 * Math.Min(1, sc + 0.1f), Ink, ra);
            }
            Flash(c, x, y, cw, ch, 22, t - land - 0.35, a);
        }
    }

    // ================================================================== 2. PODIUM
    void Podium(SKCanvas c, float H, RaceSnapshot s, DriverClass cl, double t, float a)
    {
        var col = ParseHex(cl.Color, Gold);
        float tk = Ease((t - 0.1) / 0.4);
        TopBar(c, s, a);
        c.Save(); c.Translate((1 - tk) * -60, 0);
        if (ClassLogoBmp(cl.Name) is { } lg) LogoMid(c, lg, 60, 168, 260, 46, a * tk, white: false);
        Txt(c, "PODIUM", 54, 330, Fonts.BarlowCondensed.Black, 168, Ink, a * tk);
        Txt(c, "FINAL CLASSIFICATION  ·  TOP 3", 62, 380, Fonts.BarlowCondensed.Bold, 30, Dim, a * tk, Align.Left, 4);
        c.Restore();

        var top = Sorted(cl).Take(3).ToList();
        if (top.Count == 0) return;
        // P2 left, P1 centre (tallest), P3 right; they land 3 -> 2 -> 1
        (int idx, float x, float h, double land)[] slots = { (1, 600, 500, 0.75), (0, 1020, 600, 1.10), (2, 1440, 440, 0.45) };
        const float cw = 400, bottom = 1010;
        foreach (var (idx, x, ch, land) in slots)
        {
            if (idx >= top.Count) continue;
            var d = top[idx];
            int pos = idx + 1;
            float rk = OutBack((t - land) / 0.55), ra = a * Ease((t - land) / 0.22);
            if (ra <= 0.01f) continue;
            float y = bottom - ch + (1 - rk) * 140;
            var tone = pos == 1 ? Gold : col;
            bool me = d.CarIdx == s.PlayerCarIdx;
            Slab(c, x, y, cw, ch, SlabC, ra, slant: 20, top: tone);
            Txt(c, pos.ToString(CultureInfo.InvariantCulture), x + cw - 14, y + ch - 132, Fonts.BarlowCondensed.Black, 300, Ink.WithAlpha(16), ra, Align.Right);
            Slab(c, x + 36, y + 40, 96, 64, tone, ra, slant: 10, keyline: false, shadow: false);
            Txt(c, "P" + pos, x + 84, y + 72, Fonts.BarlowCondensed.Black, 46, Bg, ra, Align.Center, 0, middle: true);
            if (Maker(d.CarBrand) is { } mk) LogoBox(c, mk, new SKRect(x + cw - 124, y + 44, x + cw - 32, y + 104), ra);
            float chipX = x + 150;
            if (pos == 1) chipX += 14 + Txt(c, "WINNER", x + 150, y + 82, Fonts.BarlowCondensed.Bold, 24, Gold, ra, Align.Left, 6);
            if (me) YouChip(c, chipX, y + 53, ra);
            var (fn, ln) = SplitName(d.Name);
            Txt(c, fn, x + 38, y + 168, Fonts.BarlowCondensed.SemiBold, 38, Ink2, ra, Align.Left, 1, maxW: cw - 70);
            Txt(c, ln, x + 34, y + 248, Fonts.BarlowCondensed.Black, 84, Ink, ra, Align.Left, 0, maxW: cw - 70, shrink: true);
            Txt(c, "#" + d.CarNumber, x + 38, y + 290, Fonts.BarlowCondensed.Black, 34, tone, ra);
            float fy = y + ch - 120;
            Line(c, x + 30, fy, x + cw - 30, ra);
            string gap = pos == 1 ? "WINNER" : GapTo(d, top[0]);
            string best = d.BestLapTime > 0 ? LapTimeFormat.Lap(d.BestLapTime) : "-";
            int inc = Inc(d);
            string[] labs = { "GAP", "BEST LAP", "INC" }, vals = { gap, best, inc >= 0 ? inc + "x" : "-" };
            float[] cols = { 0, 122, 296 };
            for (int j = 0; j < 3; j++)
            {
                Txt(c, labs[j], x + 38 + cols[j], fy + 44, Fonts.BarlowCondensed.Bold, 20, Dim, ra, Align.Left, 3);
                if (vals[j] == "WINNER") Txt(c, vals[j], x + 38 + cols[j], fy + 88, Fonts.BarlowCondensed.Black, 34, Gold, ra);
                else Txt(c, vals[j], x + 38 + cols[j], fy + 88, Timing, 25, Ink, ra, Align.Left, 0, maxW: j == 0 ? 118 : 170, shrink: true);
            }
            Flash(c, x, y, cw, ch, 20, t - land - 0.4, a);
            if (pos == 1)   // one gold pulse when the winner lands
            {
                float f = (float)Math.Clamp(1 - (t - land - 0.4) / 0.6, 0, 1);
                if (t > land + 0.4 && f > 0)
                    using (var g = SKShader.CreateRadialGradient(new SKPoint(x + cw / 2, y + 120), 900, new[] { Gold.WithAlpha((byte)(60 * f * a)), Gold.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
                    using (var gp = new SKPaint { Shader = g, BlendMode = SKBlendMode.Plus }) c.DrawRect(0, 0, DW, H, gp);
            }
        }
        // the class's fastest lap, bottom-left under the title
        var fl = FastestOf(cl);
        float fk = Ease((t - 1.6) / 0.4), fa = a * fk;
        if (fl is { } f2 && fa > 0.01f)
        {
            c.Save(); c.Translate((1 - fk) * -80, 0);
            Slab(c, 60, 860, 440, 150, Slab2, fa, slant: 16);
            Txt(c, "CLASS FASTEST LAP", 100, 912, Fonts.BarlowCondensed.Bold, 22, Purple, fa, Align.Left, 4);
            float tw = Txt(c, LapTimeFormat.Lap(f2.Time), 100, 972, Timing, 46, Ink, fa);
            Txt(c, Short(f2.Name), 100 + tw + 22, 972, Fonts.BarlowCondensed.Bold, 28, Dim, fa, Align.Left, 1, maxW: 440 - tw - 70);
            c.Restore();
        }
    }

    // ================================================================== 3. FINAL RESULTS
    void Results(SKCanvas c, float H, RaceSnapshot s, DriverClass cl, double t, float a, double len)
    {
        var col = ParseHex(cl.Color, Gold);
        float hk = Ease((t - 0.1) / 0.4), ha = a * hk;
        TopBar(c, s, a);
        if (ClassLogoBmp(cl.Name) is { } lg) LogoMid(c, lg, 60, 160, 230, 40, ha, white: false);
        float tw = Txt(c, "FINAL RESULT", 54, 290, Fonts.BarlowCondensed.Black, 136, Ink, ha);
        bool official = s.ResultsOfficial;
        string chip = official ? "OFFICIAL" : "PROVISIONAL";
        Slab(c, 54 + tw + 22, 222, 190, 50, official ? GoGreen : new SKColor(60, 66, 82), ha, slant: 10, keyline: false, shadow: false);
        Txt(c, chip, 54 + tw + 117, 247, Fonts.BarlowCondensed.Black, 26, official ? Bg : Ink, ha, Align.Center, 3, middle: true);

        const float X0 = 60, X1 = 1370, Y0 = 360;
        float bottom = Math.Min(H, DH) - 40;
        var rows = Sorted(cl);
        float avail = bottom - Y0;
        float RH = Math.Clamp(avail / Math.Max(1, rows.Count), 60, 92), k = RH / 92f;
        int fit = (int)(avail / RH);
        float scroll = rows.Count > fit ? (rows.Count - fit) * RH * Ease((t - 3.0) / Math.Max(1, len - 4.2)) : 0;
        float cChg = X0 + 112, cNum = X0 + 178, cMk = X0 + 290, cDrv = X0 + 350, cGap = X0 + 800, cBest = X0 + 970, cPit = X0 + 1160, cInc = X0 + 1240;
        foreach (var (lab, x) in new[] { ("+/-", cChg), ("CAR", cNum), ("DRIVER", cDrv), ("GAP", cGap), ("BEST LAP", cBest), ("PITS", cPit), ("INC", cInc) })
            Txt(c, lab, x, Y0 - 14, Fonts.BarlowCondensed.Bold, 20, Dim, ha, Align.Left, 3);
        var winner = rows.FirstOrDefault();
        float bestInClass = rows.Where(d => d.BestLapTime > 0).Select(d => d.BestLapTime).DefaultIfEmpty(0).Min();
        c.Save(); c.ClipRect(new SKRect(0, Y0 - 2, DW, bottom + 6));
        for (int i = 0; i < rows.Count; i++)
        {
            var d = rows[i];
            float y = Y0 + i * RH - scroll;
            if (y < Y0 - RH || y > bottom) continue;
            float rk = Ease((t - 0.45 - Math.Min(i, fit) * 0.06) / 0.35), ra = a * rk;
            if (ra <= 0.01f) continue;
            c.Save(); c.Translate((1 - rk) * 60, 0);
            bool me = d.CarIdx == s.PlayerCarIdx;
            int pos = d.PositionInClass > 0 ? d.PositionInClass : i + 1;
            float mid = y + RH / 2;
            Slab(c, X0 + 80, y + 6, X1 - X0 - 80, RH - 12, me ? MeRow : (i % 2 == 0 ? SlabC : SlabAlt), ra, slant: 12, shadow: i == 0, keyline: me);
            if (me) using (var mb = new SKPaint { Color = Ink.WithAlpha((byte)(255 * ra)) }) c.DrawRect(new SKRect(X1 - 6, y + 6, X1, y + RH - 6), mb);
            var tone = pos == 1 ? Gold : pos <= 3 ? Ink : new SKColor(52, 58, 74);
            Slab(c, X0, y + 6, 92, RH - 12, tone, ra, slant: 12, keyline: false, shadow: false);
            Txt(c, pos.ToString(CultureInfo.InvariantCulture), X0 + 46, mid, Fonts.BarlowCondensed.Black, 56 * k, pos <= 3 ? Bg : Ink, ra, Align.Center, 0, middle: true);
            using (var ce = new SKPath())
            {
                ce.MoveTo(X0 + 104, y + 6); ce.LineTo(X0 + 110, y + 6); ce.LineTo(X0 + 98, y + RH - 6); ce.LineTo(X0 + 92, y + RH - 6); ce.Close();
                using var cp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * ra)) }; c.DrawPath(ce, cp);
            }
            Delta(c, cChg, mid, RaceDiary.Gained(d), ra, k);
            Txt(c, "#" + d.CarNumber, cNum, mid, Fonts.BarlowCondensed.Black, 40 * k, col, ra, Align.Left, 0, middle: true);
            if (Maker(d.CarBrand) is { } mk) LogoBox(c, mk, new SKRect(cMk + 6 - 37 * k, mid - 22 * k, cMk + 6 + 37 * k, mid + 22 * k), ra);
            var (fn, ln) = SplitName(d.Name);
            float ix = string.IsNullOrEmpty(fn) ? 0 : 36 * k;
            if (ix > 0) Txt(c, fn[..1] + ".", cDrv, mid, Fonts.BarlowCondensed.SemiBold, 34 * k, Ink2, ra, Align.Left, 0, middle: true);
            float nw = Txt(c, ln, cDrv + ix, mid, Fonts.BarlowCondensed.Black, 50 * k, Ink, ra, Align.Left, 0, middle: true, maxW: cGap - cDrv - ix - (me ? 100 : 24), shrink: true);
            if (me) YouChip(c, cDrv + ix + nw + 14, mid - 17, ra, 0.9f);
            string gap = i == 0 ? "WINNER" : winner == null ? "-" : GapTo(d, winner);
            if (gap == "WINNER") Txt(c, gap, cGap, mid, Fonts.BarlowCondensed.Black, 34 * k, Gold, ra, Align.Left, 0, middle: true);
            else Txt(c, gap, cGap, mid, Timing, 28 * k, Ink, ra, Align.Left, 0, middle: true, maxW: 160, shrink: true);
            bool fastest = d.BestLapTime > 0 && Math.Abs(d.BestLapTime - bestInClass) < 0.0005f;
            if (fastest) Slab(c, cBest - 12, mid - 22 * k, 170, 44 * k, Purple, ra, slant: 8, keyline: false, shadow: false);
            Txt(c, d.BestLapTime > 0 ? LapTimeFormat.Lap(d.BestLapTime) : "-", cBest, mid, Timing, 28 * k, Ink, ra, Align.Left, 0, middle: true);
            Txt(c, RaceDiary.PitStops(d.CarIdx).ToString(CultureInfo.InvariantCulture), cPit + 16, mid, Timing, 28 * k, Ink, ra, Align.Center, 0, middle: true);
            int inc = Inc(d);
            Txt(c, inc >= 0 ? inc + "x" : "-", cInc + 20, mid, Timing, 28 * k, inc == 0 ? Gain : inc >= 8 ? Loss : inc >= 4 ? Amber : Ink, ra, Align.Center, 0, middle: true);
            c.Restore();
        }
        c.Restore();

        // the race in three numbers, down the right
        const float RX = 1420, RW = 440;
        var cards = new List<(string Lab, SKColor Tone, string Big, string Sub, Driver? D, bool Time)>();
        if (FastestOf(cl) is { } f)
        {
            var fd = rows.FirstOrDefault(d => d.CarIdx == f.CarIdx);
            cards.Add(("FASTEST LAP", Purple, LapTimeFormat.Lap(f.Time), Short(f.Name) + (f.Lap > 0 ? $"  ·  LAP {f.Lap}" : ""), fd, true));
        }
        var climber = rows.OrderByDescending(RaceDiary.Gained).FirstOrDefault();
        if (climber != null && RaceDiary.Gained(climber) >= 1)
        {
            int g = RaceDiary.Gained(climber), gp = RaceDiary.GridPos(climber.CarIdx);
            cards.Add(("MOST PLACES GAINED", Gain, "+" + g, Short(climber.Name) + $"  ·  P{gp} TO P{climber.PositionInClass}", climber, false));
        }
        else if (winner != null && rows.Count > 1)
            cards.Add(("WINNING MARGIN", Gold, GapTo(rows[1], winner), Short(winner.Name), winner, true));
        var withInc = rows.Where(d => Inc(d) >= 0).ToList();
        if (withInc.Count > 0)
        {
            int min = withInc.Min(Inc);
            var clean = withInc.Where(d => Inc(d) == min).ToList();
            cards.Add(("CLEANEST RACE", CleanBlue, min + "x", string.Join("  ·  ", clean.Take(2).Select(d => Short(d.Name))) + (clean.Count > 2 ? $"  +{clean.Count - 2}" : ""), clean[0], false));
        }
        for (int j = 0; j < cards.Count && j < 3; j++)
        {
            var (lab, tone, big, sub, d, isTime) = cards[j];
            float ck = Ease((t - 0.8 - j * 0.15) / 0.4), ca = a * ck;
            if (ca <= 0.01f) continue;
            float y = Y0 - 34 + j * 222;
            c.Save(); c.Translate((1 - ck) * 120, 0);
            Slab(c, RX, y, RW, 200, SlabC, ca, slant: 16, edge: tone, edgeW: 10);
            Txt(c, lab, RX + 48, y + 46, Fonts.BarlowCondensed.Bold, 22, tone, ca, Align.Left, 4);
            if (isTime) Txt(c, big, RX + 44, y + 138, Timing, 58, Ink, ca, Align.Left, 0, maxW: RW - 150, shrink: true);
            else Txt(c, big, RX + 44, y + 138, Fonts.BarlowCondensed.Black, 100, Ink, ca);
            Txt(c, sub, RX + 48, y + 178, Fonts.BarlowCondensed.Bold, 24, Dim, ca, Align.Left, 1, maxW: RW - 80);
            if (d != null && Maker(d.CarBrand) is { } mk) LogoBox(c, mk, new SKRect(RX + RW - 105, y + 42, RX + RW - 35, y + 98), ca * 0.9f);
            c.Restore();
        }
    }

    // ================================================================== drawing helpers
    enum Align { Left, Center, Right }

    SKTypeface Timing => timing ??= LoadTiming();
    static SKTypeface LoadTiming()
    {
        try { var p = Assets.Resolve("fonts", "RaycerRay-F125-Font-Timing-Bold.ttf"); if (File.Exists(p)) return SKTypeface.FromFile(p) ?? Fonts.BarlowCondensed.Bold; } catch { }
        return Fonts.BarlowCondensed.Bold;
    }

    /// <summary>Text on the design grid. Returns the drawn width. middle = y is the cap-height centre.
    /// maxW + shrink = scale the size down to fit, otherwise ellipsise.</summary>
    static float Txt(SKCanvas c, string s, float x, float y, SKTypeface tf, float size, SKColor col, float a,
                     Align al = Align.Left, float tracking = 0, bool middle = false, float maxW = 0, bool shrink = false)
    {
        if (string.IsNullOrEmpty(s) || a <= 0.003f) return 0;
        using var p = new SKPaint { IsAntialias = true, Typeface = tf, TextSize = size, Color = col.WithAlpha((byte)(col.Alpha * Math.Clamp(a, 0, 1))) };
        float Width() => p.MeasureText(s) + tracking * Math.Max(0, s.Length - 1);
        if (maxW > 0 && Width() > maxW)
        {
            if (shrink) { p.TextSize = Math.Max(8, size * maxW / Width()); }
            else { while (s.Length > 2 && Width() > maxW) s = s[..^2].TrimEnd() + "…"; }
        }
        float wd = Width();
        if (middle) { var r = new SKRect(); p.MeasureText("H", ref r); y += -r.Top / 2; }
        float x0 = al == Align.Right ? x - wd : al == Align.Center ? x - wd / 2 : x;
        if (tracking == 0) c.DrawText(s, x0, y, p);
        else foreach (var ch in s) { var cs = ch.ToString(); c.DrawText(cs, x0, y, p); x0 += p.MeasureText(cs) + tracking; }
        return wd;
    }

    /// <summary>A slanted slab: offset soft shadow, fill, optional class-colour top bar or left edge, white keyline on top.</summary>
    static void Slab(SKCanvas c, float x, float y, float w, float h, SKColor fill, float a, float slant = 18,
                     bool keyline = true, bool shadow = true, SKColor? top = null, SKColor? edge = null, float edgeW = 8)
    {
        if (a <= 0.003f) return;
        using var path = new SKPath();
        path.MoveTo(x + slant, y); path.LineTo(x + w, y); path.LineTo(x + w - slant, y + h); path.LineTo(x, y + h); path.Close();
        if (shadow)
        {
            c.Save(); c.Translate(10, 10);
            using var sp = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(150 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) };
            c.DrawPath(path, sp);
            c.Restore();
        }
        using (var fp = new SKPaint { IsAntialias = true, Color = fill.WithAlpha((byte)(fill.Alpha * Math.Clamp(a, 0, 1))) }) c.DrawPath(path, fp);
        float k = slant / Math.Max(1, h);
        if (top is SKColor tc)
        {
            using var tb = new SKPath(); const float th = 10;
            tb.MoveTo(x + slant, y); tb.LineTo(x + w, y); tb.LineTo(x + w - th * k, y + th); tb.LineTo(x + slant - th * k, y + th); tb.Close();
            using var tp = new SKPaint { IsAntialias = true, Color = tc.WithAlpha((byte)(255 * a)) }; c.DrawPath(tb, tp);
        }
        if (edge is SKColor ec)
        {
            using var eb = new SKPath();
            eb.MoveTo(x + slant, y); eb.LineTo(x + slant + edgeW, y); eb.LineTo(x + edgeW, y + h); eb.LineTo(x, y + h); eb.Close();
            using var ep = new SKPaint { IsAntialias = true, Color = ec.WithAlpha((byte)(255 * a)) }; c.DrawPath(eb, ep);
        }
        if (keyline && top == null)
            using (var kp = new SKPaint { IsAntialias = true, StrokeWidth = 1.5f, Color = new SKColor(255, 255, 255, (byte)(70 * a)) })
                c.DrawLine(x + slant, y + 0.75f, x + w, y + 0.75f, kp);
    }

    /// <summary>A quick white flash across a slab the moment it lands.</summary>
    static void Flash(SKCanvas c, float x, float y, float w, float h, float slant, double since, float a)
    {
        if (since < 0 || since > 0.35) return;
        float f = (float)(1 - since / 0.35);
        using var path = new SKPath();
        path.MoveTo(x + slant, y); path.LineTo(x + w, y); path.LineTo(x + w - slant, y + h); path.LineTo(x, y + h); path.Close();
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(70 * f * a)), BlendMode = SKBlendMode.Plus };
        c.DrawPath(path, p);
    }

    static void Checker(SKCanvas c, float x0, float y0, float w, float h, float sq, float slant, float a, float fadeFrom, double drift)
    {
        float off = (float)(drift % (sq * 2));
        int n = (int)(w / sq) + 4, m = (int)(h / sq) + 1;
        using var p = new SKPaint { IsAntialias = true };
        using var path = new SKPath();
        for (int j = 0; j < m; j++)
            for (int i = -2; i < n; i++)
            {
                if (((i + j) & 1) != 0) continue;
                float X = x0 + i * sq + j * sq * slant - off;
                float fx = Math.Clamp((X - x0) / w, 0, 1);
                float al = fadeFrom + (1 - fadeFrom) * fx;
                p.Color = new SKColor(236, 238, 244, (byte)(255 * al * a));
                path.Reset();
                path.MoveTo(X, y0 + j * sq); path.LineTo(X + sq, y0 + j * sq); path.LineTo(X + sq + sq * slant, y0 + (j + 1) * sq); path.LineTo(X + sq * slant, y0 + (j + 1) * sq); path.Close();
                c.DrawPath(path, p);
            }
    }

    static void Line(SKCanvas c, float x0, float y, float x1, float a)
    {
        using var p = new SKPaint { IsAntialias = true, StrokeWidth = 1.5f, Color = new SKColor(255, 255, 255, (byte)(40 * a)) };
        c.DrawLine(x0, y, x1, y, p);
    }

    static void YouChip(SKCanvas c, float x, float y, float a, float sc = 1f)
    {
        Slab(c, x, y, 74 * sc, 38 * sc, Ink, a, slant: 8 * sc, keyline: false, shadow: false);
        Txt(c, "YOU", x + 37 * sc, y + 19 * sc, Fonts.BarlowCondensed.Black, 26 * sc, Bg, a, Align.Center, 2, middle: true);
    }

    static void Delta(SKCanvas c, float x, float mid, int n, float a, float k)
    {
        if (n == 0) { Txt(c, "–", x + 14, mid, Fonts.BarlowCondensed.Bold, 26 * k, Dim, a, Align.Left, 0, middle: true); return; }
        var col = n > 0 ? Gain : Loss;
        float cx = x + 12, s = 8 * k;
        using (var path = new SKPath())
        {
            if (n > 0) { path.MoveTo(cx - s, mid + s * 0.75f); path.LineTo(cx + s, mid + s * 0.75f); path.LineTo(cx, mid - s); }
            else { path.MoveTo(cx - s, mid - s * 0.75f); path.LineTo(cx + s, mid - s * 0.75f); path.LineTo(cx, mid + s); }
            path.Close();
            using var p = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * a)) }; c.DrawPath(path, p);
        }
        Txt(c, Math.Abs(n).ToString(CultureInfo.InvariantCulture), x + 26, mid, Fonts.BarlowCondensed.Bold, 28 * k, col, a, Align.Left, 0, middle: true);
    }

    // ================================================================== data helpers
    static List<Driver> Sorted(DriverClass cl) => cl.Drivers.OrderBy(d => d.PositionInClass <= 0 ? 999 : d.PositionInClass).ToList();
    static int Inc(Driver d) => RaceDiary.IncOf(d);

    /// <summary>Gap from <paramref name="d"/> back to the class winner (or the car ahead given).</summary>
    static string GapTo(Driver d, Driver to)
    {
        int ld = d.LapDifference - to.LapDifference;
        if (ld > 0) return $"+{ld} LAP{(ld == 1 ? "" : "S")}";
        if (d.GapToLeaderSeconds is double g && to.GapToLeaderSeconds is double g0 && g - g0 > 0) return "+" + (g - g0).ToString("0.000", CultureInfo.InvariantCulture);
        if (d.GapToLeaderSeconds is double g1 && g1 > 0 && to.GapToLeaderSeconds is null) return "+" + g1.ToString("0.000", CultureInfo.InvariantCulture);
        return "-";
    }

    static (float Time, int CarIdx, string Name, int Lap)? FastestOf(DriverClass cl)
    {
        if (RaceDiary.Fastest(cl.Name) is { } f && f.Time > 0) return f;
        var d = cl.Drivers.Where(x => x.BestLapTime > 0).OrderBy(x => x.BestLapTime).FirstOrDefault();
        return d == null ? null : (d.BestLapTime, d.CarIdx, d.Name, 0);
    }

    static (string First, string Last) SplitName(string? name)
    {
        var parts = (name ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ("", "");
        if (parts.Length == 1) return ("", parts[0].ToUpperInvariant());
        return (string.Join(' ', parts[..^1]).ToUpperInvariant(), parts[^1].ToUpperInvariant());
    }

    static string Short(string? name)
    {
        var (f, l) = SplitName(name);
        return string.IsNullOrEmpty(f) ? l : f[..1] + ". " + l;
    }

    // ================================================================== bitmaps
    SKBitmap? Bmp(params string[] parts)
    {
        string key = string.Join("/", parts);
        if (bmps.TryGetValue(key, out var b)) return b;
        try { var p = Assets.Resolve(parts); if (File.Exists(p)) { using var st = File.OpenRead(p); b = Assets.Decode(st); } } catch { b = null; }
        return bmps[key] = b;
    }
    SKBitmap? Maker(string? brand) { var slug = ManufacturerBrand.Resolve(brand ?? ""); return slug == null ? null : Bmp("logos", "manufacturers", slug + ".png"); }
    SKBitmap? ClassLogoBmp(string? cls) { var slug = ClassLogo.Resolve(cls); return slug == null ? null : Bmp("logos", "classes", slug + ".png"); }

    /// <summary>One-colour marks (Mercedes star, Acura, Cadillac script) are drawn white like a TV
    /// graphic; crests with their own colours (Porsche, Ferrari, BMW) keep them.</summary>
    bool IsMono(SKBitmap b)
    {
        if (mono.TryGetValue(b, out var m)) return m;
        double sat = 0, sum = 0, sum2 = 0; int n = 0;
        int sx = Math.Max(1, b.Width / 48), sy = Math.Max(1, b.Height / 48);
        for (int y = 0; y < b.Height; y += sy)
            for (int x = 0; x < b.Width; x += sx)
            {
                var px = b.GetPixel(x, y);
                if (px.Alpha < 128) continue;
                int mx = Math.Max(px.Red, Math.Max(px.Green, px.Blue)), mn = Math.Min(px.Red, Math.Min(px.Green, px.Blue));
                double l = (px.Red + px.Green + px.Blue) / 3.0;
                sat += mx - mn; sum += l; sum2 += l * l; n++;
            }
        bool r = n > 0 && sat / n < 40 && Math.Sqrt(Math.Max(0, sum2 / n - (sum / n) * (sum / n))) < 50;
        return mono[b] = r;
    }

    void LogoBox(SKCanvas c, SKBitmap b, SKRect box, float a)
    {
        float s = Math.Min(box.Width / b.Width, box.Height / b.Height), w = b.Width * s, h = b.Height * s;
        DrawLogo(c, b, new SKRect(box.MidX - w / 2, box.MidY - h / 2, box.MidX + w / 2, box.MidY + h / 2), a, IsMono(b));
    }

    /// <summary>Logo from x, centred on midY, at most maxW x maxH. Returns its width.</summary>
    float LogoMid(SKCanvas c, SKBitmap b, float x, float midY, float maxW, float maxH, float a, bool white)
    {
        float s = Math.Min(maxW / b.Width, maxH / b.Height), w = b.Width * s, h = b.Height * s;
        DrawLogo(c, b, new SKRect(x, midY - h / 2, x + w, midY + h / 2), a, white);
        return w;
    }

    static void DrawLogo(SKCanvas c, SKBitmap b, SKRect dst, float a, bool white)
    {
        if (a <= 0.003f) return;
        using var cf = white ? SKColorFilter.CreateBlendMode(Ink, SKBlendMode.SrcIn) : null;
        using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, ColorFilter = cf, Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(a, 0, 1))) };
        c.DrawBitmap(b, dst, lp);
    }

    static float Ease(double x) { float v = (float)Math.Clamp(x, 0, 1); return 1 - (1 - v) * (1 - v) * (1 - v); }
    static float OutBack(double x) { float v = (float)Math.Clamp(x, 0, 1); const float c1 = 1.4f, c3 = c1 + 1; return 1 + c3 * (float)Math.Pow(v - 1, 3) + c1 * (float)Math.Pow(v - 1, 2); }
    static SKColor ParseHex(string? hex, SKColor fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        var h = hex.TrimStart('#');
        return h.Length == 6 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v) : fallback;
    }

    // ================================================================== result page, pre-race language (#486)
    static readonly SKColor PGlass = ParseHex(BroadcastPalette.TowerGlassBase, SKColors.Black).WithAlpha(214),
        PHair = ParseHex(BroadcastPalette.BorderHairline, SKColors.Gray).WithAlpha(110),
        PGold = ParseHex(BroadcastPalette.Leader, Gold), PText = ParseHex(BroadcastPalette.TextPrimary, Ink);
    readonly GridHeroBoard board = new() { Results = true };
    readonly List<DriverClass> liveCls = new();
    readonly SKPaint pp = new() { IsAntialias = true };

    /// <summary>The pre-race board card (glass, hairline, the gold top accent) with the pre-race header (gold tracked
    /// eyebrow, Cormorant title), and the class page from the starting grid showing the result. Leaves the top of the
    /// screen to the session bar (battle look + ticker).</summary>
    void ResultPage(SKCanvas c, float H, RaceSnapshot s, int ci, double t, float a)
    {
        var body = Frame(c, H, s, "FINAL RESULT", "FINAL CLASSIFICATION", a);
        // the class page, live positions (they can still settle after the flag)
        liveCls.Clear();
        foreach (var x in OutroDirector.Classes) liveCls.Add(Live(s, x));
        board.DrawClassPage(c, body, liveCls, s.PlayerCarIdx, a, ci, t, OutroDirector.Current?.Length ?? 9);
    }

    /// <summary>The opener of the outro in the pre-race language: the same card and header, each class winner on a gold
    /// hero card (top class first), the class name over it in its colour.</summary>
    void WinnersPage(SKCanvas c, float H, RaceSnapshot s, double t, float a)
    {
        var body = Frame(c, H, s, "CHEQUERED FLAG", "RACE WINNERS", a);
        var cls = OutroDirector.Classes;
        int n = Math.Min(4, cls.Count);
        if (n == 0) return;
        const float gap = 16f, lab = 46f;
        float cw = (body.Width - gap * (n - 1)) / n, ch = Math.Min(400f, body.Height - lab - 20);
        float top = body.Top + (body.Height - ch - lab) * 0.42f;
        for (int k = 0; k < n; k++)
        {
            var dc = Live(s, cls[cls.Count - 1 - k]);          // top class on the left
            Driver? w = null;
            foreach (var d in dc.Drivers) if (w == null || (d.PositionInClass > 0 && d.PositionInClass < (w.PositionInClass > 0 ? w.PositionInClass : int.MaxValue))) w = d;
            if (w == null) continue;
            float x = body.Left + k * (cw + gap);
            float la = Ease((t - 0.2 - k * 0.18) / 0.35) * a;
            var col = ParseHex(w.ClassColor, Gold);
            using (var lp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * la)), Typeface = Fonts.Inter.Bold, TextSize = 20f })
            {
                string name = global::ApexHUD.Native.Common.ClassLogo.DisplayName(dc.Name).ToUpperInvariant();
                TextTracked.Draw(c, lp, name, x + 4, top + 26, 3f);
                pp.Reset(); pp.Color = col.WithAlpha((byte)(200 * la));
                c.DrawRect(new SKRect(x + 4, top + 34, x + 4 + Math.Min(cw - 8, lp.MeasureText(name) + name.Length * 3f), top + 36), pp);
            }
            board.DrawWinner(c, new SKRect(x, top + lab, x + cw, top + lab + ch), w, k, w.CarIdx == s.PlayerCarIdx, a, t);
        }
    }

    /// <summary>The pre-race board card (glass, hairline, gold top accent) + header; returns the body rectangle.</summary>
    SKRect Frame(SKCanvas c, float H, RaceSnapshot s, string eyebrow, string title, float a)
    {
        const float hdr = 86f, padX = 26f, padY = 16f, foot = 18f;
        var card = new SKRect(200, Math.Max(164f, H * 0.15f), DW - 200, H - 36);   // under the bar + ticker
        pp.Reset(); pp.IsAntialias = true; pp.Color = PGlass.WithAlpha((byte)(PGlass.Alpha * a)); c.DrawRect(card, pp);
        Theme.SkinNow(c, card, ThemeSurface.Splash, a);   // #601: the broadcast theme (nothing on Default)
        pp.Reset(); pp.Style = SKPaintStyle.Stroke; pp.StrokeWidth = 1; pp.Color = PHair.WithAlpha((byte)(PHair.Alpha * a)); c.DrawRect(card, pp);
        pp.Reset();
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(card.Left, 0), new SKPoint(card.Right, 0),
                   new[] { PGold.WithAlpha(0), PGold.WithAlpha((byte)(128 * a)), PGold.WithAlpha((byte)(255 * a)), PGold.WithAlpha((byte)(128 * a)), PGold.WithAlpha(0) },
                   new[] { 0f, 0.2f, 0.5f, 0.8f, 1f }, SKShaderTileMode.Clamp))
        { pp.Shader = sh; c.DrawRect(new SKRect(card.Left, card.Top, card.Right, card.Top + 1.5f), pp); }
        pp.Shader = null;
        // header: FINAL RESULT · TRACK (gold, tracked), FINAL CLASSIFICATION (Cormorant), PROVISIONAL / OFFICIAL
        pp.Reset(); pp.Color = new SKColor(255, 255, 255, (byte)(5 * a)); c.DrawRect(new SKRect(card.Left, card.Top, card.Right, card.Top + hdr), pp);
        pp.Color = PHair.WithAlpha((byte)(80 * a)); c.DrawRect(new SKRect(card.Left, card.Top + hdr, card.Right, card.Top + hdr + 1), pp);
        string trk = (s.TrackDisplayName ?? "").ToUpperInvariant();
        using (var eb = new SKPaint { IsAntialias = true, Color = PGold.WithAlpha((byte)(255 * a)), Typeface = Fonts.Inter.SemiBold, TextSize = 14f })
            TextTracked.Draw(c, eb, trk.Length > 0 ? eyebrow + "  ·  " + trk : eyebrow, card.Left + 28, card.Top + 28, 3.6f);
        using (var tp = new SKPaint { IsAntialias = true, Color = PText.WithAlpha((byte)(255 * a)), Typeface = Fonts.CormorantGaramond.Bold, TextSize = 42f,
                                      ImageFilter = SKImageFilter.CreateDropShadow(0, 2, 6, 6, new SKColor(0, 0, 0, (byte)(150 * a))) })
            c.DrawText(title, card.Left + 28, card.Top + 72, tp);
        using (var ch = new SKPaint { IsAntialias = true, Color = (s.ResultsOfficial ? GoGreen : PGold).WithAlpha((byte)(255 * a)), Typeface = Fonts.Inter.SemiBold, TextSize = 14f })
        {
            string chip = s.ResultsOfficial ? "OFFICIAL" : "PROVISIONAL";
            TextTracked.Draw(c, ch, chip, card.Right - 28 - ch.MeasureText(chip) - chip.Length * 3.6f, card.Top + 50, 3.6f);
        }
        return new SKRect(card.Left + padX, card.Top + hdr + padY, card.Right - padX, card.Bottom - foot);
    }

    public void Dispose()
    {
        board.Dispose(); pp.Dispose();
        foreach (var b in bmps.Values) b?.Dispose(); bmps.Clear(); mono.Clear();
        carbon?.Dispose(); grain?.Dispose(); carbon = grain = null;
    }
}
