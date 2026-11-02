using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The project's bundled broadcast typefaces, loaded lazily from
/// <c>assets/fonts/</c> on first use via the <see cref="Assets.LoadTypeface"/>
/// pattern established in Port 01.
///
/// <para>Two families ported from the legacy ApexHUD-Web theme:
/// <b>Cormorant Garamond</b> (elegant serif, for display/body) and
/// <b>Inter</b> (technical sans, for UI/timing). Each weight is its own
/// static <c>.ttf</c>, so loading it by file path yields exactly that weight —
/// we deliberately do not rely on <see cref="SKFontManager"/> family matching
/// (Google ships weights ≥500 under split family names such as
/// "Inter SemiBold"). Italic variants are also bundled on disk; expose them
/// here when an overlay first needs one.</para>
///
/// <para>Typefaces live for the process lifetime; they are intentionally never
/// disposed.</para>
/// </summary>
public static class Fonts
{
    /// <summary>Elegant serif — display and body text. Weights 400/500/600/700.</summary>
    public static class CormorantGaramond
    {
        static readonly Lazy<SKTypeface> regular = Load("CormorantGaramond-Regular.ttf");
        static readonly Lazy<SKTypeface> medium = Load("CormorantGaramond-Medium.ttf");
        static readonly Lazy<SKTypeface> semiBold = Load("CormorantGaramond-SemiBold.ttf");
        static readonly Lazy<SKTypeface> bold = Load("CormorantGaramond-Bold.ttf");

        public static SKTypeface Regular => regular.Value;
        public static SKTypeface Medium => medium.Value;
        public static SKTypeface SemiBold => semiBold.Value;
        public static SKTypeface Bold => bold.Value;

        static Lazy<SKTypeface> Load(string file) =>
            new(() => Assets.LoadTypeface("cormorant-garamond", file));
    }

    /// <summary>Technical sans — UI, labels, timing. Weights 400/500/600/700.</summary>
    public static class Inter
    {
        static readonly Lazy<SKTypeface> regular = Load("Inter-Regular.ttf");
        static readonly Lazy<SKTypeface> medium = Load("Inter-Medium.ttf");
        static readonly Lazy<SKTypeface> semiBold = Load("Inter-SemiBold.ttf");
        static readonly Lazy<SKTypeface> bold = Load("Inter-Bold.ttf");

        public static SKTypeface Regular => regular.Value;
        public static SKTypeface Medium => medium.Value;
        public static SKTypeface SemiBold => semiBold.Value;
        public static SKTypeface Bold => bold.Value;

        static Lazy<SKTypeface> Load(string file) =>
            new(() => Assets.LoadTypeface("inter", file));
    }

    /// <summary>Condensed racing sans — broadcast headlines and big numbers
    /// (story cards). Weights 500/600/700/800/900.</summary>
    public static class BarlowCondensed
    {
        static readonly Lazy<SKTypeface> medium = Load("BarlowCondensed-Medium.ttf");
        static readonly Lazy<SKTypeface> semiBold = Load("BarlowCondensed-SemiBold.ttf");
        static readonly Lazy<SKTypeface> bold = Load("BarlowCondensed-Bold.ttf");
        static readonly Lazy<SKTypeface> extraBold = Load("BarlowCondensed-ExtraBold.ttf");
        static readonly Lazy<SKTypeface> black = Load("BarlowCondensed-Black.ttf");

        public static SKTypeface Medium => medium.Value;
        public static SKTypeface SemiBold => semiBold.Value;
        public static SKTypeface Bold => bold.Value;
        public static SKTypeface ExtraBold => extraBold.Value;
        public static SKTypeface Black => black.Value;

        static Lazy<SKTypeface> Load(string file) =>
            new(() => Assets.LoadTypeface("barlow-condensed", file));
    }

    /// <summary>Force every bundled typeface to load now (off the render thread)
    /// so the first overlay to use one does not pay the decode cost mid-frame.</summary>
    public static void WarmUp()
    {
        _ = Inter.Regular; _ = Inter.Medium; _ = Inter.SemiBold; _ = Inter.Bold;
        _ = CormorantGaramond.Regular; _ = CormorantGaramond.Medium; _ = CormorantGaramond.SemiBold; _ = CormorantGaramond.Bold;
        _ = BarlowCondensed.Medium; _ = BarlowCondensed.SemiBold; _ = BarlowCondensed.Bold; _ = BarlowCondensed.ExtraBold; _ = BarlowCondensed.Black;
    }
}
