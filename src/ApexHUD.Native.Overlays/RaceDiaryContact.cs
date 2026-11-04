using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-25 (Angelo: "I have not seen the Trading Paints one even once", in a replay full of incidents).
/// The Car Contact card listened only for incident points (4x jumps). In a replay iRacing never updates other drivers'
/// incident counts, so it never fired. This watches the cars themselves: a car that stops dead (over 6 g along the track
/// for half a second, harder than any car brakes - 4 g caught GTPs braking at Le Mans) or leaves the track with another car within 15 m of it at that
/// moment is a contact. The nearest car is the other party. Points unknown = -1 (the card says CONTACT, no "+Nx").
/// Live races keep the incident-point contacts; a telemetry contact on the same car within 10 s is not added twice.
/// </summary>
public static partial class RaceDiary
{
    sealed class Track { public double T0, D0, T1, D1, V, VPrev, VAt; }
    static readonly Dictionary<int, Track> motion = new();
    static readonly Dictionary<int, DateTime> contactAt = new();

    static void TickContacts(RaceSnapshot snap, DateTime now)
    {
        double len = TrackCorners.LengthM(snap.Extras.TrackLength);
        if (len < 500) return;
        if (snap.SessionState is >= 5) return;    // chequered flag / cool-down: cars slowing for the pits, not contacts
        double t = (now - DateTime.UnixEpoch).TotalSeconds;
        var cars = new List<(Driver D, string Cls, double Dist)>(64);
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var d in cls.Drivers)
                if (!d.IsPaceCar && !d.IsSpectator && d.IsConnected && d.LapDistPct >= 0 && d.LapsCompleted >= 0)
                    cars.Add((d, cls.Name, (d.LapsCompleted + d.LapDistPct) * len));

        foreach (var (d, cn, dist) in cars)
        {
            if (!motion.TryGetValue(d.CarIdx, out var m)) { motion[d.CarIdx] = new Track { T0 = t, D0 = dist, T1 = t, D1 = dist, VAt = t }; continue; }
            if (t - m.T1 < 0.1) continue;
            double dd = dist - m.D1, dt = t - m.T1;
            m.T0 = m.T1; m.D0 = m.D1; m.T1 = t; m.D1 = dist;
            if (dd < -50 || dd > 150 * dt + 50) { m.V = m.VPrev = 0; m.VAt = t; continue; }    // tow, reset, replay jump
            double v = Math.Max(0, dd / dt);
            m.V = 0.6 * m.V + 0.4 * v;
            if (t - m.VAt >= 0.5)
            {
                double decel = (m.VPrev - m.V) / (t - m.VAt);
                // 6 g (was 4): a GTP braking from 330 km/h pulls close to 4 g - 6 g over half a second only happens when it hits something
                bool hardStop = m.VPrev > 20 && decel > 6 * 9.81;
                bool offNow = d.OffTrack && !(wasOff.TryGetValue(d.CarIdx, out var wo) && wo);
                m.VPrev = m.V; m.VAt = t;
                if (d.IsInPit || d.LapsCompleted < 1 || (!hardStop && !offNow)) continue;
                if (contactAt.TryGetValue(d.CarIdx, out var last) && (now - last).TotalSeconds < 10) continue;
                // the nearest other car, within 15 m
                Driver? o = null; string? oc = null; double best = 15;
                foreach (var (d2, cn2, dist2) in cars)
                {
                    if (d2.CarIdx == d.CarIdx || d2.IsInPit) continue;
                    double gap = Math.Abs(((dist2 - dist) % len + len + len / 2) % len - len / 2);
                    if (gap < best) { best = gap; o = d2; oc = cn2; }
                }
                if (o == null && !hardStop) continue;               // off on his own: that's a track-limits trip, not contact
                // #700 (2026-10-31, Angelo: "we need to be able to tell what is a collision versus what is an off track"):
                // going off with a car within 15 m is normal in traffic (a GT3 running wide while it's passed) - it was
                // being logged as contact. Live, iRacing's own points decide (4x = car contact, the points path above);
                // only a dead stop counts from motion. In a replay FILE (no points for other cars) an off counts as
                // contact only when the other car is right there (6 m) and off the track too.
                if (!hardStop && !ReplayState.IsReplayFile) continue;
                if (!hardStop && (o == null || best >= 6 || !o.OffTrack)) continue;
                if (o != null && contactAt.TryGetValue(o.CarIdx, out var lo) && (now - lo).TotalSeconds < 10) continue;
                contactAt[d.CarIdx] = now; if (o != null) contactAt[o.CarIdx] = now;
                var ct = new Contact(d.CarIdx, NameFormat.Broadcast(d), d.CarBrand, cn, Math.Max(1, d.LapsCompleted + 1), -1, Math.Max(0, d.Incidents),
                                     o?.CarIdx ?? -1, o != null ? NameFormat.Broadcast(o) : null, o?.CarBrand, o != null ? -1 : 0, Math.Max(0, o?.Incidents ?? 0), now, d.LapDistPct);
                contacts.Add(ct);
                DropOffFromContact(d.CarIdx, now); if (o != null) DropOffFromContact(o.CarIdx, now);   // #700
                OverlayLog.Write($"[diary] contact (from the cars' motion): #{d.CarNumber}" + (o != null ? $" and #{o.CarNumber}" : "") + (hardStop ? " - stopped hard" : " - off together"));
                if (ContactEvent == null || d.CarIdx == snap.PlayerCarIdx || o?.CarIdx == snap.PlayerCarIdx) ContactEvent = ct;
            }
        }
    }

    /// <summary>#700: an off that was part of a contact (knocked off / spun off after the hit) is not a track-limits
    /// trip - it comes off the On the Limit count.</summary>
    static void DropOffFromContact(int idx, DateTime now)
    {
        if (pendingOff.TryGetValue(idx, out var po) && (now - po.At).TotalSeconds < 5)
        {
            int at = offSpots.FindLastIndex(x => x.CarIdx == idx && x.Pct == po.Pct);
            if (at >= 0) { offSpots.RemoveAt(at); offs[idx] = Math.Max(0, OffsOf(idx) - 1); }
            pendingOff.Remove(idx);
        }
    }

    static void ResetContacts() { motion.Clear(); contactAt.Clear(); }
}
