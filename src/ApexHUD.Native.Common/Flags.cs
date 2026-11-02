namespace ApexHUD.Native.Common;

/// <summary>
/// Decoded snapshot of iRacing's <c>SessionFlags</c> bitfield (a single uint
/// returned by <c>SessionFlags</c> telemetry). The legacy chain exposed these
/// as individual <c>Flag_Red</c>/<c>Flag_Yellow</c>/&hellip; booleans on
/// <c>NewData</c>; we decode the bitfield once per tick here so overlays can
/// bind to plain booleans.
/// </summary>
public readonly record struct FlagState(
    bool Checkered,
    bool White,
    bool Green,
    bool Yellow,
    bool Red,
    bool Blue,
    bool Black,
    bool Orange,
    bool Repair,
    bool Disqualify,
    bool Furled,
    bool ServicibleRequired,
    bool RandomWaving,
    bool StartHidden,
    bool StartReady,
    bool StartSet,
    bool StartGo)
{
    public static readonly FlagState Empty = default;

    /// <summary>
    /// Decode the raw <c>SessionFlags</c> bitfield iRacing publishes. Values
    /// match the <c>irsdk_Flags</c> enum (verified against IRSDKSharper's
    /// <c>IRacingSdkEnum.Flags</c>).
    /// </summary>
    /// <summary>2026-10-19: full-course caution (iRacing caution / caution waving).</summary>
    public bool Caution { get; init; }

    /// <summary>
    /// Decode the raw <c>SessionFlags</c> bitfield iRacing publishes, using the
    /// irsdk_Flags values. 2026-10-19: corrected - the driver block (black, DQ,
    /// servicible, furled, repair) sits at 0x10000+, and 0x4000 / 0x8000 are the
    /// full-course caution bits (they were read as BLACK / DQ, so a caution showed
    /// "BLACK FLAG" on the session bar).
    /// </summary>
    public static FlagState FromBitField(uint bits) => new(
        Checkered:          (bits & 0x00000001u) != 0,
        White:              (bits & 0x00000002u) != 0,
        Green:              (bits & 0x00000004u) != 0,
        Yellow:             (bits & 0x00000008u) != 0 || (bits & 0x00000100u) != 0,   // yellow / yellow waving
        Red:                (bits & 0x00000010u) != 0,
        Blue:               (bits & 0x00000020u) != 0,
        Black:              (bits & 0x00010000u) != 0,
        Orange:             (bits & 0x00100000u) != 0,   // repair = meatball
        Repair:             (bits & 0x00100000u) != 0,
        Disqualify:         (bits & 0x00020000u) != 0,
        Furled:             (bits & 0x00080000u) != 0,
        ServicibleRequired: (bits & 0x00040000u) != 0,
        RandomWaving:       (bits & 0x00002000u) != 0,
        StartHidden:        (bits & 0x10000000u) != 0,
        StartReady:         (bits & 0x20000000u) != 0,
        StartSet:           (bits & 0x40000000u) != 0,
        StartGo:            (bits & 0x80000000u) != 0)
    { Caution = (bits & 0x0000C000u) != 0 };}
