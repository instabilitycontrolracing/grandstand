using System.Globalization;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-06 (Angelo: "there should be a little thing somewhere that shows the target driver's name so we know who it is
/// and what they're driving"): a small plate bottom-left naming the car the camera is on - number, driver, class position,
/// car. Fades out when another graphic already names the car (name tag, featured card, battle card, story card) and swaps
/// with a short fade when the camera moves to another car. Drawn in 1080p layout units.
/// </summary>
internal sealed class CamCarChip
{
    public const float W = 470f, H = 62f, Top = 1080f - 34f - H;
    float Left = (1920f - 470f) / 2f;
    /// <summary>Where the plate would sit (for the "is anything else there" check).</summary>
    public static readonly SKRect Zone = new((1920f - 470f) / 2f - 16f, 1080f - 34f - 62f - 16f, (1920f + 470f) / 2f + 16f, 1080f);
    float k; double lastMs = -1;
    int shownCar = -1, wantCar = -1;
    string l1 = "", l2 = ""; SKColor accent = new(0xE8, 0xC9, 0x7A);
    public bool Active => k > 0.002f;
    public bool Fading => k > 0.002f && k < 0.998f;
    public SKRect LastRect { get; private set; }
    readonly SKPaint p1 = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 26f };
    readonly SKPaint p2 = new() { IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 13f };
    readonly SKPaint fill = new() { IsAntialias = true };

    public void Tick(Driver? car, bool wanted, double nowMs)
    {
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;
        wantCar = car?.CarIdx ?? -1;
        // a different car: fade the old plate out first, then bring the new one in
        bool up = wanted && car != null && (shownCar == wantCar || k <= 0.002f);
        if (up && shownCar != wantCar && car != null)
        {
            shownCar = car.CarIdx;
            l1 = $"#{car.CarNumber}  {FullName(car).ToUpperInvariant()}";   // 2026-11-06 (#866): full first name
            string pos = car.PositionInClass > 0 ? $"P{car.PositionInClass} {car.ClassName}" : car.ClassName;
            l2 = string.IsNullOrEmpty(car.CarName) ? pos : $"{pos}  ·  {car.CarName}";
            accent = ParseHex(car.ClassColor, new SKColor(0xE8, 0xC9, 0x7A));
        }
        else if (car != null && shownCar == car.CarIdx)
        {
            string pos = car.PositionInClass > 0 ? $"P{car.PositionInClass} {car.ClassName}" : car.ClassName;
            l2 = string.IsNullOrEmpty(car.CarName) ? pos : $"{pos}  ·  {car.CarName}";
        }
        float target = up ? 1f : 0f;
        k += (target > k ? 1f : -1f) * (float)(dt / (target > k ? 320.0 : 220.0));
        k = Math.Clamp(k, 0f, 1f);
    }

    public void Draw(SKCanvas c)
    {
        if (k <= 0.002f || shownCar < 0) { LastRect = SKRect.Empty; return; }
        float e = k * k * (3f - 2f * k);
        byte A(float x) => (byte)Math.Clamp(255f * x * e, 0f, 255f);
        p1.TextSize = 26f;
        float w = Math.Max(260f, Math.Min(W, Math.Max(p1.MeasureText(l1), p2.MeasureText(l2)) + 44f));
        while (p1.MeasureText(l1) > w - 40f && p1.TextSize > 16f) p1.TextSize -= 1f;
        Left = (1920f - w) / 2f;
        LastRect = new SKRect(Left, Top, Left + w, Top + H);
        int save = c.Save();
        c.Translate(0, (1f - e) * 10f);
        fill.Shader = null;
        fill.Color = new SKColor(8, 10, 16, A(0.78f));
        c.DrawRoundRect(new SKRect(Left, Top, Left + w, Top + H), 3f, 3f, fill);
        fill.Color = accent.WithAlpha(A(1f));
        c.DrawRect(new SKRect(Left, Top, Left + 4f, Top + H), fill);
        p1.Color = new SKColor(0xF4, 0xEF, 0xE4, A(1f));
        c.DrawText(l1, Left + 18f, Top + 30f, p1);
        p2.Color = new SKColor(0xC9, 0xCE, 0xD6, A(0.95f));
        c.DrawText(l2, Left + 18f, Top + 51f, p2);
        c.RestoreToCount(save);
    }

    /// <summary>The driver's full name as iRacing has it, without the trailing digits iRacing adds to duplicate names.</summary>
    internal static string FullName(Driver d)
    {
        string n = (d.Name ?? "").Trim();
        if (n.Length == 0) return NameFormat.Broadcast(d);
        while (n.Length > 0 && char.IsDigit(n[^1])) n = n[..^1];
        return n.Trim();
    }

    static SKColor ParseHex(string? s, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var h = s.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if (h.StartsWith('#')) h = h[1..];
        if (h.Length > 6) h = h[^6..];
        return uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && h.Length == 6
            ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v) : fallback;
    }
}
