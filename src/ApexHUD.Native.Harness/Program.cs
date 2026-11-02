using ApexHUD.Native.Common;
using ApexHUD.Native.Data;

// Debug harness for the Port-02 data layer. Prints a fresh snapshot of the
// race-state model once per second so we can sanity-check it against iRacing
// when the sim is running, or watch the disconnected sentinel when it isn't.
// Replaces the Session-2 proof-of-life Program.cs (the data layer is now a
// class library; this exe consumes it the same way overlays will).

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var service = new RaceStateService();
service.Exception += ex => Console.WriteLine($"[sdk error] {ex.Message}");
service.SnapshotUpdated += s =>
{
    // No-op: we sample once a second via the loop below instead of printing
    // on every 60 Hz tick. Subscribing keeps the field exercised end-to-end.
    _ = s;
};

Console.WriteLine("Grandstand data-layer harness — Ctrl+C to exit");
service.Start();

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var s = service.Current;
        if (!s.IsConnected)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] iRacing not running");
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s.Phase} · {s.SessionTypeName} · {s.TrackBroadcastName} · " +
                              $"lap {s.CurrentLap}/{s.TotalLaps} · {LapTimeFormat.SessionTimer(s.SessionTimeRemaining)} left · " +
                              $"{s.AirTempC:0}°/{s.TrackTempC:0}° {s.TemperatureUnit} · {s.OpponentsCount} cars · " +
                              $"flags=[{FlagsToString(s.Flags)}]");

            // Top three rows of each class, broadcast-style.
            foreach (var cls in s.ClassLeaderboard)
            {
                Console.WriteLine($"  {cls.Name,-6} ({cls.Drivers.Count})");
                foreach (var d in cls.Drivers.Take(3))
                {
                    var gap = LapTimeFormat.Gap(d.GapToLeaderSeconds, d.LapDifference);
                    Console.WriteLine($"    P{d.PositionInClass,2} #{d.CarNumber,-3} {Trunc(d.Name, 22),-22} " +
                                      $"best {LapTimeFormat.Lap(d.BestLapTime),7}  gap {gap,7}" +
                                      (d.IsInPit ? "  [PIT]" : ""));
                }
            }
        }
        await Task.Delay(1000, cts.Token);
    }
}
catch (OperationCanceledException) { /* Ctrl+C */ }
finally
{
    service.Stop();
    Console.WriteLine("Stopped.");
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

static string FlagsToString(FlagState f)
{
    var parts = new List<string>();
    if (f.Green) parts.Add("G");
    if (f.Yellow) parts.Add("Y");
    if (f.Red) parts.Add("R");
    if (f.White) parts.Add("W");
    if (f.Checkered) parts.Add("CHK");
    if (f.Blue) parts.Add("B");
    if (f.Black) parts.Add("Bk");
    return parts.Count == 0 ? "—" : string.Join("/", parts);
}
