using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

/// <summary>V2 preview: the real tower, session bar + watch and featured card under the night grade, the sun going down.</summary>
sealed class NightDemo : VpGraphic
{
    public readonly NightPalette Palette = new();
    readonly TimingTowerRenderer tower = new();
    readonly SessionBarRenderer bar = new();
    readonly WatchRenderer watch = new();
    readonly FeaturedCardRenderer featured = new();
    readonly RaceSnapshot snap = Canned.Build();
    public Func<double, float> NightAt = t => 0f;
    /// <summary>Alloc bench: only the grade itself over a plain card (the real renderers' own costs are theirs).</summary>
    public bool GradeOnly;
    readonly SKPaint card = new() { Color = new SKColor(10, 14, 20, 220) };

    public override bool Active(double nowMs) => true;

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        PresentationState.Tick(snap);
        Palette.Elevated = Elevated;
        Palette.Override = NightAt(nowMs);
        Palette.Tick(0, double.NaN, true, nowMs);
        int s = Palette.Begin(c);
        if (GradeOnly) { c.DrawRect(16, 18, 400, 1000, card); NightPalette.End(c, s); return; }
        tower.Draw(c, 16f, 18f, h - 34f, snap, nowMs + 5000);
        float bw = SessionBarRenderer.BarWidth, bx = (w - bw) / 2f;
        bar.Draw(c, bx, 18f, 1f, snap, new DateTime(2026, 6, 13, 20, 30, 0), nowMs + 5000, bw);
        watch.Draw(c, bx + bw / 2f, 18f + SessionBarRenderer.BarHeight / 2f, 64f, new DateTime(2026, 6, 13, 20, 30, 0));
        c.Save(); c.Translate((w - 768f) / 2f, h - 16f - 211f); featured.Draw(c, 24f, 32.5f, snap, nowMs + 5000); c.Restore();
        NightPalette.End(c, s);
    }
}
