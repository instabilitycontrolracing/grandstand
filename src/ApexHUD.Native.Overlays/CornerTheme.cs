using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Themed corner labels (2026-10-25, Angelo: the corner logos "should each theme to the corner they are on ... like
/// Dunlop Curve, it would be really cool to see the arch cut-out"). A famous corner's name gets dressed as the corner:
///   Dunlop     - the name drives under the Dunlop bridge: grey tyre arch with tread, DUNLOP on its face, the yellow
///                strip over the deck carrying the eyebrow (INTO / THROUGH)
///   PlayStation- the name sits on a strip of deep space with the PlayStation symbol, PlayStation blue accents
///   Michelin   - a Michelin-blue plate with the yellow edge and Bibendum peeking in at the end
///   Car makers - the maker's logo and colours (Porsche gold and red, Ford blue, Corvette yellow, ...)
/// Brand themes only come alive when Angelo's own picture for that brand is in corner_logos (CornerBrand.User), so
/// nothing branded ships; car makers use the logos Grandstand already has. Everything else keeps the plain gold label.
/// </summary>
public static class CornerTheme
{
    /// <summary>2026-10-27 (#115 / #118, "make the corner themes look better / ultra refined"): the refined variant of every
    /// theme - same identity, layout and sizes, finer finish: a two-layer shadow (tight contact + soft ambient) instead of
    /// one hard drop shadow, names on whole device pixels, the eyebrow flanked by short accent ticks, a rule with a lit
    /// core and a soft glow, radial glows instead of blurred blobs behind badges, a true capsule heritage pill (same
    /// place and size for every theme, text centred on its cap height, accent dots, hairline edge), a glass sheen and
    /// bevel on the Michelin plate, a lit top edge and a nebula fallback on the PlayStation plate, the bridge's contact
    /// shadow and a tapered road line under Dunlop, hairline edges on the lap-walk chips. control.json "cornerRefined",
    /// OFF by default (Settings -> Broadcast effects).</summary>
    public static bool Refined;

    public enum Kind { None, Dunlop, PlayStation, Michelin, Maker, Bricks, Speed, Earth, Portrait }

    public sealed record Theme(Kind Kind, SKColor Eye, SKColor Rule, SKBitmap? Logo);

    static readonly SKColor Gold = new(0xE8, 0xC2, 0x5A), Ivory = new(0xF4, 0xF6, 0xFA);
    static readonly SKColor DYellow = new(0xFF, 0xCE, 0x00), DGrey = new(0x9A, 0x9E, 0xA5), DGreyDark = new(0x5E, 0x62, 0x69);
    static readonly SKColor PsBlue = new(0x00, 0x70, 0xD1), PsLight = new(0x6F, 0xB6, 0xFF);
    static readonly SKColor MBlue = new(0x27, 0x50, 0x9B), MYellow = new(0xFC, 0xE5, 0x00);

    static readonly (string Word, SKColor Eye, SKColor Rule)[] MakerColours =
    {
        ("Porsche", new(0xD8, 0xB3, 0x6A), new(0xD5, 0x00, 0x1C)),
        ("Ford", new(0x6E, 0xA8, 0xFF), new(0x1C, 0x5C, 0xD6)),
        ("Corvette", new(0xFF, 0xC7, 0x2C), new(0xFF, 0xC7, 0x2C)),
        ("Ferrari", new(0xFF, 0xD2, 0x3F), new(0xDC, 0x00, 0x00)),
        ("Mercedes", new(0x9E, 0xE6, 0xDA), new(0x00, 0xA1, 0x9C)),
        ("Audi", new(0xE6, 0xE6, 0xE6), new(0xBB, 0x0A, 0x30)),
        ("BMW", new(0x7F, 0xB8, 0xFF), new(0x1C, 0x69, 0xD4)),
    };

    // 2026-10-28: the Earth colours the track packs pick from (the first four are the ones Le Mans / the IMSA tracks use)
    static readonly (string Name, SKColor Eye, SKColor Rule)[] EarthPalettes =
    {
        ("woods", new(0x9C, 0xD0, 0xA4), new(0x2F, 0x7D, 0x4A)),
        ("oaks", new(0xB8, 0xD4, 0x96), new(0x4E, 0x7A, 0x34)),
        ("redearth", new(0xE8, 0x86, 0x5A), new(0xB5, 0x46, 0x2A)),
        ("sunset", new(0xFF, 0xB0, 0x6A), new(0xE0, 0x6A, 0x2C)),
        ("water", new(0x8F, 0xD3, 0xE8), new(0x2A, 0x7F, 0xA8)),
        ("stone", new(0xD9, 0xC7, 0xA8), new(0x8A, 0x73, 0x50)),
    };

    static bool Has(string name, string word) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, $@"\b{word}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The theme for a corner or straight name, or null for a plain label.</summary>
    public static Theme? For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // 2026-10-28 (Angelo): the Dunlop bridge, PlayStation and Michelin chicanes are Le Mans landmarks - Suzuka's Dunlop
        // Curve has its own look (to be made), so these brand themes only dress Le Mans (or a preview with no track).
        bool leMans = ApexHUD.Native.Common.TrackCorners.LastTrackRaw is not { } tr || tr.StartsWith("lemans", StringComparison.OrdinalIgnoreCase);
        if (leMans && Has(name, "Dunlop") && CornerBrand.User("dunlop") != null) return new(Kind.Dunlop, DYellow, DYellow, CornerBrand.User("dunlop"));
        if (leMans && Has(name, "Playstation") && CornerBrand.User("playstation") != null) return new(Kind.PlayStation, PsLight, PsBlue, CornerBrand.User("playstation"));
        if (leMans && Has(name, "Michelin") && CornerBrand.User("michelin") != null) return new(Kind.Michelin, MYellow, MYellow, CornerBrand.User("michelin"));
        // 2026-10-28: track packs - text themes only (Earth where the place's own name is water, woods, oaks, sun or
        // rock; Speed for the long named straights). Never a brand: brand / driver corners still only change when
        // Angelo's own picture is in corner_logos (CornerBrand, below).
        // Round 2: on a hand-made track's other layouts (Silverstone 2011, Monza Combined ...) the hand themes below come first.
        var packTheme = ApexHUD.Native.Common.TrackPacks.Theme(ApexHUD.Native.Common.TrackCorners.LastTrackRaw, name);
        bool handPack = ApexHUD.Native.Common.TrackPacks.IsHand(ApexHUD.Native.Common.TrackCorners.LastTrackRaw);
        if (!handPack && FromPack(packTheme, name) is { } pk) return pk;
        // places with a story of their own (no brand): the colours and texture of the place
        if (Has(name, "Indianapolis")) return new(Kind.Bricks, new(0xF0, 0xA0, 0x7C), new(0xA8, 0x45, 0x2C), null);          // named after the Brickyard
        if (Has(name, "Mulsanne Straight")) return new(Kind.Speed, new(0x9F, 0xD3, 0xFF), new(0x9F, 0xD3, 0xFF), null);       // 400 km/h in 1988
        if (Has(name, "Tetre Rouge") || Has(name, "Tertre Rouge")) return new(Kind.Earth, new(0xE8, 0x86, 0x5A), new(0xB5, 0x46, 0x2A), null);   // the red earth
        if (Has(name, "Esses")) return new(Kind.Earth, new(0x9C, 0xD0, 0xA4), new(0x2F, 0x7D, 0x4A), null);                   // the pines that lined it
        // 2026-10-28 (IMSA season, weeks 8-12) - only where the name itself says what the place looks like:
        if (Has(name, "Copse")) return new(Kind.Earth, new(0x9C, 0xD0, 0xA4), new(0x2F, 0x7D, 0x4A), null);                   // Silverstone: named for the copses, its small woods
        if (Has(name, "Lesmo")) return new(Kind.Earth, new(0xB8, 0xD4, 0x96), new(0x4E, 0x7A, 0x34), null);                   // Monza: once the "curve of the oaks", in the royal park
        if (Has(name, "Sunset Bend")) return new(Kind.Earth, new(0xFF, 0xB0, 0x6A), new(0xE0, 0x6A, 0x2C), null);             // Sebring: driven into the setting sun late in the race
        if (Has(name, "Hangar Straight") || Has(name, "Ulmann Straight")) return new(Kind.Speed, new(0x9F, 0xD3, 0xFF), new(0x9F, 0xD3, 0xFF), null);   // the long flat-out runs
        if (handPack && FromPack(packTheme, name) is { } hpk) return hpk;
        var logo = CornerBrand.For(name);
        foreach (var (w, e, r) in MakerColours)
            if (Has(name, w)) return new(Kind.Maker, e, r, logo);
        return logo != null ? new(Kind.Maker, Gold, Gold, logo) : null;
    }

    // 2026-10-28 (round 2): Portrait - a place named after a racing driver (Senna S, Brock's Skyline, Andretti Hairpin):
    // champagne and gold, an honour-roll rule (two hairlines and a small diamond). Text only, no photo; a picture of
    // Angelo's own in corner_logos for that name still wins (it falls through to the brand look).
    static readonly SKColor PortraitEye = new(0xEA, 0xD6, 0xA6), PortraitRule = new(0xC9, 0xA4, 0x5C);

    static Theme? FromPack(string? pt, string name)
    {
        if (pt == null) return null;
        if (pt == "speed") return new(Kind.Speed, new(0x9F, 0xD3, 0xFF), new(0x9F, 0xD3, 0xFF), null);
        if (pt == "portrait") return CornerBrand.For(name) != null ? null : new(Kind.Portrait, PortraitEye, PortraitRule, null);
        foreach (var (pal, e, r) in EarthPalettes) if (pt == "earth:" + pal) return new(Kind.Earth, e, r, null);
        return null;
    }

    // heritage (2026-10-25, Angelo: "put a ton of personality and heritage into the overlay") - one line about the place,
    // from the ACO's own history of the circuit (24h-lemans.com, "Landmarks of the 24 Hours of Le Mans circuit")
    static readonly (string Track, string Word, string Line)[] Heritage =
    {
        ("lemans", "Dunlop", "A FOOTBRIDGE SINCE 1923 · DUNLOP'S SINCE 1950"),
        ("lemans", "Esses", "NAMED FOR THE PINES THAT ONCE LINED IT"),
        ("lemans", "Tetre Rouge", "NAMED FOR THE RED EARTH"),
        ("lemans", "Tertre Rouge", "NAMED FOR THE RED EARTH"),
        ("lemans", "Playstation", "THE MULSANNE CHICANES · SINCE 1990"),
        ("lemans", "Michelin", "THE MULSANNE CHICANES · SINCE 1990"),
        ("lemans", "Mulsanne Straight", "400 KM/H HERE IN 1988 · A RECORD THAT STANDS"),
        ("lemans", "Mulsanne Corner", "FROM 300 TO 75 KM/H"),
        ("lemans", "Indianapolis", "UNCHANGED SINCE 1923"),
        ("lemans", "Arnage", "BARELY CHANGED IN A CENTURY"),
        ("lemans", "Ford", "SINCE 1968"),
        ("lemans", "Corvette", "PART OF THE PORSCHE CURVES"),

        // 2026-10-28 (IMSA season, weeks 8-12): one sourced line per place, nothing where the source gives nothing.
        // Autódromo Hermanos Rodríguez - mexicogp.mx "Nombre de las curvas y rectas AHR", formula1.com (Mansell, 2015),
        // Wikipedia (the Foro stadium)
        ("mexicocity", "Moisés Solana", "FOR MEXICAN F1 DRIVER MOISÉS SOLANA"),
        ("mexicocity", "Jim Clark", "CLARK WON HERE IN 1962, 1963 AND 1967"),
        ("mexicocity", "Rebaque", "FOR MEXICAN F1 DRIVER HÉCTOR REBAQUE"),
        ("mexicocity", "Adrián Fernández", "FOR MEXICAN RACER ADRIÁN FERNÁNDEZ"),
        ("mexicocity", "Foro Sol", "A BALLPARK INSIDE THE OLD PERALTADA"),
        ("mexicocity", "Mansell", "MANSELL PASSED BERGER OUTSIDE · 1990"),
        ("mexicocity", "Peraltada", "BANKING EASED FROM 13° TO 8.5° IN 1986"),
        // Suzuka - Wikipedia (Degner, the overpass), f1chronicle (130R), Wikipedia 1989 Japanese GP (the chicane after 130R)
        ("suzuka", "Degner", "ERNST DEGNER CRASHED HERE IN 1962"),
        ("suzuka", "130R", "NAMED FOR ITS 130 M RADIUS"),
        ("suzuka", "Casio", "SENNA AND PROST COLLIDED HERE IN 1989"),
        ("suzuka", "Back Straight", "CROSSES OVER THE FRONT HALF OF THE LAP"),
        // Silverstone - formula1.com "Explained: how every Silverstone corner got its name"; Hamilton Straight renamed 2020
        ("silverstone", "Abbey", "NAMED FOR THE RUINS OF LUFFIELD ABBEY"),
        ("silverstone", "Farm", "NAMED FOR THE FARM BY THE OLD STRAIGHT"),
        ("silverstone", "Village", "NAMED FOR SILVERSTONE VILLAGE"),
        ("silverstone", "Loop", "NAMED FOR ITS SHAPE"),
        ("silverstone", "Aintree", "FOR AINTREE, A PAST BRITISH GP HOST"),
        ("silverstone", "Wellington", "RUNWAYS OF THE WELLINGTON BOMBERS"),
        ("silverstone", "Brooklands", "FOR BROOKLANDS, A PAST BRITISH GP HOST"),
        ("silverstone", "Luffield", "NAMED FOR LUFFIELD ABBEY"),
        ("silverstone", "Woodcote", "NAMED FOR THE RAC'S WOODCOTE PARK"),
        ("silverstone", "Copse", "NAMED FOR THE SMALL WOODS AROUND IT"),
        ("silverstone", "Maggotts", "NAMED FOR MAGGOTS MOOR"),
        ("silverstone", "Becketts", "A MEDIEVAL CHAPEL TO THOMAS BECKET"),
        ("silverstone", "Chapel", "A MEDIEVAL CHAPEL TO THOMAS BECKET"),
        ("silverstone", "Hangar", "NAMED FOR THE AIRFIELD'S HANGARS"),
        ("silverstone", "Club", "A SALUTE TO THE RAC'S LONDON CLUBHOUSE"),
        ("silverstone", "Hamilton", "RENAMED FOR LEWIS HAMILTON IN 2020"),
        // Sebring - 24h-lemans.com "12 Hours of Sebring: legendary race track"; sportscar365 (Kristensen Corner, 2016)
        ("sebring", "Kristensen", "TOM KRISTENSEN · A RECORD SIX WINS"),
        ("sebring", "Gurney", "FOR DAN GURNEY · WINNER HERE IN 1959"),
        ("sebring", "Fangio", "FANGIO WON HERE IN 1956 AND 1957"),
        ("sebring", "Cunningham", "BRIGGS CUNNINGHAM'S CARS WON 1953-55"),
        ("sebring", "Collier", "SAM COLLIER HELPED LAY OUT THE TRACK"),
        ("sebring", "Flying Fortress", "HENDRICKS FIELD TRAINED B-17 CREWS HERE"),
        ("sebring", "Bishop", "FOR IMSA FOUNDER JOHN BISHOP"),
        ("sebring", "Gendebien", "GENDEBIEN WON HERE THREE YEARS RUNNING"),
        ("sebring", "Le Mans", "FOR THE RACE THAT INSPIRED SEBRING"),
        ("sebring", "Ulmann", "ALEC ULMANN FOUNDED THE 12 HOURS"),
        ("sebring", "Sunset", "INTO THE SETTING SUN LATE IN THE RACE"),
        // Monza - formula1.com "Explained: how every corner at Monza got its name"; Wikipedia (1972 chicane; Ascari died at
        // the Vialone, where the chicane now is); motorsport.com (Parabolica renamed for Alboreto, 2021)
        ("monza", "Rettifilo", "CHICANE OF THE STRAIGHT · SINCE 1972"),
        ("monza", "Curva Grande", "THE GREAT CURVE · ALSO CALLED BIASSONO"),
        ("monza", "Roggia", "NAMED FOR A NEARBY STREAM"),
        ("monza", "Lesmo", "ONCE KNOWN AS THE CURVE OF THE OAKS"),
        ("monza", "Ascari", "ALBERTO ASCARI DIED HERE IN 1955"),
        ("monza", "Parabolica", "RENAMED CURVA ALBORETO IN 2021"),
    };

    /// <summary>A line of heritage for this place on this track, or null.</summary>
    public static string? HeritageOf(string? trackRaw, string? name)
    {
        if (string.IsNullOrWhiteSpace(trackRaw) || string.IsNullOrWhiteSpace(name)) return null;
        foreach (var (t, w, line) in Heritage)
            if (trackRaw.StartsWith(t, StringComparison.OrdinalIgnoreCase) && name.Contains(w, StringComparison.OrdinalIgnoreCase)) return line;
        return ApexHUD.Native.Common.TrackPacks.Heritage(trackRaw, name);   // 2026-10-28: track packs (exact name)
    }

    /// <summary>The accent colour for this place (gold when it has no theme).</summary>
    public static SKColor Accent(string? name) => For(name)?.Eye ?? Gold;

    // ------------------------------------------------------------------ the themed label as a picture (small maps)
    static readonly Dictionary<string, SKImage?> labelImgs = new();
    /// <summary>The full themed label (Dunlop bridge, Michelin plate, PlayStation space, brand lettering, speed streaks...)
    /// rendered once and trimmed to its ink, for small panels that show it scaled down (2026-10-25, Angelo: "the themes
    /// of the corner and straights cards should show up in the tiny battle card mini map too"). No heritage line.</summary>
    public static SKImage? LabelImage(string eye, string name)
    {
        string key = (Refined ? "R|" : "") + eye + "|" + name;
        if (labelImgs.TryGetValue(key, out var hit)) return hit;
        if (labelImgs.Count > 80) { foreach (var v in labelImgs.Values) v?.Dispose(); labelImgs.Clear(); }
        SKImage? img = null;
        try
        {
            const int W = 1400, H = 420;
            using var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var bc = new SKCanvas(bmp)) { bc.Clear(SKColors.Transparent); DrawLabel(bc, W / 2f, 150f, eye, name, 1f, null, compact: true); }
            // trim to the ink
            int x0 = W, y0 = H, x1 = -1, y1 = -1;
            var px = bmp.GetPixelSpan();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (px[(y * W + x) * 4 + 3] > 24) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            if (x1 > x0 && y1 > y0)
            {
                using var sub = new SKBitmap();
                if (bmp.ExtractSubset(sub, new SKRectI(Math.Max(0, x0 - 2), Math.Max(0, y0 - 2), Math.Min(W, x1 + 3), Math.Min(H, y1 + 3))))
                    img = SKImage.FromPixelCopy(sub.Info, sub.GetPixels(), sub.RowBytes);   // its own copy of the pixels
            }
        }
        catch { img = null; }
        return labelImgs[key] = img;
    }

    // ------------------------------------------------------------------ the big centred place label (onboard)
    /// <summary>Draws the centred place label: eyebrow (INTO / THROUGH / DOWN) at <paramref name="ly"/>, the name under it,
    /// themed when the corner has a theme.</summary>
    /// <remarks>#482 (2026-10-28, Angelo): <paramref name="eyeFrom"/> / <paramref name="eyeMix"/> cross-fade only the eyebrow
    /// word (INTO -> THROUGH) while the rest of the card stays put.</remarks>
    /// <summary>#619: the lowest point the last <see cref="DrawLabel"/> drew to (its own coordinates): the heritage pill, the
    /// rule, or the Ford / Corvette extras. Written by every call; the onboard reads it straight after its own call.</summary>
    public static float LastLabelBottom { get; private set; }

    public static void DrawLabel(SKCanvas c, float cx, float ly, string eye, string name, float a, string? heritage = null, bool compact = false,
                                 string? eyeFrom = null, float eyeMix = 1f)
    {
        LastLabelBottom = ly + 66f;
        eyeMix = Math.Clamp(eyeMix, 0f, 1f);
        bool eyeFade = eyeFrom != null && eyeFrom != eye && eyeMix < 1f;
        var th = For(name);
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        // 2026-10-25 (Angelo: "keep the Michelin Chicane drawn exactly as is but center it as a card, not where the words
        // center"): the Michelin plate runs further right (Bibendum's end), so the whole drawing moves left by half that
        // extra, putting the PLATE's middle on the centre line; the heritage line stays centred under the plate
        float cardCx = cx;
        if (th?.Kind == Kind.Michelin && CornerBrand.User("michelin man") != null) cx -= (150f - 40f) / 2f;
        bool corvette = th?.Kind == Kind.Maker && Has(name, "Corvette");
        // a soft shadow under all the lettering so it reads over a bright sky
        using var shadow = Refined ? RefinedShadow(a) : SKImageFilter.CreateDropShadow(0, 1.5f, 2.2f, 2.2f, new SKColor(0, 0, 0, A(0.75f)));
        if (Refined) { var snapped = SnapY(c, ly); ly = snapped; }
        using var text = new SKPaint { IsAntialias = true, ImageFilter = shadow };
        using var fill = new SKPaint { IsAntialias = true };
        // the name is lettered like the brand's own lettering when the corner carries a logo (2026-10-25, Angelo)
        void NameFont(SKPaint p) { var (tf, size, skew, _) = NameStyle(th, name); p.Typeface = tf; p.TextSize = size; p.TextSkewX = skew; }
        float sp = NameStyle(th, name).Track;
        NameFont(text);
        while (text.MeasureText(name) + sp * name.Length > 620 && text.TextSize > 18) text.TextSize -= 0.5f;
        float nameSize = text.TextSize;
        float nw = text.MeasureText(name) + sp * Math.Max(0, name.Length - 1);
        // Porsche Curves: the real PORSCHE wordmark (Angelo's download, in Porsche red) stands in for the word
        // (2026-10-25: also Corvette's own CORVETTE lettering and Ford's script - "<maker> type.png" in corner_logos)
        SKBitmap? wordmark = null; string pre = "", rest = ""; float wmScale = 0.74f;
        if (th?.Kind == Kind.Maker)
            foreach (var (w, k) in new[] { ("Porsche", 0.74f), ("Corvette", 0.5f), ("Ford", 1.2f) })
                if (Has(name, w) && CornerBrand.User(w.ToLowerInvariant() + " type") is { } wm)
                {
                    wordmark = wm; wmScale = k;
                    var mm = System.Text.RegularExpressions.Regex.Match(name, $@"(?i)\b{w}\b");
                    pre = name[..mm.Index].Trim(); rest = name[(mm.Index + mm.Length)..].Trim();
                    break;
                }
        float wmH = nameSize * wmScale, wmW = wordmark != null ? wordmark.Width * (wmH / wordmark.Height) : 0, gap = nameSize * 0.42f;
        float W(string t) => t.Length == 0 ? 0 : text.MeasureText(t) + sp * Math.Max(0, t.Length - 1);
        if (wordmark != null) nw = (pre.Length > 0 ? W(pre) + gap : 0) + wmW + (rest.Length > 0 ? gap + W(rest) : 0);
        // every word in the brand's own letters when the logo gives us the whole alphabet the name needs
        // Corvette (2026-10-25, Angelo: "the word CORNER ... matches the font from the word CORVETTE perfectly"): the whole
        // name in the chrome letters cut from his corvette type picture
        string? glyphBrand = th?.Kind == Kind.Maker && Has(name, "Porsche") ? "porsche" : corvette ? "corvette" : null;   // Michelin keeps its heavy italic (Angelo: "that font was perfect")
        if (glyphBrand != null && !BrandGlyphs.CanSpell(glyphBrand, name)) glyphBrand = null;
        float gCap = glyphBrand == "michelin" ? nameSize * 0.62f : nameSize * 0.6f;
        if (glyphBrand == "corvette")
        {
            gCap = nameSize * 0.8f;                                   // the wide chrome letters, kept to a sensible width
            float w0 = BrandGlyphs.Measure("corvette", name, gCap);
            if (w0 > 640f) gCap *= 640f / w0;
        }
        if (glyphBrand != null) { nw = BrandGlyphs.Measure(glyphBrand, name, gCap); wordmark = null; }

        switch (th?.Kind)
        {
            case Kind.Dunlop: ly += DunlopDrop(ly, nw, th.Logo); DunlopArch(c, cx, ly, nw, eye, th.Logo, a, eyeFade ? eyeFrom : null, eyeMix); break;
            case Kind.PlayStation: SpacePlate(c, cx, ly, nw, th.Logo, a); break;
            case Kind.Michelin: MichelinPlate(c, cx, ly, nw, th.Logo, a); break;
        }
        LastLabelBottom = Math.Max(LastLabelBottom, ly + (th?.Kind == Kind.Dunlop ? 74f : 66f));   // #619 (the Dunlop arch moves the label down)

        // eyebrow (the Dunlop arch carries its own on the yellow strip)
        if (th?.Kind != Kind.Dunlop)
        {
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 14f; text.TextSkewX = 0;
            if (eyeFade)
            {
                text.Color = (th?.Eye ?? Gold).WithAlpha(A(1f - eyeMix)); Centred(c, text, eyeFrom!, cx, ly, 2.8f);
                if (Refined) EyebrowTicks(c, text, eyeFrom!, cx, ly, 2.8f, th?.Eye ?? Gold, a * (1f - eyeMix));
            }
            float em = eyeFade ? eyeMix : 1f;
            text.Color = (th?.Eye ?? Gold).WithAlpha(A(em));   // #340: was 12
            Centred(c, text, eye, cx, ly, 2.8f);
            if (Refined) EyebrowTicks(c, text, eye, cx, ly, 2.8f, th?.Eye ?? Gold, a * em);
        }
        NameFont(text); text.TextSize = nameSize;
        if (th?.Kind == Kind.Dunlop)
        {
            // Dunlop's lettering: white flared serif with a black edge
            using var edge = new SKPaint { IsAntialias = true, Typeface = text.Typeface, TextSize = text.TextSize, TextSkewX = text.TextSkewX, Style = SKPaintStyle.Stroke, StrokeWidth = 3.4f, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0x14, 0x14, 0x16, A(1)) };
            Centred(c, edge, name, cx, ly + 42f, sp);
        }
        text.Color = (th?.Kind is Kind.Dunlop ? SKColors.White : Ivory).WithAlpha(A(1));
        // Ford Chicanes (2026-10-25, Angelo: "figure out how to make the Ford script on that one"): the title IS the Ford
        // script, big, lit in Ford blue, with FIRST / SECOND CHICANE set small under it
        var fordScript = th?.Kind == Kind.Maker && Has(name, "Ford") ? CornerBrand.User("ford type") : null;
        float dropY = 0;
        if (fordScript != null)
        {
            float sh = 66f, swd = fordScript.Width * (sh / fordScript.Height), top = ly + 12f;
            var sr = new SKRect(cx - swd / 2f, top, cx + swd / 2f, top + sh);
            if (Refined) RadialGlow(c, new SKRect(sr.Left - 44, sr.Top - 6, sr.Right + 44, sr.Bottom + 6), th!.Rule, 0.5f * a);
            else
            using (var glow = new SKPaint { IsAntialias = true, Color = th!.Rule.WithAlpha(A(0.55f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16) })
                c.DrawOval(new SKRect(sr.Left - 30, sr.Top + 4, sr.Right + 30, sr.Bottom - 4), glow);
            using (var wp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(A(1)), ImageFilter = shadow })
                c.DrawBitmap(fordScript, sr, wp);
            string sub = System.Text.RegularExpressions.Regex.Replace(name, @"(?i)\b(the|ford)\b", "").Trim();
            sub = System.Text.RegularExpressions.Regex.Replace(sub, @"\s+", " ");
            using (var sp2 = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14f, Color = new SKColor(0xE4, 0xEE, 0xFF, A(1)), ImageFilter = shadow })
                Centred(c, sp2, sub, cx, top + sh + 18f, 5f);
            dropY = 54f; nw = swd; LastLabelBottom = Math.Max(LastLabelBottom, top + sh + 24f);
        }
        else if (glyphBrand != null)
        {
            if (glyphBrand == "corvette")                             // chrome reads poorly on sky: a soft dark bed under it
                using (var bed = new SKPaint { IsAntialias = true, Color = new SKColor(0x05, 0x06, 0x0A, A(0.55f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
                    c.DrawRoundRect(new SKRect(cx - nw / 2f - 16f, ly + 42f - gCap - 10f, cx + nw / 2f + 16f, ly + 42f + 10f), 14, 14, bed);
            BrandGlyphs.Draw(c, glyphBrand, name, cx - nw / 2f, ly + 42f, gCap, a, shadow);
        }
        else if (wordmark != null)
        {
            float x0 = cx - nw / 2f, baseY = ly + 42f;
            if (pre.Length > 0) { TextTracked.Draw(c, text, pre, x0, baseY, sp); x0 += W(pre) + gap; }
            // the wordmark sits on the text's baseline (a script like Ford's centres on the capitals instead)
            float capH = nameSize * 0.72f, top = wmScale > 1f ? baseY - capH / 2f - wmH / 2f : baseY - wmH;
            using (var wp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(A(1)), ImageFilter = shadow })
                c.DrawBitmap(wordmark, new SKRect(x0, top, x0 + wmW, top + wmH), wp);
            if (rest.Length > 0) TextTracked.Draw(c, text, rest, x0 + wmW + gap, baseY, sp);
        }
        else Centred(c, text, name, cx, ly + 42f, sp);
        text.TextSkewX = 0;

        // the maker's logo beside the name (brand plates draw their own)
        // Corvette (2026-10-25, Angelo: "too big with the logos on either side ... put the corvette logo above or below
        // it"): one Corvette badge centred UNDER the word, with its own room; everything below moves down to make space
        if (corvette && !compact && th!.Logo is { } cvl)
        {
            float bh = 56f, by = ly + 82f;
            if (Refined) RadialGlow(c, new SKRect(cx - 92f, by - 40f, cx + 92f, by + 40f), th.Rule, 0.26f * a);
            else
            using (var glow = new SKPaint { IsAntialias = true, Color = th.Rule.WithAlpha(A(0.26f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) })
                c.DrawOval(new SKRect(cx - 70f, by - 26f, cx + 70f, by + 26f), glow);
            float bw = Math.Min(170f, cvl.Width * (bh / cvl.Height));
            CornerBrand.Draw(c, cvl, cx - bw / 2f, by, bw, bh, a);
            dropY = 66f;
            LastLabelBottom = Math.Max(LastLabelBottom, by + bh + 4f);
        }
        else if (th is { Kind: Kind.Maker, Logo: { } lg } && fordScript == null && !(corvette && compact) && !Has(name, "Porsche"))   // 2026-10-25: no badges beside Porsche Curves (Angelo)
        {
            // the maker's badge on BOTH sides of the name (2026-10-25, Angelo), each with a soft glow in the maker's colour
            for (int side = -1; side <= 1; side += 2)
            {
                float gx = cx + side * (nw / 2f + 60f);
                if (Refined) RadialGlow(c, new SKRect(gx - 54f, ly + 28f - 54f, gx + 54f, ly + 28f + 54f), th.Rule, 0.26f * a);
                else
                using (var glow = new SKPaint { IsAntialias = true, Color = th.Rule.WithAlpha(A(0.24f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) })
                    c.DrawCircle(gx, ly + 28f, 34f, glow);
                CornerBrand.Draw(c, lg, cx + side * (nw / 2f + 18f), ly + 28f, 90f, 40f, a, alignRight: side < 0);
            }
        }

        // a line of heritage under it
        if (!string.IsNullOrEmpty(heritage))
        {
            // 2026-10-25 (Angelo: "make sure the words underneath can show up well too"): brighter, a touch bigger, on a
            // soft dark pill so they read over sky, sun or tarmac
            // 2026-10-25 (Angelo: "the text below the corner names needs to be larger, it's hard to read"): 17 px (was 12)
            using var hp = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 17f, Color = new SKColor(0xF6, 0xF7, 0xFA, A(1)), ImageFilter = shadow };
            float hy = ly + (th?.Kind == Kind.Dunlop ? 104f : (th?.Kind == Kind.Michelin ? 90f : 84f)) + dropY;
            LastLabelBottom = Math.Max(LastLabelBottom, hy + 10f);
            float hw = hp.MeasureText(heritage) + 2.4f * Math.Max(0, heritage.Length - 1);
            if (Refined) HeritagePill(c, hp, heritage, cardCx, hy, hw, th?.Rule ?? Gold, a);
            else
            {
                using (var pill = new SKPaint { IsAntialias = true, Color = new SKColor(0x06, 0x08, 0x0C, A(0.66f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3) })
                    c.DrawRoundRect(new SKRect(cardCx - hw / 2 - 18, hy - 19, cardCx + hw / 2 + 18, hy + 8), 12, 12, pill);
                Centred(c, hp, heritage, cardCx, hy, 2.4f);
            }
        }

        // the rule under it, in the theme's colour
        if (th?.Kind != Kind.Dunlop && fordScript == null)
        {
            var rc = th?.Rule ?? Gold;
            float rw = Math.Max(160f, nw + 60f);
            using var rule = SKShader.CreateLinearGradient(new SKPoint(cx - rw / 2, 0), new SKPoint(cx + rw / 2, 0),
                new[] { rc.WithAlpha(0), rc.WithAlpha(A(0.86f)), rc.WithAlpha(0) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
            if (th?.Kind == Kind.Bricks)
            {
                // a strip of the Brickyard's bricks, fading out to both sides
                int layer = c.SaveLayer(new SKRect(cx - rw / 2, ly + 52f, cx + rw / 2, ly + 64f), null);
                using (var bp = new SKPaint { IsAntialias = true })
                    for (int row = 0; row < 2; row++)
                        for (float bx = cx - rw / 2 - (row == 1 ? 9f : 0f); bx < cx + rw / 2; bx += 19f)
                        {
                            bp.Color = (((int)((bx - cx) / 19f) + row) % 3 == 0 ? new SKColor(0x8E, 0x3A, 0x24) : rc).WithAlpha(A(0.95f));
                            c.DrawRoundRect(new SKRect(bx, ly + 53f + row * 5.5f, bx + 17f, ly + 57.5f + row * 5.5f), 1f, 1f, bp);
                        }
                using (var mask = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateLinearGradient(new SKPoint(cx - rw / 2, 0), new SKPoint(cx + rw / 2, 0),
                           new[] { SKColors.Transparent, SKColors.White, SKColors.Transparent }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
                    c.DrawRect(new SKRect(cx - rw / 2, ly + 52f, cx + rw / 2, ly + 64f), mask);
                c.RestoreToCount(layer);
            }
            else if (th?.Kind == Kind.Speed)
            {
                // speed streaks: the straight where they did 400 km/h
                var rnd = new Random(name.Length * 31);
                for (int i = 0; i < 9; i++)
                {
                    float len = rw * (0.25f + (float)rnd.NextDouble() * 0.6f), x0 = cx - rw / 2 + (float)rnd.NextDouble() * (rw - len);
                    using var sk = SKShader.CreateLinearGradient(new SKPoint(x0, 0), new SKPoint(x0 + len, 0), new[] { rc.WithAlpha(0), rc.WithAlpha(A(0.75f)) }, null, SKShaderTileMode.Clamp);
                    fill.Shader = sk; c.DrawRect(x0, ly + 52f + i * 1.6f, len, 1f, fill); fill.Shader = null;
                }
            }
            else if (th?.Kind == Kind.Portrait)
            {
                // an honour-roll rule: two fine gold hairlines with a small diamond between them
                float ry = ly + 56f, half = rw / 2f - 14f;
                for (int side = -1; side <= 1; side += 2)
                {
                    using var hl = SKShader.CreateLinearGradient(new SKPoint(cx + side * 12f, 0), new SKPoint(cx + side * (12f + half), 0),
                        new[] { rc.WithAlpha(A(0.95f)), rc.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
                    fill.Shader = hl;
                    c.DrawRect(Math.Min(cx + side * 12f, cx + side * (12f + half)), ry - 2.2f, half, 1f, fill);
                    c.DrawRect(Math.Min(cx + side * 12f, cx + side * (12f + half)), ry + 2.2f, half, 1f, fill);
                    fill.Shader = null;
                }
                using (var dp = new SKPaint { IsAntialias = true, Color = th.Eye.WithAlpha(A(1)) })
                using (var dm = new SKPath())
                {
                    dm.MoveTo(cx, ry - 5.5f); dm.LineTo(cx + 5.5f, ry + 0.5f); dm.LineTo(cx, ry + 6.5f); dm.LineTo(cx - 5.5f, ry + 0.5f); dm.Close();
                    c.DrawPath(dm, dp);
                }
                if (Refined) RadialGlow(c, new SKRect(cx - rw / 2.4f, ly + 40f, cx + rw / 2.4f, ly + 74f), rc, 0.18f * a);
            }
            else if (Refined) LitRule(c, cx, ly + 56f + (corvette ? dropY + 4f : 0f), rw, th?.Kind is Kind.Michelin or Kind.PlayStation or Kind.Earth ? 2f : 1.4f, rc, a);
            else { fill.Shader = rule; c.DrawRect(cx - rw / 2, ly + 56f + (corvette ? dropY + 4f : 0f), rw, th?.Kind is Kind.Michelin or Kind.PlayStation or Kind.Earth ? 2f : 1.2f, fill); fill.Shader = null; }
            if (th?.Kind == Kind.Earth && Refined) RadialGlow(c, new SKRect(cx - rw / 2.1f, ly + 36f, cx + rw / 2.1f, ly + 78f), rc, 0.26f * a);
            else if (th?.Kind == Kind.Earth)
                using (var glow = new SKPaint { IsAntialias = true, Color = rc.WithAlpha(A(0.28f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 22) })
                    c.DrawOval(new SKRect(cx - rw / 2.4f, ly + 44f, cx + rw / 2.4f, ly + 70f), glow);
        }
    }

    /// <summary>A compact, left-aligned version of the themed label for small panels (the battle map header): the
    /// eyebrow in the theme's colour, the name in the brand's lettering where there is one (Porsche letters, the Ford
    /// script, the Corvette wordmark, Dunlop's edged serif, Michelin's italic), the maker / brand logo at the right.
    /// Returns the colour used, so the panel can match it.</summary>
    public static SKColor DrawCompact(SKCanvas c, float x, float top, float maxW, string eye, string name, float a)
    {
        var th = For(name);
        var acc = th?.Eye ?? Gold;
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        using var shadow = Refined ? RefinedShadow(a, 0.7f) : SKImageFilter.CreateDropShadow(0, 1f, 1.6f, 1.6f, new SKColor(0, 0, 0, A(0.7f)));
        using (var ep = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f, Color = acc.WithAlpha(A(1)) })   // #340: was 10.5
            TextTracked.DrawHalo(c, ep, eye, x, top + 12, 2.0f);
        if (name.Length == 0) return acc;
        // the logo on the right (not for Dunlop - its lettering is the brand)
        float logoW = 0;
        var logo = th?.Kind switch { Kind.PlayStation or Kind.Michelin or Kind.Maker => th.Logo, _ => null };
        if (logo != null && !(th!.Kind == Kind.Maker && Has(name, "Ford")))
            logoW = CornerBrand.Draw(c, logo, x + maxW, top + 30, 52, 28, a, alignRight: true) + 10;
        float avail = maxW - logoW, baseY = top + 40;
        // brand letters / wordmarks
        foreach (var (w, gb, cap0) in new[] { ("Porsche", "porsche", 15f), ("Corvette", "corvette", 14f) })   // Corvette too (2026-10-25)
            if (th?.Kind == Kind.Maker && Has(name, w) && BrandGlyphs.CanSpell(gb, name))
            {
                float cap = cap0;
                while (BrandGlyphs.Measure(gb, name, cap) > avail && cap > 7) cap -= 0.5f;
                BrandGlyphs.Draw(c, gb, name, x, baseY, cap, a, shadow);
                return acc;
            }
        foreach (var (w, word) in new[] { ("Ford", "ford type"), ("Corvette", "corvette type") })
            if (th?.Kind == Kind.Maker && Has(name, w) && CornerBrand.User(word) is { } wm)
            {
                bool script = w == "Ford";
                float h = script ? 30f : 12f, ww = wm.Width * (h / wm.Height);
                if (ww > avail) { h *= avail / ww; ww = avail; }
                using (var wp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(A(1)), ImageFilter = shadow })
                    c.DrawBitmap(wm, new SKRect(x, baseY - h + (script ? 6 : 0), x + ww, baseY + (script ? 6 : 0)), wp);
                string rest = System.Text.RegularExpressions.Regex.Replace(name, $@"(?i)\b(the|{w})\b", "").Trim();
                rest = System.Text.RegularExpressions.Regex.Replace(rest, @"\s+", " ");
                if (rest.Length > 0)
                    using (var rp = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f, Color = Ivory.WithAlpha(A(1f)), ImageFilter = shadow })   // #340: was 11 @0.9
                        TextTracked.Draw(c, rp, rest, x + ww + 10, baseY - 2, 1.8f);
                return acc;
            }
        var (tf, size, skew, track) = NameStyle(th, name);
        using var np = new SKPaint { IsAntialias = true, Typeface = tf, TextSize = size * 0.52f, TextSkewX = skew, Color = (th?.Kind == Kind.Dunlop ? SKColors.White : Ivory).WithAlpha(A(1)), ImageFilter = shadow };
        float sp = track * 0.5f;
        while (np.MeasureText(name) + sp * name.Length > avail && np.TextSize > 10) np.TextSize -= 0.5f;
        if (th?.Kind == Kind.Dunlop)
            using (var edge = new SKPaint { IsAntialias = true, Typeface = tf, TextSize = np.TextSize, TextSkewX = skew, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0x14, 0x14, 0x16, A(1)) })
                TextTracked.Draw(c, edge, name, x, baseY, sp);
        TextTracked.Draw(c, np, name, x, baseY, sp);
        return acc;
    }

    /// <summary>The lettering for a corner's name: the brand's style where there is one (closest of our typefaces:
    /// Dunlop's flared serif, Michelin's heavy italic, PlayStation's clean wide sans, the makers' extended caps).</summary>
    static (SKTypeface Tf, float Size, float Skew, float Track) NameStyle(Theme? th, string name)
    {
        switch (th?.Kind)
        {
            case Kind.Dunlop: return (Fonts.CormorantGaramond.Bold, 44f, -0.12f, 1.2f);
            case Kind.Michelin: return (Fonts.BarlowCondensed.Black, 46f, -0.24f, 1.4f);
            case Kind.PlayStation: return (Fonts.Inter.SemiBold, 32f, 0f, 3.6f);
            case Kind.Maker:
                if (Has(name, "Porsche")) return (Fonts.Inter.Bold, 30f, 0f, 7f);            // Porsche's wide-spaced capitals
                if (Has(name, "Corvette")) return (Fonts.Inter.SemiBold, 30f, 0f, 8f);       // the extended Corvette wordmark
                if (Has(name, "Ford")) return (Fonts.Inter.Bold, 31f, -0.06f, 2.4f);         // Ford's own sans (the oval is the script)
                if (Has(name, "Ferrari")) return (Fonts.BarlowCondensed.ExtraBold, 42f, -0.14f, 2f);
                return (Fonts.Inter.Bold, 30f, 0f, 4f);
            default: return (Fonts.CormorantGaramond.Bold, 40f, 0f, 0.6f);
        }
    }

    static void Centred(SKCanvas c, SKPaint p, string s, float cx, float y, float sp)
    {
        p.TextAlign = SKTextAlign.Left;
        float w = p.MeasureText(s) + sp * Math.Max(0, s.Length - 1);
        TextTracked.Draw(c, p, s, cx - w / 2f, y, sp);
    }

    // the real Dunlop bridge, cut out of Angelo's photo (dunlop.png): where the road opening is, as fractions of the picture
    const float DOpenL = 274f / 1200f, DOpenR = 1000f / 1200f, DDeck = 209f / 289f;

    /// <summary>How far the label must move down so the bridge above the name stays on screen.</summary>
    static float DunlopDrop(float ly, float nw, SKBitmap? bmp)
    {
        if (bmp == null) return 0;
        float s = DunlopScale(nw, bmp);
        float topAbove = bmp.Height * s * DDeck - 6f;          // from the deck (just above the name) up to the arch top
        return Math.Max(0f, topAbove + 10f - (ly - 6f));
    }

    static float DunlopScale(float nw, SKBitmap bmp)
    {
        float openH = bmp.Height * (1 - DDeck), openW = bmp.Width * (DOpenR - DOpenL);
        return Math.Max(50f / openH, (nw + 50f) / openW);
    }

    /// <summary>The name drives under the real Dunlop bridge: the photo cut-out sits over it, the name in the road opening
    /// between the piers, INTO / THROUGH in Dunlop yellow under the road.</summary>
    static void DunlopArch(SKCanvas c, float cx, float ly, float nw, string eye, SKBitmap? bmp, float a, string? eyeFrom = null, float eyeMix = 1f)
    {
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        if (bmp == null) return;
        float s = DunlopScale(nw, bmp);
        float w = bmp.Width * s, h = bmp.Height * s;
        float deckY = ly + 6f;                                   // the deck sits just above the name
        float openMid = (DOpenL + DOpenR) / 2f;
        var dst = new SKRect(cx - w * openMid, deckY - h * DDeck, cx - w * openMid + w, deckY - h * DDeck + h);
        // soften the picture's ends into the screen
        int layer = c.SaveLayer(dst, null);
        using (var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(A(1)) }) c.DrawBitmap(bmp, dst, p);
        using (var mask = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateLinearGradient(new SKPoint(dst.Left, 0), new SKPoint(dst.Right, 0),
                   new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, new[] { 0f, 0.07f, 0.93f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(dst, mask);
        c.RestoreToCount(layer);
        if (Refined)
        {
            // the bridge's contact shadow falling on the name under the deck (inside the road opening only)
            float ow = w * (DOpenR - DOpenL) - 8f;
            using var cs = SKShader.CreateLinearGradient(new SKPoint(0, deckY - 1f), new SKPoint(0, deckY + 16f),
                new[] { new SKColor(0, 0, 0, A(0.42f)), new SKColor(0, 0, 0, 0) }, null, SKShaderTileMode.Clamp);
            using var csp = new SKPaint { IsAntialias = true, Shader = cs };
            c.DrawRect(new SKRect(cx - ow / 2f, deckY - 1f, cx + ow / 2f, deckY + 16f), csp);
            // the road: a tapered yellow line, brightest in the middle
            float rw = nw + 40f;
            using var rs = SKShader.CreateLinearGradient(new SKPoint(cx - rw / 2f, 0), new SKPoint(cx + rw / 2f, 0),
                new[] { DYellow.WithAlpha(0), DYellow.WithAlpha(A(0.85f)), DYellow.WithAlpha(A(0.85f)), DYellow.WithAlpha(0) }, new[] { 0f, 0.18f, 0.82f, 1f }, SKShaderTileMode.Clamp);
            using (var road = new SKPaint { IsAntialias = true, Shader = rs }) c.DrawRect(cx - rw / 2f, ly + 56f, rw, 2f, road);
        }
        else
        // the road under the bridge, and INTO / THROUGH in Dunlop yellow beneath it
        using (var road = new SKPaint { Color = DYellow.WithAlpha(A(0.75f)) }) c.DrawRect(cx - nw / 2f - 20f, ly + 56f, nw + 40f, 2f, road);
        using (var sh2 = Refined ? RefinedShadow(a) : SKImageFilter.CreateDropShadow(0, 1.5f, 2.2f, 2.2f, new SKColor(0, 0, 0, A(0.75f))))
        using (var ep = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14f, Color = DYellow.WithAlpha(A(eyeFrom != null ? eyeMix : 1)), ImageFilter = sh2 })   // #340: was 12
        {
            Centred(c, ep, eye, cx, ly + 77f, 2.8f);
            if (eyeFrom != null) { ep.Color = DYellow.WithAlpha(A(1 - eyeMix)); Centred(c, ep, eyeFrom, cx, ly + 77f, 2.8f); }
        }
    }

    /// <summary>The PlayStation plate: a strip of deep space behind the name, the symbol at its start.</summary>
    static void SpacePlate(SKCanvas c, float cx, float ly, float nw, SKBitmap? logo, float a)
    {
        var bg = CornerBrand.User("playstation bg");
        float pad = logo != null ? 96f : 44f;                       // the symbol on BOTH sides of the name (2026-10-25, Angelo)
        float left = cx - nw / 2f - pad, right = cx + nw / 2f + pad;
        var r = new SKRect(left, ly - 26f, right, ly + 66f);
        int layer = c.SaveLayer(new SKRect(r.Left - 2, r.Top - 2, r.Right + 2, r.Bottom + 2), null);
        using (var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(235 * a)) })
        {
            c.Save(); c.ClipRoundRect(new SKRoundRect(r, 12, 12), antialias: true);
            if (bg != null)
            {
                float s = Math.Max(r.Width / bg.Width, r.Height / bg.Height);
                float w = bg.Width * s, h = bg.Height * s;
                c.DrawBitmap(bg, new SKRect(r.MidX - w / 2, r.MidY - h / 2, r.MidX + w / 2, r.MidY + h / 2), p);
            }
            else if (Refined) Nebula(c, r, a);
            else { p.Color = new SKColor(0x10, 0x0C, 0x3A, (byte)(235 * a)); c.DrawRect(r, p); }
            if (Refined)
            {
                // a lit top edge in PlayStation blue and a faint floor reflection
                using var edge = new SKPaint { IsAntialias = true, Color = PsLight.WithAlpha((byte)(110 * a)) };
                c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + 1.2f), edge);
                using var floor = SKShader.CreateLinearGradient(new SKPoint(0, r.Bottom - 22f), new SKPoint(0, r.Bottom),
                    new[] { PsBlue.WithAlpha(0), PsBlue.WithAlpha((byte)(70 * a)) }, null, SKShaderTileMode.Clamp);
                using var fp = new SKPaint { IsAntialias = true, Shader = floor };
                c.DrawRect(new SKRect(r.Left, r.Bottom - 22f, r.Right, r.Bottom), fp);
            }
            c.Restore();
        }
        // feather both ends into the picture
        using (var mask = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0),
                   new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, new[] { 0f, 0.12f, 0.88f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(r, mask);
        c.RestoreToCount(layer);
        if (logo != null)
        {
            CornerBrand.Draw(c, logo, r.Left + 36f, ly + 22f, 50f, 40f, a);
            CornerBrand.Draw(c, logo, r.Right - 36f, ly + 22f, 50f, 40f, a, alignRight: true);
        }
    }

    /// <summary>The Michelin plate: Michelin blue, the yellow edge, Bibendum peeking in at the end.</summary>
    static void MichelinPlate(SKCanvas c, float cx, float ly, float nw, SKBitmap? logo, float a)
    {
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        var man = CornerBrand.User("michelin man");
        float left = cx - nw / 2f - 40f, right = cx + nw / 2f + (man != null ? 150f : 40f);
        var r = new SKRect(left, ly - 26f, right, ly + 66f);
        using var fill = new SKPaint { IsAntialias = true };
        if (Refined)
        {
            // contact + ambient shadow (the plate sits on the picture instead of floating in a grey cloud)
            using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, A(0.30f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
                c.DrawRoundRect(new SKRect(r.Left + 6, r.Top + 10, r.Right - 6, r.Bottom + 10), 14, 14, sh);
            using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, A(0.45f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3) })
                c.DrawRoundRect(new SKRect(r.Left + 1, r.Top + 2, r.Right - 1, r.Bottom + 2), 14, 14, sh);
        }
        else
        using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, A(0.4f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10) })
            c.DrawRoundRect(new SKRect(r.Left, r.Top + 6, r.Right, r.Bottom + 6), 14, 14, sh);
        fill.Color = MBlue.WithAlpha(A(0.94f)); c.DrawRoundRect(r, 14, 14, fill);
        c.Save(); c.ClipRoundRect(new SKRoundRect(r, 14, 14), antialias: true);
        fill.Color = MYellow.WithAlpha(A(1)); c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 9, r.Bottom), fill);
        if (man != null)
        {
            float mh = r.Height * 0.98f, mw = man.Width * (mh / man.Height);
            using var mp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(A(1)) };
            c.DrawBitmap(man, new SKRect(r.Right - mw + 4, r.Bottom - mh + 4, r.Right + 4, r.Bottom + 4), mp);
        }
        if (Refined)
        {
            // glass sheen over the top third, under Bibendum's head
            using var sheen = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Top + r.Height * 0.45f),
                new[] { new SKColor(255, 255, 255, A(0.13f)), new SKColor(255, 255, 255, 0) }, null, SKShaderTileMode.Clamp);
            using var sp = new SKPaint { IsAntialias = true, Shader = sheen };
            c.DrawRect(new SKRect(r.Left + 9, r.Top, r.Right, r.Top + r.Height * 0.45f), sp);
        }
        c.Restore();
        if (Refined)
        {
            // bevel: light along the top edge, dark along the bottom
            using var bev = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                new[] { new SKColor(255, 255, 255, A(0.42f)), new SKColor(255, 255, 255, A(0.06f)), new SKColor(0, 0, 0, A(0.35f)) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
            using var hl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Shader = bev };
            c.DrawRoundRect(new SKRect(r.Left + 0.6f, r.Top + 0.6f, r.Right - 0.6f, r.Bottom - 0.6f), 13.4f, 13.4f, hl);
        }
        else
        using (var hl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(255, 255, 255, A(0.35f)) }) c.DrawRoundRect(r, 14, 14, hl);
    }

    // ------------------------------------------------------------------ small chips (track card lap walk)
    /// <summary>Paints a themed chip background; returns the accent colour to use for its edge and eyebrow.</summary>
    public static SKColor Chip(SKCanvas c, SKRect box, string name, float a)
    {
        var th = For(name);
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        using var fill = new SKPaint { IsAntialias = true };
        switch (th?.Kind)
        {
            case Kind.Michelin:
                fill.Color = MBlue.WithAlpha(A(0.95f)); c.DrawRoundRect(box, 6, 6, fill);
                if (Refined) ChipEdge(c, box, MYellow, a);
                return MYellow;
            case Kind.PlayStation:
                var bg = CornerBrand.User("playstation bg");
                c.Save(); c.ClipRoundRect(new SKRoundRect(box, 6, 6), antialias: true);
                if (bg != null)
                {
                    float s = Math.Max(box.Width / bg.Width, box.Height / bg.Height);
                    using var p = new SKPaint { FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(A(1)) };
                    c.DrawBitmap(bg, new SKRect(box.MidX - bg.Width * s / 2, box.MidY - bg.Height * s / 2, box.MidX + bg.Width * s / 2, box.MidY + bg.Height * s / 2), p);
                }
                c.Restore();
                return PsLight;
            case Kind.Dunlop:
                fill.Color = new SKColor(0x3A, 0x3D, 0x42, A(0.95f)); c.DrawRoundRect(box, 6, 6, fill);
                fill.Color = DYellow.WithAlpha(A(1)); c.DrawRect(new SKRect(box.Left + 6, box.Bottom - 5, box.Right - 6, box.Bottom - 2), fill);
                if (Refined) ChipEdge(c, box, DYellow, a);
                return DYellow;
            default:
                fill.Color = new SKColor(0x0B, 0x0E, 0x14, A(0.86f)); c.DrawRoundRect(box, 6, 6, fill);
                if (Refined) ChipEdge(c, box, th?.Rule ?? Gold, a);
                return th?.Rule ?? Gold;
        }
    }

    // ------------------------------------------------------------------ refined finish (#115 / #118), all OFF unless Refined
    /// <summary>Contact shadow (tight, dark) under an ambient one (soft, wide): crisper edges, no grey halo.</summary>
    static SKImageFilter RefinedShadow(float a, float k = 1f)
    {
        byte A(float v) => (byte)Math.Clamp(v * 255f * a * k, 0, 255);
        var contact = SKImageFilter.CreateDropShadow(0, 1f, 1.1f, 1.1f, new SKColor(0, 0, 0, A(0.62f)));
        return SKImageFilter.CreateDropShadow(0, 3f, 6f, 6f, new SKColor(0, 0, 0, A(0.34f)), contact);
    }

    /// <summary>The baseline on a whole device pixel (crisper caps on the TV).</summary>
    static float SnapY(SKCanvas c, float y)
    {
        var m = c.TotalMatrix;
        if (Math.Abs(m.SkewX) > 1e-4f || Math.Abs(m.SkewY) > 1e-4f || m.ScaleY <= 0 || m.Persp2 != 1f) return y;   // only plain scale / translate
        float dy = m.ScaleY * y + m.TransY;
        return (MathF.Round(dy) - m.TransY) / m.ScaleY;
    }

    /// <summary>Two short accent ticks either side of the eyebrow, fading outwards.</summary>
    static void EyebrowTicks(SKCanvas c, SKPaint p, string eye, float cx, float y, float sp, SKColor col, float a)
    {
        float w = p.MeasureText(eye) + sp * Math.Max(0, eye.Length - 1);
        float mid = y - p.TextSize * 0.36f, len = 22f, gap = 12f;
        using var tick = new SKPaint { IsAntialias = true };
        for (int side = -1; side <= 1; side += 2)
        {
            float x0 = side < 0 ? cx - w / 2f - gap - len : cx + w / 2f + gap;
            using var sh = SKShader.CreateLinearGradient(new SKPoint(x0, 0), new SKPoint(x0 + len, 0),
                side < 0 ? new[] { col.WithAlpha(0), col.WithAlpha((byte)(190 * a)) } : new[] { col.WithAlpha((byte)(190 * a)), col.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
            tick.Shader = sh;
            c.DrawRect(new SKRect(x0, mid - 0.6f, x0 + len, mid + 0.6f), tick);
            tick.Shader = null;
        }
    }

    /// <summary>A smooth radial glow filling the oval (no blur pass, no banding at 4K).</summary>
    static void RadialGlow(SKCanvas c, SKRect oval, SKColor col, float alpha)
    {
        if (alpha <= 0.003f) return;
        byte a0 = (byte)Math.Clamp(255 * alpha, 0, 255);
        c.Save();
        c.Translate(oval.MidX, oval.MidY);
        c.Scale(oval.Width / 2f, oval.Height / 2f);
        using var sh = SKShader.CreateRadialGradient(SKPoint.Empty, 1f,
            new[] { col.WithAlpha(a0), col.WithAlpha((byte)(a0 * 0.45f)), col.WithAlpha((byte)(a0 * 0.12f)), col.WithAlpha(0) },
            new[] { 0f, 0.38f, 0.7f, 1f }, SKShaderTileMode.Clamp);
        using var p = new SKPaint { IsAntialias = true, Shader = sh };
        c.DrawCircle(0, 0, 1f, p);
        c.Restore();
    }

    /// <summary>The rule: a hairline with a lit plateau in the middle and a soft glow under it.</summary>
    static void LitRule(SKCanvas c, float cx, float y, float rw, float h, SKColor rc, float a)
    {
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        RadialGlow(c, new SKRect(cx - rw * 0.42f, y - 7f, cx + rw * 0.42f, y + h + 7f), rc, 0.22f * a);
        using var sh = SKShader.CreateLinearGradient(new SKPoint(cx - rw / 2, 0), new SKPoint(cx + rw / 2, 0),
            new[] { rc.WithAlpha(0), rc.WithAlpha(A(0.7f)), rc.WithAlpha(A(0.95f)), rc.WithAlpha(A(0.7f)), rc.WithAlpha(0) },
            new[] { 0f, 0.3f, 0.5f, 0.7f, 1f }, SKShaderTileMode.Clamp);
        using var p = new SKPaint { IsAntialias = true, Shader = sh };
        c.DrawRect(cx - rw / 2, y, rw, h, p);
        // the white-hot core
        using var core = SKShader.CreateLinearGradient(new SKPoint(cx - rw * 0.18f, 0), new SKPoint(cx + rw * 0.18f, 0),
            new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(A(0.55f)), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        p.Shader = core;
        c.DrawRect(cx - rw * 0.18f, y + h * 0.25f, rw * 0.36f, Math.Max(0.6f, h * 0.5f), p);
    }

    /// <summary>The heritage line on a true capsule: the same shape and place for every theme, the text centred on its cap
    /// height, accent dots at both ends, a hairline edge lit from above.</summary>
    static void HeritagePill(SKCanvas c, SKPaint hp, string heritage, float cx, float hy, float hw, SKColor accent, float a)
    {
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        const float H = 30f, padX = 30f;
        float capH = hp.TextSize * 0.727f;                     // Inter's cap height
        float midY = hy - 6.5f;                                // same centre as the old pill (hy-19 .. hy+8)
        var r = new SKRect(cx - hw / 2f - padX, midY - H / 2f, cx + hw / 2f + padX, midY + H / 2f);
        using (var amb = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, A(0.30f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) })
            c.DrawRoundRect(new SKRect(r.Left + 4, r.Top + 3, r.Right - 4, r.Bottom + 4), H / 2f, H / 2f, amb);
        using (var bg = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                   new[] { new SKColor(0x10, 0x14, 0x1C, A(0.78f)), new SKColor(0x05, 0x07, 0x0B, A(0.70f)) }, null, SKShaderTileMode.Clamp))
        using (var fp = new SKPaint { IsAntialias = true, Shader = bg })
            c.DrawRoundRect(r, H / 2f, H / 2f, fp);
        using (var edge = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                   new[] { new SKColor(255, 255, 255, A(0.20f)), new SKColor(255, 255, 255, A(0.04f)) }, null, SKShaderTileMode.Clamp))
        using (var ep = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Shader = edge })
            c.DrawRoundRect(new SKRect(r.Left + 0.5f, r.Top + 0.5f, r.Right - 0.5f, r.Bottom - 0.5f), H / 2f - 0.5f, H / 2f - 0.5f, ep);
        using (var dot = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(A(0.95f)) })
        {
            c.DrawCircle(r.Left + 15f, midY, 2.6f, dot);
            c.DrawCircle(r.Right - 15f, midY, 2.6f, dot);
        }
        Centred(c, hp, heritage, cx, MathF.Round(midY + capH / 2f), 2.4f);
    }

    /// <summary>Fallback space for the PlayStation plate when there's no background picture: a nebula and a few stars.</summary>
    static void Nebula(SKCanvas c, SKRect r, float a)
    {
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(0x0A, 0x08, 0x2A, (byte)(235 * a)) };
        c.DrawRect(r, p);
        RadialGlow(c, new SKRect(r.Left + r.Width * 0.05f, r.Top - r.Height * 0.4f, r.Left + r.Width * 0.6f, r.Bottom + r.Height * 0.3f), new SKColor(0x3A, 0x2A, 0xB8), 0.55f * a);
        RadialGlow(c, new SKRect(r.Left + r.Width * 0.45f, r.Top - r.Height * 0.2f, r.Right + r.Width * 0.05f, r.Bottom + r.Height * 0.5f), PsBlue, 0.45f * a);
        var rnd = new Random(7);
        p.Color = SKColors.White;
        for (int i = 0; i < 48; i++)
        {
            float x = r.Left + (float)rnd.NextDouble() * r.Width, y = r.Top + (float)rnd.NextDouble() * r.Height;
            float s = 0.4f + (float)rnd.NextDouble() * (i % 9 == 0 ? 1.3f : 0.6f);
            p.Color = SKColors.White.WithAlpha((byte)((80 + rnd.Next(140)) * a));
            c.DrawCircle(x, y, s, p);
        }
    }

    /// <summary>A lap-walk chip's hairline edge in its accent colour, lit from above.</summary>
    static void ChipEdge(SKCanvas c, SKRect box, SKColor accent, float a)
    {
        using var sh = SKShader.CreateLinearGradient(new SKPoint(0, box.Top), new SKPoint(0, box.Bottom),
            new[] { accent.WithAlpha((byte)(120 * a)), accent.WithAlpha((byte)(30 * a)) }, null, SKShaderTileMode.Clamp);
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Shader = sh };
        c.DrawRoundRect(new SKRect(box.Left + 0.5f, box.Top + 0.5f, box.Right - 0.5f, box.Bottom - 0.5f), 5.5f, 5.5f, p);
    }
}
