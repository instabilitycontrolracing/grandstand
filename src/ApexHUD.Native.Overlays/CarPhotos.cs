namespace ApexHUD.Native.Overlays;

/// <summary>
/// Finds the big press photo for a car (2026-10-20): assets\logos\sponsors\car_*_panel.png,
/// matched by the words in the file name - "car_porsche_963_panel" fits iRacing's "Porsche 963 GTP",
/// "car_bmw_m4_gt3_panel" fits "BMW M4 GT3 EVO" - so iRacing's exact naming never has to match a file.
/// The best match is the file whose words are ALL in the car name, preferring the most words.
/// </summary>
public static class CarPhotos
{
    static string[]? files;
    static readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Find(string? carName)
    {
        if (string.IsNullOrWhiteSpace(carName)) return null;
        if (cache.TryGetValue(carName, out var hit)) return hit;
        files ??= LoadList();
        return cache[carName] = Best(files, carName);
    }

    /// <summary>True when the owner's press photos are installed (any car_*_panel.png in assets\logos\sponsors).
    /// They are personal and never shipped (2026-10-24).</summary>
    public static bool PersonalInstalled => (files ??= LoadList()).Length > 0;

    /// <summary>Same word matching over picture files in any folder (the user's Documents\iRacing\Grandstand\cars,
    /// 2026-10-24): car_&lt;words&gt;_panel.png/.jpg or car_&lt;words&gt;.png/.jpg.</summary>
    public static string? FindIn(string[] candidates, string? carName) =>
        string.IsNullOrWhiteSpace(carName) ? null : Best(candidates, carName);

    static string? Best(string[] candidates, string carName)
    {
        var words = Words(carName);
        string? best = null; int bestN = 0;
        foreach (var f in candidates)
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            if (!stem.StartsWith("car_", StringComparison.Ordinal)) continue;
            stem = stem.EndsWith("_panel", StringComparison.Ordinal) ? stem.Substring(4, stem.Length - 4 - 6) : stem.Substring(4);   // strip car_ / _panel
            var fw = stem.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (fw.Length == 0 || !fw.All(w => words.Contains(w))) continue;
            if (fw.Length > bestN) { best = f; bestN = fw.Length; }
        }
        return best;
    }

    static HashSet<string> Words(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;   // huracán -> huracan
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        var set = new HashSet<string>(sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // common iRacing spellings
        if (set.Contains("mercedes") || set.Contains("amg")) { set.Add("mercedes"); set.Add("amg"); }
        if (set.Contains("corvette")) set.Add("chevrolet");
        if (set.Contains("vantage")) { set.Add("aston"); set.Add("martin"); }
        if (set.Contains("v") && set.Contains("series")) set.Add("cadillac");
        if (set.Contains("r8")) set.Add("audi");
        if (set.Contains("gt3r")) { set.Add("gt3"); set.Add("r"); }
        return set;
    }

    static string[] LoadList()
    {
        try
        {
            var dir = Assets.Resolve("logos", "sponsors");
            return System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "car_*_panel.png") : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }
}
