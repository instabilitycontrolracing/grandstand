// #339 (2026-10-27): the MINI story cards for battles, drawn by the real StoryCards.DrawMini, over a battle frame drawn
// by the real renderers laid out as OverlayCompositor does it: session bar in battle mode (full width) + watch + standings
// ticker, the battle card (bottom centre), the battle map (top right), and the timing tower in its lane for reference
// (on air it steps aside during a battle). The mini card places itself from VpLayout, as on air.
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

static class MiniPreview
{
    const int W = 1920, H = 1080;

    public static void Run(string outDir, RaceSnapshot snap, List<(string Name, StoryKind Kind, string Weather, Action? Setup)> cards, SKSurface surf, bool alloc)
    {
        Directory.CreateDirectory(outDir);
        var c = surf.Canvas;
        var tower = new TimingTowerRenderer();
        var bar = new SessionBarRenderer();
        var watch = new WatchRenderer();
        var ticker = new StandingsTickerRenderer();
        var battle = new BattleCardRenderer();
        var map = new BattleMap();
        var radio = new RadioPopupRenderer();
        var clock = new DateTime(2026, 10, 27, 15, 20, 0);

        // the battle: you and the cars either side of you in class, nose to tail
        var cls = snap.ClassLeaderboard.First(cl => cl.Drivers.Any(d => d.CarIdx == snap.PlayerCarIdx));
        int pi = cls.Drivers.ToList().FindIndex(d => d.CarIdx == snap.PlayerCarIdx);
        var ahead = cls.Drivers[Math.Max(0, pi - 1)]; var behind = cls.Drivers[Math.Min(cls.Drivers.Count - 1, pi + 1)];
        var cars = new List<int> { ahead.CarIdx, snap.PlayerCarIdx, behind.CarIdx };
        float u = 1f / 212f;
        RaceSnapshot BattleSnap(int f)
        {
            float pm = 0.40f + f * 0.0002f;
            var list = cls.Drivers.Select(d =>
                d.CarIdx == ahead.CarIdx ? d with { LapDistPct = pm + 0.8f * u } :
                d.CarIdx == snap.PlayerCarIdx ? d with { LapDistPct = pm, IntervalSeconds = 0.8 } :
                d.CarIdx == behind.CarIdx ? d with { LapDistPct = pm - 0.5f * u, IntervalSeconds = 0.5 } : d).ToList();
            var me = list.First(d => d.CarIdx == snap.PlayerCarIdx);
            var classes = snap.ClassLeaderboard.Select(cl => cl == cls ? new DriverClass(cl.Name, cl.Color, list) : cl).ToList();
            return snap with { ClassLeaderboard = classes, Player = me, CameraTarget = me };
        }
        // warm the battle card + map into their settled state
        double t = 1000;
        RaceSnapshot bs = snap;
        using (var nd = new SKNoDrawCanvas(W, H))
            for (int f = 0; f < 600; f++, t += 16.7) { bs = BattleSnap(f); Frame(nd, bs, t, false); }
        double battleT = t;

        void Frame(SKCanvas cv, RaceSnapshot s, double tt, bool withTower)
        {
            PresentationState.IsBattleActive = true;
            if (withTower) Mock.Frame(cv, W, H, s);
            if (withTower) tower.Draw(cv, 16f, 18f, H - 34f, s, tt);
            float wl = (W - 1280f) / 2f, wt = H - 348f;
            battle.Draw(cv, wl + 140f, wt + 30f, s, tt);
            map.Draw(cv, W - 24f - BattleMap.W, 172f, s, cars, true, tt);
            VpLayout.BattleMapVisible = true;
            VpLayout.BattleMap = new SKRect(W - 24f - BattleMap.W, 172f, W - 24f, 172f + BattleMap.H);
            ticker.Draw(cv, 0f, 110f, W, s, tt);
            VpLayout.TickerVisible = true;
            VpLayout.Ticker = new SKRect(0, 110, W, 110 + StandingsTickerRenderer.BarHeight);
            bar.Draw(cv, 0, 18f, 1f, s, clock, tt, W);
            watch.Draw(cv, W / 2f, 18f + SessionBarRenderer.BarHeight / 2f, 64f, clock.AddMilliseconds(tt));
            VpLayout.SessionBar = new SKRect(0, 18, W, 18 + SessionBarRenderer.BarHeight);
            VpLayout.SessionBarVisible = true;
            VpLayout.BattleStyle = true;
            VpLayout.RadioVisible = false;
        }

        var shots = new List<(string Label, SKImage Img)>();
        var allocRows = new List<string> { "card,mini_bytes_per_frame,full_column_bytes_per_frame" };
        void Save(string name, string label)
        {
            var img = surf.Snapshot();
            using (var data = img.Encode(SKEncodedImageFormat.Png, 90)) File.WriteAllBytes(Path.Combine(outDir, name + ".png"), data.ToArray());
            shots.Add((label, img));
        }
        int n = 0;
        foreach (var card in cards)
        {
            card.Setup?.Invoke();
            var sc = new StoryCards();
            double t0 = battleT;
            StoryDirector.ForceMini(card.Kind, t0, card.Weather);
            using (var nd = new SKNoDrawCanvas(W, H)) for (double q = t0; q < t0 + 2500; q += 50) sc.DrawMini(nd, W, H, bs, q);
            Frame(c, bs, battleT, true);
            sc.DrawMini(c, W, H, bs, t0 + 2500);
            var mr = StoryCards.MiniRect(W);
            Mock.Overlay(c, W, H, $"MINI CARD (battle)  ·  {card.Name}");
            n++;
            Save($"{n:00}_{card.Name}", $"{n}. {card.Name}");
            if (alloc)
            {
                StoryDirector.ForceMini(card.Kind, t0, card.Weather);
                using var nd = new SKNoDrawCanvas(W, H);
                for (double q = t0; q < t0 + 3000; q += 50) sc.DrawMini(nd, W, H, bs, q);
                long b0 = GC.GetAllocatedBytesForCurrentThread(); int k = 0;
                for (double q = t0 + 3000; q < t0 + 4000; q += 50, k++) sc.DrawMini(nd, W, H, bs, q);
                long mini = (GC.GetAllocatedBytesForCurrentThread() - b0) / k;
                // the same card in full (tall column), for comparison
                StoryCards.Layout = CardLayout.Column;
                var fc = new StoryCards();
                StoryDirector.Force(card.Kind, 0, card.Weather);
                for (double q = 0; q < 3000; q += 50) fc.Draw(nd, W, H, snap, q);
                b0 = GC.GetAllocatedBytesForCurrentThread(); k = 0;
                for (double q = 3000; q < 4000; q += 50, k++) fc.Draw(nd, W, H, snap, q);
                long full = (GC.GetAllocatedBytesForCurrentThread() - b0) / k;
                StoryDirector.Force(StoryKind.None, 0);
                StoryCards.Layout = CardLayout.Mixed;
                StoryDirector.ForceMini(StoryKind.None, 0);
                allocRows.Add($"{card.Name},{mini},{full}");
                Console.WriteLine($"  {card.Name,-13} mini {mini,7} B/frame   full column {full,7} B/frame   mini at {mr}");
            }
            RaceSim.UndoDq();
        }

        // the motion: Race So Far sliding in (0.2 s), held, and its colour bar wiping it away (6.6 s)
        {
            var sc = new StoryCards();
            double t0 = battleT;
            foreach (var (ms, tag) in new[] { (200.0, "in_0.2s"), (6600.0, "out_6.6s") })
            {
                StoryDirector.ForceMini(StoryKind.RaceSoFar, t0);
                using (var nd = new SKNoDrawCanvas(W, H)) for (double q = t0; q < t0 + ms; q += 50) sc.DrawMini(nd, W, H, bs, q);
                Frame(c, bs, battleT, true);
                sc.DrawMini(c, W, H, bs, t0 + ms);
                Mock.Overlay(c, W, H, $"MINI CARD  ·  RaceSoFar  ·  {tag.Replace('_', ' ')}");
                n++;
                Save($"{n:00}_motion_{tag}", $"{n}. RaceSoFar {tag.Replace('_', ' ')}");
            }
        }
        // a team-radio call up at the same time: short lines leave room, a long one makes the mini card wait / step out
        foreach (var (tag, text) in new[] { ("radio_short", "Gap is 0.8. Keep it tidy."), ("radio_long", "Car behind has a run out of the last corner on most laps. Cover the inside into turn one, he has no lift and coast to give.") })
        {
            var sc = new StoryCards();
            double t0 = battleT + 20000 * (tag == "radio_long" ? 2 : 1);
            RadioFeed.Push("ENGINEER", text);
            using (var nd = new SKNoDrawCanvas(W, H))
                for (double q = t0 - 1500; q < t0 + 1500; q += 50)
                {
                    float rxx = W - 24f, byy = H * 0.665f;
                    nd.Save(); nd.Translate(rxx, byy); nd.Scale(0.9f); nd.Translate(-rxx, -byy); radio.Draw(nd, rxx, byy, bs, q); nd.Restore();
                }
            StoryDirector.ForceMini(StoryKind.Cleanest, t0);
            using (var nd = new SKNoDrawCanvas(W, H)) for (double q = t0; q < t0 + 2500; q += 50) sc.DrawMini(nd, W, H, bs, q);
            Mock.Frame(c, W, H, bs);
            tower.Draw(c, 16f, 18f, H - 34f, bs, battleT);
            float rx = W - 24f, by = H * 0.665f;
            c.Save(); c.Translate(rx, by); c.Scale(0.9f); c.Translate(-rx, -by);
            radio.Draw(c, rx, by, bs, t0 + 2500);
            c.Restore();
            var radioR = new SKRect(rx - RadioPopupRenderer.Width * 0.9f, by - radio.LastHeight * 0.9f, rx, by);
            Frame(c, bs, battleT, false);
            var mr = StoryCards.MiniRect(W); mr.Inflate(8, 8);
            bool clash = radio.LastHeight > 1 && mr.IntersectsWith(radioR);
            if (!clash) sc.DrawMini(c, W, H, bs, t0 + 2500);
            Mock.Overlay(c, W, H, clash ? "MINI CARD  ·  long radio call up: the mini card waits / steps out" : "MINI CARD  ·  short radio call up: both fit");
            n++;
            Save($"{n:00}_{tag}", $"{n}. {(clash ? "long radio: mini waits" : "short radio: both fit")}");
        }

        Sheet(Path.Combine(outDir, "contact_mini_cards.png"), "MINI STORY CARDS in a battle (#339 preview, miniCardsInBattle OFF on air)", shots);
        foreach (var s in shots) s.Img.Dispose();
        if (alloc) File.WriteAllLines(Path.Combine(outDir, "mini_alloc.csv"), allocRows);
        Console.WriteLine($"mini: {shots.Count} frames -> {outDir}");
    }

