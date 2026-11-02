using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>How big a viewer moment is. Tier 1 is the most expensive (Angelo's numbering).</summary>
public enum ViewerTier { Chat = 0, Tier3 = 3, Tier2 = 2, Tier1 = 1 }

/// <summary>
/// 2026-10-29 (#576): the tiered viewer looks - which look a viewer event gets, from the control app's thresholds.
/// <list type="bullet">
/// <item>Chat comment: a small chat card.</item>
/// <item>Tier 3 (under <see cref="Tier2From"/>, default $10; also every new member / sub): a gold-edged card.</item>
/// <item>Tier 2 (<see cref="Tier2From"/> up to <see cref="Tier1From"/>; also <see cref="GiftTier2"/>+ gifted subs): takes
///   over the session bar for ~8 s.</item>
/// <item>Tier 1 (from <see cref="Tier1From"/>, default $50): a full-screen moment, then a thank-you band on the tower.</item>
/// </list>
/// Amounts: the number in the event's own text ("$5.00", "€20", "£7,50", "500 bits"); bits are divided by
/// <see cref="BitsPerDollar"/>; any currency is taken as its plain number (nothing is converted - close enough to pick a look).
/// OFF by default (<see cref="LooksOn"/>): until Angelo approves, the existing strip / member card keep playing.
/// </summary>
public static class ViewerTiers
{
    /// <summary>control.json viewer.tierLooks - the tiered looks replace the strip / member card. OFF by default.</summary>
    public static volatile bool LooksOn;
    /// <summary>From this amount (dollars) a paid message is tier 2. Below it: tier 3.</summary>
    public static double Tier2From = 10;
    /// <summary>From this amount (dollars) a paid message is tier 1.</summary>
    public static double Tier1From = 50;
    /// <summary>Bits per dollar (Twitch: 100 bits = $1).</summary>
    public static double BitsPerDollar = 100;
    /// <summary>A gift of this many subs or more is tier 2 (a single member / sub is tier 3).</summary>
    public static int GiftTier2 = 5;
    /// <summary>Play the thank-you roll by itself after the post-race outro. OFF by default.</summary>
    public static volatile bool AutoThanks;
    /// <summary>#603: control.json viewer.tierSounds - the looks play the existing broadcast stingers (TV / stream only). ON by default.</summary>
    public static volatile bool SoundsOn = true;

    /// <summary>The dollar value used to pick a look (and to rank supporters): the amount's number times a rough rate for
    /// its currency (<see cref="CurrencyOf"/> / <see cref="RoughUsdRate"/>); bits / 100. Never shown on screen - the amount
    /// is always shown exactly as it came in. 2026-10-30 (#603): a ¥1,000 or ₹100 Super Chat (a few dollars) used to count
    /// as $1,000 / $100 and got the full-screen tier 1 moment.</summary>
    public static double? Usd(ViewerKind kind, string? amount, string? currency = null)
    {
        if (string.IsNullOrWhiteSpace(amount)) return null;
        var m = Regex.Match(amount, @"\d[\d.,]*");
        if (!m.Success) return null;
        bool bits = kind == ViewerKind.Bits || amount.Contains("bit", StringComparison.OrdinalIgnoreCase);
        var p = ViewerFeed.ParseMoney(amount);
        if (p is not { } v) return null;
        double val = v.Value;
        if (bits) return val / Math.Max(1, BitsPerDollar);
        string code = CurrencyOf(amount, currency);
        return code.Length > 0 && RoughUsdRate.TryGetValue(code, out double rate) ? Math.Round(val * rate, 2) : val;
    }

    /// <summary>The ISO code of an amount: the event's own code (YouTube's currencyCode, StreamElements' tipCurrency) when
    /// sent, otherwise worked out from the symbol in the text ("CA$10.00", "¥1,000", "₹100.00", "R$ 10,00", "10 EUR").
    /// Empty when it can't tell (the plain number is used then).</summary>
    public static string CurrencyOf(string? amount, string? currency)
    {
        string c = (currency ?? "").Trim().ToUpperInvariant();
        if (c.Length == 3 && RoughUsdRate.ContainsKey(c)) return c;
        string a = (amount ?? "").Trim();
        if (a.Length == 0) return "";
        var code = Regex.Match(a.ToUpperInvariant(), @"\b[A-Z]{3}\b");
        if (code.Success && RoughUsdRate.ContainsKey(code.Value)) return code.Value;
        foreach (var (sym, iso) in Symbols)
            if (a.Contains(sym, StringComparison.Ordinal)) return iso;
        return "";
    }

    // longest first: "CA$" before "$", "CN¥" before "¥"
    static readonly (string Sym, string Iso)[] Symbols =
    {
        ("CA$", "CAD"), ("C$", "CAD"), ("AU$", "AUD"), ("A$", "AUD"), ("NZ$", "NZD"), ("R$", "BRL"), ("MX$", "MXN"), ("HK$", "HKD"),
        ("NT$", "TWD"), ("SG$", "SGD"), ("S$", "SGD"), ("US$", "USD"), ("CN¥", "CNY"), ("RMB", "CNY"), ("元", "CNY"), ("￥", "JPY"),
        ("¥", "JPY"), ("€", "EUR"), ("£", "GBP"), ("₹", "INR"), ("₩", "KRW"), ("₱", "PHP"), ("₺", "TRY"), ("₪", "ILS"), ("₫", "VND"),
        ("₴", "UAH"), ("₦", "NGN"), ("฿", "THB"), ("₽", "RUB"), ("zł", "PLN"), ("Kč", "CZK"), ("Ft", "HUF"), ("RM", "MYR"),
        ("Rp", "IDR"), ("kr", "SEK"), ("CHF", "CHF"), ("$", "USD"),
    };

    /// <summary>Rough dollars per unit, only to pick a tier look and rank the roll (late-2026 ballpark; a few percent off
    /// doesn't change a look). Nothing on screen is ever converted.</summary>
    public static readonly IReadOnlyDictionary<string, double> RoughUsdRate = new Dictionary<string, double>
    {
        ["USD"] = 1, ["EUR"] = 1.08, ["GBP"] = 1.27, ["CAD"] = 0.73, ["AUD"] = 0.65, ["NZD"] = 0.60, ["CHF"] = 1.12,
        ["JPY"] = 0.0066, ["CNY"] = 0.14, ["HKD"] = 0.128, ["TWD"] = 0.031, ["SGD"] = 0.74, ["KRW"] = 0.00073, ["INR"] = 0.012,
        ["PHP"] = 0.017, ["IDR"] = 0.000062, ["MYR"] = 0.22, ["THB"] = 0.028, ["VND"] = 0.00004, ["BRL"] = 0.18, ["MXN"] = 0.055,
        ["ARS"] = 0.001, ["CLP"] = 0.00105, ["COP"] = 0.00025, ["PEN"] = 0.27, ["SEK"] = 0.095, ["NOK"] = 0.093, ["DKK"] = 0.145,
        ["ISK"] = 0.0072, ["PLN"] = 0.25, ["CZK"] = 0.043, ["HUF"] = 0.0027, ["RON"] = 0.22, ["BGN"] = 0.55, ["TRY"] = 0.03,
        ["ILS"] = 0.27, ["SAR"] = 0.27, ["AED"] = 0.27, ["QAR"] = 0.27, ["EGP"] = 0.02, ["ZAR"] = 0.054, ["NGN"] = 0.00065,
        ["UAH"] = 0.024, ["RUB"] = 0.011, ["KZT"] = 0.002, ["RSD"] = 0.0092, ["GEL"] = 0.37, ["MAD"] = 0.1, ["KES"] = 0.0077,
        ["PKR"] = 0.0036, ["BDT"] = 0.0084, ["LKR"] = 0.0033, ["CRC"] = 0.0019, ["DOP"] = 0.017, ["GTQ"] = 0.13, ["HNL"] = 0.04,
        ["PYG"] = 0.00013, ["UYU"] = 0.025, ["BOB"] = 0.145, ["BYN"] = 0.3,
    };

    /// <summary>The look for one event.</summary>
    public static ViewerTier TierOf(ViewerEvent e)
    {
        if (e.Kind == ViewerKind.Comment) return ViewerTier.Chat;
        if (e.Kind is ViewerKind.Member or ViewerKind.Sub) return e.Gifts >= GiftTier2 ? ViewerTier.Tier2 : ViewerTier.Tier3;
        return TierOfUsd(e.Usd ?? 0);
    }

    public static ViewerTier TierOfUsd(double usd) => usd >= Tier1From ? ViewerTier.Tier1 : usd >= Tier2From ? ViewerTier.Tier2 : ViewerTier.Tier3;

    /// <summary>"$10 – $49.99" style range text for a tier (roll pages, control app).</summary>
    public static string RangeText(ViewerTier t) => t switch
    {
        ViewerTier.Tier1 => Money(Tier1From) + " AND UP",
        ViewerTier.Tier2 => Money(Tier2From) + " – " + Money(Math.Max(Tier2From, Tier1From - 0.01)),
        ViewerTier.Tier3 => "UP TO " + Money(Math.Max(0.01, Tier2From - 0.01)) + "  ·  MEMBERS & SUBS",
        _ => "",
    };

    static string Money(double v) => v % 1 == 0
        ? "$" + v.ToString("N0", CultureInfo.InvariantCulture)
        : "$" + v.ToString("N2", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- text safety

    static readonly Dictionary<(SKTypeface, int), bool> glyphCache = new();
    static readonly object glyphGate = new();

    /// <summary>Drops every character the typeface can't draw (emoji, symbols outside the font) so nothing shows as a box;
    /// collapses the spaces left behind. Empty if nothing drawable is left.</summary>
    public static string Drawable(string? s, SKTypeface tf)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(r)) { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            bool ok;
            lock (glyphGate)
            {
                if (!glyphCache.TryGetValue((tf, r.Value), out ok))
                {
                    ok = tf.ContainsGlyph(r.Value);
                    if (glyphCache.Count > 20000) glyphCache.Clear();
                    glyphCache[(tf, r.Value)] = ok;
                }
            }
            if (ok) sb.Append(r.ToString());
        }
        return sb.ToString().Trim();
    }
}
