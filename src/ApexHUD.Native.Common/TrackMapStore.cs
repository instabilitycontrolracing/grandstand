using System.Text.Json;

namespace ApexHUD.Native.Common;

/// <summary>
/// The permanent track-map cache (Angelo 2026-10-19). Maps live in
/// Documents\iRacing\ApexHUD\maps as "&lt;Track Display Name&gt;.json" (the racing line in a
/// 1200 px wide canvas, same format as map_from_ibt.py). The live recorder writes here;
/// the overlays copy new entries into assets\maps and render the matching PNG.
/// </summary>
public static class TrackMapStore
{
    // 2026-10-27 (#48 rename stage 2): Documents\iRacing\Grandstand\maps. The old ApexHUD\maps folder is copied
    // across once (never deleted) and still read if the copy couldn't be made.
    public static readonly string Folder = MigrateFolder(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "ApexHUD", "maps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "maps"));

    /// <summary>The new folder, filled once from the old one (files that aren't there yet); the old one if that fails.</summary>
    public static string MigrateFolder(string oldDir, string newDir)
    {
        try
        {
            Directory.CreateDirectory(newDir);
            if (Directory.Exists(oldDir))
                foreach (var f in Directory.GetFiles(oldDir, "*", SearchOption.AllDirectories))
                {
                    string dst = Path.Combine(newDir, Path.GetRelativePath(oldDir, f));
                    if (File.Exists(dst)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst);
                }
            return newDir;
        }
        catch { return Directory.Exists(oldDir) ? oldDir : newDir; }
    }
    static readonly string Bundled = Path.Combine(AppContext.BaseDirectory, "assets", "maps");

    static int version;
    /// <summary>Bumps every time a new map is saved, so overlays can refresh their index.</summary>
    public static int Version => Volatile.Read(ref version);

    public static string SafeName(string trackName)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(trackName.Trim().Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
    }

    /// <summary>True if a map already exists for this track (cache or bundled), by exact or loose name.</summary>
    public static bool Has(string trackName)
    {
        string key = Norm(trackName);
        if (key.Length < 4) return true;   // no usable name - don't record
        foreach (var dir in new[] { Folder, Bundled })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                string k = Norm(Path.GetFileNameWithoutExtension(f));
                if (k == key || k.Contains(key) || key.Contains(k)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Save a recorded lap (world metres, x east / y north, in lap order starting at the
    /// start/finish line). Turns it landscape, puts the start/finish on the lower half,
    /// fits it to a 1200 px canvas and writes the JSON. Returns the file path.
    /// </summary>
    public static string Save(string trackName, IReadOnlyList<(double X, double Y)> world)
    {
        int n = world.Count;
        // screen space: y down
        var pts = world.Select(p => (X: p.X, Y: -p.Y)).ToArray();
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        // principal axis -> horizontal, so the track fills the wide broadcast frames
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts) { double dx = p.X - mx, dy = p.Y - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
        double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        double ca = Math.Cos(-ang), sa = Math.Sin(-ang);
        var rot = pts.Select(p => (X: (p.X - mx) * ca - (p.Y - my) * sa, Y: (p.X - mx) * sa + (p.Y - my) * ca)).ToArray();
        // start/finish on the lower half (the broadcast convention), by a 180 degree turn if needed
        if (rot[0].Y < 0) rot = rot.Select(p => (X: -p.X, Y: -p.Y)).ToArray();

        const double Pad = 40, Long = 1200;
        double minX = rot.Min(p => p.X), maxX = rot.Max(p => p.X), minY = rot.Min(p => p.Y), maxY = rot.Max(p => p.Y);
        double s = (Long - 2 * Pad) / Math.Max(maxX - minX, maxY - minY);
        int w = (int)((maxX - minX) * s + 2 * Pad), h = (int)((maxY - minY) * s + 2 * Pad);
        int step = Math.Max(1, n / 600);
        var outPts = new List<double[]>();
        for (int i = 0; i < n; i += step)
            outPts.Add(new[] { Math.Round((rot[i].X - minX) * s + Pad, 1), Math.Round((rot[i].Y - minY) * s + Pad, 1) });

        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, SafeName(trackName) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { w, h, points = outPts }));
        Interlocked.Increment(ref version);
        return path;
    }

    /// <summary>
    /// Best map file (name without extension) in <paramref name="folder"/> for this track + layout:
    /// 1) "Track - Layout" exactly, 2) "Track" alone (hand-made / recorded maps), 3) any layout of
    /// the track, 4) loose name containment ("Road Atlanta" ~ "Michelin Raceway Road Atlanta").
    /// </summary>
    public static string? FindBaseName(string folder, string? config, params string?[] trackNames)
    {
        if (!Directory.Exists(folder)) return null;
        var files = Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        var full = files.Select(f => (File: f, Key: NormFull(f), Base: Norm(f))).ToList();
        foreach (var raw in trackNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string n = Norm(raw);
            if (n.Length < 4) continue;
            if (!string.IsNullOrWhiteSpace(config))
            {
                string want = NormFull(raw.Split(" - ")[0] + config);
                var hit = full.FirstOrDefault(f => f.Key == want);
                if (hit.File != null) return hit.File;
            }
            var exact = full.FirstOrDefault(f => f.Key == n);
            if (exact.File != null) return exact.File;
            var sameTrack = full.FirstOrDefault(f => f.Base == n);
            if (sameTrack.File != null) return sameTrack.File;
            var loose = full.FirstOrDefault(f => f.Base.Contains(n) || n.Contains(f.Base));
            if (loose.File != null) return loose.File;
        }
        return null;
    }

    static string NormFull(string s) => Norm(s.Replace(" - ", " "));

    public static string Norm(string s)
    {
        var d = (s ?? "").Split(" - ")[0].Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (char ch in d)
            if (char.IsLetterOrDigit(ch) && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
