using System.Globalization;
using IRSDKSharper;

namespace ApexHUD.Native.Data;

/// <summary>
/// 2026-10-24 (control app + replay director): small, on-demand reads of the raw iRacing feed and
/// the SDK's camera / replay broadcast messages. Nothing here runs on the render loop: the control
/// server calls the readers only while its page is open, and the replay director only while it
/// is switched on. Every call is guarded - iRacing closed, a variable missing, a bad value - and
/// returns a neutral answer instead of throwing.
/// </summary>
public sealed partial class RaceStateService
{
    /// <summary>One camera group from the session info CameraInfo ("TV1", "Blimp", "Chopper"...).</summary>
    public sealed record CameraGroup(int Num, string Name, bool IsScenic);

    /// <summary>One flattened session-info line (path like "WeekendInfo.TrackName"), for the data browser.</summary>
    public sealed record SessionVar(string Name, string Value);

    /// <summary>True while connected to iRacing and its telemetry headers are readable.</summary>
    public bool SdkReady
    {
        get { try { return sdkConnected && irsdk.Data.TelemetryDataPropertiesReady; } catch { return false; } }
    }

    /// <summary>Raw value of one telemetry variable (index into array variables), or null.</summary>
    public object? ReadVar(string name, int index = 0)
    {
        try
        {
            var data = irsdk.Data;
            if (!sdkConnected || !data.TelemetryDataPropertiesReady) return null;
            if (!data.TelemetryDataProperties.TryGetValue(name, out var d)) return null;
            if (index < 0 || index >= d.Count) return null;
            return data.GetValue(d, index);
        }
        catch { return null; }
    }

    /// <summary>Number of entries of a telemetry variable (64 for CarIdx arrays), 0 when missing.</summary>
    public int VarCount(string name)
    {
        try
        {
            var data = irsdk.Data;
            if (!sdkConnected || !data.TelemetryDataPropertiesReady) return 0;
            return data.TelemetryDataProperties.TryGetValue(name, out var d) ? d.Count : 0;
        }
        catch { return 0; }
    }

    public bool ReadBool(string name, int index = 0) => ReadVar(name, index) switch
    {
        bool b => b,
        int i => i != 0,
        _ => false,
    };

    public int ReadInt(string name, int index = 0, int fallback = -1) => ReadVar(name, index) switch
    {
        int i => i,
        bool b => b ? 1 : 0,
        float f => (int)f,
        double d => (int)d,
        uint u => (int)u,
        _ => fallback,
    };

    public double ReadDouble(string name, int index = 0, double fallback = double.NaN) => ReadVar(name, index) switch
    {
        double d => d,
        float f => f,
        int i => i,
        bool b => b ? 1 : 0,
        _ => fallback,
    };

    /// <summary>Camera groups of the loaded session, in iRacing's order. Empty when not connected.</summary>
    public IReadOnlyList<CameraGroup> CameraGroups()
    {
        var list = new List<CameraGroup>();
        try
        {
            if (!sdkConnected) return list;
            var groups = irsdk.Data.SessionInfo?.CameraInfo?.Groups;
            if (groups == null) return list;
            foreach (var g in groups)
                if (g != null) list.Add(new CameraGroup(g.GroupNum, g.GroupName ?? "", Convert.ToString(g.IsScenic, CultureInfo.InvariantCulture) is "true" or "True" or "1"));
        }
        catch { }
        return list;
    }

    /// <summary>CarIdx → iRacing's raw car number (what the camera-switch message wants).</summary>
    public IReadOnlyDictionary<int, int> CarNumbersRaw()
    {
        var map = new Dictionary<int, int>();
        try
        {
            var drivers = irsdk.Data.SessionInfo?.DriverInfo?.Drivers;
            if (drivers == null) return map;
            foreach (var d in drivers) if (d != null) map[d.CarIdx] = d.CarNumberRaw;
        }
        catch { }
        return map;
    }

    /// <summary>"full" when live, "replay" when watching a saved replay file ("" when unknown).</summary>
    public string SimMode
    {
        get { try { return irsdk.Data.SessionInfo?.WeekendInfo?.SimMode ?? ""; } catch { return ""; } }
    }

    /// <summary>The session info's own update counter (changes when iRacing re-sends the YAML).</summary>
    public int SessionInfoUpdate
    {
        get { try { return irsdk.Data.SessionInfoUpdate; } catch { return -1; } }
    }

