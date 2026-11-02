using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Draws the broadcast Rolex-style analog watch with SkiaSharp: the dial image,
/// a fluted polished bezel, three static subdial accent hands, and live
/// hour/minute/second hands sweeping off a supplied <see cref="DateTime"/>.
///
/// The watch is authored in the legacy 100×100 viewBox (centre 50,50, outer
/// radius 50). <see cref="Draw"/> maps that space onto any centre + radius, so
/// the same renderer can stand alone (Port 01) or sit inside the session bar
/// (Port 03). All geometry, colours and proportions are ported verbatim from
/// the ApexHUD-Web <c>.centerpiece</c> SVG.
/// </summary>
public sealed class WatchRenderer : IDisposable
{
    // --- ported palette ---
    static readonly SKColor DialCream = new(0xF4, 0xF0, 0xE6);
    static readonly SKColor SubdialHand = new(0x2A, 0x24, 0x18);
    static readonly SKColor Gold = new(0xC5, 0xA5, 0x72);   // #C5A572
    static readonly SKColor HandIvory = new(0xF2, 0xE8, 0xD0);
    static readonly SKColor PinionGold = new(0xE4, 0xC8, 0x87);
    static readonly SKColor PinionCore = new(0x7A, 0x62, 0x40);

    // Dial image placement (legacy: slot inset 6% → viewBox 6..94; image is
    // cover-fit at 124% of the slot, centred, clipped to the circular slot).
    const float SlotRadius = 44f;
    // Register pivots measured on dial.png (2026-10-20) - the hands now sit exactly on the printed dials.
    static readonly SKPoint SubL = new(28.8f, 47.65f), SubR = new(70.75f, 47.65f), SubB = new(49.95f, 71.35f);
    const float SubHand = 6.5f;
    const float DialImgHalf = 88f * 1.24f / 2f;             // 54.56
    static readonly SKRect DialDest =
        new(50 - DialImgHalf, 50 - DialImgHalf, 50 + DialImgHalf, 50 + DialImgHalf);

    // 2026-10-24: the dial comes from ClockDial (classic / homage / the user's own picture). The face
    // is shared by every watch and swapped whole; we only compare its version int each frame.
    ClockDial.Face face = ClockDial.Current;
    int faceVersion = -1;
    readonly SKPath dialClip;

    readonly SKPaint creamPaint;
    readonly SKPaint dialImgPaint;
    readonly SKPaint bezelMainPaint;
    readonly SKPaint bezelDarkenPaint;
    readonly SKPaint bezelGlossPaint;
    readonly SKPaint fluteIvoryPaint;
    readonly SKPaint fluteBlackPaint;
    readonly SKPaint innerEdgePaint;
    readonly SKPaint outerEdgePaint;
    readonly SKPaint subdialHandPaint;
    readonly SKPaint subdialDotPaint;
    readonly SKPaint hourPaint;
    readonly SKPaint minutePaint;
    readonly SKPaint secondPaint;
    readonly SKPaint hourEdgePaint;   // 2026-10-24: dark edge so ivory hands read on light dials too
    readonly SKPaint minuteEdgePaint;
    readonly SKPaint pinionGoldPaint;
    readonly SKPaint pinionCorePaint;

    // Precomputed coin-edge flutes: 128 radial segments (r 42 → 50), alternating
    // ivory/black, spaced 360/128°. Built once; the bezel never changes shape.
    readonly (SKPoint inner, SKPoint outer, bool ivory)[] flutes;

    public WatchRenderer()
    {
        faceVersion = ClockDial.Version;
        face = ClockDial.Current;

        dialClip = new SKPath();
        dialClip.AddCircle(50, 50, SlotRadius);

        creamPaint = new SKPaint { Color = DialCream, IsAntialias = true };
        dialImgPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };

        // Vertical polished-metal gradient across the bezel ring's bounding box
        // (ring r=46, width=8 → y spans 4..96).
        bezelMainPaint = StrokeRing(8f, SKShader.CreateLinearGradient(
            new SKPoint(50, 4), new SKPoint(50, 96),
            new[] { Hex(0xFCF7E8), Hex(0xEDE3CA), Hex(0xC7BB9C), Hex(0x8E8267), Hex(0x534A38) },
            new[] { 0f, 0.22f, 0.48f, 0.72f, 1f },
            SKShaderTileMode.Clamp));

