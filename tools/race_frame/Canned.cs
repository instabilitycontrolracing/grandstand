using ApexHUD.Native.Common;
// made-up drivers, copied from GpuHost/CannedSnapshot.cs (internal there) for the previews



/// <summary>
/// A representative, fully-populated <see cref="RaceSnapshot"/> for driving the
/// renderers when iRacing isn't connected (or when <c>--demo</c> forces it).
/// It puts the field in the Broadcast phase with the leader a lap in and GTP +
/// LMP2 + GT3 all present — exactly the conditions <see cref="SceneDirector"/>
/// needs to run its full OVERALL → GTP → LMP2 → GT3 rotation, so the GTP video
/// splash (the Phase-B success bar) actually plays. Live data, when available,
/// is always preferred; this is the fallback / demo feed.
/// </summary>
public static class Canned
{
    public static RaceSnapshot Build()
    {
        // made-up names (2026-10-24): the demo never shows real drivers
        var gtp = MakeClass("GTP", BroadcastPalette.Class("GTP"), 0, new[]
        {
            ("Marco Albera",       "ALB", "5", "Acura"),
            ("Tobias Blom",        "BLO", "01", "Cadillac"),
            ("Nate Tanner",        "TAN", "6", "Porsche"),
            ("Mattis Jansen",      "JAM", "7", "Porsche"),
            ("Rory Tayler",        "TAY", "10", "Acura"),
            ("Paulo Deran",        "DER", "31", "Cadillac"),
        }, bestLap: 94.2f);

        var lmp2 = MakeClass("LMP2", BroadcastPalette.Class("LMP2"), 6, new[]
        {
            ("Bert Keaton",     "KEA", "2",  "Oreca"),
            ("Pierre Chalon",   "CHA", "04", "Oreca"),
            ("Stefan Thorne",   "THO", "11", "Oreca"),
            ("Glen Kurtzman",   "KUR", "04", "Oreca"),
            ("Niko Pinter",     "PIN", "8",  "Oreca"),
        }, bestLap: 99.8f);

        var gt3 = MakeClass("GT3", BroadcastPalette.Class("GT3"), 11, new[]
        {
            ("Andres Garza",    "GAR", "3",  "Chevrolet"),
            ("Jordy Tallis",    "TAY", "81", "BMW"),
            ("Lars Heine",      "HEI", "9",  "Porsche"),
            ("Dario Serrano",   "SER", "62", "Ferrari"),
            ("Kai Bachmann",    "BAC", "77", "Porsche"),
            ("Alessio Pieri",   "PIE", "82", "Ferrari"),
        }, bestLap: 104.6f);

        var leader = gtp.Drivers[0];

        return new RaceSnapshot(
            IsConnected: true,
            CapturedAt: DateTime.UtcNow,
            SessionState: 4,
            SessionTypeName: "Race",
            Phase: RacePhase.Broadcast,
            Flags: FlagState.Empty,
            CurrentLap: 13,
            TotalLaps: 0,
            LapsRemaining: 0,
            SessionTimeRemaining: 6 * 3600,
            SessionTimeOfDay: 20.5 * 3600,
            AirTempC: 24f,
            TrackTempC: 31f,
            TemperatureUnit: "C",
            IsRaining: false,
            TrackRawName: "lemans full",
            TrackDisplayName: "Circuit des 24 Heures du Mans",
            TrackBroadcastName: "Le Mans",
            TrackCountryCode: "FR",
            SeriesId: null,
            OpponentsCount: gtp.Drivers.Count + lmp2.Drivers.Count + gt3.Drivers.Count,
            PlayerCarIdx: -1,
            CameraCarIdx: leader.CarIdx,
            Player: null,
            Leader: leader,
            CameraTarget: leader,
            ClassLeaderboard: new[] { gtp, lmp2, gt3 });
    }

    static DriverClass MakeClass(string name, string color, int idxBase,
        (string name, string abbrev, string num, string brand)[] rows, float bestLap)
    {
        var drivers = new List<Driver>(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            var (full, abbrev, num, brand) = rows[i];
            double gap = i == 0 ? 0 : i * 4.3 + i * i * 0.7;
            double interval = i == 0 ? 0 : 1.1 + i * 0.4;
            drivers.Add(new Driver(
                CarIdx: idxBase + i,
                Name: full,
                AbbrevName: abbrev,
                Initials: abbrev,
                CarNumber: num,
                CarName: brand,
                CarBrand: brand,
                ClassName: name,
                ClassColor: color,
                LicenceString: "A",
                LicenceColor: BroadcastPalette.License("A"),
                Position: idxBase + i + 1,
                PositionInClass: i + 1,
                CurrentLap: 13,
                LapDistPct: 0.42f,
                LapsCompleted: 12 - i,
                LapDifference: 0,
                BestLapTime: bestLap + i * 0.18f,
                LastLapTime: bestLap + i * 0.18f + 0.3f,
                GapToLeaderSeconds: gap,
                IntervalSeconds: interval,
                IsInPit: false,
                IsConnected: true,
                IsPaceCar: false,
                IsSpectator: false,
                IRating: 5000 - i * 120));
        }
        return new DriverClass(name, color, drivers);
    }
}
