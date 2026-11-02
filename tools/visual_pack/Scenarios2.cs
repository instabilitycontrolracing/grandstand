using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

static partial class Scenarios
{
    const string GTPc = "#000000", LMP2c = "#0072CE", GT3c = "#00A94F";
    static StrategyRow SR(int pos, string num, string name, int[] stops, int lap, int typical, bool pit = false, bool you = false)
    {
        int last = stops.Length > 0 ? stops[^1] : 0;
        var planned = new List<int>();
        for (int l = Math.Max(last + typical, lap + 1); l < 30; l += typical) planned.Add(l);
        return new StrategyRow(pos, num, name, stops.Length, lap - last, last, planned.Count > 0 ? planned[0] : 0, pit, stops, planned, you);
    }

    static Comp? v4comp;
    static StrategyModel V4Model() => new("GTP", GTP, 14, 30, 12, new[]
    {
        SR(1, "6", "L. ARDENNE", new[] { 12 }, 14, 12),
        SR(2, "01", "R. KESTREL", new[] { 11 }, 14, 12),
        SR(3, "23", "K. VOSSKAMP", new int[0], 14, 12, you: true),
        SR(4, "7", "J. HALVERSEN", new[] { 13 }, 14, 12),
        SR(5, "31", "P. OKAFOR", new int[0], 14, 12, pit: true),
        SR(6, "5", "S. LINDQVIST", new[] { 6, 13 }, 14, 7),
        SR(7, "10", "D. BRANNIGAN", new[] { 12 }, 14, 12),
        SR(8, "40", "A. MORVAN", new[] { 10 }, 14, 11),
    }, "YOUR FUEL  41.8 L  ·  3.2 L / LAP  ·  13 LAPS LEFT");

    // round 3: right third only (never over the tower, centre third clear); the team-radio card comes up at 5 s and the
    // board gives up rows to stay above it
    static Scenario V4() => new("V4", "V4", "Sponsored strategy board, right third, clear of the tower and the radio card (fictional placeholder partner)", "bg_pits", 9.0, new[] { 0.3, 1.3, 4.0, 5.6, 8.3 },
        elev => { v4comp = new Comp { RadioAtMs = 5000 }; return new StrategyBoard { Elevated = elev }; },
        (g, t) => ((StrategyBoard)g).Show(V4Model(), t))
    {
        Frame = (g, t) => { if (Cross(t, 8000)) g.Hide(t); },
        Under = (c, w, h, t) => v4comp!.Under(c, t),
        Top = (c, w, h, t) => v4comp!.Top(c, t),
        Sheet = new[] { "0.3 s  slides in, right third", "1.3 s  rows in", "4.0 s  held: 8 cars", "5.6 s  radio card up: 3 rows (own car kept)", "8.3 s  out" },
        Note = "Round 3: the board lives in the right third (x 1296..1896) in the tallest free stretch per VpLayout - under the session bar stack, above the radio card. Type sizes unchanged (bigger lap counts); STOPS and LAST STOP columns dropped, rows drop to the top N that fit (own car always kept). A partner whose mark can't frame a full-width board inside the right third (this round mark would squeeze the columns) gets the house glass in its colour with PRESENTED BY + its logo in the header - no boom; a wide mark still frames it, boom and flash kept inside the board's own spot. Fictional placeholder partner (\"Orbital Logistics\").",
    };

    static Comp? v4hcomp;
    static Scenario V4H() => new("V4H", "V4H", "Strategy board, house look, right third (radio card at 5 s)", "bg_pits", 9.0, new[] { 1.3, 4.0, 5.6 },
        elev => { v4hcomp = new Comp { RadioAtMs = 5000 }; return new StrategyBoard { Elevated = elev, HouseLook = true }; },
        (g, t) => ((StrategyBoard)g).Show(V4Model(), t))
    {
        Frame = (g, t) => { if (Cross(t, 8000)) g.Hide(t); },
        Under = (c, w, h, t) => v4hcomp!.Under(c, t),
        Top = (c, w, h, t) => v4hcomp!.Top(c, t),
        Sheet = new[] { "1.3 s  in", "4.0 s  8 cars", "5.6 s  radio card up: fewer rows" },
        Note = "The same board without a partner (sponsors.json slot StrategyBoard off).",
    };

