// #517 (2026-10-29): the split story card around the team radio card. Plays a regular card in its full layout, brings a
// real radio line in partway (the REAL RadioPopupRenderer, drawn underneath at the compositor's spot and 90 % scale), and
// saves frames before / during / after the glide into the split layout and back; then a card that starts with the radio
// already up (straight in split) and a radio blip shorter than the debounce (the card must not move).
// Usage: storycard_preview OUT_DIR --radio [--only RaceSoFar]
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

static class RadioPreview
{
    const int W = 1920, H = 1080;

    public static void Run(string outDir, RaceSnapshot snap, SKSurface surf, StoryKind kind, string name)
    {
        var c = surf.Canvas;
        string dir = Path.Combine(outDir, name);
        Directory.CreateDirectory(dir);
        StoryCards.Layout = CardLayout.Mixed;
        StoryDirector.CardMs = 12000;
        var sc = new StoryCards();
        var radio = new RadioPopupRenderer();
        var log = new List<string>();

        void Frame(double now, bool draw, string? file, string label)
        {
            // as the compositor: the radio tick first (StoryCards.RadioOn), the radio card underneath, the story card on top
            StoryCards.RadioOn = radio.IsActive(now);
            SKCanvas cv = draw ? c : new SKNoDrawCanvas(W, H);
            if (draw) Mock.Frame(c, W, H, snap);
            float rx = W - 24f, by = H * 0.665f;
            cv.Save(); cv.Translate(rx, by); cv.Scale(0.9f); cv.Translate(-rx, -by);
            radio.Draw(cv, rx, by, snap, now);
            cv.Restore();
            float rh = radio.LastHeight * 0.9f;
            VpLayout.RadioVisible = rh > 1f && radio.IsActive(now);
            VpLayout.Radio = new SKRect(rx - RadioPopupRenderer.Width * 0.9f, by - rh, rx, by);
            sc.Draw(cv, W, H, snap, now);
            if (!draw) { cv.Dispose(); return; }
            Mock.Overlay(c, W, H, $"#517  {name}  {label}  (shown: {sc.ShownLayout}, radio {(StoryCards.RadioOn ? "on" : "off")})");
            using var img = surf.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(dir, file + ".png"), data.ToArray());
            log.Add($"{file}: t={now / 1000.0:0.000}s shown={sc.ShownLayout} radio={StoryCards.RadioOn}");
        }

        // A: the card is up in its full layout, then the radio comes in and goes away again
        double t0 = 100000;
        StoryDirector.Force(kind, t0, "");
        double radioAt = t0 + 3000, radioOff = double.NaN;
        var shots = new SortedDictionary<double, (string File, string Label)>
        {
            [t0 + 2500] = ("a1_full_before", "full, no radio"),
            [radioAt + 200] = ("a2_radio_in_debounce", "radio up 0.2 s (debounce)"),
            [radioAt + 300 + 120] = ("a3_glide_30", "glide in, ~30 %"),
            [radioAt + 300 + 210] = ("a4_glide_50", "glide in, ~50 %"),
            [radioAt + 300 + 320] = ("a5_glide_75", "glide in, ~75 %"),
            [radioAt + 1500] = ("a6_split", "split, radio up"),
        };
        bool pushed = false;
        for (double now = t0; now < t0 + 12500; now += 1000.0 / 60)
        {
            if (!pushed && now >= radioAt) { RadioFeed.Push("ENGINEER", "Box this lap, box this lap."); pushed = true; }
            Frame(now, false, null, "");
            if (pushed && double.IsNaN(radioOff) && !StoryCards.RadioOn)
            {
                radioOff = now;
                shots[now + 200] = ("a7_radio_gone_debounce", "radio gone 0.2 s (debounce)");
                shots[now + 300 + 210] = ("a8_glide_back_50", "glide back, ~50 %");
                shots[now + 1200] = ("a9_full_after", "full again");
            }
            foreach (var k in shots.Keys.Where(k => k > now - 1000.0 / 60 && k <= now).ToList())
            { Frame(now, true, shots[k].File, shots[k].Label); shots.Remove(k); }
        }

        // B: the radio is already up when the next card starts -> straight into split, its own entrance
        double t1 = 300000;
        RadioFeed.Push("ENGINEER", "Gap behind is one point two, he is closing.");
        Frame(t1 - 1500, false, null, "");
        for (double now = t1 - 1500; now < t1 - 1000.0 / 60; now += 1000.0 / 60) Frame(now, false, null, "");
        StoryDirector.Force(kind, t1, "");
        for (double now = t1; now < t1 + 2500; now += 1000.0 / 60)
        {
            bool shot = Math.Abs(now - (t1 + 300)) < 8 || Math.Abs(now - (t1 + 2400)) < 8;
            Frame(now, shot, now < t1 + 1000 ? "b1_start_split_entering" : "b2_start_split_in", now < t1 + 1000 ? "starts with radio up (entering)" : "starts with radio up");
        }

        // C: a 200 ms radio blip (shorter than the debounce) - the card must not move at all
        double t2 = 500000;
        StoryDirector.Force(kind, t2, "");
        var lin = typeof(StoryCards).GetField("splitLin", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        double maxLin = 0;
        using (var nd = new SKNoDrawCanvas(W, H))
            for (double now = t2; now < t2 + 6000; now += 1000.0 / 60)
            {
                StoryCards.RadioOn = now >= t2 + 3000 && now < t2 + 3200;
                VpLayout.RadioVisible = false;
                sc.Draw(nd, W, H, snap, now);
                maxLin = Math.Max(maxLin, (double)lin.GetValue(sc)!);
            }
        log.Add($"c_blip_200ms: max split amount {maxLin:0.000} (0 = the card never moved)");

        File.WriteAllLines(Path.Combine(dir, "frames.txt"), log);
        Console.WriteLine($"radio ({name}): {log.Count} frames -> {dir}");
    }
}
