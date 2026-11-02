// #494 (2026-10-29): viewer cards + themes previews, drawn by the REAL renderers at 1920x1080 over a TV picture (or a flat
// colour): the Super Chat ticker item, the big-amount tower banner, the member card, the track fund bar, and the Halloween
// theme on the tower + session bar. The viewer and field are samples (the canned demo field).
//   dotnet viewer_preview.dll OUT_DIR [background.png]
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

string outDir = args.Length > 0 ? args[0] : "out";
string? bgPath = args.Length > 1 && args[1].Length > 0 && !args[1].StartsWith("--") ? args[1] : null;
Directory.CreateDirectory(outDir);
StorySound.Enabled = false;
Fonts.WarmUp();
const int W = 1920, H = 1080;
SKBitmap? bg = bgPath != null && File.Exists(bgPath) ? SKBitmap.Decode(bgPath) : null;
// #576: the tiered looks + thank-you roll (stills and clips) instead of the #494 cards
if (args.Contains("--tiers")) { Tiers.Out = outDir; Tiers.SetBg(bg); Tiers.Run(outDir, bg, args); return; }

void Scene(string name, string theme, Action<Comp, ViewerCardsRenderer> setup, double atMs, bool fund = false)
{
    try
    {
        Theme.Set(theme);
        ViewerFund.Enabled = fund; ViewerFund.Goal = "Track fund: Le Mans entry"; ViewerFund.Target = fund ? 250 : 0;
        if (fund) ViewerFund.Set(87.5);
        var comp = new Comp();
        var v = new ViewerCardsRenderer { ForceClear = true };
        setup(comp, v);
        using var surf = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surf.Canvas;
        using var nd = new SKNoDrawCanvas(W, H);
        for (double t = 0; t <= atMs; t += 16.7)
        {
            bool last = t + 16.7 > atMs;
            SKCanvas cv = last ? c : nd;
            if (last)
            {
                if (bg != null) { using var p = new SKPaint { FilterQuality = SKFilterQuality.High }; c.DrawBitmap(bg, new SKRect(0, 0, W, H), p); }
                else c.Clear(new SKColor(70, 82, 74));
            }
            comp.Layout(t);
            comp.Under(cv, t);
            v.Tick(comp.Snap, t);
            v.Draw(cv, W, H, VpLayout.SessionBar, new SKPoint(16f, 18f), t);
            comp.Top(cv, t);
        }
        using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 92);
        File.WriteAllBytes(Path.Combine(outDir, name + ".png"), data.ToArray());
        Console.WriteLine($"ok   {name}  (playing: {v.Current?.Kind.ToString() ?? "-"})");
    }
    catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace); }
}

// 1. a Super Chat riding the strip under the session bar (part way through its scroll)
Scene("01_superchat_ticker", "default", (_, _) => ViewerFeed.Test("superchat"), 3000);
// 2. a big Super Chat: the strip + the banner over the tower's cap
Scene("02_big_superchat_banner", "default", (_, _) => ViewerFeed.Test("bigsuperchat"), 2400);
// 3. a new member: the pre-race style card under the session bar
Scene("03_member_card", "default", (_, _) => ViewerFeed.Test("member"), 2200);
// 4. the Halloween theme on the tower + session bar (nothing playing)
Scene("04_halloween_tower_bar", "halloween", (_, _) => { }, 1500);
// 5. Halloween: a big Super Chat (strip + banner) and the track fund bar
Scene("05_halloween_big_superchat_fund", "halloween", (_, _) => ViewerFeed.Test("bigsuperchat"), 2600, fund: true);
// 6. Halloween: a Twitch sub card
Scene("06_halloween_sub_card", "halloween", (_, _) => ViewerFeed.Test("sub"), 2200);
// 7. the track fund bar on the default theme, with bits on the strip
Scene("07_fund_bits", "default", (_, _) => ViewerFeed.Test("bits"), 3600, fund: true);

// word filter + parsing sanity checks (printed, not drawn)
Console.WriteLine("filter: " + string.Join(", ", new[] { "great race!", "you f.u.c.k.i.n.g legend", "sh1t driving", "visit www.spam.com", "Scunthorpe" }
    .Select(s => $"\"{s}\" -> {(ViewerFeed.Blocked(s) || ViewerFeed.HasLink(s) ? "hidden" : "shown")}")));
Console.WriteLine("money: " + string.Join(", ", new[] { "$5.00", "€4,99", "£20", "CA$25.00", "1,234.50", "500" }
    .Select(s => $"{s} -> {ViewerFeed.ParseMoney(s)}")));
