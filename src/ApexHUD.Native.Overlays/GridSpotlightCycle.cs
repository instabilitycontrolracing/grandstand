using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Drives the pre-race "hero cycle": a sequence of <see cref="GridSpotlightRenderer"/>
/// cards spotlighting the front-runners of each class (top <see cref="TopPerClass"/>),
/// shown one at a time before the field settles into the full
/// <see cref="StartingGridRenderer"/> board.
///
/// <para>Active only during <see cref="RacePhase.PreRace"/>. The roster is built
/// once on entering the phase; each card holds for <see cref="PerCardMs"/> with a
/// short cross-fade at each edge. When the sequence completes the cycle goes
/// inactive and the compositor lets the full grid board fade in. Stateless w.r.t.
/// the renderers it draws — purely a timing/selection controller.</para>
/// </summary>
public sealed class GridSpotlightCycle : IDisposable
{
    public const double PerCardMs = 3000.0;
    const double FadeMs = 320.0;
    const double ExitFadeMs = 360.0;   // hero card fade-out if the green catches the cycle mid-card
    const int TopPerClass = 3;

    // IMSA-style class order, matching StartingGridRenderer's CLASS_ORDER.
    static readonly string[] ClassOrder =
    {
        "GTP", "LMDh", "LMP", "LMP2", "LMP3", "GTDP", "GTE", "GTD", "GT3", "GT4", "OTHER",
    };

    readonly GridSpotlightRenderer card = new() { ShowFlagPlaceholder = false };
    readonly List<Driver> roster = new();
    bool wasPre;
    double startMs = -1;
    double exitMs = -1;

    /// <summary>True while the cycle is mid-sequence (compositor gates the full
    /// board off and presents continuously while this holds).</summary>
    public bool IsActive { get; private set; }

    /// <summary>Advance the cycle clock. Call once per host tick with the live
    /// elapsed clock (the same <c>nowMs</c> the compositor passes to Draw).</summary>
    public void Tick(RaceSnapshot snap, double nowMs)
    {
        bool pre = snap.Phase == RacePhase.PreRace;
        if (!pre)
        {
            // If the green catches the cycle mid-card (a short formation), fade
            // the hero card out over ExitFadeMs instead of cutting it dead, so
            // the pre-green → green handoff is never a hard pop.
            if (IsActive && exitMs < 0 && startMs >= 0) exitMs = nowMs;
            if (exitMs >= 0 && nowMs - exitMs < ExitFadeMs) return; // hold active through the fade
            wasPre = false;
            startMs = -1;
            exitMs = -1;
            roster.Clear();
            IsActive = false;
            return;
        }

        if (!wasPre) { roster.Clear(); startMs = -1; }
        wasPre = true;
        exitMs = -1;

        if (roster.Count == 0) BuildRoster(snap);          // (re)try until the field is populated
        if (roster.Count > 0 && startMs < 0) startMs = nowMs;

        IsActive = roster.Count > 0 && startMs >= 0
                   && (nowMs - startMs) < roster.Count * PerCardMs;
    }

    /// <summary>Draw the current card, centred in the canvas, with edge fades.</summary>
    public void Draw(SKCanvas c, int width, int height, double nowMs)
    {
        if (!IsActive || startMs < 0 || roster.Count == 0) return;

        double elapsed = nowMs - startMs;
        int idx = (int)(elapsed / PerCardMs);
        if (idx < 0) idx = 0;
        if (idx >= roster.Count) idx = roster.Count - 1;

        double local = elapsed - idx * PerCardMs;
        float a = 1f;
        if (local < FadeMs) a = (float)(local / FadeMs);
        else if (local > PerCardMs - FadeMs) a = (float)((PerCardMs - local) / FadeMs);
        if (a < 0f) a = 0f;
        if (a > 1f) a = 1f;
        a = (float)BroadcastTiming.EaseOutSoft(a);

        // Exit fade when the green caught the cycle mid-card (see Tick).
        if (exitMs >= 0)
        {
            float e = (float)((nowMs - exitMs) / ExitFadeMs);
            if (e < 0f) e = 0f;
            if (e > 1f) e = 1f;
            a *= 1f - e;
        }

        float ox = (width - GridSpotlightRenderer.CardWidth) / 2f;
        float oy = (height - GridSpotlightRenderer.CardHeight) / 2f;

        using var layer = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(a * 255f)) };
        c.SaveLayer(layer);
        card.Draw(c, ox, oy, roster[idx]);
        c.Restore();
    }

    void BuildRoster(RaceSnapshot snap)
    {
        roster.Clear();
        var classes = new List<DriverClass>(snap.ClassLeaderboard);
        classes.Sort((p, q) => ClassIndex(p.Name).CompareTo(ClassIndex(q.Name)));
        foreach (var cls in classes)
        {
            var drivers = new List<Driver>(cls.Drivers);
            drivers.Sort((p, q) => p.PositionInClass.CompareTo(q.PositionInClass));
            int n = Math.Min(TopPerClass, drivers.Count);
            for (int i = 0; i < n; i++) roster.Add(drivers[i]);
        }
    }

    static int ClassIndex(string name)
    {
        for (int i = 0; i < ClassOrder.Length; i++)
            if (string.Equals(ClassOrder[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return ClassOrder.Length;
    }

    public void Dispose() => card.Dispose();
}
