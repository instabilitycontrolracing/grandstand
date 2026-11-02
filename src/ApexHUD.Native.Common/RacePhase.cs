namespace ApexHUD.Native.Common;

/// <summary>
/// Three-way race phase that overlays gate visibility on. Mirrors the legacy
/// JS <c>race-phase.js</c> output (<c>pre-race</c> / <c>broadcast</c> /
/// <c>post-race</c> / <c>unknown</c>); rebuilt natively so each overlay binds
/// to a plain enum instead of a CSS <c>data-race-phase</c> attribute.
/// </summary>
public enum RacePhase
{
    /// <summary>SessionState hasn't been populated yet (boot-time / disconnected).</summary>
    Unknown,
    /// <summary>SessionState ∈ {1 GetInCar, 2 Warmup, 3 ParadeLaps}. Starting grid is visible.</summary>
    PreRace,
    /// <summary>SessionState == 4 Racing, or any non-race session. The tower is visible.</summary>
    Broadcast,
    /// <summary>SessionState ∈ {5 Checkered, 6 CoolDown}. Final classification is visible.</summary>
    PostRace,
}

public static class RacePhaseMap
{
    /// <summary>
    /// Map iRacing's <c>SessionState</c> enum to a <see cref="RacePhase"/>,
    /// guarding on whether the current session is a Race (non-race always
    /// resolves to <see cref="RacePhase.Broadcast"/>). Mirrors the legacy
    /// <c>race-phase.js</c> detection rules exactly.
    /// </summary>
    public static RacePhase From(int? sessionState, string? sessionTypeName)
    {
        var isRace = sessionTypeName != null &&
                     sessionTypeName.Contains("race", StringComparison.OrdinalIgnoreCase);
        if (!isRace) return RacePhase.Broadcast;
        return sessionState switch
        {
            null => RacePhase.Unknown,
            1 or 2 or 3 => RacePhase.PreRace,
            4 => RacePhase.Broadcast,
            5 or 6 => RacePhase.PostRace,
            _ => RacePhase.Broadcast,
        };
    }
}
