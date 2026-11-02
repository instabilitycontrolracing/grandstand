// carIdx_probe (2026-10-27, W10 PiP onboard): during a LIVE iRacing session, once per second, logs whether
// CarIdxSteer / CarIdxGear / CarIdxRPM carry real (non-default, changing) values for cars other than yours.
// .ibt files on disk do NOT contain CarIdx arrays, so this has to run against the live sim.
//
//   dotnet run -c Release -- [seconds=120] [logfile=Documents\iRacing\carIdx_probe.txt]
//
// Per line: session time, replay flag, then for the other cars on track: how many have non-zero steer / gear / rpm,
// how many CHANGED since the last second, and three sample cars "#num steer gear rpm".
// Verdict at the end: OK (the PiP's virtual onboard can work for other cars live) or NOT FILLED.
using System.Globalization;
using IRSDKSharper;

int seconds = args.Length > 0 && int.TryParse(args[0], out var sArg) ? sArg : 120;
string log = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "carIdx_probe.txt");
var inv = CultureInfo.InvariantCulture;
var lines = new List<string>();
void Say(string s) { Console.WriteLine(s); lines.Add(s); }

const int N = 64;
var steer = new float[N]; var rpm = new float[N]; var gear = new int[N]; var pct = new float[N]; var surf = new int[N];
var pSteer = new float[N]; var pRpm = new float[N]; var pGear = new int[N];
bool havePrev = false;
int okSeconds = 0, liveSeconds = 0;
double lastLog = -1;
var numbers = new Dictionary<int, string>();
var irsdk = new IRacingSdk();
var done = new ManualResetEventSlim();
var started = DateTime.UtcNow;

irsdk.OnTelemetryData += () =>
{
    try
    {
        var d = irsdk.Data;
        var props = d.TelemetryDataProperties;
        double t = d.GetDouble(props["SessionTime"]);
        if (lastLog >= 0 && t - lastLog < 1.0 && t >= lastLog) return;
        lastLog = t;
        if (numbers.Count == 0 && d.SessionInfo?.DriverInfo?.Drivers is { } drivers)
            foreach (var dr in drivers) if (dr != null) numbers[dr.CarIdx] = dr.CarNumber ?? "?";
        int me = props.TryGetValue("PlayerCarIdx", out var pci) ? d.GetInt(pci) : -1;
        bool replay = props.TryGetValue("IsReplayPlaying", out var rp) && d.GetBool(rp);
        bool hasS = props.TryGetValue("CarIdxSteer", out var ds), hasG = props.TryGetValue("CarIdxGear", out var dg), hasR = props.TryGetValue("CarIdxRPM", out var dr2);
        if (hasS) d.GetFloatArray(ds!, steer, 0, N);
        if (hasG) d.GetIntArray(dg!, gear, 0, N);
        if (hasR) d.GetFloatArray(dr2!, rpm, 0, N);
        if (props.TryGetValue("CarIdxLapDistPct", out var dp)) d.GetFloatArray(dp, pct, 0, N);
        if (props.TryGetValue("CarIdxTrackSurface", out var dsf)) d.GetIntArray(dsf, surf, 0, N);
        int others = 0, nzS = 0, nzG = 0, nzR = 0, chS = 0, chG = 0, chR = 0;
        var samples = new List<string>();
        for (int i = 0; i < N; i++)
        {
            if (i == me || pct[i] < 0 || surf[i] < 0) continue;   // not in the world
            others++;
            if (steer[i] != 0) nzS++; if (gear[i] != 0) nzG++; if (rpm[i] != 0) nzR++;
            if (havePrev) { if (steer[i] != pSteer[i]) chS++; if (gear[i] != pGear[i]) chG++; if (rpm[i] != pRpm[i]) chR++; }
            if (samples.Count < 3) samples.Add($"#{(numbers.TryGetValue(i, out var nn) ? nn : i.ToString(inv))} {steer[i]:0.000} g{gear[i]} {rpm[i]:0}rpm");
        }
        Array.Copy(steer, pSteer, N); Array.Copy(gear, pGear, N); Array.Copy(rpm, pRpm, N); havePrev = true;
        bool good = others > 0 && nzR > 0 && chR > 0 && (chS > 0 || nzS > 0);
        if (!replay) { liveSeconds++; if (good) okSeconds++; }
        Say(string.Format(inv, "t={0,8:0.0} {1} vars:{2}{3}{4} others={5,2} nonzero steer/gear/rpm={6}/{7}/{8} changed={9}/{10}/{11}  {12}",
            t, replay ? "REPLAY" : "LIVE  ", hasS ? "S" : "-", hasG ? "G" : "-", hasR ? "R" : "-", others, nzS, nzG, nzR, chS, chG, chR, string.Join(" | ", samples)));
        if ((DateTime.UtcNow - started).TotalSeconds >= seconds) done.Set();
    }
    catch (Exception ex) { Say("error: " + ex.Message); }
};
irsdk.OnConnected += () => Say("connected to iRacing");
irsdk.OnDisconnected += () => Say("iRacing disconnected");
Say($"carIdx_probe: {seconds} s, waiting for iRacing...");
irsdk.Start();
done.Wait(TimeSpan.FromSeconds(seconds + 600));
irsdk.Stop();
string verdict = liveSeconds == 0 ? "NO LIVE DATA SEEN (run it while driving or spectating a live session)"
    : okSeconds >= liveSeconds * 0.8 ? $"OK - other cars' steer/gear/RPM are filled and moving live ({okSeconds}/{liveSeconds} s)"
    : $"NOT FILLED for other cars live ({okSeconds}/{liveSeconds} s looked real) - keep the PiP off";
Say("VERDICT: " + verdict);
try { Directory.CreateDirectory(Path.GetDirectoryName(log)!); File.WriteAllLines(log, lines); Console.WriteLine("written " + log); } catch (Exception ex) { Console.WriteLine("couldn't write log: " + ex.Message); }
