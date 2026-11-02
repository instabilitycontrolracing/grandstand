namespace ApexHUD.Native.Common;

/// <summary>
/// Canonical broadcast driver-name formatter. Every overlay in the suite
/// routes through <see cref="Broadcast(string?, string?)"/> so names render
/// as <c>"F. LASTNAME"</c> in ALL CAPS everywhere they appear
/// (e.g. <c>"A. RAMORELLOS"</c>).
/// </summary>
/// <remarks>
/// Accepts both shapes iRacing exposes:
/// <list type="bullet">
///   <item><description><c>UserName</c> form: <c>"First Last"</c> →
///   <c>"F. LAST"</c>.</description></item>
///   <item><description><c>AbbrevName</c> form: <c>"Last, F"</c> →
///   <c>"F. LAST"</c>.</description></item>
/// </list>
/// <para><b>Surname assumption.</b> For the <c>"First Last"</c> form, the
/// last whitespace-delimited token is treated as the surname. This is wrong
/// for names with multi-token surnames (e.g. <c>"Pieter Van der Berg"</c>
/// would resolve to <c>"P. BERG"</c>). We prefer the comma form when it's
/// available because iRacing's <c>AbbrevName</c> is authored with the full
/// surname before the comma, which sidesteps the assumption entirely.</para>
/// </remarks>
public static class NameFormat
{
    /// <summary>
    /// Format a driver name as <c>"F. LASTNAME"</c> upper-cased, choosing
    /// the best of the two sources iRacing exposes.
    /// </summary>
    /// <param name="userName"><c>DriverModel.UserName</c> — typically
    /// <c>"First Last"</c>.</param>
    /// <param name="abbrevName"><c>DriverModel.AbbrevName</c> — typically
    /// <c>"Last, F"</c>. Preferred when present because it carries the full
    /// surname unambiguously.</param>
    public static string Broadcast(string? userName, string? abbrevName = null)
    {
        var fromAbbrev = FormatOne(abbrevName);
        if (!string.IsNullOrEmpty(fromAbbrev)) return fromAbbrev;
        return FormatOne(userName);
    }

    /// <summary>Convenience overload for the common <see cref="Driver"/> case.</summary>
    public static string Broadcast(Driver d) => Broadcast(d.Name, d.AbbrevName);

    static string FormatOne(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();

        // Strip a legacy roster prefix like "GTP-Smith, Joe" → "Smith, Joe".
        int dash = s.IndexOf('-');
        if (dash > 1 && dash <= 5 && IsAllUpper(s.AsSpan(0, dash)))
            s = s[(dash + 1)..].Trim();

        // Comma form ("Last, F" or "Last, First") — preferred shape.
        int comma = s.IndexOf(',');
        if (comma > 0)
        {
            var lastPart = s[..comma].Trim();
            var firstPart = s[(comma + 1)..].Trim();
            if (lastPart.Length == 0) return string.Empty;
            if (firstPart.Length == 0) return lastPart.ToUpperInvariant();
            return char.ToUpperInvariant(firstPart[0]) + ". " + lastPart.ToUpperInvariant();
        }

        // Space-separated form ("First Last" or "First Middle Last").
        var parts = s.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return char.ToUpperInvariant(parts[0][0]) + ". " + parts[^1].ToUpperInvariant();
        return parts[0].ToUpperInvariant();
    }

    static bool IsAllUpper(System.ReadOnlySpan<char> s)
    {
        foreach (var ch in s)
            if (!char.IsLetter(ch) || !char.IsUpper(ch)) return false;
        return true;
    }
}