    // ---- broadcast messages (camera + replay). All no-ops when iRacing is not connected. ----

    /// <summary>Points the camera at a car (by raw car number) using a camera group (0 = keep).</summary>
    public bool CamToCar(int carNumberRaw, int group, int camera = 0)
    {
        if (!sdkConnected) return false;
        try { irsdk.CamSwitchNum(IRacingSdkEnum.CamSwitchMode.FocusAtDriver, carNumberRaw, group, camera); return true; }
        catch { return false; }
    }

    /// <summary>Points the camera at whoever is in race position <paramref name="position"/>.</summary>
    public bool CamToPosition(int position, int group, int camera = 0)
    {
        if (!sdkConnected) return false;
        try { irsdk.CamSwitchPos(IRacingSdkEnum.CamSwitchMode.FocusAtDriver, position, group, camera); return true; }
        catch { return false; }
    }

    /// <summary>Replay speed: 1 = normal; with <paramref name="slowMotion"/> the speed is a divisor (2 = half speed).</summary>
    public bool ReplaySpeed(int speed, bool slowMotion)
    {
        if (!sdkConnected) return false;
        try { irsdk.ReplaySetPlaySpeed(speed, slowMotion); return true; }
        catch { return false; }
    }

    /// <summary>Jumps the replay to a moment of a session.</summary>
    public bool ReplayToSessionTime(int sessionNum, int sessionTimeMs)
    {
        if (!sdkConnected) return false;
        try { irsdk.ReplaySearchSessionTime(sessionNum, Math.Max(0, sessionTimeMs)); return true; }
        catch { return false; }
    }

    /// <summary>Back to the live point (end of the tape) at normal speed.</summary>
    public bool ReplayToLive()
    {
        if (!sdkConnected) return false;
        try
        {
            irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToEnd);
            irsdk.ReplaySetPlaySpeed(1, false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The session info YAML flattened to "Section.Key = value" lines (lists numbered), for the
    /// control app's data browser. Called only while that screen is open; capped at
    /// <paramref name="max"/> lines so a 60-car field can't flood the page.
    /// </summary>
    public IReadOnlyList<SessionVar> ListSessionInfo(int max = 3000)
    {
        var list = new List<SessionVar>();
        try
        {
            if (!sdkConnected) return list;
            string? yaml = irsdk.Data.SessionInfoYaml;
            if (string.IsNullOrEmpty(yaml)) return list;
            FlattenYaml(yaml, list, max);
        }
        catch { }
        return list;
    }

    /// <summary>
    /// A deliberately small reader for iRacing's own YAML (indentation-based maps, "- " list items,
    /// "key: value" scalars). It never throws: anything it doesn't understand is skipped.
    /// </summary>
    internal static void FlattenYaml(string yaml, List<SessionVar> output, int max)
    {
        var stack = new List<(int Indent, string Path)>();
        var listIndex = new Dictionary<string, int>();
        foreach (var raw in yaml.Split('\n'))
        {
            if (output.Count >= max) return;
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#') || line.StartsWith("---") || line.StartsWith("...")) continue;
            int indent = line.Length - line.TrimStart(' ').Length;
            string body = line.Trim();
            bool item = body.StartsWith("- ") || body == "-";
            if (item)
            {
                // iRacing writes list items at the same column as their parent key, so an item only
                // closes deeper entries (and the previous item at this column, stored one deeper)
                while (stack.Count > 0 && stack[^1].Indent > indent) stack.RemoveAt(stack.Count - 1);
                string parent = stack.Count > 0 ? stack[^1].Path : "";
                int n = listIndex.TryGetValue(parent, out var k) ? k + 1 : 0;
                listIndex[parent] = n;
                stack.Add((indent + 1, parent + "[" + n.ToString(CultureInfo.InvariantCulture) + "]"));
                body = body.Length > 2 ? body[2..].Trim() : "";
                indent += 2;
                if (body.Length == 0) continue;
            }
            int colon = body.IndexOf(':');
            if (colon <= 0) continue;
            string key = body[..colon].Trim();
            string value = body[(colon + 1)..].Trim();
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            string basePath = stack.Count > 0 ? stack[^1].Path : "";
            string path = basePath.Length == 0 ? key : basePath + "." + key;
            if (value.Length == 0) { stack.Add((indent, path)); listIndex.Remove(path); continue; }
            if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            if (value.Length > 200) value = value[..200] + "...";
            output.Add(new SessionVar(path, value));
        }
    }
}
