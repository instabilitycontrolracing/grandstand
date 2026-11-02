// Corner theme previews (2026-10-27, #115 / #118): every themed corner label drawn twice - base (left) and the refined
// variant (right, CornerTheme.Refined) - on the same backgrounds, at the TV's 2x scale, plus the lap-walk chips and the
// compact battle-map header. Output: corner_themes_before_after.png (+ one PNG per corner with --each).
//
// The themes use Angelo's own pictures from Documents\iRacing\Grandstand\corner_logos (Dunlop bridge, PlayStation symbol
// and space, Michelin plate + Bibendum, the Porsche / Corvette / Ford lettering and glyphs). Without them (this cloud
// machine) Dunlop / PlayStation / Michelin fall back to the plain gold label and the makers to their badge + type, so
// those rows must be re-rendered on the PC. The backgrounds are PLACEHOLDERS (a sky and a tarmac gradient), not footage.
using ApexHUD.Native.Overlays;
using SkiaSharp;

string outDir = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? ".";
bool each = args.Contains("--each");
Directory.CreateDirectory(outDir);

(string Eye, string Name)[] corners =
{
    ("INTO", "DUNLOP CURVE"), ("THROUGH", "DUNLOP CHICANE"), ("INTO", "TERTRE ROUGE"), ("THROUGH", "THE ESSES"),
    ("DOWN", "MULSANNE STRAIGHT"), ("INTO", "PLAYSTATION CHICANE"), ("INTO", "MICHELIN CHICANE"), ("INTO", "MULSANNE CORNER"),
    ("INTO", "INDIANAPOLIS"), ("INTO", "ARNAGE"), ("THROUGH", "PORSCHE CURVES"), ("INTO", "CORVETTE CORNER"),
    ("THROUGH", "FORD CHICANES"), ("INTO", "TURN 7"),
};

const float S = 2f;                 // the TV: 3840x2160 drawn from 1920x1080 design units
const float CellW = 900f, CellH = 250f, LabelY = 78f;
int rows = corners.Length;
var info = new SKImageInfo((int)(2 * CellW * S), (int)((rows * CellH + 70f + 260f) * S));
using var surf = SKSurface.Create(info);
var c = surf.Canvas;
c.Clear(new SKColor(0x0E, 0x10, 0x14));
c.Scale(S);

using var title = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 20f, Color = SKColors.White };
using var small = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 12f, Color = new SKColor(0xB8, 0xBE, 0xC8) };
c.DrawText("BASE (as on air today)", 24, 34, title);
c.DrawText("REFINED (CornerTheme.Refined, default OFF)", CellW + 24, 34, title);
c.DrawText("Placeholder backgrounds. Rows whose theme needs corner_logos show the fallback here - re-render on the PC.", 24, 56, small);

void Background(SKRect r, int row)
{
    // alternate a bright sky (hardest for the lettering) and a dark tarmac/trees placeholder
    var cols = row % 2 == 0
        ? new[] { new SKColor(0x9C, 0xC4, 0xE8), new SKColor(0xD8, 0xE6, 0xF0), new SKColor(0x6E, 0x7A, 0x6A) }
        : new[] { new SKColor(0x2A, 0x33, 0x2C), new SKColor(0x3A, 0x3E, 0x44), new SKColor(0x23, 0x25, 0x29) };
    using var sh = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), cols, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
    using var p = new SKPaint { Shader = sh };
    c.DrawRect(r, p);
}

for (int i = 0; i < rows; i++)
{
    var (eye, name) = corners[i];
    string? her = CornerTheme.HeritageOf("lemans full", name);
    for (int v = 0; v < 2; v++)
    {
        CornerTheme.Refined = v == 1;
        var cell = new SKRect(v * CellW, 70f + i * CellH, (v + 1) * CellW, 70f + (i + 1) * CellH);
        Background(cell, i);
        c.Save();
        c.ClipRect(cell);
        CornerTheme.DrawLabel(c, cell.MidX, cell.Top + LabelY, eye, name, 1f, her);
        c.Restore();
        var th = CornerTheme.For(name);
        using var tag = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 10f, Color = new SKColor(0, 0, 0, 150) };
        c.DrawText($"{th?.Kind.ToString() ?? "plain"}{(th?.Logo == null && th?.Kind is not (null or CornerTheme.Kind.Bricks or CornerTheme.Kind.Speed or CornerTheme.Kind.Earth) ? " (no picture)" : "")}", cell.Left + 8, cell.Bottom - 8, tag);
        if (each)
        {
            using var img = surf.Snapshot(new SKRectI((int)(cell.Left * S), (int)(cell.Top * S), (int)(cell.Right * S), (int)(cell.Bottom * S)));
            using var d = img.Encode(SKEncodedImageFormat.Png, 95);
            File.WriteAllBytes(Path.Combine(outDir, $"corner_{i:00}_{name.Replace(' ', '_').ToLowerInvariant()}_{(v == 0 ? "base" : "refined")}.png"), d.ToArray());
        }
    }
}

// chips (track card lap walk) and the compact battle-map header, base vs refined
float y0 = 70f + rows * CellH;
for (int v = 0; v < 2; v++)
{
    CornerTheme.Refined = v == 1;
    var area = new SKRect(v * CellW, y0, (v + 1) * CellW, y0 + 260f);
    using (var bg = new SKPaint { Color = new SKColor(0x16, 0x19, 0x20) }) c.DrawRect(area, bg);
    c.DrawText("lap-walk chips / battle-map header", area.Left + 24, area.Top + 26, small);
    float x = area.Left + 24;
    foreach (var nm in new[] { "DUNLOP CURVE", "MICHELIN CHICANE", "PLAYSTATION CHICANE", "ARNAGE", "PORSCHE CURVES" })
    {
        var box = new SKRect(x, area.Top + 44, x + 160, area.Top + 84);
        var acc = CornerTheme.Chip(c, box, nm, 1f);
        using var tp = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 10.5f, Color = acc };
        c.DrawText(nm, box.Left + 10, box.MidY + 4, tp);
        x += 170;
        if (x > area.Right - 170) break;
    }
    float hx = area.Left + 24;
    foreach (var nm in new[] { "PORSCHE CURVES", "TERTRE ROUGE" })
    {
        using (var plate = new SKPaint { Color = new SKColor(0x0B, 0x0E, 0x14, 230) }) c.DrawRoundRect(new SKRect(hx - 10, area.Top + 104, hx + 380, area.Top + 170), 8, 8, plate);
        CornerTheme.DrawCompact(c, hx, area.Top + 110, 360, "INTO", nm, 1f);
        hx += 420;
    }
}
CornerTheme.Refined = false;

using (var img = surf.Snapshot())
using (var d = img.Encode(SKEncodedImageFormat.Png, 92))
    File.WriteAllBytes(Path.Combine(outDir, "corner_themes_before_after.png"), d.ToArray());
Console.WriteLine($"wrote {Path.Combine(outDir, "corner_themes_before_after.png")} ({info.Width}x{info.Height})");
Console.WriteLine("corner_logos folder: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "corner_logos")
                  + (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "corner_logos")) ? " (found)" : " (NOT found - fallbacks)"));