    // V7 round 2: the four awards one after another, each its own sponsored card (placeholder drivers / numbers)
    static Comp? v7comp;
    const double AwardEvery = 8600;
    static readonly AwardCard[] V7Cards =
    {
        new(AwardKind.Stint, "R. Kestrel", "01", "GTP", GTPc, "Cadillac V-Series.R", "18", "1 STOP ALL RACE  ·  FINISHED P2 IN GTP", new float[] { 4, 22 }),
        new(AwardKind.LapsLed, "L. Ardenne", "6", "GTP", GTPc, "Porsche 963", "23", "LED GTP FOR 23 OF 30 LAPS  ·  FINISHED P1", new float[] { 23, 30 }),
        new(AwardKind.Consistent, "M. Haldane", "10", "LMP2", LMP2c, "Dallara P217", "0.21", "22 CLEAN LAPS  ·  FINISHED P1 IN LMP2",
            new float[] { 0.12f, -0.18f, 0.05f, 0.31f, -0.09f, 0.0f, -0.22f, 0.14f, 0.27f, -0.05f, 0.08f, -0.31f, 0.18f, -0.12f, 0.02f, 0.24f, -0.16f, 0.09f, -0.03f, 0.21f, -0.26f, 0.06f }),
        new(AwardKind.PhotoFinish, "T. Morvan", "7", "GT3", GT3c, "BMW M4 GT3", "0.084", "GT3 WON BY 0.084 S AFTER 28 LAPS", new float[] { 0.084f }, "J. Tallis", "81"),
    };
    static Scenario V7() => new("V7", "V7", "Race awards: one at a time, each its own sponsored card", "bg_crowd", AwardEvery * 4 / 1000.0,
        new[] { 0.75, 2.0, 5.0, 8.6 + 0.5, 8.6 + 2.2, 8.6 + 5.0, 17.2 + 0.7, 17.2 + 4.5, 25.8 + 0.6, 25.8 + 1.3, 25.8 + 4.0, 25.8 + 7.8 },
        elev => { v7comp = new Comp(); return new RaceAwards(); },
        (g, t) => ((RaceAwards)g).Show(V7Cards[0], 0, 4, t))
    {
        Frame = (g, t) =>
        {
            for (int i = 1; i < 4; i++) if (Cross(t, AwardEvery * i)) ((RaceAwards)g).Show(V7Cards[i], i, 4, t);
        },
        Under = (c, w, h, t) => v7comp!.Under(c, t),
        Top = (c, w, h, t) => v7comp!.Top(c, t),
        Variants = new[] { false },
        Sheet = new[] { "0.75 s  LONGEST STINT lands", "2.0 s  needle falling", "5.0 s  held", "9.1 s  MOST LAPS LED powers on", "10.8 s  lit", "13.6 s  held",
                        "17.9 s  MOST CONSISTENT", "21.7 s  held", "26.4 s  PHOTO FINISH winds in", "27.1 s  margin stamped", "29.8 s  held", "33.6 s  exit" },
        Note = "Round 4: tall portrait cards in the story cards' right-third column (x 1290..1884, 594 wide, top 172). Partners in this preview: GO FAST (stint), A&B (laps led), Trading Paints (consistent), StormExhibit (photo finish) - typed names / the existing partner marks, no logo files here. Each slot is set in sponsors.json; a Custom partner's logo file gives the card its colour.",
    };

    static readonly (float Pct, float Speed, string Col)[] Field = Enumerable.Range(0, 26).Select(i =>
        ((i * 0.0383f + (i % 3) * 0.011f) % 1f, i < 8 ? 1f / 208f : i < 14 ? 1f / 216f : 1f / 232f, i < 8 ? "#F4F6FA" : i < 14 ? LMP2 : GT3)).ToArray();

    static Comp? v8comp;
    static TrackTerrain.Data? Ground()
    {
        // the track card's terrain (cached in Documents\iRacing\Grandstand\terrain): loaded in the background, wait for it
        for (int i = 0; i < 100; i++)
        {
            var d = TrackTerrain.For(MapKey, MapFile, 47.949759, 0.207672, 13626);
            if (d != null) return d;
            Thread.Sleep(100);
        }
        Console.WriteLine("   V8: no terrain cached - the map is drawn without ground");
        return null;
    }
    static Scenario V8() => new("V8", "V8", "Yellow sector on the 3D track map: the camera flies down to it (radio card at 5 s)", "bg_trees", 8.0, new[] { 0.4, 1.3, 2.1, 3.4, 6.5 },
        elev => { v8comp = new Comp { RadioAtMs = 5000 }; return new YellowSectorMap(); },
        (g, t) =>
        {
            var y = (YellowSectorMap)g;
            if (Track != null) y.Show(new YellowMapModel(Track, 0.118f, 0.168f, TertreRouge, "TERTRE ROUGE", "CAR #51 STOPPED ON TRACK  ·  LAP 14", 3, Ground()), t);
        })
    {
        Under = (c, w, h, t) => v8comp!.Under(c, t),
        Top = (c, w, h, t) => v8comp!.Top(c, t),
        Variants = new[] { false },
        Sheet = new[] { "0.4 s  in, right third", "1.3 s  lap traced", "2.1 s  flying down", "3.4 s  on the sector", "6.5 s  radio card up: compact plate" },
        Note = "One design, on the track card's 3D map system: the tilted ground plane with the real terrain (TrackTerrain), the lifted white line with its gold wall, the lap-walk camera flying from the whole lap down to the yellow sector; the battle map's plate with the corner's own themed label and two marshal light panels.",
        Frame = (g, t) =>
        {
            var y = (YellowSectorMap)g;
            y.ClearCars();
            for (int i = 0; i < Field.Length; i++)
            {
                var f = Field[i];
                float p = (f.Pct + (float)(t / 1000.0) * f.Speed * 6f) % 1f;   // 6x so they visibly move
                y.SetCar(i, p, SKColor.Parse(f.Col == "#000000" ? "#F4F6FA" : f.Col), false);
            }
            y.SetCar(40, TertreRouge + 0.002f, SKColor.Parse(GT3), true);
        },
    };

