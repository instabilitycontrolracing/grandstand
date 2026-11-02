using System.IO;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Where a sound comes from (2026-10-24, public build hygiene). For a file name like
/// <c>story_exit.wav</c> the first one that exists wins:
/// <list type="number">
/// <item><c>assets\audio\&lt;name&gt;</c> - the owner's personal sounds (never in git, never shipped)</item>
/// <item><c>Documents\iRacing\Grandstand\audio\&lt;name&gt;</c> - a user's own sounds</item>
/// <item><c>assets\audio_public\&lt;name&gt;</c> - Grandstand's original sounds (tools/make_sfx.py, CC0)</item>
/// </list>
/// Lookups are cached (a sound is played a few times a race, never per frame) and the user folder is
/// created on first use with a README.txt. The opener theme can be any of several extensions, so
/// <see cref="Find(string, string[])"/> tries every extension in a folder before moving to the next one
/// (the owner's grid_opener.mp3 wins over the public grid_opener.wav).
/// </summary>
public static class SoundFiles
{
    public static readonly string UserFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "audio");

    static readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object gate = new();
    static bool readmeDone;

    /// <summary>Full path of the sound to play for <paramref name="fileName"/>, or null when none exists.</summary>
    public static string? Resolve(string fileName) => Find(Path.GetFileNameWithoutExtension(fileName), Path.GetExtension(fileName));

    /// <summary>First existing <c>&lt;stem&gt;&lt;ext&gt;</c> over the three folders (all extensions per folder).</summary>
    public static string? Find(string stem, params string[] extensions)
    {
        string key = stem + "|" + string.Join(",", extensions);
        lock (gate)
        {
            if (cache.TryGetValue(key, out var hit)) return hit;
            EnsureReadme();
            string? found = null;
            try
            {
                foreach (var dir in Folders())
                {
                    foreach (var ext in extensions)
                    {
                        var p = Path.Combine(dir, stem + ext);
                        if (File.Exists(p)) { found = p; break; }
                    }
                    if (found != null) break;
                }
            }
            catch { found = null; }
            return cache[key] = found;
        }
    }

    /// <summary>"personal", "user" or "public" for a resolved path (logs and the control app).</summary>
    public static string SourceOf(string? path)
    {
        if (path == null) return "none";
        if (path.StartsWith(Assets.Resolve("audio") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "personal";
        if (path.StartsWith(UserFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "user";
        return "public";
    }

    /// <summary>Forget cached lookups (after the user drops new files in).</summary>
    public static void Rescan() { lock (gate) cache.Clear(); }

    static IEnumerable<string> Folders()
    {
        yield return Assets.Resolve("audio");
        yield return UserFolder;
        yield return Assets.Resolve("audio_public");
    }

    static void EnsureReadme()
    {
        if (readmeDone) return;
        readmeDone = true;
        try
        {
            Directory.CreateDirectory(UserFolder);
            var readme = Path.Combine(UserFolder, "README.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme,
                    "Grandstand - your own sounds\r\n" +
                    "============================\r\n\r\n" +
                    "Put a .wav file here with the same name as one of Grandstand's sounds and Grandstand plays yours\r\n" +
                    "instead (restart Grandstand after adding files). Names:\r\n\r\n" +
                    "  story_racesofar  story_yourrace  story_moveofrace  story_fastestlaps  story_pitstops\r\n" +
                    "  story_halfway  story_weather  story_driverofday  story_contact  story_limits  story_clean\r\n" +
                    "  story_exit (a card leaving)   grid_tick   outro_flag  outro_podium  outro_results\r\n" +
                    "  pre_panel_in  pre_weather  pre_poles  pre_ab  pre_storm  pre_globe\r\n" +
                    "  grid_opener (.mp3, .m4a, .wav or .wma - the pre-race opener theme; the grid intro's pages\r\n" +
                    "               land at 0.0, 4.4, 8.0 and 12.07 s, the board leaves at 32.21 s)\r\n\r\n" +
                    "Use 16-bit PCM .wav for the short sounds (Windows plays those directly). Keep them short and\r\n" +
                    "not too loud. Only use sounds you made or have the rights to share on a stream.\r\n" +
                    "Grandstand's own sounds are in <install folder>\\assets\\audio_public (original, CC0).\r\n");
        }
        catch { /* Documents not writable: sounds still come from the install folder */ }
    }
}
