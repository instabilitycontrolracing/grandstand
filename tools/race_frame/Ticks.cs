using System.Diagnostics;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;

/// <summary>--ticks: the per-call state work OverlayCompositor.FrameNeeded does (the host calls it every loop pass, i.e.
/// every ~2 ms while something animates), minus the parts that live in Grandstand.exe itself (control state, OBS scene,
/// intro watch, the App snapshot override). CPU µs and managed bytes per call, overall and per step.</summary>
static class Ticks
{
    public static void Run(RaceSnapshot baseSnap, int calls)
    {
        var lens = new LensRain();
        var wipe = new ReplayWipe();
        var onboard = new OnboardFullRenderer();
        var viewer = new ViewerCardsRenderer();
        var vp = new VisualPackDirector();
        var radio = new RadioPopupRenderer();
        Func<string, double> readVar = _ => 0;
        string[] names = { "PresentationState", "SceneDirector", "lens+wipe", "HighlightLog", "visualpack", "RaceDiary", "TrackTerrain", "TrackSpeeds", "StoryDirector", "onboard.Feed", "Outro", "viewer", "CustomCards", "GlobeIntro" };
        var us = new double[names.Length]; var by = new long[names.Length];
        double now = 30000; var snap = baseSnap;
        int warm = 500;
        for (int n = 0; n < calls + warm; n++)
        {
            if (n % 8 == 0) snap = baseSnap with { CapturedAt = DateTime.UtcNow };   // a new snapshot every ~16 ms like the 60 Hz feed
            bool rec = n >= warm;
            void S(int i, Action a)
            {
                long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
                a();
                if (rec) { us[i] += (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency; by[i] += GC.GetAllocatedBytesForCurrentThread() - a0; }
            }
            S(0, () => PresentationState.Tick(snap));
            S(1, () => SceneDirector.Tick(snap));
            S(2, () => { lens.Tick(snap, now, 1920, 1080); wipe.Tick(snap, now, true); });
            S(3, () => { HighlightLog.Enabled = false; HighlightLog.Note(snap, PresentationState.IsBattleActive); });
            S(4, () =>
            {
                var ob = snap.Onboard;
                var ctx = new VpContext
                {
                    BattleActive = PresentationState.IsBattleActive, Opponent = null, RadioActive = radio.IsActive(now), WipeFired = null,
                    TvCamera = LensRain.IsTvCamera(ob.GroupOnlyName, ob.CamGroupName), CameraKey = ob.CamGroup * 4099 + ob.CamCarIdx,
                    ReadVar = readVar, CameraFree = false,
                };
                vp.Tick(snap, now, ctx);
            });
            S(5, () => RaceDiary.Tick(snap));
            S(6, () => TrackTerrain.Prepare(snap));
            S(7, () => TrackSpeeds.Tick(snap));
            S(8, () => StoryDirector.Tick(snap, now, PresentationState.IsBattleActive));
            S(9, () => onboard.Feed(snap, now));
            S(10, () => OutroDirector.Tick(snap, now));
            S(11, () => viewer.Tick(snap, now));
            S(12, () => CustomCards.Tick(snap, now, StoryDirector.Visible));
            S(13, () => GlobeIntro.Prepare(snap));
            now += 2.0;
        }
        double tu = us.Sum(); long tb = by.Sum();
        Console.WriteLine($"ticks: {calls} FrameNeeded-style calls (2 ms apart, canned field)");
        for (int i = 0; i < names.Length; i++) Console.WriteLine($"  {names[i],-18} {us[i] / calls,7:F2} us/call  {by[i] / (double)calls,8:F1} B/call");
        Console.WriteLine($"  {"TOTAL",-18} {tu / calls,7:F2} us/call  {tb / (double)calls,8:F1} B/call");
    }
}