    // round 4: the yellow map during a battle - compact plate under the battle map, clear of the battle card
    static Comp? v8bcomp;
    static Scenario V8B() => new("V8B", "V8B", "Yellow map during a battle: compact plate in the right third", "bg_trees", 6.0, new[] { 0.6, 2.4, 4.5 },
        elev => { v8bcomp = new Comp { Battle = true, BattleCard = true }; return new YellowSectorMap(); },
        (g, t) =>
        {
            var y = (YellowSectorMap)g;
            if (Track != null) y.Show(new YellowMapModel(Track, 0.118f, 0.168f, TertreRouge, "TERTRE ROUGE", "CAR #51 STOPPED ON TRACK  ·  LAP 14", 3, Ground()), t);
        })
    {
        Under = (c, w, h, t) => v8bcomp!.Under(c, t),
        Top = (c, w, h, t) => v8bcomp!.Top(c, t),
        Variants = new[] { false },
        Sheet = new[] { "0.6 s  compact plate in", "2.4 s  flying down", "4.5 s  on the sector" },
        Frame = (g, t) =>
        {
            var y = (YellowSectorMap)g;
            y.ClearCars();
            for (int i = 0; i < Field.Length; i++)
            {
                var f = Field[i];
                float p = (f.Pct + (float)(t / 1000.0) * f.Speed * 6f) % 1f;
                y.SetCar(i, p, SKColor.Parse(f.Col == "#000000" ? "#F4F6FA" : f.Col), false);
            }
            y.SetCar(40, TertreRouge + 0.002f, SKColor.Parse(GT3), true);
        },
    };

    // #425: the plate takes the incident place's theme as the camera flies down, then alternates yellow <-> theme
    static Comp? v8tcomp;
    static Scenario V8Theme(string id, string place, float s0, float s1, string bg) => new(id, id, "Yellow map, theme of " + place + " (#425)", bg, 5.0,
        new[] { 1.5, 2.2, 3.6, 4.4 },
        elev => { v8tcomp = new Comp(); return new YellowSectorMap(); },
        (g, t) =>
        {
            var y = (YellowSectorMap)g;
            if (Track != null) y.Show(new YellowMapModel(Track, s0, s1, (s0 + s1) / 2, place, "CAR #51 STOPPED ON TRACK  ·  LAP 14", 3, Ground()), t);
        })
    {
        Under = (c, w, h, t) => v8tcomp!.Under(c, t),
        Top = (c, w, h, t) => v8tcomp!.Top(c, t),
        Variants = new[] { false },
        Sheet = new[] { "1.5 s  zoom starts (yellow)", "2.2 s  theme fading in", "3.6 s  zoomed: yellow", "4.4 s  zoomed: theme" },
        Frame = (g, t) =>
        {
            var y = (YellowSectorMap)g;
            y.ClearCars();
            for (int i = 0; i < Field.Length; i++)
            {
                var f = Field[i];
                float p = (f.Pct + (float)(t / 1000.0) * f.Speed * 6f) % 1f;
                y.SetCar(i, p, SKColor.Parse(f.Col == "#000000" ? "#F4F6FA" : f.Col), false);
            }
            y.SetCar(40, (s0 + s1) / 2, SKColor.Parse(GT3), true);
        },
    };
    static Scenario V8PS() => V8Theme("V8PS", "PLAYSTATION CHICANE", 0.300f, 0.325f, "bg_straight");
    static Scenario V8DU() => V8Theme("V8DU", "DUNLOP CURVE", 0.028f, 0.057f, "bg_crowd");
    static Scenario V8AR() => V8Theme("V8AR", "ARNAGE", 0.758f, 0.772f, "bg_trees");
}
