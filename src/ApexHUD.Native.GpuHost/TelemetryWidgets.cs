using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// #778 (2026-11-05): custom telemetry overlays. Each widget shows one piece of car data in one style, for a car the user
/// picks, wherever they put it (1080p layout units). Defined in Documents\iRacing\Grandstand\telemetry_widgets.json
/// (a starter file with examples is written the first time); on air only while the control page's "Telemetry widgets"
/// switch is on. Re-read when the file changes.
///
/// Styles: number, bar, gauge (arc), trace (the last few seconds as a line).
/// Data, your own car: speed, rpm, gear, throttle, brake, clutch, steer, fuel, delta, lap, position, gap_ahead.
/// Data, any other car ("camera" = the car the camera is on, "position:3", "number:72"): speed (from lap distance), rpm,
/// gear, steer, position, gap_ahead, lap. iRacing only shares pedals and fuel for your own car.
/// </summary>
internal sealed class TelemetryWidgets
{
    public sealed class Widget
    {
        public string Type { get; set; } = "number";   // number | bar | gauge | trace
        public string Data { get; set; } = "speed";
        public string Car { get; set; } = "player";    // player | camera | position:N | number:NN
        public float X { get; set; } = 40;
        public float Y { get; set; } = 600;
        public float W { get; set; } = 220;
        public float H { get; set; } = 64;
        public string? Label { get; set; }
        public string Color { get; set; } = "#E8B44A";
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double Seconds { get; set; } = 6;       // trace length
        [JsonIgnore] public readonly Queue<(double T, double V)> Hist = new();
    }
    sealed class FileModel { public List<Widget> Widgets { get; set; } = new(); }

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand");
    static readonly string FilePath = Path.Combine(Dir, "telemetry_widgets.json");
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly RaceStateService rs;
    List<Widget> widgets = new();
    DateTime fileStamp = DateTime.MinValue;
    double lastCheckMs = -1e9;
    readonly Dictionary<int, (double Pct, double T)> lastPos = new();
    readonly Dictionary<int, double> carSpeed = new();
    readonly SKPaint glass = new() { Color = new SKColor(10, 14, 22, 200), IsAntialias = true };
    readonly SKPaint edge = new() { Color = new SKColor(255, 255, 255, 40), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint track = new() { Color = new SKColor(255, 255, 255, 30), IsAntialias = true };
    readonly SKPaint line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    readonly SKPaint label = new() { IsAntialias = true, Color = new SKColor(200, 205, 215), TextSize = 13 };
    readonly SKPaint value = new() { IsAntialias = true, Color = SKColors.White, TextSize = 30 };
    readonly SKPath path = new();

    public TelemetryWidgets(RaceStateService rs)
    {
        this.rs = rs;
        try { label.Typeface = Fonts.Inter.SemiBold; value.Typeface = Fonts.BarlowCondensed.Bold; } catch { }
    }

    public bool Any => widgets.Count > 0;

    void Reload(double nowMs)
    {
        if (nowMs - lastCheckMs < 2000) return;
        lastCheckMs = nowMs;
        try
        {
            if (!File.Exists(FilePath)) { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, Starter()); }
            var st = File.GetLastWriteTimeUtc(FilePath);
            if (st == fileStamp) return;
            fileStamp = st;
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), Json);
            widgets = m?.Widgets ?? new();
            Program.Log($"telemetry widgets: {widgets.Count} loaded from {FilePath}");
        }
        catch (Exception ex) { Program.Log("telemetry widgets: " + ex.Message); }
    }

    static string Starter() => JsonSerializer.Serialize(new FileModel
    {
        Widgets =
        {
            new Widget { Type = "number", Data = "speed", Car = "camera", X = 1660, Y = 760, W = 220, H = 64, Label = "SPEED" },
            new Widget { Type = "gauge", Data = "rpm", Car = "camera", X = 1660, Y = 830, W = 108, H = 108, Label = "RPM", Color = "#E8B44A" },
            new Widget { Type = "number", Data = "gear", Car = "camera", X = 1772, Y = 830, W = 108, H = 108, Label = "GEAR" },
            new Widget { Type = "bar", Data = "throttle", Car = "player", X = 1660, Y = 944, W = 220, H = 30, Label = "THROTTLE", Color = "#2EA043" },
            new Widget { Type = "bar", Data = "brake", Car = "player", X = 1660, Y = 980, W = 220, H = 30, Label = "BRAKE", Color = "#E5484D" },
            new Widget { Type = "trace", Data = "speed", Car = "player", X = 40, Y = 940, W = 360, H = 90, Label = "SPEED TRACE", Seconds = 8, Color = "#4DA3FF" },
        }
    }, Json);

    /// <summary>Speeds for other cars from how fast their lap distance changes (iRacing only gives speed for your car).</summary>
    void TrackSpeeds(RaceSnapshot snap, double nowMs)
    {
        double lenM = TrackMetres(snap);
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var d in cls.Drivers)
            {
                if (d.LapDistPct < 0) continue;
                double pos = d.LapsCompleted + d.LapDistPct;
                if (lastPos.TryGetValue(d.CarIdx, out var p) && nowMs - p.T > 150)
                {
                    double dp = pos - p.Pct;
                    if (dp >= 0 && dp < 0.05 && lenM > 0)
                    {
                        double mps = dp * lenM / ((nowMs - p.T) / 1000.0);
                        carSpeed[d.CarIdx] = carSpeed.TryGetValue(d.CarIdx, out var old) ? old * 0.6 + mps * 0.4 : mps;
                    }
                    lastPos[d.CarIdx] = (pos, nowMs);
                }
                else if (!lastPos.ContainsKey(d.CarIdx)) lastPos[d.CarIdx] = (pos, nowMs);
            }
    }

    static double TrackMetres(RaceSnapshot snap)
    {
        var tl = snap.Extras.TrackLength ?? "";
        var num = new string(tl.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        return tl.Contains("mi", StringComparison.OrdinalIgnoreCase) ? v * 1609.34 : v * 1000;
    }

    Driver? CarFor(RaceSnapshot snap, string car)
    {
        var all = snap.ClassLeaderboard.SelectMany(c => c.Drivers);
        if (car.Equals("player", StringComparison.OrdinalIgnoreCase)) return snap.Player;
        if (car.Equals("camera", StringComparison.OrdinalIgnoreCase)) return all.FirstOrDefault(d => d.CarIdx == snap.CameraCarIdx) ?? snap.Player;
        if (car.StartsWith("position:", StringComparison.OrdinalIgnoreCase) && int.TryParse(car[9..], out var p)) return all.FirstOrDefault(d => d.Position == p);
        if (car.StartsWith("number:", StringComparison.OrdinalIgnoreCase)) { var n = car[7..].Trim(); return all.FirstOrDefault(d => d.CarNumber == n); }
        return snap.Player;
    }

    /// <summary>The value, its display text and its usual range. Null when this car doesn't share that data.</summary>
    (double V, string Text, double Min, double Max, string Unit)? Read(RaceSnapshot snap, Driver car, string data)
    {
        bool me = snap.Player != null && car.CarIdx == snap.Player.CarIdx;
        bool mph = !string.Equals(snap.TemperatureUnit, "C", StringComparison.OrdinalIgnoreCase);
        double num;
        switch (data.ToLowerInvariant())
        {
            case "speed":
                double mps = me ? rs.ReadDouble("Speed") : carSpeed.TryGetValue(car.CarIdx, out var s) ? s : double.NaN;
                if (double.IsNaN(mps)) return null;
                num = mph ? mps * 2.23694 : mps * 3.6;
                return (num, num.ToString("0"), 0, mph ? 220 : 350, mph ? "MPH" : "KM/H");
            case "rpm":
                num = me ? rs.ReadDouble("RPM") : rs.ReadDouble("CarIdxRPM", car.CarIdx);
                if (double.IsNaN(num)) return null;
                return (num, num.ToString("0"), 0, 10000, "RPM");
            case "gear":
                int g = me ? rs.ReadInt("Gear", 0, int.MinValue) : rs.ReadInt("CarIdxGear", car.CarIdx, int.MinValue);
                if (g == int.MinValue) return null;
                return (g, g < 0 ? "R" : g == 0 ? "N" : g.ToString(), -1, 8, "");
            case "steer":
                num = me ? rs.ReadDouble("SteeringWheelAngle") : rs.ReadDouble("CarIdxSteer", car.CarIdx);
                if (double.IsNaN(num)) return null;
                num *= 180 / Math.PI;
                return (num, num.ToString("0") + "°", -270, 270, "");
            case "throttle": case "brake": case "clutch":
                if (!me) return null;
                num = rs.ReadDouble(data.ToLowerInvariant() switch { "throttle" => "Throttle", "brake" => "Brake", _ => "Clutch" });
                if (double.IsNaN(num)) return null;
                if (data.Equals("clutch", StringComparison.OrdinalIgnoreCase)) num = 1 - num;   // iRacing: 1 = released
                return (num * 100, (num * 100).ToString("0") + "%", 0, 100, "");
            case "fuel":
                if (!me) return null;
                num = rs.ReadDouble("FuelLevel");
                if (double.IsNaN(num)) return null;
                double shown = mph ? num * 0.264172 : num;
                return (shown, shown.ToString("0.0"), 0, mph ? 30 : 110, mph ? "GAL" : "L");
            case "delta":
                if (!me) return null;
                num = rs.ReadDouble("LapDeltaToBestLap");
                if (double.IsNaN(num)) return null;
                return (num, (num >= 0 ? "+" : "") + num.ToString("0.00"), -2, 2, "S");
            case "lap": return (car.CurrentLap, car.CurrentLap.ToString(), 0, Math.Max(1, snap.TotalLaps), "LAP");
            case "position": return (car.PositionInClass, "P" + car.PositionInClass, 1, 40, "");
            case "gap_ahead":
                if (car.IntervalSeconds is not double gap || gap <= 0) return null;
                return (gap, "+" + gap.ToString("0.0"), 0, 5, "S");
        }
        return null;
    }

    public void Draw(SKCanvas c, RaceSnapshot snap, double nowMs)
    {
        Reload(nowMs);
        if (widgets.Count == 0 || !snap.IsConnected) return;
        TrackSpeeds(snap, nowMs);
        foreach (var w in widgets)
        {
            var car = CarFor(snap, w.Car);
            if (car == null) continue;
            var r = Read(snap, car, w.Data);
            if (r is not { } v) continue;
            double min = w.Min ?? v.Min, max = w.Max ?? v.Max;
            double k = max > min ? Math.Clamp((v.V - min) / (max - min), 0, 1) : 0;
            var col = SKColor.TryParse(w.Color, out var cc) ? cc : new SKColor(232, 180, 74);
            var rect = new SKRect(w.X, w.Y, w.X + w.W, w.Y + w.H);
            c.DrawRoundRect(rect, 8, 8, glass);
            c.DrawRoundRect(rect, 8, 8, edge);
            string lab = (w.Label ?? w.Data.ToUpperInvariant()) + (w.Car is "player" or "camera" ? "" : "  #" + car.CarNumber);
            fill.Color = col;
            switch (w.Type.ToLowerInvariant())
            {
                case "bar":
                {
                    var bar = new SKRect(rect.Left + 8, rect.Top + rect.Height * 0.55f, rect.Right - 8, rect.Bottom - 6);
                    c.DrawRoundRect(bar, 3, 3, track);
                    c.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + (float)(bar.Width * k), bar.Bottom), 3, 3, fill);
                    label.TextSize = Math.Min(13, rect.Height * 0.4f);
                    c.DrawText(lab, rect.Left + 10, rect.Top + label.TextSize + 3, label);
                    c.DrawText(v.Text, rect.Right - 10 - label.MeasureText(v.Text), rect.Top + label.TextSize + 3, label);
                    break;
                }
                case "gauge":
                {
                    float cx = rect.MidX, cy = rect.MidY + rect.Height * 0.08f, rad = Math.Min(rect.Width, rect.Height) * 0.38f;
                    var arc = new SKRect(cx - rad, cy - rad, cx + rad, cy + rad);
                    line.Color = new SKColor(255, 255, 255, 40); line.StrokeWidth = 6;
                    using (var p = new SKPath()) { p.AddArc(arc, 135, 270); c.DrawPath(p, line); }
                    line.Color = col;
                    using (var p = new SKPath()) { p.AddArc(arc, 135, (float)(270 * k)); c.DrawPath(p, line); }
                    value.TextSize = rad * 0.7f;
                    c.DrawText(v.Text, cx - value.MeasureText(v.Text) / 2, cy + value.TextSize * 0.35f, value);
                    label.TextSize = 11;
                    c.DrawText(lab, cx - label.MeasureText(lab) / 2, rect.Bottom - 6, label);
                    break;
                }
                case "trace":
                {
                    w.Hist.Enqueue((nowMs, k));
                    while (w.Hist.Count > 0 && nowMs - w.Hist.Peek().T > w.Seconds * 1000) w.Hist.Dequeue();
                    path.Reset();
                    var area = new SKRect(rect.Left + 8, rect.Top + 22, rect.Right - 8, rect.Bottom - 8);
                    bool first = true;
                    foreach (var (t, hv) in w.Hist)
                    {
                        float x = area.Right - (float)((nowMs - t) / (w.Seconds * 1000) * area.Width);
                        float y = area.Bottom - (float)(hv * area.Height);
                        if (first) { path.MoveTo(x, y); first = false; } else path.LineTo(x, y);
                    }
                    line.Color = col; line.StrokeWidth = 2.5f;
                    c.DrawPath(path, line);
                    label.TextSize = 12;
                    c.DrawText(lab, rect.Left + 10, rect.Top + 16, label);
                    c.DrawText(v.Text + " " + v.Unit, rect.Right - 10 - label.MeasureText(v.Text + " " + v.Unit), rect.Top + 16, label);
                    break;
                }
                default:   // number
                {
                    label.TextSize = Math.Min(13, rect.Height * 0.2f);
                    c.DrawText(lab, rect.Left + 10, rect.Top + label.TextSize + 4, label);
                    value.TextSize = Math.Min(rect.Height * 0.55f, rect.Width * 0.35f);
                    string txt = v.Unit.Length > 0 && w.Data != "gear" ? v.Text : v.Text;
                    float tx = rect.Left + 10, ty = rect.Bottom - rect.Height * 0.14f;
                    c.DrawText(txt, tx, ty, value);
                    if (v.Unit.Length > 0) { label.TextSize = 11; c.DrawText(v.Unit, tx + value.MeasureText(txt) + 6, ty, label); }
                    break;
                }
            }
        }
    }
}