    static void Sheet(string path, string title, List<(string Label, SKImage Img)> shots)
    {
        const int Cols = 3, TW = 640, TH = 360, Pad = 12, Head = 64, Cap = 30;
        int rows = (shots.Count + Cols - 1) / Cols;
        int w = Cols * TW + (Cols + 1) * Pad, h = Head + rows * (TH + Cap + Pad) + Pad;
        using var s = SKSurface.Create(new SKImageInfo(w, h));
        var c = s.Canvas; c.Clear(new SKColor(16, 18, 24));
        using var tp = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 34, Typeface = Fonts.BarlowCondensed.Bold };
        c.DrawText(title, Pad, 44, tp);
        using var lp = new SKPaint { IsAntialias = true, Color = new SKColor(200, 206, 216), TextSize = 20, Typeface = Fonts.Inter.SemiBold };
        using var ip = new SKPaint { FilterQuality = SKFilterQuality.High };
        for (int i = 0; i < shots.Count; i++)
        {
            float x = Pad + (i % Cols) * (TW + Pad), y = Head + (i / Cols) * (TH + Cap + Pad);
            c.DrawImage(shots[i].Img, new SKRect(x, y, x + TW, y + TH), ip);
            c.DrawText(shots[i].Label, x + 2, y + TH + 22, lp);
        }
        using var img = s.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
    }
}