        // Darkening from the bottom (radial, centre bottom of the ring bbox).
        bezelDarkenPaint = StrokeRing(8f, SKShader.CreateRadialGradient(
            new SKPoint(50, 96), 59.8f,
            new[] { new SKColor(20, 16, 8, 140), new SKColor(20, 16, 8, 26), new SKColor(20, 16, 8, 0) },
            new[] { 0f, 0.6f, 1f },
            SKShaderTileMode.Clamp));

        // Gloss highlight from the upper-left.
        bezelGlossPaint = StrokeRing(8f, SKShader.CreateRadialGradient(
            new SKPoint(33.4f, 22.4f), 50.6f,
            new[] { new SKColor(255, 255, 250, 179), new SKColor(255, 255, 250, 31), new SKColor(255, 255, 250, 0) },
            new[] { 0f, 0.55f, 1f },
            SKShaderTileMode.Clamp));

        fluteIvoryPaint = new SKPaint { Color = new SKColor(255, 252, 240, 140), StrokeWidth = 0.32f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        fluteBlackPaint = new SKPaint { Color = new SKColor(0, 0, 0, 140), StrokeWidth = 0.34f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        innerEdgePaint = new SKPaint { Color = new SKColor(0, 0, 0, 179), StrokeWidth = 0.55f, IsAntialias = true, Style = SKPaintStyle.Stroke };
        outerEdgePaint = new SKPaint { Color = new SKColor(0, 0, 0, 115), StrokeWidth = 0.4f, IsAntialias = true, Style = SKPaintStyle.Stroke };

        subdialHandPaint = new SKPaint { Color = SubdialHand, StrokeWidth = 1.1f, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke };
        subdialDotPaint = new SKPaint { Color = Gold, IsAntialias = true };
        hourPaint = new SKPaint { Color = HandIvory, StrokeWidth = 2.8f, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke };
        minutePaint = new SKPaint { Color = HandIvory, StrokeWidth = 1.9f, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke };
        hourEdgePaint = new SKPaint { Color = new SKColor(0x1A, 0x16, 0x10, 0xB0), StrokeWidth = 3.7f, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke };
        minuteEdgePaint = new SKPaint { Color = new SKColor(0x1A, 0x16, 0x10, 0xB0), StrokeWidth = 2.7f, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke };
        secondPaint = new SKPaint { Color = Gold, StrokeWidth = 0.7f, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke };
        pinionGoldPaint = new SKPaint { Color = PinionGold, IsAntialias = true };
        pinionCorePaint = new SKPaint { Color = PinionCore, IsAntialias = true };

        flutes = new (SKPoint, SKPoint, bool)[128];
        for (int i = 0; i < flutes.Length; i++)
        {
            double a = i * (2.0 * Math.PI / flutes.Length);
            float sin = (float)Math.Sin(a), cos = (float)Math.Cos(a);
            flutes[i] = (
                new SKPoint(50 + 42 * sin, 50 - 42 * cos),
                new SKPoint(50 + 50 * sin, 50 - 50 * cos),
                i % 2 == 0);
        }
    }

    /// <summary>
    /// Render the watch centred at <paramref name="cx"/>,<paramref name="cy"/>
    /// with outer radius <paramref name="radius"/> (in device pixels), showing
    /// <paramref name="now"/>. Hands sweep continuously (sub-second precision).
    /// </summary>
    public void Draw(SKCanvas canvas, float cx, float cy, float radius, DateTime now)
    {
        float scale = radius / 50f;
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(scale);
        canvas.Translate(-50f, -50f);   // henceforth drawing in 100×100 viewBox units

        DrawDial(canvas);
        DrawSubdials(canvas);
        DrawHands(canvas, now);
        DrawPinion(canvas);
        DrawBezel(canvas);

        canvas.Restore();
    }

    /// <summary>2026-10-27 (#336): only the parts that never move - dial, subdial accents and bezel - so a caller can bake
    /// them into one picture once and draw just the hands per frame (<see cref="DrawHandsOnly"/>). The bezel sits outside
    /// the hands' reach, so drawing it under them looks the same as <see cref="Draw"/>.</summary>
    public void DrawFace(SKCanvas canvas, float cx, float cy, float radius)
    {
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(radius / 50f);
        canvas.Translate(-50f, -50f);
        DrawDial(canvas);
        DrawSubdials(canvas);
        DrawBezel(canvas);
        canvas.Restore();
    }

    /// <summary>2026-10-27 (#336): hour / minute / second hands and the pinion for <paramref name="now"/> (no allocation).</summary>
    public void DrawHandsOnly(SKCanvas canvas, float cx, float cy, float radius, DateTime now)
    {
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(radius / 50f);
        canvas.Translate(-50f, -50f);
        DrawHands(canvas, now);
        DrawPinion(canvas);
        canvas.Restore();
    }

    /// <summary>The dial's version the face was last drawn with changed (a new dial was picked): re-bake.</summary>
    public static int FaceVersion => ClockDial.Version;

    /// <summary>
    /// 2026-10-19: the same watch as a CHRONOGRAPH reading a lap time (the Fastest Laps
    /// story card). Main hands parked at 10:10 like a catalogue shot; the centre chrono hand
    /// (in <paramref name="chronoColor"/>) shows the seconds, the 9 o'clock register the
    /// minutes (30-minute scale), the 6 o'clock register the tenths. <paramref name="spin"/>
    /// 0..1 winds the hands up to the reading (0 = still spinning, 1 = settled).
    /// </summary>
    public void DrawChrono(SKCanvas canvas, float cx, float cy, float radius, double lapSeconds, float spin, SKColor chronoColor)
    {
        float e = Math.Clamp(spin, 0f, 1f);
        var (secA, minA, tenA) = ChronoAngles(lapSeconds);
        DrawChronoAngles(canvas, cx, cy, radius, secA + 1080 * (1 - e), minA * e, tenA + 720 * (1 - e), chronoColor);
    }

    /// <summary>Hand angles (degrees) for a lap time: centre seconds, 9 o'clock minutes
    /// (30-minute register, 12 deg a minute), 6 o'clock tenths (36 deg a tenth).</summary>
    public static (double Sec, double Min, double Tenth) ChronoAngles(double lapSeconds)
        => (lapSeconds % 60 * 6, Math.Floor(lapSeconds / 60) * 12, (lapSeconds * 10) % 10 * 36);

    /// <summary>The chronograph with its hands at explicit angles - lets a caller sweep the
    /// hands from one reading to the next on the same face.</summary>
    public void DrawChronoAngles(SKCanvas canvas, float cx, float cy, float radius, double secA, double minA, double tenA, SKColor chronoColor)
    {
        float scale = radius / 50f;
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(scale);
        canvas.Translate(-50f, -50f);
        DrawDial(canvas);
        if (face.SubdialHands)
        using (var sub = new SKPaint { Color = SubdialHand, StrokeWidth = 1.1f, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke })
        {
            DrawRotatedLine(canvas, SubL.X, SubL.Y, SubL.X, SubL.Y - SubHand - 1, (float)minA, SubL.X, SubL.Y, sub);
            DrawRotatedLine(canvas, SubB.X, SubB.Y, SubB.X, SubB.Y - SubHand - 1, (float)tenA, SubB.X, SubB.Y, sub);
            DrawRotatedLine(canvas, SubR.X, SubR.Y, SubR.X, SubR.Y - SubHand, 15, SubR.X, SubR.Y, sub);
            canvas.DrawCircle(SubL.X, SubL.Y, 0.9f, subdialDotPaint);
            canvas.DrawCircle(SubB.X, SubB.Y, 0.9f, subdialDotPaint);
            canvas.DrawCircle(SubR.X, SubR.Y, 0.9f, subdialDotPaint);
        }
        float secA0 = (float)secA;
        // main hands parked at 10:10
        DrawRotatedLine(canvas, 50, 55, 50, 28, -60 + 5, 50, 50, hourEdgePaint);
        DrawRotatedLine(canvas, 50, 55, 50, 16, 60, 50, 50, minuteEdgePaint);
        DrawRotatedLine(canvas, 50, 55, 50, 28, -60 + 5, 50, 50, hourPaint);
        DrawRotatedLine(canvas, 50, 55, 50, 16, 60, 50, 50, minutePaint);
        // chrono seconds hand
        using (var ch = new SKPaint { Color = chronoColor, StrokeWidth = 1.0f, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke })
        using (var chf = new SKPaint { Color = chronoColor, IsAntialias = true })
        {
            canvas.Save();
            canvas.RotateDegrees(secA0, 50, 50);
            canvas.DrawLine(50, 61, 50, 9, ch);
            canvas.DrawCircle(50, 17, 1.6f, chf);
            canvas.Restore();
        }
        DrawPinion(canvas);
        DrawBezel(canvas);
        canvas.Restore();
    }

    void DrawDial(SKCanvas c)
    {
        int v = ClockDial.Version;                      // int compare only; no per-frame allocation or IO
        if (v != faceVersion) { faceVersion = v; face = ClockDial.Current; }
        c.Save();
        c.ClipPath(dialClip, SKClipOperation.Intersect, antialias: true);
        c.DrawCircle(50, 50, SlotRadius, creamPaint);   // fallback under the image
        if (face.Bitmap is { } bmp) c.DrawBitmap(bmp, face.Dest, dialImgPaint);
        c.Restore();
    }

    void DrawSubdials(SKCanvas c)
    {
        if (!face.SubdialHands) return;                 // custom faces without Daytona-style registers
        // Static accent hands seated in the dial's printed registers (9/6/3).
        DrawRotatedLine(c, SubL.X, SubL.Y, SubL.X, SubL.Y - SubHand, -25, SubL.X, SubL.Y, subdialHandPaint);
        DrawRotatedLine(c, SubB.X, SubB.Y, SubB.X, SubB.Y - SubHand, 40, SubB.X, SubB.Y, subdialHandPaint);
        DrawRotatedLine(c, SubR.X, SubR.Y, SubR.X, SubR.Y - SubHand, 15, SubR.X, SubR.Y, subdialHandPaint);
        c.DrawCircle(SubL.X, SubL.Y, 0.9f, subdialDotPaint);
        c.DrawCircle(SubB.X, SubB.Y, 0.9f, subdialDotPaint);
        c.DrawCircle(SubR.X, SubR.Y, 0.9f, subdialDotPaint);
    }

    void DrawHands(SKCanvas c, DateTime now)
    {
        double secOfDay = now.Hour * 3600 + now.Minute * 60 + now.Second + now.Millisecond / 1000.0;
        float secAngle = (float)(secOfDay % 60 * 6);
        float minAngle = (float)(secOfDay / 60 % 60 * 6);
        float hourAngle = (float)(secOfDay / 3600 % 12 * 30);

        DrawRotatedLine(c, 50, 55, 50, 28, hourAngle, 50, 50, hourEdgePaint);
        DrawRotatedLine(c, 50, 55, 50, 16, minAngle, 50, 50, minuteEdgePaint);
        DrawRotatedLine(c, 50, 55, 50, 28, hourAngle, 50, 50, hourPaint);
        DrawRotatedLine(c, 50, 55, 50, 16, minAngle, 50, 50, minutePaint);

        c.Save();
        c.RotateDegrees(secAngle, 50, 50);
        c.DrawLine(50, 60, 50, 13, secondPaint);
        c.DrawCircle(50, 19, 1.2f, secondPaint);
        c.Restore();
    }

    void DrawPinion(SKCanvas c)
    {
        c.DrawCircle(50, 50, 2.3f, pinionGoldPaint);
        c.DrawCircle(50, 50, 1.2f, pinionCorePaint);
    }

    void DrawBezel(SKCanvas c)
    {
        c.DrawCircle(50, 50, 46, bezelMainPaint);
        c.DrawCircle(50, 50, 46, bezelDarkenPaint);
        foreach (var (inner, outer, ivory) in flutes)
            c.DrawLine(inner, outer, ivory ? fluteIvoryPaint : fluteBlackPaint);
        c.DrawCircle(50, 50, 42, innerEdgePaint);    // inner depth shadow
        c.DrawCircle(50, 50, 49.9f, outerEdgePaint); // outer edge
        c.DrawCircle(50, 50, 46, bezelGlossPaint);   // gloss on top
    }

    static void DrawRotatedLine(SKCanvas c, float x1, float y1, float x2, float y2,
        float angleDeg, float px, float py, SKPaint paint)
    {
        c.Save();
        c.RotateDegrees(angleDeg, px, py);
        c.DrawLine(x1, y1, x2, y2, paint);
        c.Restore();
    }

    static SKPaint StrokeRing(float width, SKShader shader) =>
        new() { Style = SKPaintStyle.Stroke, StrokeWidth = width, Shader = shader, IsAntialias = true };

    static SKColor Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public void Dispose()
    {
        // the dial bitmap belongs to ClockDial (shared by every watch) - not disposed here
        dialClip.Dispose();
        creamPaint.Dispose();
        dialImgPaint.Dispose();
        bezelMainPaint.Dispose();
        bezelDarkenPaint.Dispose();
        bezelGlossPaint.Dispose();
        fluteIvoryPaint.Dispose();
        fluteBlackPaint.Dispose();
        innerEdgePaint.Dispose();
        outerEdgePaint.Dispose();
        subdialHandPaint.Dispose();
        subdialDotPaint.Dispose();
        hourPaint.Dispose();
        minutePaint.Dispose();
        secondPaint.Dispose();
        hourEdgePaint.Dispose();
        minuteEdgePaint.Dispose();
        pinionGoldPaint.Dispose();
        pinionCorePaint.Dispose();
    }
}
