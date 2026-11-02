using System.Diagnostics;

namespace ApexHUD.Native.Overlays;

/// <summary>2026-10-24 (choppy intro): a tiny section stopwatch for the render thread. Sections add their
/// time to a fixed slot; the intro watcher prints the biggest ones for every opener frame, then clears.
/// Cheap: two timestamps per section, no allocation.</summary>
public static class Prof
{
    public const int Max = 48;
    static readonly string[] names = new string[Max];
    static readonly double[] ms = new double[Max];
    static int count;
    static readonly Dictionary<string, int> slot = new();

    public static long T() => Stopwatch.GetTimestamp();

    public static int Slot(string name)
    {
        if (slot.TryGetValue(name, out int i)) return i;
        if (count >= Max) return -1;
        names[count] = name; slot[name] = count;
        return count++;
    }

    /// <summary>Adds the time since <paramref name="t0"/> to the section and returns "now" (chain the next section).</summary>
    public static long Lap(int s, long t0)
    {
        long t1 = Stopwatch.GetTimestamp();
        if (s >= 0) ms[s] += (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        return t1;
    }

    public static void Add(int s, double v) { if (s >= 0) ms[s] += v; }

    /// <summary>"globe.verts 21.3  e.radio 18.0 ..." - the biggest sections this frame (over 0.5 ms).</summary>
    public static string Top(int n)
    {
        Span<int> best = stackalloc int[8]; int k = 0;
        n = Math.Min(n, 8);
        for (int i = 0; i < count; i++)
        {
            if (ms[i] < 0.5) continue;
            int pos = k;
            while (pos > 0 && ms[best[pos - 1]] < ms[i]) pos--;
            if (pos >= n) continue;
            for (int j = Math.Min(k, n - 1); j > pos; j--) best[j] = best[j - 1];
            best[pos] = i; if (k < n) k++;
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < k; i++) sb.Append(names[best[i]]).Append(' ').Append(ms[best[i]].ToString("F1")).Append("  ");
        return sb.ToString();
    }

    public static void Clear() { Array.Clear(ms, 0, count); }
}
