using System.Linq;
using IRSDKSharper;

var outPath = @"C:\Users\Administrator\Documents\iRacing\sdk_probe_prerace.txt";
var L = new List<string>();
var irsdk = new IRacingSdk();
bool done = false;

static object? GetProp(object? o, string name)
{
    if (o == null) return null;
    try { return o.GetType().GetProperty(name)?.GetValue(o); } catch { return null; }
}

irsdk.OnTelemetryData += () =>
{
    if (done) return;
    try
    {
        var data = irsdk.Data;
        var si = data.SessionInfo;
        if (si == null) return;
        var di = GetProp(si, "DriverInfo");
        var drivers = GetProp(di, "Drivers") as System.Collections.IEnumerable;
        if (drivers == null) return;

        var nameByIdx = new Dictionary<int, string>();
        foreach (var d in drivers)
        {
            if (GetProp(d, "CarIdx") is int ii)
                nameByIdx[ii] = $"#{GetProp(d, "CarNumber")} {GetProp(d, "UserName")} ({GetProp(d, "CarScreenName")})";
        }

        L.Add("===== SessionInfo top-level props =====");
        foreach (var p in si.GetType().GetProperties()) L.Add("  " + p.Name);

        const int N = 64;
        void DumpIntArr(string key)
        {
            try
            {
                var datum = data.TelemetryDataProperties[key];
                var buf = new int[N];
                data.GetIntArray(datum, buf, 0, N);
                var parts = new List<string>();
                for (int i = 0; i < N; i++) if (nameByIdx.ContainsKey(i)) parts.Add($"[{i}]={buf[i]}");
                L.Add(key + ": " + string.Join("  ", parts));
            }
            catch (Exception e) { L.Add(key + ": ERR " + e.Message); }
        }
        L.Add("===== live CarIdx arrays (active idx only) =====");
        DumpIntArr("CarIdxPosition");
        DumpIntArr("CarIdxClassPosition");
        DumpIntArr("CarIdxPaceLine");
        DumpIntArr("CarIdxPaceRow");
        DumpIntArr("CarIdxPaceFlags");

        L.Add("===== QualifyResultsInfo =====");
        var qri = GetProp(si, "QualifyResultsInfo");
        if (qri != null) L.Add("  props: " + string.Join(",", qri.GetType().GetProperties().Select(p => p.Name)));
        var qresults = GetProp(qri, "Results") as System.Collections.IEnumerable;
        if (qresults == null) L.Add("  Results: (null)");
        else foreach (var r in qresults)
        {
            var idx = GetProp(r, "CarIdx");
            string who = (idx is int qi && nameByIdx.TryGetValue(qi, out var w)) ? w : "?";
            L.Add($"  CarIdx={idx} Pos={GetProp(r, "Position")} ClassPos={GetProp(r, "ClassPosition")}  {who}");
        }

        L.Add("===== SessionInfo.Sessions ResultsPositions =====");
        var sinfo = GetProp(si, "SessionInfo");
        var sessions = GetProp(sinfo, "Sessions") as System.Collections.IEnumerable;
        if (sessions == null) L.Add("  Sessions: (null)");
        else
        {
            bool first = true;
            foreach (var s in sessions)
            {
                if (first) { L.Add("  session props: " + string.Join(",", s.GetType().GetProperties().Select(p => p.Name))); first = false; }
                L.Add($"  -- Session num={GetProp(s, "SessionNum")} type={GetProp(s, "SessionType")} name={GetProp(s, "SessionName")}");
                var rp = GetProp(s, "ResultsPositions") as System.Collections.IEnumerable;
                if (rp == null) { L.Add("     ResultsPositions: (null)"); continue; }
                foreach (var r in rp)
                {
                    var idx = GetProp(r, "CarIdx");
                    string who = (idx is int si2 && nameByIdx.TryGetValue(si2, out var w)) ? w : "?";
                    L.Add($"     CarIdx={idx} Pos={GetProp(r, "Position")} ClassPos={GetProp(r, "ClassPosition")}  {who}");
                }
            }
        }

        File.WriteAllLines(outPath, L);
        done = true;
    }
    catch (Exception ex) { L.Add("EX: " + ex); File.WriteAllLines(outPath, L); done = true; }
};

irsdk.UpdateInterval = 1;
irsdk.Start();
Console.WriteLine("probe: waiting for pre-race session yaml...");
for (int i = 0; i < 400 && !done; i++) System.Threading.Thread.Sleep(50);
irsdk.Stop();
Console.WriteLine(done ? ("wrote " + outPath) : "NO data");
